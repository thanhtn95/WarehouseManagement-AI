using Npgsql;
using Wms.IntegrationTests.Infrastructure;
using Wms.Modules.Inventory.Application;
using Wms.Modules.Inventory.Contracts;
using Wms.Modules.Inventory.Infrastructure;
using Xunit;

namespace Wms.IntegrationTests.Inventory;

/// <summary>
/// Phase 1A exit criterion 2: "Reconciliation recomputes balances from the
/// ledger and matches to zero variance."
/// </summary>
/// <remarks>
/// This is the system's own correctness monitor (H1). Everything else in the
/// design is an argument that the ledger and the balances agree; this is the
/// only thing that actually checks, so a test proving it detects nothing
/// would be the most expensive false comfort in the project.
/// </remarks>
public sealed class ReconciliationServiceTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>
{
    private readonly ReconciliationService _reconciliation = new();
    private readonly StockLedger _ledger = new(new BusinessCalendar());

    [Fact]
    public async Task AfterRealMovements_ReconciliationFindsZeroVariance()
    {
        Seeded data = await SeedAsync();

        await PostAsync(data, to: data.BinA, quantity: 100m, type: "receipt");
        await PostAsync(data, from: data.BinA, to: data.BinB, quantity: 30m, type: "putaway");
        await PostAsync(data, from: data.BinB, to: data.BinA, quantity: 10m, type: "move");

        ReconciliationResult result = await RunAsync(data, backdate: true);

        Assert.NotNull(result.RunId);
        Assert.Empty(result.Variances);
        Assert.True(result.RowsChecked >= 2);

        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT variance_count FROM balance_reconciliation_run WHERE id = @p;",
            result.RunId!.Value));
        Assert.Equal("completed", await ScalarAsync<string>(
            "SELECT status FROM balance_reconciliation_run WHERE id = @p;",
            result.RunId!.Value));
    }

    /// <summary>
    /// The case the job exists for: a balance that disagrees with the ledger.
    /// </summary>
    /// <remarks>
    /// Simulated by corrupting <c>stock_balance</c> directly — which is the
    /// only way to produce the state, because every application path writes
    /// both sides in one transaction. That is also why this check matters: if
    /// a future code path ever writes one without the other, this is what
    /// notices.
    /// </remarks>
    [Fact]
    public async Task ADivergentBalance_IsReportedAsAVariance_AndNotCorrected()
    {
        Seeded data = await SeedAsync();
        await PostAsync(data, to: data.BinA, quantity: 100m, type: "receipt");

        await ExecuteAsync(
            "UPDATE stock_balance SET on_hand = 88 WHERE location_id = @p;", data.BinA);

        ReconciliationResult result = await RunAsync(data, backdate: true);

        BalanceVariance variance = Assert.Single(result.Variances);
        Assert.Equal(100m, variance.LedgerQuantity);
        Assert.Equal(88m, variance.BalanceQuantity);
        Assert.Equal(12m, variance.Difference);

        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM balance_variance WHERE run_id = @p;", result.RunId!.Value));

        // It does NOT auto-correct. Silent correction would destroy the only
        // evidence of whatever caused the divergence (H1).
        Assert.Equal(88m, await ScalarAsync<decimal>(
            "SELECT on_hand FROM stock_balance WHERE location_id = @p;", data.BinA));
    }

    /// <summary>
    /// ADR 0002: the watermark is bounded by settling time, never by
    /// <c>max(sequence)</c>.
    /// </summary>
    /// <remarks>
    /// A movement that has not yet aged past the visibility lag must be left
    /// for the next run rather than swept past. If the watermark advanced over
    /// it, no later scan would ever ask for it again — the run would report
    /// zero variance across rows it had silently skipped, which is worse than
    /// not running at all.
    /// </remarks>
    [Fact]
    public async Task AMovementInsideTheVisibilityLag_IsLeftForTheNextRun()
    {
        Seeded data = await SeedAsync();

        await PostAsync(data, to: data.BinA, quantity: 50m, type: "receipt");
        await BackdateAsync(data);

        // A second, very recent movement — still inside the lag.
        await PostAsync(data, to: data.BinB, quantity: 7m, type: "receipt");

        ReconciliationResult first = await RunAsync(data, backdate: false);

        // The settled movement was checked; the fresh one was not swept past.
        Assert.NotNull(first.RunId);
        long sequenceOfRecent = await ScalarAsync<long>(
            "SELECT max(sequence) FROM stock_movement WHERE to_location_id = @p;", data.BinB);
        Assert.True(first.ToSequence < sequenceOfRecent);

        // Once it ages, the next run picks it up and starts where the first
        // stopped — nothing is skipped and nothing is double-counted.
        await BackdateAsync(data);
        ReconciliationResult second = await RunAsync(data, backdate: false);

        Assert.NotNull(second.RunId);
        Assert.Equal(first.ToSequence, second.FromSequence);
        Assert.True(second.ToSequence >= sequenceOfRecent);
        Assert.Empty(second.Variances);
    }

    /// <summary>
    /// A position touched again in a later window must be recomputed from its
    /// <strong>whole</strong> history, not from the window's movements alone.
    /// </summary>
    /// <remarks>
    /// This is the trap at the centre of incremental reconciliation, and it is
    /// invisible to any single-run test: the <em>selection</em> of what to
    /// check is incremental, but the <em>recompute</em> must not be. Summing
    /// only the new movements and comparing that to a running total reports a
    /// variance on every row that had prior history — so the job would cry
    /// wolf constantly and be switched off, taking the system's only
    /// correctness monitor with it.
    ///
    /// Written after a mutation test showed the original suite could not tell
    /// the two apart: every earlier test had all its movements inside the
    /// first window, where a windowed recompute and a full one give the same
    /// answer.
    /// </remarks>
    [Fact]
    public async Task APositionTouchedInALaterWindow_IsRecomputedFromItsWholeHistory()
    {
        Seeded data = await SeedAsync();

        await PostAsync(data, to: data.BinA, quantity: 100m, type: "receipt");
        ReconciliationResult first = await RunAsync(data, backdate: true);
        Assert.NotNull(first.RunId);
        Assert.Empty(first.Variances);

        // The SAME position moves again, in a later window.
        await PostAsync(data, to: data.BinA, quantity: 50m, type: "receipt");
        ReconciliationResult second = await RunAsync(data, backdate: true);

        Assert.NotNull(second.RunId);
        Assert.True(second.FromSequence >= first.ToSequence);

        // Balance is 150 and the ledger totals 150. A recompute windowed to
        // this run alone would total 50, report a difference of -100, and be
        // wrong.
        Assert.Empty(second.Variances);
        Assert.Equal(150m, await ScalarAsync<decimal>(
            "SELECT on_hand FROM stock_balance WHERE location_id = @p;", data.BinA));
    }

    [Fact]
    public async Task WithNothingSettled_NoRunIsRecorded()
    {
        Seeded data = await SeedAsync();
        await PostAsync(data, to: data.BinA, quantity: 5m, type: "receipt");

        // Deliberately not backdated: everything is inside the lag.
        ReconciliationResult result = await RunAsync(data, backdate: false);

        Assert.Null(result.RunId);
        Assert.Empty(result.Variances);
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM balance_reconciliation_run WHERE warehouse_id = @p;",
            data.WarehouseId));
    }

    /// <summary>
    /// A second run over already-reconciled history must not re-report the
    /// same rows, or the variance count becomes meaningless.
    /// </summary>
    [Fact]
    public async Task RunningTwice_DoesNotRecheckSettledHistory()
    {
        Seeded data = await SeedAsync();
        await PostAsync(data, to: data.BinA, quantity: 100m, type: "receipt");
        await BackdateAsync(data);

        ReconciliationResult first = await RunAsync(data, backdate: false);
        ReconciliationResult second = await RunAsync(data, backdate: false);

        Assert.NotNull(first.RunId);
        Assert.True(first.RowsChecked > 0);

        // Nothing new settled, so there is nothing to do.
        Assert.Null(second.RunId);
        Assert.Equal(first.ToSequence, second.FromSequence);
    }

    private async Task<ReconciliationResult> RunAsync(Seeded data, bool backdate)
    {
        if (backdate)
        {
            await BackdateAsync(data);
        }

        await using NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync();
        return await _reconciliation.RunAsync(connection, data.WarehouseId, CancellationToken.None);
    }

    /// <summary>
    /// Ages this warehouse's movements past the visibility lag.
    /// </summary>
    /// <remarks>
    /// The alternative — waiting five real minutes — is not a test. Only this
    /// warehouse's rows are touched, so the class can share a container.
    /// </remarks>
    private async Task BackdateAsync(Seeded data)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            UPDATE stock_movement sm
               SET recorded_at = now() - interval '1 hour'
              FROM location l
             WHERE l.id = COALESCE(sm.to_location_id, sm.from_location_id)
               AND l.warehouse_id = @warehouseId;
            """);
        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task PostAsync(
        Seeded data, decimal quantity, string type, Guid? from = null, Guid? to = null)
    {
        await using NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        await _ledger.PostAsync(
            connection,
            transaction,
            new PostMovementCommand(
                WarehouseId: data.WarehouseId,
                OwnerId: data.OwnerId,
                ItemId: data.ItemId,
                LotId: Guid.Empty,
                StockStatus: "available",
                FromLocationId: from,
                ToLocationId: to,
                QuantityBase: quantity,
                EnteredQuantity: quantity,
                EnteredUom: "EACH",
                MovementType: type,
                ReasonCode: null,
                ReferenceType: null,
                ReferenceId: null,
                ActorUserId: data.UserId,
                DeviceId: null,
                IdempotencyKey: Guid.CreateVersion7().ToString()),
            CancellationToken.None);

        await transaction.CommitAsync();
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

    private async Task<Seeded> SeedAsync()
    {
        Seeded data = new(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, right(@warehouseId::text, 12), 'T', 'Asia/Tokyo', now(), now());

            INSERT INTO zone (id, warehouse_id, code, name_i18n, zone_type, created_at, updated_at)
            VALUES (@zoneId, @warehouseId, 'A', '{"en":"A"}', 'bulk', now(), now());

            INSERT INTO location (id, warehouse_id, zone_id, code, location_type,
                                  pick_sequence, created_at, updated_at)
            VALUES (@binA, @warehouseId, @zoneId, right(@binA::text, 12), 'bin', 1, now(), now()),
                   (@binB, @warehouseId, @zoneId, right(@binB::text, 12), 'bin', 2, now(), now());

            INSERT INTO owner (id, code, name, created_at, updated_at)
            VALUES (@ownerId, right(@ownerId::text, 12), 'O', now(), now());

            INSERT INTO item (id, sku_code, name_i18n, base_uom, created_at, updated_at)
            VALUES (@itemId, right(@itemId::text, 12), '{"en":"I"}', 'EACH', now(), now());

            INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                  valid_from, created_at, updated_at)
            VALUES (@userId, 'operator', 'Op', 'active', gen_random_uuid(),
                    current_date, now(), now());
            """);

        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("zoneId", data.ZoneId);
        command.Parameters.AddWithValue("binA", data.BinA);
        command.Parameters.AddWithValue("binB", data.BinB);
        command.Parameters.AddWithValue("ownerId", data.OwnerId);
        command.Parameters.AddWithValue("itemId", data.ItemId);
        command.Parameters.AddWithValue("userId", data.UserId);

        await command.ExecuteNonQueryAsync();
        return data;
    }

    private sealed record Seeded(
        Guid WarehouseId,
        Guid ZoneId,
        Guid BinA,
        Guid BinB,
        Guid OwnerId,
        Guid ItemId)
    {
        public Guid UserId { get; } = Guid.CreateVersion7();
    }
}
