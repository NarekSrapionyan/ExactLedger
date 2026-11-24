using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using el.Api.Clearing;
using el.Api.Reporting;
using el.Application.Audit;
using el.Application.Ledger;
using el.Application.Reporting;
using el.Domain.Clearing;
using el.Domain.Common;
using el.Domain.Ledger;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static el.IntegrationTests.EscrowApiTests;

namespace el.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class ReportApiTests(PostgreSqlFixture database)
{
    private static readonly Guid Seller = SyntheticSeed.SellerOne.Id.Value;

    [Fact]
    public async Task Reports_reconcile_reservations_and_real_postings_before_and_after_confirmation()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "report-1", Today.AddDays(-10));
        using var prepare = await SendAsync(host, "finance", HttpMethod.Post, "/v1/clearing-batches",
            new PrepareBatchRequest(DateOnly.FromDateTime(Today.DateTime), "USD"));
        prepare.EnsureSuccessStatusCode();
        var batch = (await prepare.Content.ReadFromJsonAsync<ClearingBatchResponse>(Json))!;
        var before = await ReadAsync(host, "batch-summary");
        Assert.Equal("191.95", before.Rows[0]["instructions"]);
        Assert.Equal("191.95", before.Rows[0]["reserved"]);
        Assert.Equal("0.00", before.Rows[0]["paid"]);
        Assert.Equal("true", before.Rows[0]["isReconciled"]);
        var statement = await ReadAsync(host, "payee-statement", "payee-seller-1");
        Assert.Equal("204.95", statement.Totals.Single().Amounts["grossAllocation"]);
        Assert.Equal("20.50", statement.Totals.Single().Amounts["heldEscrow"]);
        Assert.Equal("184.45", statement.Totals.Single().Amounts["reserved"]);
        Assert.Equal("0.00", statement.Totals.Single().Amounts["confirmedPayouts"]);
        var balances = await ReadAsync(host, "account-balances", "payee-seller-1");
        var payable = balances.Rows.Single(row => row["accountId"] == AccountId("PAYABLE.SELLER-1.USD").ToString());
        Assert.Equal("184.45", payable["balance"]);
        Assert.Equal("184.45", payable["reserved"]);
        Assert.Equal("0.00", payable["available"]);

        using var export = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/clearing-batches/{batch.Id}/export");
        export.EnsureSuccessStatusCode();
        using var simulated = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/clearing-batches/{batch.Id}/simulate-statement");
        simulated.EnsureSuccessStatusCode();
        using var confirm = new HttpRequestMessage(HttpMethod.Post, $"/v1/clearing-batches/{batch.Id}/confirm-simulated");
        confirm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await host.TokenAsync("finance"));
        confirm.Content = new ByteArrayContent(await simulated.Content.ReadAsByteArrayAsync());
        confirm.Content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        using var confirmed = await host.Client.SendAsync(confirm);
        confirmed.EnsureSuccessStatusCode();

        var after = await ReadAsync(host, "batch-summary");
        Assert.Equal("Confirmed", after.Rows[0]["state"]);
        foreach (var field in new[] { "instructions", "paid", "payableDebits", "bankCredits" })
            Assert.Equal("191.95", after.Rows[0][field]);
        Assert.Equal("0.00", after.Rows[0]["reserved"]);
        Assert.Equal("true", after.Rows[0]["isReconciled"]);
        var paid = await ReadAsync(host, "payee-statement", "payee-seller-1");
        Assert.Equal("0.00", paid.Totals.Single().Amounts["reserved"]);
        Assert.Equal("184.45", paid.Totals.Single().Amounts["confirmedPayouts"]);
        Assert.All(paid.Rows, row => Assert.Equal(Seller.ToString(), row["partyId"]));
        var hold = Assert.Single(await HoldsAsync(host, settlement.Id));
        _ = await ReleaseAsync(host, hold.Id);
        var released = await ReadAsync(host, "payee-statement", "payee-seller-1");
        Assert.Equal("0.00", released.Totals.Single().Amounts["heldEscrow"]);
        Assert.Equal("20.50", released.Totals.Single().Amounts["releasedEscrow"]);
        Assert.Equal("184.45", released.Totals.Single().Amounts["confirmedPayouts"]);
        var trial = await ReadAsync(host, "trial-balance");
        Assert.Equal("0.00", trial.Rows.Single()["difference"]);
        var cut = await ReadAsync(host, "journal-register", query: $"?cutSequence={batch.CutSequence}");
        Assert.All(cut.Rows, row => Assert.Equal(settlement.JournalEntryId.ToString(), row["journalEntryId"]));
    }

    [Theory]
    [InlineData("journal-register")]
    [InlineData("payee-statement")]
    [InlineData("payout-instructions")]
    [InlineData("batch-summary")]
    public async Task Csv_exports_are_frozen_rectangular_and_authorized(string kind)
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: false);
        _ = await SubmitAsync(host, profile, "csv-1", Today);
        if (kind is "payout-instructions" or "batch-summary")
        {
            using var batch = await SendAsync(host, "finance", HttpMethod.Post, "/v1/clearing-batches",
                new PrepareBatchRequest(DateOnly.FromDateTime(Today.DateTime), "USD"));
            batch.EnsureSuccessStatusCode();
        }
        var filter = new ReportFilter(PartyId: kind == "payee-statement" ? Seller : null);
        var first = await ExportAsync(host, kind, filter, "frozen");
        using var download = await host.GetAsAsync("auditor", first.DownloadUrl);
        download.EnsureSuccessStatusCode();
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(first.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal("\"" + first.Sha256 + "\"", download.Headers.ETag!.ToString());
        Assert.True(Rfc4180.TryParse(Encoding.UTF8.GetString(bytes), out var records, out _));
        Assert.Equal(first.RecordCount + 1, records.Count);
        Assert.All(records, record => Assert.Equal(records[0].Count, record.Count));
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        _ = await SubmitAsync(host, profile, "csv-2", Today, "second");
        var retry = await ExportAsync(host, kind, filter, "frozen");
        Assert.Equal(first, retry with { Totals = first.Totals });
        using var again = await host.GetAsAsync("finance", first.DownloadUrl);
        Assert.Equal(bytes, await again.Content.ReadAsByteArrayAsync());
        using var changed = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/reports/{kind}/exports",
            filter with { Currency = "JPY" }, "frozen");
        await ErrorAsync(changed, HttpStatusCode.Conflict, "EXPORT_KEY_CONFLICT");
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.report_exports"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation = 'report.exported'"));
    }

    [Fact]
    public async Task Payees_cannot_read_foreign_records_or_download_global_or_foreign_exports()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "private-1", Today);
        var own = await ReadAsync(host, "payee-statement", "payee-seller-1");
        Assert.All(own.Rows, row => Assert.Equal(Seller.ToString(), row["partyId"]));
        Assert.DoesNotContain("description", own.Columns);
        using var foreign = await host.GetAsAsync("payee-seller-1", $"/v1/reports/payee-statement?partyId={SyntheticSeed.SellerTwo.Id.Value}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var global = await ExportAsync(host, "payout-instructions", new(), "global");
        var other = await ExportAsync(host, "payee-statement", new(PartyId: SyntheticSeed.SellerTwo.Id.Value), "foreign");
        foreach (var artifact in new[] { global, other })
        {
            using var metadata = await host.GetAsAsync("payee-seller-1", $"/v1/reports/exports/{artifact.Id}");
            using var download = await host.GetAsAsync("payee-seller-1", artifact.DownloadUrl);
            Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        }
        var allowed = await ExportAsync(host, "payee-statement", new(), "own", "payee-seller-1");
        using var ownDownload = await host.GetAsAsync("payee-seller-1", allowed.DownloadUrl);
        Assert.Equal(HttpStatusCode.OK, ownDownload.StatusCode);
        foreach (var user in new[] { "integrator", "admin", "scheduler", "payee-seller-1" })
        {
            using var ledger = await host.GetAsAsync(user, "/v1/reports/journal-register");
            Assert.Equal(HttpStatusCode.Forbidden, ledger.StatusCode);
        }
        using var anonymous = await host.Client.GetAsync("/v1/reports/journal-register");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Refund_statement_records_own_deductions_and_reverses_held_escrow_without_duplicate_lines()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "refund-report", Today);
        using var refund = await SendAsync(host, "integrator", HttpMethod.Post, $"/v1/settlements/{settlement.Id}/refund",
            new { amount = Usd(25000) }, "refund-report");
        refund.EnsureSuccessStatusCode();
        var report = await ReadAsync(host, "payee-statement", "payee-seller-1");
        Assert.Equal("204.95", report.Totals.Single().Amounts["grossAllocation"]);
        Assert.Equal("204.95", report.Totals.Single().Amounts["deductions"]);
        Assert.Equal("0.00", report.Totals.Single().Amounts["heldEscrow"]);
        Assert.Equal(4, report.Rows.Count);
    }

    [Fact]
    public async Task Filters_pagination_and_export_rollback_preserve_correct_totals()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: false);
        _ = await SubmitAsync(host, profile, "filter-1", Today);
        var page = await ReadAsync(host, "journal-register", query: "?limit=1");
        Assert.Single(page.Rows);
        Assert.True(page.RecordCount > 1);
        Assert.Equal("250.00", page.Totals.Single().Amounts["debit"]);
        var empty = await ReadAsync(host, "journal-register", query: "?currency=JPY");
        Assert.Empty(empty.Rows);
        var future = await ReadAsync(host, "journal-register", query: "?from=2090-01-01");
        Assert.Empty(future.Rows);
        foreach (var path in new[] { "journal-register?currency=EUR", "journal-register?limit=0", "journal-register?cutSequence=-1",
                     "journal-register?from=2026-12-01&to=2026-01-01", "trial-balance?partyId=" + Seller })
        {
            using var invalid = await host.GetAsAsync("finance", "/v1/reports/" + path);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var unknownParty = await SendAsync(host, "finance", HttpMethod.Post, "/v1/reports/payee-statement/exports",
            new ReportFilter(PartyId: Guid.NewGuid()), "unknown-party");
        Assert.Equal(HttpStatusCode.NotFound, unknownParty.StatusCode);
        await using var failing = host.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAuditTrail>();
            services.AddScoped<IAuditTrail, FailingAudit>();
        }));
        using var client = failing.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/reports/journal-register/exports");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await host.TokenAsync("finance"));
        request.Headers.Add("Idempotency-Key", "rollback");
        request.Content = JsonContent.Create(new ReportFilter());
        using var failure = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.report_exports"));
        var saved = await ExportAsync(host, "journal-register", new(), "rollback");
        await Assert.ThrowsAsync<PostgresException>(() => RuntimeSqlAsync(host, $"DELETE FROM ledger.report_exports WHERE id = '{saved.Id}'"));
        await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync($"UPDATE ledger.report_exports SET record_count = 0 WHERE id = '{saved.Id}'"));
    }

    private sealed class FailingAudit : IAuditTrail
    {
        public Task RecordAsync(string operation, string targetId, CancellationToken cancellationToken) => throw new InvalidOperationException("Injected report audit failure");
    }

    [Fact]
    public async Task Concurrent_export_retries_store_one_artifact_and_one_audit_event()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        _ = await host.TokenAsync("finance");
        var results = await Task.WhenAll(ExportAsync(host, "trial-balance", new(), "parallel"),
            ExportAsync(host, "trial-balance", new(), "parallel"));
        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Equal(results[0].GeneratedAtUtc, results[1].GeneratedAtUtc);
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.report_exports"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation = 'report.exported'"));
    }

    [Fact]
    public async Task Journal_csv_preserves_three_currency_precisions_and_escaped_description()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        const string description = "Synthetic, \"quoted\"\r\nsecond line";
        foreach (var (currency, minor) in new[] { ("USD", 5L), ("JPY", 123L), ("KWD", 1234L) })
        {
            var code = CurrencyCode.Parse(currency);
            var cash = SyntheticSeed.Accounts.Single(account => account.Code.Value == "CLEARING.BANK." + currency);
            var payable = SyntheticSeed.Accounts.Single(account => account.Code.Value == "PAYABLE.SELLER-1." + currency);
            var entry = JournalEntry.Create(new JournalEntryId(Guid.NewGuid()), JournalReference.Parse("REPORT:" + currency), description,
                [new JournalLineDraft(cash, EntrySide.Debit, Money.FromMinorUnits(minor, code)),
                 new JournalLineDraft(payable, EntrySide.Credit, Money.FromMinorUnits(minor, code))]);
            await using var scope = host.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<JournalPostingService>().PostAsync(entry, CancellationToken.None);
        }
        var report = await ReadAsync(host, "journal-register");
        Assert.Equal(6, report.RecordCount);
        foreach (var (currency, amount) in new[] { ("USD", "0.05"), ("JPY", "123"), ("KWD", "1.234") })
        {
            var total = report.Totals.Single(total => total.Currency == currency);
            Assert.Equal(amount, total.Amounts["debit"]);
            Assert.Equal(amount, total.Amounts["credit"]);
        }
        var export = await ExportAsync(host, "journal-register", new(), "quoted");
        using var download = await host.GetAsAsync("auditor", export.DownloadUrl);
        var csv = await download.Content.ReadAsStringAsync();
        Assert.True(Rfc4180.TryParse(csv, out var records, out _));
        var descriptionColumn = records[0].ToList().IndexOf("description");
        Assert.All(records.Skip(1), row => Assert.Equal(description, row[descriptionColumn]));
    }

    private static async Task<ReportResponse> ReadAsync(IdentityApiHost host, string kind, string user = "finance", string query = "")
    {
        using var response = await host.GetAsAsync(user, "/v1/reports/" + kind + query);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReportResponse>())!;
    }

    private static async Task<ReportExportResponse> ExportAsync(IdentityApiHost host, string kind, ReportFilter filter, string key, string user = "finance")
    {
        using var response = await SendAsync(host, user, HttpMethod.Post, $"/v1/reports/{kind}/exports", filter, key);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReportExportResponse>())!;
    }
}
