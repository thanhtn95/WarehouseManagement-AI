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

namespace Wms.IntegrationTests.Sync;

/// <summary>
/// <c>POST /api/v1/sync/facts</c> end to end, over real HTTP against the real
/// application, with a real PostgreSQL behind it.
/// </summary>
/// <remarks>
/// The batch semantics are the point here, and they are what no handler-level
/// test can reach: that one unusable fact is reported beside its accepted
/// neighbours instead of failing the request, that the actor comes from the
/// credential rather than the payload, and that a device sees <c>202</c> for
/// a divergence it cannot undo.
/// </remarks>
public sealed class SyncFactsEndpointTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Endpoint = "/api/v1/sync/facts";

    /// <summary>
    /// Set explicitly on every host built here rather than relying on
    /// <c>appsettings.Development.json</c> being on the content root: the API
    /// refuses to start without one (it must never invalidate every token on
    /// an unattended restart), so a test that depended on the file would hide
    /// that requirement instead of exercising it.
    /// </summary>
    private const string TestSigningKey =
        "test-only-signing-key-for-integration-tests-not-for-production";

    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            // Development is what registers the header-driven principal
            // resolver. That gate is asserted separately by
            // OutsideDevelopment_TheDevelopmentHeaderAuthenticatesNobody,
            // which builds its own host in Production.
            host.UseEnvironment(Environments.Development);
            host.UseSetting("ConnectionStrings:Wms", fixture.ConnectionString);
            host.UseSetting("Wms:Auth:SigningKey", TestSigningKey);

            // Set explicitly rather than relying on appsettings.Development
            // being on the content root: the development principal needs two
            // independent conditions, and a test that silently depended on
            // one of them coming from a file would hide that.
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
    /// Phase 1A exit criterion 5, now over the wire: the status code a device
    /// actually receives for an over-receipt is <c>202</c>.
    /// </summary>
    [Fact]
    public async Task OverReceipt_Returns202WithAnException_NotAnError()
    {
        Seeded data = await SeedAsync(expectedQuantity: 100m);

        HttpResponseMessage response = await PostAsync(data.ReceiverId, new
        {
            facts = new[] { Fact(data, quantity: 106m) },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        JsonElement result = (await ResultsOf(response))[0];
        Assert.Equal("accepted", result.GetProperty("status").GetString());
        Assert.Single(result.GetProperty("movementIds").EnumerateArray());
        Assert.Single(result.GetProperty("exceptionIds").EnumerateArray());

        Assert.Equal(106m, await ScalarAsync<decimal>(
            "SELECT on_hand FROM stock_balance WHERE location_id = @p;", data.LocationId));
    }

    /// <summary>
    /// The property the whole envelope exists for: one unusable fact must not
    /// cost the nineteen good ones travelling with it.
    /// </summary>
    [Fact]
    public async Task OneBadFactInABatch_DoesNotFailTheOthers()
    {
        Seeded first = await SeedAsync(expectedQuantity: 10m);
        Seeded second = await SeedAsync(expectedQuantity: 20m);

        HttpResponseMessage response = await PostAsync(first.ReceiverId, new
        {
            facts = new object[]
            {
                Fact(first, quantity: 10m),
                new
                {
                    clientFactId = Guid.CreateVersion7().ToString(),
                    type = "receipt_confirmed",
                    payload = new
                    {
                        receiptLineId = Guid.CreateVersion7(),  // never existed
                        quantity = 5m,
                        uom = "EACH",
                        toLocationId = first.LocationId,
                    },
                },
                new
                {
                    clientFactId = Guid.CreateVersion7().ToString(),
                    type = "not_a_real_fact_type",
                    payload = new { },
                },
                Fact(second, quantity: 20m, actorLocation: second.LocationId),
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        JsonElement[] results = await ResultsOf(response);
        Assert.Equal(4, results.Length);
        Assert.Equal("accepted", results[0].GetProperty("status").GetString());
        Assert.Equal("rejected", results[1].GetProperty("status").GetString());
        Assert.Equal("rejected", results[2].GetProperty("status").GetString());
        Assert.Equal("accepted", results[3].GetProperty("status").GetString());

        // The good facts really landed — asserted against the ledger, not
        // against the response that claimed they did.
        Assert.Equal(10m, await ScalarAsync<decimal>(
            "SELECT on_hand FROM stock_balance WHERE location_id = @p;", first.LocationId));
        Assert.Equal(20m, await ScalarAsync<decimal>(
            "SELECT on_hand FROM stock_balance WHERE location_id = @p;", second.LocationId));
    }

    /// <summary>
    /// Phase 1A exit criterion 3: the handheld resubmits the identical batch
    /// after a reboot, and the stock moves once.
    /// </summary>
    [Fact]
    public async Task ResubmittingTheIdenticalBatch_MovesStockOnce()
    {
        Seeded data = await SeedAsync(expectedQuantity: 10m);
        object batch = new { facts = new[] { Fact(data, quantity: 10m) } };

        JsonElement[] firstRun = await ResultsOf(await PostAsync(data.ReceiverId, batch));
        JsonElement[] secondRun = await ResultsOf(await PostAsync(data.ReceiverId, batch));

        Assert.Equal("accepted", firstRun[0].GetProperty("status").GetString());
        Assert.Equal("duplicate", secondRun[0].GetProperty("status").GetString());

        // A duplicate returns the ORIGINAL movement id, so the device can
        // reconcile its queue against what actually landed.
        Assert.Equal(
            firstRun[0].GetProperty("movementIds")[0].GetString(),
            secondRun[0].GetProperty("movementIds")[0].GetString());

        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM stock_movement WHERE reference_id = @p;", data.ReceiptLineId));
    }

    /// <summary>
    /// Invariant 8: the actor is taken from the credential. A payload cannot
    /// name who performed a movement.
    /// </summary>
    [Fact]
    public async Task TheMovementIsAttributedToTheAuthenticatedOperator_NotThePayload()
    {
        Seeded data = await SeedAsync(expectedQuantity: 5m);

        await PostAsync(data.ReceiverId, new
        {
            facts = new[] { Fact(data, quantity: 5m) },
        });

        Assert.Equal(data.ReceiverId, await ScalarAsync<Guid>(
            "SELECT actor_user_id FROM stock_movement WHERE reference_id = @p;",
            data.ReceiptLineId));
    }

    [Fact]
    public async Task WithoutACredential_ItIs401_AndNothingIsWritten()
    {
        Seeded data = await SeedAsync(expectedQuantity: 5m);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            Endpoint,
            new { facts = new[] { Fact(data, quantity: 5m) } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM stock_movement WHERE reference_id = @p;", data.ReceiptLineId));
    }

    /// <summary>
    /// Phase 1A exit criterion 6, server side: authorization is real even
    /// though authentication is currently a development stand-in. An Auditor
    /// holds no mutating permission and cannot confirm a receipt.
    /// </summary>
    [Fact]
    public async Task AnOperatorLackingTheFactsPermission_IsRejectedPerFact()
    {
        Seeded data = await SeedAsync(expectedQuantity: 5m);

        JsonElement[] results = await ResultsOf(await PostAsync(data.AuditorId, new
        {
            facts = new[] { Fact(data, quantity: 5m) },
        }));

        Assert.Equal("rejected", results[0].GetProperty("status").GetString());
        Assert.Contains(
            "insufficient-permission",
            results[0].GetProperty("error").GetProperty("type").GetString());

        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM stock_movement WHERE reference_id = @p;", data.ReceiptLineId));
    }

    /// <summary>
    /// A destination that does not exist must be one rejected entry, not a
    /// `500` for the batch.
    /// </summary>
    /// <remarks>
    /// Without a check in the application, this reaches the
    /// <c>stock_balance.location_id</c> foreign key and the resulting
    /// <c>PostgresException</c> escapes the per-fact boundary — taking down
    /// the whole batch and reaching a handheld as a `5xx` it cannot act on.
    /// </remarks>
    [Fact]
    public async Task UnknownDestination_IsRejectedPerFact_NotA500()
    {
        Seeded data = await SeedAsync(expectedQuantity: 10m);

        HttpResponseMessage response = await PostAsync(data.ReceiverId, new
        {
            facts = new object[]
            {
                Fact(data, quantity: 10m, actorLocation: Guid.CreateVersion7()),
                Fact(data, quantity: 10m),
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        JsonElement[] results = await ResultsOf(response);
        Assert.Equal("rejected", results[0].GetProperty("status").GetString());
        Assert.Equal("accepted", results[1].GetProperty("status").GetString());
    }

    /// <summary>
    /// A real bin, but in another warehouse. Accepting it puts stock
    /// physically in site B while the movement's business date comes from
    /// site A's day boundary and the discrepancy is raised into site A's
    /// exception queue — so B's supervisor never sees a problem sitting in
    /// B's rack. A mis-scanned label carrying a valid id from another site is
    /// enough to cause it; no malice required.
    /// </summary>
    [Fact]
    public async Task DestinationInAnotherWarehouse_IsRejected()
    {
        Seeded data = await SeedAsync(expectedQuantity: 10m);

        JsonElement[] results = await ResultsOf(await PostAsync(data.ReceiverId, new
        {
            facts = new[] { Fact(data, quantity: 10m, actorLocation: data.ForeignLocationId) },
        }));

        Assert.Equal("rejected", results[0].GetProperty("status").GetString());

        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM stock_balance WHERE location_id = @p;", data.ForeignLocationId));
    }

    /// <summary>
    /// A missing <c>payload</c> binds as an Undefined JsonElement, which
    /// throws <c>InvalidOperationException</c> rather than <c>JsonException</c>
    /// on deserialize — a different exception type than the obvious catch
    /// expects, and so the same batch-wide blast radius.
    /// </summary>
    [Fact]
    public async Task FactWithNoPayloadAtAll_IsRejectedPerFact_NotA500()
    {
        Seeded data = await SeedAsync(expectedQuantity: 10m);

        HttpResponseMessage response = await PostAsync(data.ReceiverId, new
        {
            facts = new[]
            {
                new { clientFactId = Guid.CreateVersion7().ToString(), type = "receipt_confirmed" },
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("rejected", (await ResultsOf(response))[0].GetProperty("status").GetString());
    }

    /// <summary>
    /// The single control keeping header-trust out of a customer deployment.
    /// </summary>
    /// <remarks>
    /// The other `401` test runs in Development and exercises the development
    /// resolver returning null — it says nothing about this. Without this
    /// test, moving the registration out of its environment check would break
    /// nothing and ship.
    /// </remarks>
    [Fact]
    public async Task OutsideDevelopment_TheDevelopmentHeaderAuthenticatesNobody()
    {
        Seeded data = await SeedAsync(expectedQuantity: 10m);

        using WebApplicationFactory<Program> production =
            new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            {
                host.UseEnvironment(Environments.Production);
                host.UseSetting("ConnectionStrings:Wms", fixture.ConnectionString);
                host.UseSetting("Wms:Auth:SigningKey", TestSigningKey);

                // Deliberately set: proves the environment check holds even
                // when the configuration flag says yes, so neither condition
                // alone is sufficient.
                host.UseSetting("Wms:Auth:AllowDevelopmentHeaderPrincipal", "true");
            });

        using HttpClient client = production.CreateClient();
        HttpRequestMessage request = new(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new { facts = new[] { Fact(data, quantity: 10m) } }),
        };

        // Credentials that work in Development, presented in Production.
        request.Headers.Add(
            DevelopmentPrincipalResolver.OperatorHeader, data.ReceiverId.ToString());
        request.Headers.Add("Authorization", data.ReceiverId.ToString());

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM stock_movement WHERE reference_id = @p;", data.ReceiptLineId));
    }

    /// <summary>
    /// Agency labour whose <c>valid_until</c> has passed. Access that was
    /// meant to end on a date must end on that date, not whenever someone
    /// remembers to flip <c>status</c> by hand.
    /// </summary>
    [Fact]
    public async Task AnExpiredUser_DoesNotAuthenticate()
    {
        Seeded data = await SeedAsync(expectedQuantity: 10m);
        await ExecuteAsync(
            "UPDATE app_user SET valid_until = current_date - 1 WHERE id = @p;",
            data.ReceiverId);

        HttpResponseMessage response = await PostAsync(data.ReceiverId, new
        {
            facts = new[] { Fact(data, quantity: 10m) },
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Proves <c>putaway_confirmed</c> is actually registered and
    /// permission-mapped.
    /// </summary>
    /// <remarks>
    /// The distinction under test is subtle and the reason this exists: an
    /// unregistered fact type is rejected as "Unknown fact type", which looks
    /// identical to a rejection at a glance. Asserting on the *reason*
    /// catches a handler that was written but never wired into the registry —
    /// which no handler-level test can see.
    /// </remarks>
    [Fact]
    public async Task PutawayConfirmed_IsARegisteredFactType_NotAnUnknownOne()
    {
        Seeded data = await SeedAsync(expectedQuantity: 10m);

        JsonElement[] results = await ResultsOf(await PostAsync(data.ReceiverId, new
        {
            facts = new[]
            {
                new
                {
                    clientFactId = Guid.CreateVersion7().ToString(),
                    type = "putaway_confirmed",
                    payload = new
                    {
                        taskLineId = Guid.CreateVersion7(),   // no such line
                        fromLocationId = data.LocationId,
                        toLocationId = data.LocationId,
                        quantity = 10m,
                        uom = "EACH",
                    },
                },
            },
        }));

        Assert.Equal("rejected", results[0].GetProperty("status").GetString());

        // It reached the putaway handler — not the "unknown fact type" arm,
        // and not a permission refusal.
        Assert.Equal(
            "Unknown task line.",
            results[0].GetProperty("error").GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AnEmptyBatch_IsTheOneThingRefusedOutright()
    {
        HttpResponseMessage response = await PostAsync(
            Guid.CreateVersion7(), new { facts = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static object Fact(Seeded data, decimal quantity, Guid? actorLocation = null) => new
    {
        clientFactId = Guid.CreateVersion7().ToString(),
        type = "receipt_confirmed",
        occurredAtDevice = DateTimeOffset.UtcNow,
        payload = new
        {
            receiptLineId = data.ReceiptLineId,
            quantity,
            uom = "EACH",
            toLocationId = actorLocation ?? data.LocationId,
        },
    };

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

    private static async Task<JsonElement[]> ResultsOf(HttpResponseMessage response)
    {
        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. body.RootElement.GetProperty("results").EnumerateArray()];
    }

    private async Task ExecuteAsync(string sql, object parameter)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("p", parameter);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, object parameter)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("p", parameter);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// A receipt line, plus two operators with genuinely different
    /// permissions drawn from the seeded role matrix.
    /// </summary>
    private async Task<Seeded> SeedAsync(decimal expectedQuantity)
    {
        Seeded data = new(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7());

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, right(@warehouseId::text, 12), 'T', 'Asia/Tokyo', now(), now());

            INSERT INTO zone (id, warehouse_id, code, name_i18n, zone_type, created_at, updated_at)
            VALUES (@zoneId, @warehouseId, 'A', '{"en":"A"}', 'receiving', now(), now());

            INSERT INTO location (id, warehouse_id, zone_id, code, location_type,
                                  pick_sequence, created_at, updated_at)
            VALUES (@locationId, @warehouseId, @zoneId, right(@locationId::text, 12),
                    'bin', 1, now(), now());

            -- A second, entirely separate site, so "a real bin, but not this
            -- warehouse's bin" is expressible.
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@otherWarehouseId, right(@otherWarehouseId::text, 12), 'T2',
                    'Europe/Berlin', now(), now());

            INSERT INTO zone (id, warehouse_id, code, name_i18n, zone_type, created_at, updated_at)
            VALUES (@otherZoneId, @otherWarehouseId, 'A', '{"en":"A"}', 'receiving', now(), now());

            INSERT INTO location (id, warehouse_id, zone_id, code, location_type,
                                  pick_sequence, created_at, updated_at)
            VALUES (@foreignLocationId, @otherWarehouseId, @otherZoneId,
                    right(@foreignLocationId::text, 12), 'bin', 1, now(), now());

            INSERT INTO owner (id, code, name, created_at, updated_at)
            VALUES (@ownerId, right(@ownerId::text, 12), 'O', now(), now());

            INSERT INTO item (id, sku_code, name_i18n, base_uom, created_at, updated_at)
            VALUES (@itemId, right(@itemId::text, 12), '{"en":"I"}', 'EACH', now(), now());

            INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                  valid_from, created_at, updated_at)
            VALUES (@receiverId, 'operator', 'Receiver', 'active', gen_random_uuid(),
                    current_date, now(), now()),
                   (@auditorId, 'staff', 'Auditor', 'active', gen_random_uuid(),
                    current_date, now(), now());

            -- Real seeded roles, so the permission check under test is the
            -- production one: Receiver holds receipt.confirm, Auditor holds
            -- no mutating permission at all.
            INSERT INTO user_role_scope (id, user_id, role_id, warehouse_id,
                                         granted_by, granted_at)
            VALUES (@receiverScopeId, @receiverId,
                    '10000000-0000-0000-0000-000000000005', @warehouseId,
                    @receiverId, now()),
                   (@auditorScopeId, @auditorId,
                    '10000000-0000-0000-0000-000000000007', @warehouseId,
                    @auditorId, now());

            INSERT INTO receipt (id, warehouse_id, receipt_number, receipt_type, owner_id,
                                 status, created_by, created_at, updated_at)
            VALUES (@receiptId, @warehouseId, right(@receiptId::text, 12), 'against_asn',
                    @ownerId, 'in_progress', @receiverId, now(), now());

            INSERT INTO receipt_line (id, receipt_id, line_no, item_id, owner_id,
                                      expected_quantity, uom_code)
            VALUES (@receiptLineId, @receiptId, 1, @itemId, @ownerId,
                    @expectedQuantity, 'EACH');
            """);

        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("zoneId", data.ZoneId);
        command.Parameters.AddWithValue("locationId", data.LocationId);
        command.Parameters.AddWithValue("ownerId", data.OwnerId);
        command.Parameters.AddWithValue("itemId", data.ItemId);
        command.Parameters.AddWithValue("receiverId", data.ReceiverId);
        command.Parameters.AddWithValue("auditorId", data.AuditorId);
        command.Parameters.AddWithValue("receiverScopeId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("auditorScopeId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("receiptId", data.ReceiptId);
        command.Parameters.AddWithValue("receiptLineId", data.ReceiptLineId);
        command.Parameters.AddWithValue("otherWarehouseId", data.OtherWarehouseId);
        command.Parameters.AddWithValue("otherZoneId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("foreignLocationId", data.ForeignLocationId);
        command.Parameters.AddWithValue("expectedQuantity", expectedQuantity);

        await command.ExecuteNonQueryAsync();
        return data;
    }

    private sealed record Seeded(
        Guid WarehouseId,
        Guid ZoneId,
        Guid LocationId,
        Guid OwnerId,
        Guid ItemId,
        Guid ReceiverId,
        Guid AuditorId,
        Guid ReceiptId,
        Guid ReceiptLineId,
        Guid OtherWarehouseId,
        Guid ForeignLocationId);
}
