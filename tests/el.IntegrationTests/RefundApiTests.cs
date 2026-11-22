using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using el.Api.Settlements;
using el.Application.Settlements;
using el.Domain.Common;
using el.Domain.Settlements;
using el.Infrastructure;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static el.IntegrationTests.EscrowApiTests;

namespace el.IntegrationTests;

/// <summary>BE-012.2: full pre-clearing refund as an exact, linked reversal of the original journal.</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class RefundApiTests(PostgreSqlFixture database)
{
    // Same advisory key as MonetaryOperationLock.LockKey ("ELMONEY1").
    private const long MonetaryLockKey = 4993451089857960241;

    [Fact]
    public async Task A_full_refund_reverses_the_original_lines_exactly_and_marks_holds_refunded()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-REFUND", Today.AddDays(-1));
        var hold = Assert.Single(await HoldsAsync(host, settlement.Id));

        var refund = await RefundAsync(host, settlement.Id, 25000, "refund-1");

        Assert.Equal(settlement.Id, refund.SettlementId);
        Assert.Equal(settlement.JournalEntryId, refund.OriginalJournalEntryId);
        Assert.Equal(25000, refund.Amount.MinorUnits);
        Assert.Equal(hold.Id, Assert.Single(refund.RefundedHoldIds));
        Assert.Equal(settlement.JournalEntryId, await host.ScalarAsync<Guid>(
            $"SELECT reversal_of FROM ledger.journal_entries WHERE id = '{refund.JournalEntryId}'"));

        // Every original line is mirrored with the opposite side, so original + reversal nets each account to zero.
        Assert.Equal(await host.ScalarAsync<long>($"SELECT count(*) FROM ledger.journal_lines WHERE entry_id = '{settlement.JournalEntryId}'"),
            await host.ScalarAsync<long>($"SELECT count(*) FROM ledger.journal_lines WHERE entry_id = '{refund.JournalEntryId}'"));
        Assert.Equal(0L, await host.ScalarAsync<long>("""
            SELECT count(*) FROM (SELECT account_id FROM ledger.journal_lines GROUP BY account_id
              HAVING sum(CASE side WHEN 1 THEN amount_minor ELSE -amount_minor END) <> 0) unbalanced
            """));
        Assert.Equal(0m, await CreditBalanceAsync(host, EscrowCode));
        Assert.Equal(0m, await CreditBalanceAsync(host, PayableCode));

        // Holds are Refunded; the original settlement history is unchanged and still replays.
        Assert.Equal("Refunded", (await ReadHoldAsync(host, hold.Id)).State);
        using var original = await SendAsync(host, "auditor", HttpMethod.Get, $"/v1/settlements/{settlement.Id}");
        Assert.Equal(settlement.ResultHash, (await original.Content.ReadFromJsonAsync<FullSettlementResponse>(Json))!.ResultHash);
        using var replay = await SendAsync(host, "auditor", HttpMethod.Get, $"/v1/settlements/{settlement.Id}/replay");
        Assert.True((await replay.Content.ReadFromJsonAsync<SettlementReplayResponse>())!.Matches);
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation = 'settlement.refunded'"));

        // A refunded hold can no longer be released.
        using var release = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/escrow/holds/{hold.Id}/release");
        await ErrorAsync(release, HttpStatusCode.Conflict, "HOLD_NOT_RELEASABLE");
        await AssertEscrowReconcilesAsync(host);

        using var read = await SendAsync(host, "integrator", HttpMethod.Get, $"/v1/settlements/{settlement.Id}/refund");
        Assert.Equal(JsonSerializer.Serialize(refund, Json), JsonSerializer.Serialize(await read.Content.ReadFromJsonAsync<RefundResponse>(Json), Json));
    }

    [Fact]
    public async Task Same_key_retries_return_the_original_refund_and_other_attempts_are_rejected()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-RETRY", Today.AddDays(-1));
        _ = await host.TokenAsync("integrator");

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => RefundAsync(host, settlement.Id, 25000, "refund-1")));

        Assert.Single(results.Select(result => JsonSerializer.Serialize(result, Json)).Distinct());
        using var changed = await SendRefundAsync(host, settlement.Id, 24999, "refund-1");
        await ErrorAsync(changed, HttpStatusCode.Conflict, "IDEMPOTENCY_CONFLICT");
        using var newKey = await SendRefundAsync(host, settlement.Id, 25000, "refund-2");
        await ErrorAsync(newKey, HttpStatusCode.Conflict, "REFUND_ALREADY_EXISTS");
        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.settlement_refunds"));
    }

    [Fact]
    public async Task Ineligible_refunds_are_rejected_without_posting()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-INELIGIBLE", Today.AddDays(-30));

        using var partial = await SendRefundAsync(host, settlement.Id, 24999, "refund-partial");
        await ErrorAsync(partial, HttpStatusCode.BadRequest, "REFUND_AMOUNT_MISMATCH");
        using var noKey = await SendRefundAsync(host, settlement.Id, 25000, null);
        await ErrorAsync(noKey, HttpStatusCode.BadRequest, "INVALID_IDEMPOTENCY_KEY");
        using var unknown = await SendRefundAsync(host, Guid.NewGuid(), 25000, "refund-unknown");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var finance = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/settlements/{settlement.Id}/refund",
            new RefundRequest(Usd(25000)), "refund-finance");
        Assert.Equal(HttpStatusCode.Forbidden, finance.StatusCode);

        await ReleaseAsync(host, Assert.Single(await HoldsAsync(host, settlement.Id)).Id);
        using var released = await SendRefundAsync(host, settlement.Id, 25000, "refund-after-release");
        await ErrorAsync(released, HttpStatusCode.Conflict, "HOLD_ALREADY_RELEASED");

        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries"));
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.settlement_refunds"));
    }

    [Fact]
    public async Task A_settlement_without_holds_is_refunded_in_full()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: false);
        var settlement = await SubmitAsync(host, profile, "T-NO-HOLD", Today.AddDays(-1));

        var refund = await RefundAsync(host, settlement.Id, 25000, "refund-1");

        Assert.Empty(refund.RefundedHoldIds);
        Assert.Equal(0m, await CreditBalanceAsync(host, PayableCode));
    }

    [Fact]
    public async Task A_refund_waits_for_the_shared_monetary_lock_so_it_cannot_interleave_with_a_batch_cut()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-LOCK", Today.AddDays(-1));
        _ = await host.TokenAsync("integrator");

        // Another monetary operation, such as the daily cut, holds the lock in its transaction.
        await using var connection = new NpgsqlConnection(host.AdminConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({MonetaryLockKey})", connection, transaction))
            await command.ExecuteNonQueryAsync();

        var refund = RefundAsync(host, settlement.Id, 25000, "refund-1");
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(refund.IsCompleted);
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.settlement_refunds"));

        await transaction.CommitAsync();
        Assert.Equal(25000, (await refund).Amount.MinorUnits);
    }

    [Fact]
    public async Task A_settlement_assigned_to_a_batch_cannot_be_refunded()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-BATCH", Today.AddDays(-1));
        var services = new ServiceCollection().AddInfrastructure(host.RuntimeConnection);
        services.Replace(ServiceDescriptor.Scoped<ISettlementBatchMembership, AlwaysInBatch>());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var exception = await Assert.ThrowsAsync<RefundException>(() => scope.ServiceProvider.GetRequiredService<RefundService>()
            .RefundAsync("demo-client-1", settlement.Id, Usd(25000), "refund-1", CancellationToken.None));

        Assert.Equal(RefundException.InBatch, exception.Code);
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries"));
    }

    [Fact]
    public async Task Another_client_cannot_refund_or_read_the_refund()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-OWNER", Today.AddDays(-1));
        var services = new ServiceCollection().AddInfrastructure(host.RuntimeConnection);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await Assert.ThrowsAsync<SettlementNotFoundException>(() => scope.ServiceProvider.GetRequiredService<RefundService>()
            .RefundAsync("another-client", settlement.Id, Usd(25000), "refund-1", CancellationToken.None));

        using var foreignPost = await SendAsync(host, "integrator-other", HttpMethod.Post,
            $"/v1/settlements/{settlement.Id}/refund", new RefundRequest(Usd(25000)), "foreign-refund");
        Assert.Equal(HttpStatusCode.NotFound, foreignPost.StatusCode);

        await RefundAsync(host, settlement.Id, 25000, "refund-1");
        using var foreignRead = await SendAsync(host, "integrator-other", HttpMethod.Get, $"/v1/settlements/{settlement.Id}/refund");
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        using var payee = await SendAsync(host, "payee-seller-1", HttpMethod.Get, $"/v1/settlements/{settlement.Id}/refund");
        Assert.Equal(HttpStatusCode.NotFound, payee.StatusCode);
    }

    [Fact]
    public async Task The_database_rejects_refunds_that_bypass_the_application()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-BYPASS", Today.AddDays(-1));
        var hold = Assert.Single(await HoldsAsync(host, settlement.Id));

        // Pointing a refund at the original entry is not a linked reversal.
        var notReversal = await Assert.ThrowsAsync<PostgresException>(() => RuntimeSqlAsync(host,
            "INSERT INTO ledger.settlement_refunds (settlement_id, client_id, idempotency_key, request_hash, journal_entry_id, currency, amount_minor, refunded_at_utc) " +
            $"VALUES ('{settlement.Id}', 'demo-client-1', 'k', '{new string('a', 64)}', '{settlement.JournalEntryId}', 'USD', 25000, now())"));
        // A Refunded hold transition needs a matching settlement refund.
        var unbacked = await Assert.ThrowsAsync<PostgresException>(() => RuntimeSqlAsync(host,
            "INSERT INTO ledger.escrow_hold_transitions (hold_id, state, journal_entry_id, currency, amount_minor, occurred_at_utc) " +
            $"VALUES ('{hold.Id}', 'Refunded', '{settlement.JournalEntryId}', 'USD', 2050, now())"));

        Assert.Equal(LedgerDatabase.InvalidRefund, notReversal.SqlState);
        Assert.Equal(LedgerDatabase.InvalidEscrowRelease, unbacked.SqlState);

        await RefundAsync(host, settlement.Id, 25000, "refund-1");
        var runtimeDelete = await Assert.ThrowsAsync<PostgresException>(() => RuntimeSqlAsync(host, "DELETE FROM ledger.settlement_refunds"));
        var ownerUpdate = await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync("UPDATE ledger.settlement_refunds SET amount_minor = 1"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, runtimeDelete.SqlState);
        Assert.Equal(LedgerDatabase.HistoryImmutable, ownerUpdate.SqlState);
    }

    private static Task<HttpResponseMessage> SendRefundAsync(IdentityApiHost host, Guid settlementId, long minor, string? key) =>
        SendAsync(host, "integrator", HttpMethod.Post, $"/v1/settlements/{settlementId}/refund", new RefundRequest(Usd(minor)), key);

    private static async Task<RefundResponse> RefundAsync(IdentityApiHost host, Guid settlementId, long minor, string key)
    {
        using var response = await SendRefundAsync(host, settlementId, minor, key);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RefundResponse>(Json))!;
    }

    private sealed class AlwaysInBatch : ISettlementBatchMembership
    {
        public Task<bool> IsAssignedAsync(Guid settlementId, CancellationToken ct) => Task.FromResult(true);
    }
}
