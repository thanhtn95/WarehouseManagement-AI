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

namespace Wms.IntegrationTests.Receiving;

/// <summary>
/// The receipt commands over real HTTP — routing, permission mapping, and the
/// status codes §5.4 specifies.
/// </summary>
/// <remarks>
/// These exist because the service-level tests cannot see any of it. A
/// receipt endpoint mapped to the wrong permission, or not mapped at all,
/// would leave every service test green.
/// </remarks>
public sealed class ReceiptEndpointTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
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

    [Fact]
    public async Task ARecevierCanOpenAndStartAReceipt()
    {
        Seeded data = await SeedAsync();

        HttpResponseMessage created = await PostAsync(data.ReceiverId, "/api/v1/receipts", new
        {
            warehouseId = data.WarehouseId,
            receiptType = "blind",
            ownerId = data.OwnerId,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid receiptId = body.RootElement.GetProperty("id").GetGuid();
        Assert.StartsWith("RCV-", body.RootElement.GetProperty("receiptNumber").GetString()!,
            StringComparison.Ordinal);

        HttpResponseMessage started = await PostAsync(
            data.ReceiverId, $"/api/v1/receipts/{receiptId}/start", new { });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        // Starting again is a documented 409, not a 500 and not a silent OK.
        HttpResponseMessage again = await PostAsync(
            data.ReceiverId, $"/api/v1/receipts/{receiptId}/start", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("invalid-transition", await again.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An Auditor holds no mutating permission at all — the seeded role matrix
    /// says so, and the endpoint must enforce it rather than trusting the UI
    /// to hide the button (invariant 8).
    /// </summary>
    [Fact]
    public async Task AnAuditorCannotOpenAReceipt()
    {
        Seeded data = await SeedAsync();

        HttpResponseMessage response = await PostAsync(data.AuditorId, "/api/v1/receipts", new
        {
            warehouseId = data.WarehouseId,
            receiptType = "blind",
            ownerId = data.OwnerId,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("insufficient-permission", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Invariant 8 is permission <em>plus</em> scope. A receiver holds
    /// <c>receipt.create</c>, but only in the warehouse they were scoped to —
    /// naming a different one must not be enough to open a receipt there.
    /// </summary>
    [Fact]
    public async Task AReceiverCannotOpenAReceiptInAWarehouseTheyAreNotScopedTo()
    {
        Seeded data = await SeedAsync();

        HttpResponseMessage response = await PostAsync(data.ReceiverId, "/api/v1/receipts", new
        {
            warehouseId = data.OtherWarehouseId,
            receiptType = "blind",
            ownerId = data.OwnerId,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("insufficient-permission", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Start</c>/<c>Complete</c> carry only the receipt id, so the scope
    /// check has to look the warehouse up rather than trust the caller —
    /// otherwise a receiver scoped to one site could confirm receipts opened
    /// in a site they hold no grant in at all.
    /// </summary>
    [Fact]
    public async Task AReceiverCannotStartAReceiptOpenedInAnotherWarehouse()
    {
        Seeded data = await SeedAsync();
        Guid foreignReceiptId = await SeedDraftReceiptAsync(data.OtherWarehouseId, data.OwnerId);

        HttpResponseMessage response = await PostAsync(
            data.ReceiverId, $"/api/v1/receipts/{foreignReceiptId}/start", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("insufficient-permission", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletingAnUnknownReceipt_Is404_NotA500()
    {
        Seeded data = await SeedAsync();

        HttpResponseMessage response = await PostAsync(
            data.ReceiverId, $"/api/v1/receipts/{Guid.CreateVersion7()}/complete", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownReceiptType_Is400()
    {
        Seeded data = await SeedAsync();

        HttpResponseMessage response = await PostAsync(data.ReceiverId, "/api/v1/receipts", new
        {
            warehouseId = data.WarehouseId,
            receiptType = "teleported",
            ownerId = data.OwnerId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A receipt already opened in a warehouse the test's receiver holds no
    /// scope in — standing in for another site's own receiver having created
    /// it, which is the scenario the scope check on <c>start</c>/<c>complete</c>
    /// exists for.
    /// </summary>
    private async Task<Guid> SeedDraftReceiptAsync(Guid warehouseId, Guid ownerId)
    {
        Guid receiptId = Guid.CreateVersion7();
        Guid creatorId = Guid.CreateVersion7();

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                  valid_from, created_at, updated_at)
            VALUES (@creatorId, 'operator', 'Foreign receiver', 'active', gen_random_uuid(),
                    current_date, now(), now());

            INSERT INTO receipt (id, warehouse_id, receipt_number, receipt_type,
                                 owner_id, status, created_by, created_at, updated_at)
            VALUES (@receiptId, @warehouseId, right(@receiptId::text, 12), 'blind',
                    @ownerId, 'draft', @creatorId, now(), now());
            """);

        command.Parameters.AddWithValue("receiptId", receiptId);
        command.Parameters.AddWithValue("warehouseId", warehouseId);
        command.Parameters.AddWithValue("ownerId", ownerId);
        command.Parameters.AddWithValue("creatorId", creatorId);

        await command.ExecuteNonQueryAsync();
        return receiptId;
    }

    private Task<HttpResponseMessage> PostAsync(Guid operatorId, string path, object body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(DevelopmentPrincipalResolver.OperatorHeader, operatorId.ToString());
        request.Headers.Add("Authorization", operatorId.ToString());

        return _client.SendAsync(request);
    }

    private async Task<Seeded> SeedAsync()
    {
        Seeded data = new(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, right(@warehouseId::text, 12), 'T', 'Asia/Tokyo', now(), now()),
                   (@otherWarehouseId, right(@otherWarehouseId::text, 12), 'T2', 'Asia/Tokyo', now(), now());

            INSERT INTO owner (id, code, name, created_at, updated_at)
            VALUES (@ownerId, right(@ownerId::text, 12), 'O', now(), now());

            INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                  valid_from, created_at, updated_at)
            VALUES (@receiverId, 'operator', 'Receiver', 'active', gen_random_uuid(),
                    current_date, now(), now()),
                   (@auditorId, 'staff', 'Auditor', 'active', gen_random_uuid(),
                    current_date, now(), now());

            INSERT INTO user_role_scope (id, user_id, role_id, warehouse_id,
                                         granted_by, granted_at)
            VALUES (@receiverScope, @receiverId,
                    '10000000-0000-0000-0000-000000000005', @warehouseId, @receiverId, now()),
                   (@auditorScope, @auditorId,
                    '10000000-0000-0000-0000-000000000007', @warehouseId, @auditorId, now());
            """);

        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("otherWarehouseId", data.OtherWarehouseId);
        command.Parameters.AddWithValue("ownerId", data.OwnerId);
        command.Parameters.AddWithValue("receiverId", data.ReceiverId);
        command.Parameters.AddWithValue("auditorId", data.AuditorId);
        command.Parameters.AddWithValue("receiverScope", data.ScopeSeed);
        command.Parameters.AddWithValue("auditorScope", Guid.CreateVersion7());

        await command.ExecuteNonQueryAsync();
        return data;
    }

    private sealed record Seeded(
        Guid WarehouseId,
        Guid OwnerId,
        Guid ReceiverId,
        Guid AuditorId,
        Guid ScopeSeed,
        Guid OtherWarehouseId);
}
