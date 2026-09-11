using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wms.IntegrationTests.Infrastructure;
using Wms.Modules.Identity.Infrastructure;
using Xunit;

namespace Wms.IntegrationTests.Work;

/// <summary>
/// <c>POST /work/leases</c> over HTTP — routing, permission mapping, and the
/// empty-queue contract.
/// </summary>
/// <remarks>
/// No tasks are seeded: an empty queue is enough to prove the endpoint is
/// reachable, mapped to <c>task.lease</c>, and answers <c>200</c> rather than
/// <c>404</c> when there is nothing to do. The dispatch behaviour itself is
/// covered at the service level, where a race can actually be staged.
/// </remarks>
public sealed class WorkLeaseEndpointTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Endpoint = "/api/v1/work/leases";

    /// <summary>
    /// Set explicitly rather than relying on <c>appsettings.Development.json</c>
    /// being on the content root: the API refuses to start without one, so a
    /// test that depended on the file would hide that requirement.
    /// </summary>
    private const string TestSigningKey =
        "test-only-signing-key-for-integration-tests-not-for-production";

    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment(Environments.Development);
            host.UseSetting("ConnectionStrings:Wms", fixture.ConnectionString);
            host.UseSetting("Wms:Auth:SigningKey", TestSigningKey);
            host.UseSetting("Wms:Auth:AllowDevelopmentHeaderPrincipal", "true");
        });

        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// An idle poller is a normal state (§5.3). A handheld polling every few
    /// seconds would otherwise show an operator a failure for having nothing
    /// to do.
    /// </summary>
    [Fact]
    public async Task WithAnEmptyQueue_ItIs200WithNoLease_Not404()
    {
        Seeded data = await SeedAsync();

        HttpResponseMessage response = await PostAsync(data.PutawayOperatorId, new
        {
            warehouseId = data.WarehouseId,
            taskTypes = new[] { "putaway" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("leaseId").ValueKind);
        Assert.Empty(body.RootElement.GetProperty("tasks").EnumerateArray());
    }

    [Fact]
    public async Task AnOperatorWithoutTaskLease_IsForbidden()
    {
        Seeded data = await SeedAsync();

        HttpResponseMessage response = await PostAsync(data.AuditorId, new
        {
            warehouseId = data.WarehouseId,
            taskTypes = new[] { "putaway" },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Invariant 8 is permission <em>plus</em> scope. The putaway operator
    /// holds <c>task.lease</c>, but only where they were scoped to — naming a
    /// different warehouse must not let them drain its ready queue.
    /// </summary>
    [Fact]
    public async Task AnOperatorCannotLeaseFromAWarehouseTheyAreNotScopedTo()
    {
        Seeded data = await SeedAsync();
        Guid otherWarehouseId = await SeedAnotherWarehouseAsync();

        HttpResponseMessage response = await PostAsync(data.PutawayOperatorId, new
        {
            warehouseId = otherWarehouseId,
            taskTypes = new[] { "putaway" },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("insufficient-permission", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownTaskType_Is400()
    {
        Seeded data = await SeedAsync();

        HttpResponseMessage response = await PostAsync(data.PutawayOperatorId, new
        {
            warehouseId = data.WarehouseId,
            taskTypes = new[] { "teleport" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReleasingALeaseThatDoesNotExist_Is204_NotAnError()
    {
        Seeded data = await SeedAsync();

        HttpRequestMessage request = new(
            HttpMethod.Delete, $"{Endpoint}/{Guid.CreateVersion7()}");
        request.Headers.Add(
            DevelopmentPrincipalResolver.OperatorHeader, data.PutawayOperatorId.ToString());
        request.Headers.Add("Authorization", data.PutawayOperatorId.ToString());

        HttpResponseMessage response = await _client.SendAsync(request);

        // A device retrying after a dropped response must not be told it
        // failed for succeeding twice.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private Task<HttpResponseMessage> PostAsync(Guid operatorId, object body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(DevelopmentPrincipalResolver.OperatorHeader, operatorId.ToString());
        request.Headers.Add("Authorization", operatorId.ToString());

        return _client.SendAsync(request);
    }

    private async Task<Seeded> SeedAsync()
    {
        Seeded data = new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, right(@warehouseId::text, 12), 'T', 'Asia/Tokyo', now(), now());

            INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                  valid_from, created_at, updated_at)
            VALUES (@putawayId, 'operator', 'Putaway', 'active', gen_random_uuid(),
                    current_date, now(), now()),
                   (@auditorId, 'staff', 'Auditor', 'active', gen_random_uuid(),
                    current_date, now(), now());

            -- Real seeded roles: Putaway Operator holds task.lease, Auditor
            -- holds no mutating permission at all.
            INSERT INTO user_role_scope (id, user_id, role_id, warehouse_id,
                                         granted_by, granted_at)
            VALUES (gen_random_uuid(), @putawayId,
                    '10000000-0000-0000-0000-000000000006', @warehouseId, @putawayId, now()),
                   (gen_random_uuid(), @auditorId,
                    '10000000-0000-0000-0000-000000000007', @warehouseId, @auditorId, now());
            """);

        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("putawayId", data.PutawayOperatorId);
        command.Parameters.AddWithValue("auditorId", data.AuditorId);

        await command.ExecuteNonQueryAsync();
        return data;
    }

    private async Task<Guid> SeedAnotherWarehouseAsync()
    {
        Guid warehouseId = Guid.CreateVersion7();

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, right(@warehouseId::text, 12), 'T2', 'Asia/Tokyo', now(), now());
            """);
        command.Parameters.AddWithValue("warehouseId", warehouseId);

        await command.ExecuteNonQueryAsync();
        return warehouseId;
    }

    private sealed record Seeded(Guid WarehouseId, Guid PutawayOperatorId, Guid AuditorId);
}
