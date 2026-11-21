using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using el.Api.Contracts;
using el.Api.Escrow;
using el.Api.Knowledge;
using el.Api.Settlements;
using el.Domain.Common;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Npgsql;

namespace el.IntegrationTests;

/// <summary>
/// BE-012.1. Trades are dated relative to today: a 30-day-old trade's 7-day hold is due, a
/// 1-day-old trade's hold is not. The API uses the real clock, so the tests never depend on a date.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class EscrowApiTests(PostgreSqlFixture database)
{
    internal static readonly DateTimeOffset Today = new(DateTime.UtcNow.Date, TimeSpan.Zero);
    internal static readonly JsonSerializerOptions Json = CreateJson();
    internal const string EscrowCode = "ESCROW.SELLER-1.USD";
    internal const string PayableCode = "PAYABLE.SELLER-1.USD";

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new MoneyJsonConverter(CurrencyReferenceData.Catalog, 1));
        return options;
    }

    internal static Money Usd(long minor) => Money.FromMinorUnits(minor, CurrencyReferenceData.Usd);

    internal static Guid AccountId(string code) => SyntheticSeed.Accounts.Single(account => account.Code.Value == code).Id.Value;

    [Fact]
    public async Task A_due_hold_is_released_in_full_once_and_balances_reconcile_exactly()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-DUE", Today.AddDays(-30));
        var hold = Assert.Single(await HoldsAsync(host, settlement.Id));

        Assert.Equal("Held", hold.State);
        Assert.Equal(2050, hold.Held.MinorUnits);
        Assert.Equal(0, hold.Released.MinorUnits);
        Assert.Equal(Today.AddDays(-23), hold.DueAtUtc);
        Assert.Equal(2050m, await CreditBalanceAsync(host, EscrowCode));
        Assert.Equal(18445m, await CreditBalanceAsync(host, PayableCode));

        var release = await ReleaseAsync(host, hold.Id);

        Assert.Equal(hold.Id, release.HoldId);
        Assert.Equal(settlement.Id, release.SettlementId);
        Assert.Equal(2050, release.Amount.MinorUnits);
        Assert.True(release.ReleasedAtUtc >= hold.DueAtUtc);
        Assert.Equal(0m, await CreditBalanceAsync(host, EscrowCode));
        Assert.Equal(20495m, await CreditBalanceAsync(host, PayableCode));

        var released = await ReadHoldAsync(host, hold.Id);
        Assert.Equal("Released", released.State);
        Assert.Equal(2050, released.Released.MinorUnits);
        Assert.Equal("Released", await host.ScalarAsync<string>($"SELECT state FROM ledger.escrow_hold_status WHERE id = '{hold.Id}'"));

        // Repeating the release returns the stored result and posts nothing.
        var repeated = await ReleaseAsync(host, hold.Id);
        Assert.Equal(JsonSerializer.Serialize(release, Json), JsonSerializer.Serialize(repeated, Json));
        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.escrow_hold_transitions"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation = 'escrow.released'"));
        await AssertEscrowReconcilesAsync(host);
    }

    [Fact]
    public async Task A_hold_that_is_not_due_is_rejected_and_nothing_changes()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-EARLY", Today.AddDays(-1));
        var hold = Assert.Single(await HoldsAsync(host, settlement.Id));

        using var response = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/escrow/holds/{hold.Id}/release");

        await ErrorAsync(response, HttpStatusCode.Conflict, "HOLD_NOT_DUE");
        Assert.Equal("Held", (await ReadHoldAsync(host, hold.Id)).State);
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries"));
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.escrow_hold_transitions"));
        Assert.Equal(2050m, await CreditBalanceAsync(host, EscrowCode));
    }

    [Fact]
    public async Task Ten_concurrent_releases_of_one_hold_post_a_single_result()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-PARALLEL", Today.AddDays(-30));
        var hold = Assert.Single(await HoldsAsync(host, settlement.Id));
        _ = await host.TokenAsync("finance");

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => ReleaseAsync(host, hold.Id)));

        Assert.Single(results.Select(result => result.JournalEntryId).Distinct());
        Assert.Single(results.Select(result => JsonSerializer.Serialize(result, Json)).Distinct());
        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.escrow_hold_transitions"));
        Assert.Equal(20495m, await CreditBalanceAsync(host, PayableCode));
    }

    [Fact]
    public async Task The_scheduler_run_releases_only_due_holds_and_is_safe_to_repeat()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var due = Assert.Single(await HoldsAsync(host, (await SubmitAsync(host, profile, "T-DUE", Today.AddDays(-30))).Id));
        var early = Assert.Single(await HoldsAsync(host, (await SubmitAsync(host, profile, "T-EARLY", Today.AddDays(-1), "command-2")).Id));

        var first = await RunSchedulerAsync(host);
        var second = await RunSchedulerAsync(host);

        Assert.Equal(due.Id, Assert.Single(first.Released).HoldId);
        Assert.Empty(first.Skipped);
        Assert.Empty(second.Released);
        Assert.Equal("Released", (await ReadHoldAsync(host, due.Id)).State);
        Assert.Equal("Held", (await ReadHoldAsync(host, early.Id)).State);
        Assert.Equal(2050m, await CreditBalanceAsync(host, EscrowCode));
        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation = 'escrow.release-run'"));
        await AssertEscrowReconcilesAsync(host);
    }

    [Fact]
    public async Task Holds_of_an_inactive_party_are_skipped_by_the_scheduler_and_rejected_manually()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var hold = Assert.Single(await HoldsAsync(host, (await SubmitAsync(host, profile, "T-SUSPENDED", Today.AddDays(-30))).Id));
        await host.ExecuteAsync($"UPDATE ledger.parties SET status = 2 WHERE id = '{SyntheticSeed.SellerOne.Id}'");

        var run = await RunSchedulerAsync(host);
        using var manual = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/escrow/holds/{hold.Id}/release");

        Assert.Empty(run.Released);
        var skipped = Assert.Single(run.Skipped);
        Assert.Equal(hold.Id, skipped.HoldId);
        Assert.Equal("PARTY_NOT_ACTIVE", skipped.Code);
        await ErrorAsync(manual, HttpStatusCode.Conflict, "PARTY_NOT_ACTIVE");
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.escrow_hold_transitions"));
    }

    [Fact]
    public async Task A_zero_escrow_settlement_creates_no_hold_and_nothing_to_release()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: false);
        var settlement = await SubmitAsync(host, profile, "T-NO-ESCROW", Today.AddDays(-30));

        Assert.Empty(await HoldsAsync(host, settlement.Id));
        Assert.Empty((await RunSchedulerAsync(host)).Released);
        Assert.Equal(0m, await CreditBalanceAsync(host, EscrowCode));
        Assert.Equal(20495m, await CreditBalanceAsync(host, PayableCode));
    }

    [Fact]
    public async Task Release_and_hold_reads_enforce_roles_and_unknown_holds_return_404()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var hold = Assert.Single(await HoldsAsync(host, (await SubmitAsync(host, profile, "T-ROLES", Today.AddDays(-30))).Id));

        foreach (var user in new[] { "integrator", "admin", "auditor", "payee-seller-1" })
        {
            using var release = await SendAsync(host, user, HttpMethod.Post, $"/v1/escrow/holds/{hold.Id}/release");
            using var run = await SendAsync(host, user, HttpMethod.Post, "/v1/escrow/releases");
            Assert.Equal(HttpStatusCode.Forbidden, release.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
        }

        using var scheduler = await SendAsync(host, "scheduler", HttpMethod.Post, $"/v1/escrow/holds/{hold.Id}/release");
        Assert.Equal(HttpStatusCode.OK, scheduler.StatusCode);
        using var auditorRead = await SendAsync(host, "auditor", HttpMethod.Get, $"/v1/escrow/holds/{hold.Id}");
        Assert.Equal(HttpStatusCode.OK, auditorRead.StatusCode);
        using var payeeRead = await SendAsync(host, "payee-seller-1", HttpMethod.Get, $"/v1/escrow/holds/{hold.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, payeeRead.StatusCode);

        using var missingRelease = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/escrow/holds/{Guid.NewGuid()}/release");
        using var missingRead = await SendAsync(host, "finance", HttpMethod.Get, $"/v1/escrow/holds/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missingRelease.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingRead.StatusCode);
    }

    [Fact]
    public async Task The_database_rejects_releases_that_bypass_the_application()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-BYPASS", Today.AddDays(-30));
        var hold = Assert.Single(await HoldsAsync(host, settlement.Id));
        var insert = $"INSERT INTO ledger.escrow_hold_transitions (hold_id, state, journal_entry_id, currency, amount_minor, occurred_at_utc) " +
                     $"VALUES ('{hold.Id}', '{{0}}', '{settlement.JournalEntryId}', 'USD', 2050, now())";

        // The capture entry is not a Dr Escrow / Cr Payable release of exactly this hold.
        var wrongJournal = await Assert.ThrowsAsync<PostgresException>(() => RuntimeSqlAsync(host, string.Format(System.Globalization.CultureInfo.InvariantCulture, insert, "Released")));
        var refunded = await Assert.ThrowsAsync<PostgresException>(() => RuntimeSqlAsync(host, string.Format(System.Globalization.CultureInfo.InvariantCulture, insert, "Refunded")));

        Assert.Equal(LedgerDatabase.InvalidEscrowRelease, wrongJournal.SqlState);
        // A Refunded transition is only valid with a matching settlement refund (BE-012.2).
        Assert.Equal(LedgerDatabase.InvalidEscrowRelease, refunded.SqlState);

        await ReleaseAsync(host, hold.Id);
        var runtimeUpdate = await Assert.ThrowsAsync<PostgresException>(() => RuntimeSqlAsync(host, "UPDATE ledger.escrow_hold_transitions SET amount_minor = 1"));
        var ownerDelete = await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync("DELETE FROM ledger.escrow_hold_transitions"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, runtimeUpdate.SqlState);
        Assert.Equal(LedgerDatabase.HistoryImmutable, ownerDelete.SqlState);
    }

    internal static async Task AssertEscrowReconcilesAsync(IdentityApiHost host)
    {
        // Every escrow account's balance equals the sum of its holds that are still Held.
        Assert.Equal(0L, await host.ScalarAsync<long>("""
            SELECT count(*) FROM ledger.accounts a
             WHERE a.type = 3 AND
                   (SELECT COALESCE(sum(CASE l.side WHEN 2 THEN l.amount_minor ELSE -l.amount_minor END), 0)
                      FROM ledger.journal_lines l WHERE l.account_id = a.id)
                <> (SELECT COALESCE(sum(s.original_held_minor), 0)
                      FROM ledger.escrow_hold_status s JOIN ledger.escrow_holds h ON h.id = s.id
                     WHERE h.escrow_account_id = a.id AND s.state = 'Held')
            """));
    }

    internal static Task<decimal> CreditBalanceAsync(IdentityApiHost host, string accountCode) => host.ScalarAsync<decimal>($"""
        SELECT COALESCE(sum(CASE l.side WHEN 2 THEN l.amount_minor ELSE -l.amount_minor END), 0)
          FROM ledger.journal_lines l JOIN ledger.accounts a ON a.id = l.account_id WHERE a.code = '{accountCode}'
        """);

    internal static async Task RuntimeSqlAsync(IdentityApiHost host, string sql)
    {
        await using var connection = new NpgsqlConnection(host.RuntimeConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<(Guid ProfileId, string CorporateCode)> PublishProfileAsync(IdentityApiHost host, bool escrow)
    {
        var held = escrow ? 2050 : 0;
        var profile = new ProfileRequest(Guid.NewGuid(), 1, "demo-client-1", escrow ? "ESCROW-USD" : "NO-ESCROW-USD", "USD",
            12_000_000, 2_900_000, Usd(30), null, null, "HalfEven", escrow ? 10_000_000 : 0, 7,
            AccountId("REVENUE.COMMISSION.USD"), AccountId("PAYABLE.PROCESSOR.USD"), Today.AddDays(-60), Today.AddYears(1),
            "Synthetic escrow lifecycle example", true,
            [new("Independent expected components", Usd(25000), Usd(20495), Usd(3000), Usd(755), Usd(held), Usd(20495 - held))]);
        using var create = await SendAsync(host, "admin", HttpMethod.Post, "/v1/fee-profiles", profile);
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var saved = (await create.Content.ReadFromJsonAsync<ProfileResponse>(Json))!;
        using var publish = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/fee-profiles/{saved.Id}/publish");
        Assert.True(publish.StatusCode == HttpStatusCode.NoContent, await publish.Content.ReadAsStringAsync());
        return (profile.ProfileId, profile.CorporateCode);
    }

    internal static async Task<FullSettlementResponse> SubmitAsync(IdentityApiHost host, (Guid ProfileId, string CorporateCode) profile,
        string tradeId, DateTimeOffset occurredAt, string key = "command-1")
    {
        var trade = new TradeRequest(tradeId, occurredAt, "USD", Usd(25000), "DEMO-NONE", profile.ProfileId, profile.CorporateCode,
            [new("affiliate", SyntheticSeed.Affiliate.Id.Value, "Affiliate", 0, 3_000_000), new("seller", SyntheticSeed.SellerOne.Id.Value, "Remainder", 0, 0)]);
        using var response = await SendAsync(host, "integrator", HttpMethod.Post, "/v1/settlements", trade, key);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<FullSettlementResponse>(Json))!;
    }

    internal static async Task<EscrowHoldResponse[]> HoldsAsync(IdentityApiHost host, Guid settlementId)
    {
        using var response = await SendAsync(host, "finance", HttpMethod.Get, $"/v1/escrow/holds?settlementId={settlementId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EscrowHoldResponse[]>(Json))!;
    }

    internal static async Task<EscrowHoldResponse> ReadHoldAsync(IdentityApiHost host, Guid holdId)
    {
        using var response = await SendAsync(host, "finance", HttpMethod.Get, $"/v1/escrow/holds/{holdId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EscrowHoldResponse>(Json))!;
    }

    internal static async Task<EscrowReleaseResponse> ReleaseAsync(IdentityApiHost host, Guid holdId)
    {
        using var response = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/escrow/holds/{holdId}/release");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<EscrowReleaseResponse>(Json))!;
    }

    internal static async Task<DueEscrowReleaseResponse> RunSchedulerAsync(IdentityApiHost host)
    {
        using var response = await SendAsync(host, "scheduler", HttpMethod.Post, "/v1/escrow/releases");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DueEscrowReleaseResponse>(Json))!;
    }

    internal static async Task<HttpResponseMessage> SendAsync(IdentityApiHost host, string user, HttpMethod method, string path,
        object? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await host.TokenAsync(user));
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return await host.Client.SendAsync(request);
    }

    internal static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
}
