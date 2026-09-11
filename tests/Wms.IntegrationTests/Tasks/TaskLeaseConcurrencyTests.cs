using System.Collections.Concurrent;
using Npgsql;
using Wms.IntegrationTests.Infrastructure;
using Xunit;

namespace Wms.IntegrationTests.Tasks;

/// <summary>
/// Mandatory concurrency test 1: no duplicate lease.
///
/// N devices lease from the same zone simultaneously; the union of task ids
/// they receive must contain no repeats. Two operators sent to the same bin
/// for the same work is the failure this design's SKIP LOCKED dispatch
/// exists to prevent, and it is invisible in single-user testing.
///
/// This is deliberately written before the lease service exists. It tests
/// the dispatch query against the real schema and index, which is the part
/// that carries the correctness — an application wrapper around a wrong
/// query would pass a test that mocked the database.
/// </summary>
public sealed class TaskLeaseConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const int Operators = 8;
    private const int BatchSize = 5;
    private const int Iterations = 50;

    private static readonly Guid WarehouseId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ZoneId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    /// <summary>
    /// The dispatch query from design doc §6.8.
    ///
    /// This test pins DISJOINTNESS — no task issued twice — which comes from
    /// the row lock plus the `status = 'ready'` predicate, not from
    /// SKIP LOCKED: under READ COMMITTED a blocked FOR UPDATE re-checks that
    /// predicate and drops a row leased while it waited. Verified by
    /// mutation: removing SKIP LOCKED leaves this test green.
    ///
    /// SKIP LOCKED's own property — pollers never blocking each other — is
    /// covered by LeaseServiceTests.
    /// </summary>
    private const string LeaseSql = """
        UPDATE task
           SET status             = 'leased',
               lease_user_id      = @userId,
               lease_id           = @leaseId,
               lease_expires_at   = now() + interval '10 hours',
               lease_heartbeat_at = now()
         WHERE id IN (
             SELECT id
               FROM task
              WHERE status = 'ready'
                AND warehouse_id = @warehouseId
                AND zone_id = @zoneId
              ORDER BY priority, sort_sequence
              FOR UPDATE SKIP LOCKED
              LIMIT @batchSize
         )
        RETURNING id;
        """;

    [Fact]
    public async Task LeaseTasks_WhenOperatorsRaceForTheSameZone_NeverIssuesTheSameTaskTwice()
    {
        await SeedWarehouseAndZoneAsync();
        Guid[] operatorIds = await SeedOperatorsAsync(Operators);

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            await ResetTasksAsync(Operators * BatchSize);

            ConcurrentBag<Guid> leased = [];
            using Barrier barrier = new(Operators);

            await Task.WhenAll(operatorIds.Select(async operatorId =>
            {
                // Every worker blocks here, so they hit the database together.
                // Sequential calls would pass regardless of SKIP LOCKED and
                // prove nothing.
                await Task.Yield();
                barrier.SignalAndWait();

                foreach (Guid id in await LeaseAsync(operatorId))
                {
                    leased.Add(id);
                }
            }));

            // Assert on database state, not on what the calls returned.
            Guid[] issued = [.. leased];
            Assert.Equal(issued.Length, issued.Distinct().Count());

            int leasedInDatabase = await ScalarAsync<int>(
                "SELECT count(*)::int FROM task WHERE status = 'leased';");
            Assert.Equal(issued.Length, leasedInDatabase);

            // With exactly Operators * BatchSize ready tasks, every one should
            // have gone to exactly one operator.
            Assert.Equal(Operators * BatchSize, issued.Length);
        }
    }

    private async Task<List<Guid>> LeaseAsync(Guid operatorId)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(LeaseSql);
        command.Parameters.AddWithValue("userId", operatorId);
        command.Parameters.AddWithValue("leaseId", Guid.NewGuid());
        command.Parameters.AddWithValue("warehouseId", WarehouseId);
        command.Parameters.AddWithValue("zoneId", ZoneId);
        command.Parameters.AddWithValue("batchSize", BatchSize);

        List<Guid> ids = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private async Task SeedWarehouseAndZoneAsync()
    {
        await ExecuteAsync(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, 'TKY', 'Tokyo DC1', 'Asia/Tokyo', now(), now())
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO zone (id, warehouse_id, code, name_i18n, zone_type, created_at, updated_at)
            VALUES (@zoneId, @warehouseId, 'A', '{"en":"Zone A"}', 'pick', now(), now())
            ON CONFLICT (id) DO NOTHING;
            """,
            command =>
            {
                command.Parameters.AddWithValue("warehouseId", WarehouseId);
                command.Parameters.AddWithValue("zoneId", ZoneId);
            });
    }

    private async Task<Guid[]> SeedOperatorsAsync(int count)
    {
        Guid[] ids = [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid())];

        foreach (Guid id in ids)
        {
            await ExecuteAsync(
                """
                INSERT INTO app_user (id, user_type, display_name, status,
                                      security_stamp, valid_from, created_at, updated_at)
                VALUES (@id, 'operator', 'Operator', 'active',
                        gen_random_uuid(), current_date, now(), now());
                """,
                command => command.Parameters.AddWithValue("id", id));
        }

        return ids;
    }

    private async Task ResetTasksAsync(int readyTaskCount)
    {
        await ExecuteAsync("DELETE FROM task_line; DELETE FROM task;");
        await ExecuteAsync(
            """
            INSERT INTO task (id, warehouse_id, zone_id, task_type, status,
                              priority, sort_sequence, created_at)
            SELECT gen_random_uuid(), @warehouseId, @zoneId, 'putaway', 'ready',
                   100, g, now()
              FROM generate_series(1, @count) AS g;
            """,
            command =>
            {
                command.Parameters.AddWithValue("warehouseId", WarehouseId);
                command.Parameters.AddWithValue("zoneId", ZoneId);
                command.Parameters.AddWithValue("count", readyTaskCount);
            });
    }

    private async Task ExecuteAsync(string sql, Action<NpgsqlCommand>? configure = null)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(sql);
        configure?.Invoke(command);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(sql);
        object? result = await command.ExecuteScalarAsync();
        return (T)result!;
    }
}
