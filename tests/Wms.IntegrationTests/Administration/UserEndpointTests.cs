using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wms.IntegrationTests.Infrastructure;
using Wms.Modules.Identity.Infrastructure;
using Xunit;

namespace Wms.IntegrationTests.Administration;

/// <summary>
/// <c>POST /api/v1/users</c> over real HTTP — the endpoint-level permission
/// gate, as distinct from <c>UserDirectoryTests</c>' service-level checks.
/// </summary>
/// <remarks>
/// <c>user.manage</c> alone opens an account. Assigning it a role, or a
/// credential to log in with, are separate acts and require the same
/// permission their own dedicated endpoints do — otherwise <c>user.manage</c>
/// is an identity-minting capability: create a user, hand it a role, set its
/// password, all under one permission (invariant 9).
/// </remarks>
public sealed class UserEndpointTests(PostgresFixture fixture)
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
    public async Task UserManageAlone_CanCreateAPlainUserWithNoRolesOrCredentials()
    {
        Seeded data = await SeedAsync(["user.manage"]);

        HttpResponseMessage response = await PostAsync(data.ActorId, new
        {
            userType = "staff",
            displayName = "Plain User",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// Without <c>role.manage</c>, a <c>user.manage</c> holder could otherwise
    /// hand out any role they themselves hold — the same escalation H13 closes
    /// on the dedicated grant endpoint, reachable here by a shorter route.
    /// </summary>
    [Fact]
    public async Task UserManageAlone_CannotAssignARoleScopeAtCreation()
    {
        Seeded data = await SeedAsync(["user.manage"]);

        HttpResponseMessage response = await PostAsync(data.ActorId, new
        {
            userType = "operator",
            displayName = "Would-be picker",
            roleScopes = new[]
            {
                new { roleCode = "RECEIVER", warehouseId = data.WarehouseId, zoneIds = Array.Empty<Guid>() },
            },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("role.manage", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Without <c>credential.manage</c>, a <c>user.manage</c> holder could
    /// mint a working, loginable identity unilaterally.
    /// </summary>
    [Fact]
    public async Task UserManageAlone_CannotSetACredentialAtCreation()
    {
        Seeded data = await SeedAsync(["user.manage"]);

        HttpResponseMessage response = await PostAsync(data.ActorId, new
        {
            userType = "staff",
            displayName = "Would-be admin",
            credentials = new[] { new { type = "password", secret = "correct horse battery staple" } },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("credential.manage", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoldingCredentialManage_CanSetACredentialAtCreation()
    {
        Seeded data = await SeedAsync(["user.manage", "credential.manage"]);

        HttpResponseMessage response = await PostAsync(data.ActorId, new
        {
            userType = "operator",
            displayName = "Given a PIN",
            credentials = new[] { new { type = "pin", secret = "4821" } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// role.manage clears the endpoint gate; the deeper H13 subset check
    /// (covered by <c>UserDirectoryTests</c>) is why the actor also needs to
    /// hold everything RECEIVER carries, exactly as the dedicated grant
    /// endpoint would require.
    /// </summary>
    [Fact]
    public async Task HoldingRoleManageAndTheRolesOwnPermissions_CanAssignItAtCreation()
    {
        Seeded data = await SeedAsync([
            "user.manage", "role.manage",
            "receipt.create", "receipt.read", "receipt.confirm", "receipt.complete",
            "task.lease", "putaway.execute", "inventory.read",
            "handlingunit.read", "handlingunit.write", "device.claim",
        ]);

        HttpResponseMessage response = await PostAsync(data.ActorId, new
        {
            userType = "operator",
            displayName = "Given a role",
            roleScopes = new[]
            {
                new { roleCode = "RECEIVER", warehouseId = data.WarehouseId, zoneIds = Array.Empty<Guid>() },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private Task<HttpResponseMessage> PostAsync(Guid operatorId, object body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/users")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(DevelopmentPrincipalResolver.OperatorHeader, operatorId.ToString());
        request.Headers.Add("Authorization", operatorId.ToString());

        return _client.SendAsync(request);
    }

    /// <summary>
    /// A custom role holding exactly the permissions named — none of the
    /// seeded system roles hold <c>user.manage</c> without also holding
    /// <c>role.manage</c>, so the boundary under test needs a purpose-built one.
    /// </summary>
    private async Task<Seeded> SeedAsync(IReadOnlyList<string> permissionCodes)
    {
        Guid warehouseId = Guid.CreateVersion7();
        Guid actorId = Guid.CreateVersion7();
        Guid roleId = Guid.CreateVersion7();

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, right(@warehouseId::text, 12), 'T', 'Asia/Tokyo', now(), now());

            INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                  valid_from, created_at, updated_at)
            VALUES (@actorId, 'staff', 'Actor', 'active', gen_random_uuid(),
                    current_date, now(), now());

            INSERT INTO role (id, code, name_i18n, is_system, is_active, created_at, updated_at)
            VALUES (@roleId, @roleCode, '{"en":"Test role"}', false, true, now(), now());

            INSERT INTO role_permission (role_id, permission_code)
            SELECT @roleId, code FROM unnest(@permissionCodes) AS code;

            INSERT INTO user_role_scope (id, user_id, role_id, warehouse_id, granted_by, granted_at)
            VALUES (gen_random_uuid(), @actorId, @roleId, @warehouseId, @actorId, now());
            """);

        command.Parameters.AddWithValue("warehouseId", warehouseId);
        command.Parameters.AddWithValue("actorId", actorId);
        command.Parameters.AddWithValue("roleId", roleId);
        command.Parameters.AddWithValue("roleCode", $"TEST_ROLE_{roleId:N}");
        command.Parameters.AddWithValue("permissionCodes", permissionCodes.ToArray());

        await command.ExecuteNonQueryAsync();

        return new Seeded(warehouseId, actorId);
    }

    private sealed record Seeded(Guid WarehouseId, Guid ActorId);
}
