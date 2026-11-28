using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using el.Api.Clearing;
using el.Api.Knowledge;
using el.Api.Settlements;
using el.Application.Audit;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static el.IntegrationTests.EscrowApiTests;

namespace el.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class BackendAcceptanceApiTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task Recent_settlements_are_paged_and_keep_payee_and_client_data_private()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var first = await SubmitAsync(host, profile, "LIST-1", Today);
        var trade = first.Input with { TradeId = "LIST-2", Beneficiaries = [new("seller", SyntheticSeed.SellerTwo.Id.Value, "Remainder", 0, 0)] };
        using var posted = await SendAsync(host, "integrator", HttpMethod.Post, "/v1/settlements", trade, "list-2");
        posted.EnsureSuccessStatusCode();
        var second = (await posted.Content.ReadFromJsonAsync<FullSettlementResponse>(Json))!;
        foreach (var user in new[] { "finance", "admin", "auditor", "integrator" })
        {
            var page = await PageAsync(host, user, "?limit=1");
            Assert.Equal(second.Id, Assert.Single(page.Items).Id);
            Assert.True(page.HasMore);
            Assert.Equal(25000, page.Items[0].Gross!.MinorUnits);
            var next = await PageAsync(host, user, "?limit=1&offset=1");
            Assert.Equal(first.Id, Assert.Single(next.Items).Id);
            Assert.False(next.HasMore);
        }
        Assert.Empty((await PageAsync(host, "integrator-other", "?clientId=demo-client-1")).Items);
        using var own = await host.GetAsAsync("payee-seller-1", "/v1/settlements?partyId=" + SyntheticSeed.SellerTwo.Id.Value);
        var text = await own.Content.ReadAsStringAsync();
        foreach (var field in new[] { "gross", "clientId", "tradeId", "partyId", "payoutReference", "snapshot" })
            Assert.DoesNotContain("\"" + field + "\"", text);
        var slice = (await own.Content.ReadFromJsonAsync<SettlementPageResponse>(Json))!;
        Assert.Equal(first.Id, Assert.Single(slice.Items).Id);
        Assert.Equal(20495, slice.Items[0].Allocations!.Sum(a => a.Amount.MinorUnits));
        Assert.Empty((await PageAsync(host, "finance", "?currency=JPY")).Items);
        using var scheduler = await host.GetAsAsync("scheduler", "/v1/settlements");
        Assert.Equal(HttpStatusCode.Forbidden, scheduler.StatusCode);
        using var anonymous = await host.Client.GetAsync("/v1/settlements");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        foreach (var query in new[] { "?limit=0", "?offset=-1", "?currency=EUR" })
        {
            using var invalid = await host.GetAsAsync("finance", "/v1/settlements" + query);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    [Fact]
    public async Task Admin_registration_is_atomic_retryable_and_a_new_payee_can_settle_and_clear()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var request = new RegisterPartyRequest(Guid.NewGuid(), "Synthetic new seller");
        _ = await host.TokenAsync("admin");
        using var first = await RegisterAsync(host, request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var responses = await Task.WhenAll(RegisterAsync(host, request), RegisterAsync(host, request));
        foreach (var retry in responses) { using (retry) Assert.Equal(HttpStatusCode.OK, retry.StatusCode); }
        using var changed = await RegisterAsync(host, request with { PayoutEligible = false });
        await ErrorAsync(changed, HttpStatusCode.Conflict, "PARTY_CONFLICT");
        Assert.Equal(1L, await host.ScalarAsync<long>($"SELECT count(*) FROM ledger.audit_events WHERE operation='party.registered' AND target_id='{request.Id}'"));
        using var accounts = await host.GetAsAsync("admin", $"/v1/accounts?partyId={request.Id}");
        var mappings = (await accounts.Content.ReadFromJsonAsync<ProvisionedAccountResponse[]>())!;
        Assert.Equal(6, mappings.Length);
        foreach (var currency in new[] { "USD", "JPY", "KWD" })
            Assert.Equal(new[] { "Escrow", "Payable" }, mappings.Where(a => a.Currency == currency).Select(a => a.Type).Order());
        using var lookup = await host.GetAsAsync("auditor", "/v1/parties/" + request.Id);
        Assert.True((await lookup.Content.ReadFromJsonAsync<RegisteredPartyResponse>())!.PayoutEligible);
        var profile = await PublishProfileAsync(host, escrow: false);
        var trade = new TradeRequest("NEW-PARTY", Today, "USD", Usd(25000), "DEMO-NONE", profile.ProfileId, profile.CorporateCode,
            [new("seller", request.Id, "Remainder", 0, 0)]);
        using var settlement = await SendAsync(host, "integrator", HttpMethod.Post, "/v1/settlements", trade, "new-party");
        settlement.EnsureSuccessStatusCode();
        using var cut = await SendAsync(host, "finance", HttpMethod.Post, "/v1/clearing-batches", new PrepareBatchRequest(DateOnly.FromDateTime(Today.DateTime), "USD"));
        cut.EnsureSuccessStatusCode();
        var instruction = Assert.Single((await cut.Content.ReadFromJsonAsync<ClearingBatchResponse>(Json))!.Instructions);
        Assert.Equal(request.Id, instruction.PartyId);
        Assert.Equal(21245, instruction.Amount.MinorUnits);
        await Assert.ThrowsAsync<PostgresException>(() => RuntimeSqlAsync(host, $"DELETE FROM ledger.payout_parties WHERE party_id='{request.Id}'"));
        await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync($"DELETE FROM ledger.payout_parties WHERE party_id='{request.Id}'"));
    }

    [Fact]
    public async Task Party_setup_enforces_role_grants_validation_and_audit_rollback()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var request = new RegisterPartyRequest(Guid.NewGuid(), "Synthetic participant");
        foreach (var user in new[] { "finance", "integrator", "payee-seller-1", "scheduler", "auditor" })
        {
            using var denied = await RegisterAsync(host, request, user);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var list = await host.GetAsAsync(user, "/v1/parties");
            Assert.Equal(user == "auditor" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, list.StatusCode);
            using var accounts = await host.GetAsAsync(user, "/v1/accounts");
            Assert.Equal(user == "auditor" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, accounts.StatusCode);
        }
        foreach (var invalid in new[] { request with { Id = Guid.Empty }, request with { Name = " " }, request with { Name = new string('x', 201) } })
        {
            using var response = await RegisterAsync(host, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        await using var failing = host.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAuditTrail>(); services.AddScoped<IAuditTrail, FailingAudit>();
        }));
        using var client = failing.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await host.TokenAsync("admin"));
        using var failure = await client.PostAsJsonAsync("/v1/parties", request);
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>($"SELECT count(*) FROM ledger.parties WHERE id='{request.Id}'"));
        Assert.Equal(0L, await host.ScalarAsync<long>($"SELECT count(*) FROM ledger.accounts WHERE party_id='{request.Id}'"));
        Assert.Equal(0L, await host.ScalarAsync<long>($"SELECT count(*) FROM ledger.payout_parties WHERE party_id='{request.Id}'"));
        using var retry = await RegisterAsync(host, request);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task Refund_reason_is_stored_immutable_and_part_of_the_retry_payload()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: false);
        var settlement = await SubmitAsync(host, profile, "REASON", Today);
        foreach (var reason in new string?[] { null, " ", new('x', 501), "invalid\nreason" })
        {
            using var invalid = await SendAsync(host, "integrator", HttpMethod.Post, $"/v1/settlements/{settlement.Id}/refund", new RefundRequest(Usd(25000), reason), "reason");
            await ErrorAsync(invalid, HttpStatusCode.BadRequest, "INVALID_REFUND_REASON");
        }
        var request = new RefundRequest(Usd(25000), "  Synthetic order cancelled  ");
        using var first = await SendAsync(host, "integrator", HttpMethod.Post, $"/v1/settlements/{settlement.Id}/refund", request, "reason");
        first.EnsureSuccessStatusCode();
        var result = (await first.Content.ReadFromJsonAsync<RefundResponse>(Json))!;
        Assert.Equal("Synthetic order cancelled", result.Reason);
        using var retry = await SendAsync(host, "integrator", HttpMethod.Post, $"/v1/settlements/{settlement.Id}/refund", request with { Reason = result.Reason }, "reason");
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        using var read = await host.GetAsAsync("finance", $"/v1/settlements/{settlement.Id}/refund");
        Assert.Equal(result.Reason, (await read.Content.ReadFromJsonAsync<RefundResponse>(Json))!.Reason);
        using var changed = await SendAsync(host, "integrator", HttpMethod.Post, $"/v1/settlements/{settlement.Id}/refund", request with { Reason = "Different reason" }, "reason");
        await ErrorAsync(changed, HttpStatusCode.Conflict, "IDEMPOTENCY_CONFLICT");
        Assert.Equal(result.Reason, await host.ScalarAsync<string>("SELECT reason FROM ledger.settlement_refunds"));
        await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync("UPDATE ledger.settlement_refunds SET reason='changed'"));
    }

    [Fact]
    public async Task A_legacy_refund_keeps_its_unknown_reason_and_original_retry_key_after_migration()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: false);
        var settlement = await SubmitAsync(host, profile, "LEGACY", Today);
        // Reproduce the deployed pre-change schema and command hash, then apply the real migration.
        await using (var db = LedgerSchema.NewContext(host.AdminConnection))
        {
            var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
            await migrator.MigrateAsync("20261124170000_ReportExports");
        }
        var refundId = Guid.NewGuid();
        var legacyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"refund|{settlement.Id:N}|USD|25000")));
        await host.ExecuteAsync($"""
            BEGIN;
            INSERT INTO ledger.journal_entries (id, reference, description, currency, reversal_of, finalized)
            VALUES ('{refundId}', 'LEGACY:REFUND', 'Synthetic legacy refund', 'USD', '{settlement.JournalEntryId}', false);
            INSERT INTO ledger.journal_lines (entry_id, line_number, account_id, side, amount_minor, currency)
            SELECT '{refundId}', line_number, account_id, CASE side WHEN 1 THEN 2 ELSE 1 END, amount_minor, currency
            FROM ledger.journal_lines WHERE entry_id='{settlement.JournalEntryId}';
            SELECT ledger.finalize_journal_entry('{refundId}');
            INSERT INTO ledger.settlement_refunds (settlement_id, client_id, idempotency_key, request_hash, journal_entry_id, currency, amount_minor, refunded_at_utc)
            VALUES ('{settlement.Id}', 'demo-client-1', 'legacy', '{legacyHash}', '{refundId}', 'USD', 25000, now());
            COMMIT;
            """);
        await using (var db = LedgerSchema.NewContext(host.AdminConnection)) await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database);
        using var retry = await SendAsync(host, "integrator", HttpMethod.Post, $"/v1/settlements/{settlement.Id}/refund", new RefundRequest(Usd(25000)), "legacy");
        retry.EnsureSuccessStatusCode();
        var result = (await retry.Content.ReadFromJsonAsync<RefundResponse>(Json))!;
        Assert.Equal(refundId, result.JournalEntryId);
        Assert.Null(result.Reason);
        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries"));
    }

    private sealed class FailingAudit : IAuditTrail
    {
        public Task RecordAsync(string operation, string targetId, CancellationToken cancellationToken) => throw new InvalidOperationException("Injected registration failure");
    }
    private static Task<HttpResponseMessage> RegisterAsync(IdentityApiHost host, RegisterPartyRequest request, string user = "admin") =>
        SendAsync(host, user, HttpMethod.Post, "/v1/parties", request);
    private static async Task<SettlementPageResponse> PageAsync(IdentityApiHost host, string user, string query)
    {
        using var response = await host.GetAsAsync(user, "/v1/settlements" + query);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SettlementPageResponse>(Json))!;
    }
}
