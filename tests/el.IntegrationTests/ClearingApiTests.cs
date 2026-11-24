using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using el.Api.Clearing;
using el.Api.Contracts;
using el.Api.Settlements;
using el.Application.Audit;
using el.Application.Clearing;
using el.Domain.Common;
using el.Infrastructure;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static el.IntegrationTests.EscrowApiTests;

namespace el.IntegrationTests;

/// <summary>BE-013: daily cut, deterministic export and simulated confirmation over HTTP and PostgreSQL.</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed partial class ClearingApiTests(PostgreSqlFixture database)
{
    private const string PayableCode = "PAYABLE.SELLER-1.USD";
    private const string EscrowCode = "ESCROW.SELLER-1.USD";
    private const string AffiliatePayable = "PAYABLE.AFFILIATE-1.USD";
    private const string ProcessorPayable = "PAYABLE.PROCESSOR.USD";
    private const string CsvHeader = "reference,business_date,party_id,currency,amount\r\n";

    private static readonly DateOnly Present = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly DateOnly Yesterday = Present.AddDays(-1);
    private static readonly CurrencyDefinition UsdDefinition =
        CurrencyReferenceData.Catalog.Resolve(CurrencyReferenceData.Usd, CurrencyReferenceData.InitialVersion);
    private static readonly string[] ClearingTables =
    [
        "clearing_batches", "clearing_batch_entries", "payout_instructions",
        "clearing_batch_exports", "clearing_batch_confirmations", "payout_confirmations",
    ];
    private static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new MoneyJsonConverter(CurrencyReferenceData.Catalog, CurrencyReferenceData.InitialVersion));

        return options;
    }

    private static DateTimeOffset Today => new(Present.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static Money Usd(long minorUnits) => Money.FromMinorUnits(minorUnits, CurrencyReferenceData.Usd);

    private sealed class InjectedFailure : Exception;

    private sealed class FailingAudit(IAuditTrail inner, string operation) : IAuditTrail
    {
        public Task RecordAsync(string op, string targetId, CancellationToken cancellationToken) =>
            op == operation ? throw new InjectedFailure() : inner.RecordAsync(op, targetId, cancellationToken);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---------------------------------------------------------------- cut

    [Fact]
    public async Task The_cut_reserves_only_seller_and_affiliate_payables_and_posts_nothing()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "T-1", Today.AddDays(-1));

        var batch = await PrepareAsync(host, Yesterday);

        Assert.Equal("Prepared", batch.State);
        Assert.Equal(Date(Yesterday), batch.BusinessDate);
        Assert.Equal("USD", batch.Currency);
        Assert.Equal("191.95", batch.Total.Amount);
        Assert.Equal(1, batch.EntryCount);
        Assert.Equal(2, batch.Instructions.Count);
        Assert.Equal(18445, batch.Instructions.Single(item => item.PartyId == SyntheticSeed.SellerOne.Id.Value).Amount.MinorUnits);
        Assert.Equal(750, batch.Instructions.Single(item => item.PartyId == SyntheticSeed.Affiliate.Id.Value).Amount.MinorUnits);
        Assert.DoesNotContain(batch.Instructions, item => item.PartyId == SyntheticSeed.Processor.Id.Value
            || item.PartyId == SyntheticSeed.TaxAuthority.Id.Value || item.PartyId == SyntheticSeed.SellerTwo.Id.Value);
        Assert.All(batch.Instructions, item => Assert.Matches("^PO-[0-9]{8}-USD-[0-9]{6}$", item.Reference));
        Assert.Equal(await host.ScalarAsync<long>("SELECT max(sequence) FROM ledger.journal_entries"), batch.CutSequence);

        // The cut posts nothing and debits no payable.
        Assert.Equal(1L, await CountAsync(host, "journal_entries"));
        Assert.Equal(18445m, await CreditBalanceAsync(host, PayableCode));
        Assert.Equal(750m, await CreditBalanceAsync(host, AffiliatePayable));
        Assert.Equal(755m, await CreditBalanceAsync(host, ProcessorPayable));
        Assert.Equal("Settlement", await host.ScalarAsync<string>("SELECT kind FROM ledger.clearing_batch_entries"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation = 'clearing.batch.prepared'"));

        // A retry returns the stored batch and changes nothing.
        var again = await PrepareAsync(host, Yesterday, HttpStatusCode.OK);

        Assert.Equal(batch.Id, again.Id);
        Assert.Equal(1L, await CountAsync(host, "clearing_batches"));
        Assert.Equal(1L, await CountAsync(host, "clearing_batch_entries"));
        Assert.Equal(2L, await CountAsync(host, "payout_instructions"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation = 'clearing.batch.prepared'"));
    }

    [Fact]
    public async Task Credits_posted_after_the_cut_wait_for_the_next_cut_and_one_batch_stays_unconfirmed()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var firstTrade = await SubmitAsync(host, profile, "T-1", Today.AddDays(-1));
        var first = await PrepareAsync(host, Yesterday);
        var secondTrade = await SubmitAsync(host, profile, "T-2", Today.AddDays(-1), "command-2");

        Assert.True(await host.ScalarAsync<bool>($"""
            SELECT (SELECT sequence FROM ledger.journal_entries WHERE id = '{firstTrade.JournalEntryId}')
                 < (SELECT sequence FROM ledger.journal_entries WHERE id = '{secondTrade.JournalEntryId}')
            """));
        Assert.True(secondTrade.JournalEntryId != Guid.Empty && first.CutSequence
            < await host.ScalarAsync<long>($"SELECT sequence FROM ledger.journal_entries WHERE id = '{secondTrade.JournalEntryId}'"));

        using (var blocked = await SendAsync(host, "finance", HttpMethod.Post, "/v1/clearing-batches", new PrepareBatchRequest(Present, "USD")))
        {
            await ErrorAsync(blocked, HttpStatusCode.Conflict, "UNCONFIRMED_BATCH_EXISTS");
        }

        _ = await ExportAsync(host, first.Id);
        var statement = await SimulateAsync(host, first.Id);
        using (var confirmed = await ConfirmAsync(host, "finance", first.Id, statement))
        {
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        }

        var second = await PrepareAsync(host, Present);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(1, second.EntryCount);
        Assert.Equal("191.95", second.Total.Amount);
        Assert.Equal(2L, await CountAsync(host, "clearing_batch_entries"));
    }

    [Fact]
    public async Task Overdue_unprocessed_entries_and_escrow_releases_are_included_exactly_once()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-DUE", Today.AddDays(-30));
        await ReleaseAsync(host, Assert.Single(await HoldsAsync(host, settlement.Id)).Id);

        var batch = await PrepareAsync(host, Yesterday);

        Assert.Equal(2, batch.EntryCount);
        Assert.Equal("212.45", batch.Total.Amount);
        Assert.Equal("EscrowRelease,Settlement", await host.ScalarAsync<string>(
            "SELECT string_agg(kind, ',' ORDER BY kind) FROM ledger.clearing_batch_entries"));
        Assert.Equal(0m, await CreditBalanceAsync(host, EscrowCode));
        Assert.Equal(20495m, await CreditBalanceAsync(host, PayableCode));
    }

    [Fact]
    public async Task A_refund_before_the_cut_nets_to_no_payout_and_a_refund_after_the_cut_is_rejected()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var refunded = await SubmitAsync(host, profile, "T-REFUNDED", Today.AddDays(-1));
        using (var refund = await SendAsync(host, "integrator", HttpMethod.Post, $"/v1/settlements/{refunded.Id}/refund",
            new RefundRequest(Usd(25000)), "refund-1"))
        {
            Assert.Equal(HttpStatusCode.Created, refund.StatusCode);
        }

        var kept = await SubmitAsync(host, profile, "T-KEPT", Today.AddDays(-1), "command-2");
        var batch = await PrepareAsync(host, Yesterday);

        Assert.Equal(3, batch.EntryCount);
        Assert.Equal("191.95", batch.Total.Amount);
        Assert.Equal("Refund,Settlement,Settlement", await host.ScalarAsync<string>(
            "SELECT string_agg(kind, ',' ORDER BY kind) FROM ledger.clearing_batch_entries"));

        using var late = await SendAsync(host, "integrator", HttpMethod.Post, $"/v1/settlements/{kept.Id}/refund",
            new RefundRequest(Usd(25000)), "refund-2");
        await ErrorAsync(late, HttpStatusCode.Conflict, "SETTLEMENT_IN_BATCH");
    }

    [Fact]
    public async Task A_batch_of_only_a_refunded_trade_has_no_instructions_and_confirms_without_posting()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-REFUNDED", Today.AddDays(-1));
        using (var refund = await SendAsync(host, "integrator", HttpMethod.Post, $"/v1/settlements/{settlement.Id}/refund",
            new RefundRequest(Usd(25000)), "refund-1"))
        {
            Assert.Equal(HttpStatusCode.Created, refund.StatusCode);
        }

        var batch = await PrepareAsync(host, Yesterday);

        Assert.Equal(2, batch.EntryCount);
        Assert.Empty(batch.Instructions);
        Assert.Equal("0.00", batch.Total.Amount);

        var exported = await ExportAsync(host, batch.Id);
        var (bytes, _) = await DownloadAsync(host, batch.Id);

        Assert.Equal(0, exported.Export!.RecordCount);
        Assert.Equal(CsvHeader, Encoding.UTF8.GetString(bytes));

        using var confirmed = await ConfirmAsync(host, "finance", batch.Id, await SimulateAsync(host, batch.Id));

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("Confirmed", (await ReadAsync(host, batch.Id)).State);
        Assert.Equal(2L, await CountAsync(host, "journal_entries"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Competing_refund_and_cut_cannot_both_consume_the_settlement(bool refundFirst)
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var settlement = await SubmitAsync(host, profile, "T-RACE", Today.AddDays(-1));
        _ = await host.TokenAsync("finance");
        _ = await host.TokenAsync("integrator");

        await using var connection = new NpgsqlConnection(host.AdminConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var holdLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(4993451089857960241)", connection, transaction))
            await holdLock.ExecuteNonQueryAsync();

        Task<HttpResponseMessage> Refund() => SendAsync(host, "integrator", HttpMethod.Post,
            $"/v1/settlements/{settlement.Id}/refund", new RefundRequest(Usd(25000)), "race-refund");
        Task<HttpResponseMessage> Cut() => SendAsync(host, "finance", HttpMethod.Post,
            "/v1/clearing-batches", new PrepareBatchRequest(Yesterday, "USD"));
        async Task WaitForContenders(int count)
        {
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (await host.ScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event = 'advisory'") < count)
            {
                Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(5), "The HTTP commands did not reach the monetary lock.");
                await Task.Delay(20);
            }
        }

        var first = refundFirst ? Refund() : Cut();
        await WaitForContenders(1);
        var second = refundFirst ? Cut() : Refund();
        await WaitForContenders(2);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await transaction.CommitAsync();

        using var firstResponse = await first;
        using var secondResponse = await second;
        var refundResponse = refundFirst ? firstResponse : secondResponse;
        var cutResponse = refundFirst ? secondResponse : firstResponse;
        Assert.Equal(HttpStatusCode.Created, cutResponse.StatusCode);
        var batch = (await cutResponse.Content.ReadFromJsonAsync<ClearingBatchResponse>(Json))!;
        if (refundFirst)
        {
            Assert.Equal(HttpStatusCode.Created, refundResponse.StatusCode);
            Assert.Empty(batch.Instructions);
            Assert.Equal("0.00", batch.Total.Amount);
            Assert.Equal(2, batch.EntryCount);
            Assert.Equal(2L, await CountAsync(host, "journal_entries"));
        }
        else
        {
            await ErrorAsync(refundResponse, HttpStatusCode.Conflict, "SETTLEMENT_IN_BATCH");
            Assert.Equal("191.95", batch.Total.Amount);
            Assert.Equal(0L, await CountAsync(host, "settlement_refunds"));
            Assert.Equal(1L, await CountAsync(host, "journal_entries"));
        }
    }

    [Fact]
    public async Task Cut_input_is_validated_and_an_empty_cut_creates_nothing()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);

        using (var none = await SendAsync(host, "finance", HttpMethod.Post, "/v1/clearing-batches", new PrepareBatchRequest(Yesterday, "USD")))
        {
            Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
        }

        using (var future = await SendAsync(host, "finance", HttpMethod.Post, "/v1/clearing-batches", new PrepareBatchRequest(Present.AddDays(2), "USD")))
        {
            await ErrorAsync(future, HttpStatusCode.BadRequest, "INVALID_BUSINESS_DATE");
        }

        foreach (var currency in new string?[] { "EUR", "usd", null })
        {
            using var response = await SendAsync(host, "finance", HttpMethod.Post, "/v1/clearing-batches", new PrepareBatchRequest(Yesterday, currency));
            await ErrorAsync(response, HttpStatusCode.BadRequest, "UNSUPPORTED_CURRENCY");
        }

        using (var unknown = await SendAsync(host, "finance", HttpMethod.Get, $"/v1/clearing-batches/{Guid.NewGuid()}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        await AssertNoClearingRowsAsync(host);
    }

    [Fact]
    public async Task A_negative_payable_fails_verification_and_commits_nothing()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var amount = Money.FromMinorUnits(100, CurrencyReferenceData.Usd);
        var payable = SyntheticSeed.Accounts.Single(account => account.Code.Value == PayableCode);
        var clearing = SyntheticSeed.Accounts.Single(account => account.Code.Value == "CLEARING.BANK.USD");
        await using var provider = new ServiceCollection().AddInfrastructure(host.RuntimeConnection).BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<el.Application.Ledger.JournalPostingService>().PostAsync(
                el.Domain.Ledger.JournalEntry.Create(new el.Domain.Ledger.JournalEntryId(Guid.NewGuid()),
                    el.Domain.Ledger.JournalReference.Parse("TEST:NEGATIVE-PAYABLE"), "Synthetic overdraft",
                    [new(payable, el.Domain.Ledger.EntrySide.Debit, amount), new(clearing, el.Domain.Ledger.EntrySide.Credit, amount)]),
                CancellationToken.None);
        }

        using var response = await SendAsync(host, "finance", HttpMethod.Post, "/v1/clearing-batches", new PrepareBatchRequest(Yesterday, "USD"));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CLEARING_VERIFICATION_FAILED", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("NegativePayable", json.RootElement.GetProperty("details")[0].GetProperty("code").GetString());
        await AssertNoClearingRowsAsync(host);
    }

    [Fact]
    public async Task A_failure_after_the_batch_was_written_rolls_everything_back_and_a_retry_succeeds()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "T-1", Today.AddDays(-1));

        await using (var provider = FaultyProvider(host, "clearing.batch.prepared"))
        await using (var scope = provider.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<InjectedFailure>(() => scope.ServiceProvider.GetRequiredService<ClearingService>()
                .PrepareAsync(Yesterday, "USD", CancellationToken.None));
        }

        await AssertNoClearingRowsAsync(host);
        Assert.Equal("191.95", (await PrepareAsync(host, Yesterday)).Total.Amount);
    }

    [Fact]
    public async Task Ten_parallel_cuts_create_one_batch()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "T-1", Today.AddDays(-1));
        _ = await host.TokenAsync("finance");

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            SendAsync(host, "finance", HttpMethod.Post, "/v1/clearing-batches", new PrepareBatchRequest(Yesterday, "USD"))));

        try
        {
            Assert.Equal(1, responses.Count(item => item.StatusCode == HttpStatusCode.Created));
            Assert.Equal(9, responses.Count(item => item.StatusCode == HttpStatusCode.OK));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Assert.Equal(1L, await CountAsync(host, "clearing_batches"));
        Assert.Equal(2L, await CountAsync(host, "payout_instructions"));
    }

    // ---------------------------------------------------------------- authorization

    [Fact]
    public async Task Clearing_routes_enforce_the_PRP_role_matrix_over_HTTP()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "T-1", Today.AddDays(-1));
        var batch = await PrepareAsync(host, Yesterday, user: "scheduler");
        _ = await ExportAsync(host, batch.Id, user: "scheduler");
        var id = batch.Id;

        foreach (var user in new[] { "integrator", "admin", "auditor", "payee-seller-1" })
        {
            foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
            {
                (HttpMethod.Post, "/v1/clearing-batches", new PrepareBatchRequest(Yesterday, "USD")),
                (HttpMethod.Get, "/v1/clearing-batches", null),
                (HttpMethod.Get, $"/v1/clearing-batches/{id}", null),
                (HttpMethod.Post, $"/v1/clearing-batches/{id}/export", null),
                (HttpMethod.Get, $"/v1/clearing-batches/{id}/export", null),
                (HttpMethod.Post, $"/v1/clearing-batches/{id}/simulate-statement", null),
                (HttpMethod.Post, $"/v1/clearing-batches/{id}/confirm-simulated", null),
            })
            {
                using var response = await SendAsync(host, user, method, path, body);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{user} {method} {path}: {response.StatusCode}");
            }
        }

        foreach (var user in new[] { "finance", "scheduler" })
        {
            foreach (var path in new[] { "/v1/clearing-batches", $"/v1/clearing-batches/{id}", $"/v1/clearing-batches/{id}/export" })
            {
                using var response = await SendAsync(host, user, HttpMethod.Get, path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        // The Scheduler can prepare and export but can never simulate or confirm bank execution.
        foreach (var path in new[] { "simulate-statement", "confirm-simulated" })
        {
            using var response = await SendAsync(host, "scheduler", HttpMethod.Post, $"/v1/clearing-batches/{id}/{path}");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var simulated = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/clearing-batches/{id}/simulate-statement");
        Assert.Equal(HttpStatusCode.OK, simulated.StatusCode);
        using var anonymous = await host.Client.GetAsync($"/v1/clearing-batches/{id}/export");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0L, await CountAsync(host, "clearing_batch_confirmations"));
    }

    // ---------------------------------------------------------------- export

    [Fact]
    public async Task Export_persists_deterministic_bytes_once_and_repeats_return_the_same_artifact_without_posting()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "T-1", Today.AddDays(-1));
        var batch = await PrepareAsync(host, Yesterday);

        using (var early = await SendAsync(host, "finance", HttpMethod.Get, $"/v1/clearing-batches/{batch.Id}/export"))
        {
            await ErrorAsync(early, HttpStatusCode.Conflict, "BATCH_NOT_EXPORTED");
        }

        var first = await ExportAsync(host, batch.Id);
        var second = await ExportAsync(host, batch.Id, HttpStatusCode.OK);
        var (bytes, sha) = await DownloadAsync(host, batch.Id);
        var (again, _) = await DownloadAsync(host, batch.Id, "scheduler");

        Assert.Equal("Exported", first.State);
        Assert.Equal(2, first.Export!.RecordCount);
        Assert.Equal("191.95", first.Export.Total.Amount);
        Assert.Equal(first.Export.GeneratedAtUtc, second.Export!.GeneratedAtUtc);
        Assert.Equal(first.Export.Sha256, second.Export.Sha256);
        Assert.Equal(bytes, again);
        Assert.Equal(first.Export.Sha256, sha);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha);

        var expected = CsvHeader + string.Concat(batch.Instructions.OrderBy(item => item.Reference, StringComparer.Ordinal).Select(item =>
            $"{item.Reference},{Date(Yesterday)},{item.PartyId:D},USD,{MoneyText.Format(item.Amount, UsdDefinition)}\r\n"));

        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));

        // Exporting posts nothing, debits no payable and releases no reservation.
        Assert.Equal(1L, await CountAsync(host, "journal_entries"));
        Assert.Equal(18445m, await CreditBalanceAsync(host, PayableCode));
        Assert.Equal(0L, await CountAsync(host, "payout_confirmations"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation = 'clearing.batch.exported'"));
    }

    [Fact]
    public async Task A_failed_export_leaves_no_artifact_and_the_batch_stays_prepared()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "T-1", Today.AddDays(-1));
        var batch = await PrepareAsync(host, Yesterday);

        await using (var provider = FaultyProvider(host, "clearing.batch.exported"))
        await using (var scope = provider.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<InjectedFailure>(() => scope.ServiceProvider.GetRequiredService<ClearingExportService>()
                .ExportAsync(batch.Id, CancellationToken.None));
        }

        Assert.Equal(0L, await CountAsync(host, "clearing_batch_exports"));
        Assert.Equal("Prepared", (await ReadAsync(host, batch.Id)).State);
        using (var download = await SendAsync(host, "finance", HttpMethod.Get, $"/v1/clearing-batches/{batch.Id}/export"))
        {
            await ErrorAsync(download, HttpStatusCode.Conflict, "BATCH_NOT_EXPORTED");
        }

        Assert.Equal("Exported", (await ExportAsync(host, batch.Id)).State);
    }

    // ---------------------------------------------------------------- confirmation

    [Fact]
    public async Task Confirmation_posts_one_payable_debit_per_instruction_consumes_reservations_and_reconciles()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "T-1", Today.AddDays(-1));
        var batch = await PrepareAsync(host, Yesterday);

        using (var early = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/clearing-batches/{batch.Id}/simulate-statement"))
        {
            await ErrorAsync(early, HttpStatusCode.Conflict, "BATCH_NOT_EXPORTED");
        }

        _ = await ExportAsync(host, batch.Id);
        var statement = await SimulateAsync(host, batch.Id);
        var lines = Encoding.UTF8.GetString(statement).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("reference,currency,amount,source", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.All(lines.Skip(1), line => Assert.EndsWith(",USD," + line.Split(',')[2] + ",SYNTHETIC", line, StringComparison.Ordinal));
        Assert.Equal(1L, await CountAsync(host, "journal_entries"));

        ClearingBatchResponse confirmed;
        using (var response = await ConfirmAsync(host, "finance", batch.Id, statement))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            confirmed = (await response.Content.ReadFromJsonAsync<ClearingBatchResponse>(Json))!;
        }

        Assert.Equal("Confirmed", confirmed.State);
        Assert.Equal(2, confirmed.Confirmation!.InstructionCount);
        Assert.Equal("191.95", confirmed.Confirmation.Total.Amount);
        Assert.Equal(3L, await CountAsync(host, "journal_entries"));
        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries WHERE reference LIKE 'PAYOUT:%'"));
        Assert.Equal(19195L, await host.ScalarAsync<long>("""
            SELECT COALESCE(sum(l.amount_minor), 0)::bigint FROM ledger.journal_lines l
              JOIN ledger.journal_entries e ON e.id = l.entry_id WHERE e.reference LIKE 'PAYOUT:%' AND l.side = 1
            """));
        Assert.Equal(19195L, await host.ScalarAsync<long>("""
            SELECT COALESCE(sum(l.amount_minor), 0)::bigint FROM ledger.journal_lines l
              JOIN ledger.journal_entries e ON e.id = l.entry_id JOIN ledger.accounts a ON a.id = l.account_id
             WHERE e.reference LIKE 'PAYOUT:%' AND l.side = 2 AND a.type = 1
            """));
        Assert.Equal(0m, await CreditBalanceAsync(host, PayableCode));
        Assert.Equal(0m, await CreditBalanceAsync(host, AffiliatePayable));
        Assert.Equal(755m, await CreditBalanceAsync(host, ProcessorPayable));
        Assert.Equal(2050m, await CreditBalanceAsync(host, EscrowCode));
        Assert.Equal(5805L, await host.ScalarAsync<long>("""
            SELECT COALESCE(sum(CASE l.side WHEN 1 THEN l.amount_minor ELSE -l.amount_minor END), 0)::bigint
              FROM ledger.journal_lines l JOIN ledger.accounts a ON a.id = l.account_id WHERE a.code = 'CLEARING.BANK.USD'
            """));
        Assert.Equal(0L, await host.ScalarAsync<long>("""
            SELECT count(*) FROM (SELECT currency FROM ledger.journal_lines GROUP BY currency
                                   HAVING sum(CASE side WHEN 1 THEN amount_minor ELSE -amount_minor END) <> 0) unbalanced
            """));
        Assert.Equal(0L, await host.ScalarAsync<long>("""
            SELECT count(*) FROM ledger.payout_instructions i
             WHERE NOT EXISTS (SELECT 1 FROM ledger.payout_confirmations c WHERE c.instruction_id = i.id)
            """));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation = 'clearing.batch.confirmed'"));

        // Repeating the same statement has no new effect; a different statement is a conflict.
        using (var repeat = await ConfirmAsync(host, "finance", batch.Id, statement))
        {
            Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        }

        using (var different = await ConfirmAsync(host, "finance", batch.Id, statement.Concat("\r\n"u8.ToArray()).ToArray()))
        {
            await ErrorAsync(different, HttpStatusCode.Conflict, "STATEMENT_CONFLICT");
        }

        Assert.Equal(3L, await CountAsync(host, "journal_entries"));
        Assert.Equal(2L, await CountAsync(host, "payout_confirmations"));

        // Nothing is left to pay out, so the next cut finds nothing.
        using var next = await SendAsync(host, "finance", HttpMethod.Post, "/v1/clearing-batches", new PrepareBatchRequest(Present, "USD"));
        Assert.Equal(HttpStatusCode.NoContent, next.StatusCode);
    }

    [Fact]
    public async Task Every_incomplete_or_mismatched_statement_is_rejected_without_changing_state()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "T-1", Today.AddDays(-1));
        var batch = await PrepareAsync(host, Yesterday);

        using (var notExported = await ConfirmAsync(host, "finance", batch.Id, Encoding.UTF8.GetBytes("reference,currency,amount,source\r\n")))
        {
            await ErrorAsync(notExported, HttpStatusCode.Conflict, "BATCH_NOT_EXPORTED");
        }

        _ = await ExportAsync(host, batch.Id);
        var text = Encoding.UTF8.GetString(await SimulateAsync(host, batch.Id));
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var variants = new (string Name, byte[] Body, string Code, HttpStatusCode Status)[]
        {
            ("amount", Bytes(text.Replace("184.45", "184.46", StringComparison.Ordinal)), "STATEMENT_MISMATCH", HttpStatusCode.Conflict),
            ("missing", Bytes(string.Join("\r\n", lines.Take(lines.Length - 1)) + "\r\n"), "STATEMENT_MISMATCH", HttpStatusCode.Conflict),
            ("unknown", Bytes(text + "PO-UNKNOWN-1,USD,1.00,SYNTHETIC\r\n"), "STATEMENT_MISMATCH", HttpStatusCode.Conflict),
            ("duplicate", Bytes(text + lines[1] + "\r\n"), "STATEMENT_MISMATCH", HttpStatusCode.Conflict),
            ("not synthetic", Bytes(text.Replace("SYNTHETIC", "REAL", StringComparison.Ordinal)), "STATEMENT_MISMATCH", HttpStatusCode.Conflict),
            ("currency", Bytes(text.Replace(",USD,", ",JPY,", StringComparison.Ordinal)), "STATEMENT_MISMATCH", HttpStatusCode.Conflict),
            ("not canonical", Bytes(text.Replace("7.50", "7.5", StringComparison.Ordinal)), "STATEMENT_MISMATCH", HttpStatusCode.Conflict),
            ("header", Bytes("ref,currency,amount,source\r\n"), "STATEMENT_INVALID", HttpStatusCode.BadRequest),
            ("empty", [], "STATEMENT_INVALID", HttpStatusCode.BadRequest),
            ("garbage", Bytes("\"open"), "STATEMENT_INVALID", HttpStatusCode.BadRequest),
        };

        foreach (var variant in variants)
        {
            using var response = await ConfirmAsync(host, "finance", batch.Id, variant.Body);

            Assert.True(response.StatusCode == variant.Status, variant.Name + ": " + response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(variant.Code, json.RootElement.GetProperty("code").GetString());
            Assert.True(json.RootElement.GetProperty("details").GetArrayLength() > 0, variant.Name);

            if (variant.Name == "amount")
            {
                Assert.Equal("AmountMismatch", json.RootElement.GetProperty("details")[0].GetProperty("code").GetString());
            }

            Assert.Equal("Exported", (await ReadAsync(host, batch.Id)).State);
            Assert.Equal(1L, await CountAsync(host, "journal_entries"));
            Assert.Equal(0L, await CountAsync(host, "clearing_batch_confirmations"));
            Assert.Equal(0L, await CountAsync(host, "payout_confirmations"));
        }

        using var wrongType = await ConfirmAsync(host, "finance", batch.Id, Bytes(text), "application/json");
        await ErrorAsync(wrongType, HttpStatusCode.BadRequest, "STATEMENT_INVALID");
    }

    [Fact]
    public async Task A_crash_during_confirmation_rolls_back_every_posting_and_a_retry_confirms_once()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "T-1", Today.AddDays(-1));
        var batch = await PrepareAsync(host, Yesterday);
        _ = await ExportAsync(host, batch.Id);
        var statement = await SimulateAsync(host, batch.Id);

        await using (var provider = FaultyProvider(host, "clearing.batch.confirmed"))
        await using (var scope = provider.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<InjectedFailure>(() => scope.ServiceProvider.GetRequiredService<ClearingConfirmationService>()
                .ConfirmAsync(batch.Id, statement, CancellationToken.None));
        }

        Assert.Equal("Exported", (await ReadAsync(host, batch.Id)).State);
        Assert.Equal(1L, await CountAsync(host, "journal_entries"));
        Assert.Equal(0L, await CountAsync(host, "clearing_batch_confirmations"));
        Assert.Equal(0L, await CountAsync(host, "payout_confirmations"));
        Assert.Equal(18445m, await CreditBalanceAsync(host, PayableCode));

        _ = await host.TokenAsync("finance");
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ConfirmAsync(host, "finance", batch.Id, statement)));

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries WHERE reference LIKE 'PAYOUT:%'"));
        Assert.Equal(1L, await CountAsync(host, "clearing_batch_confirmations"));
        Assert.Equal(2L, await CountAsync(host, "payout_confirmations"));
        Assert.Equal("Confirmed", (await ReadAsync(host, batch.Id)).State);
    }

    // ---------------------------------------------------------------- scheduler

    [Fact]
    public async Task The_scheduler_runner_prepares_the_previous_utc_day_and_a_rerun_changes_nothing()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "T-1", Today.AddDays(-1));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedClock(Today.AddDays(1).AddMinutes(5)));
        services.AddLogging();
        services.AddInfrastructure(host.RuntimeConnection);
        services.AddSingleton<ClearingScheduleRunner>();
        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<ClearingScheduleRunner>();

        Assert.Equal(1, await runner.RunAsync(CancellationToken.None));
        Assert.Equal(0, await runner.RunAsync(CancellationToken.None));

        Assert.Equal(1L, await CountAsync(host, "clearing_batches"));
        Assert.Equal(Date(Present), await host.ScalarAsync<string>("SELECT business_date::text FROM ledger.clearing_batches"));
        Assert.Equal(1L, await host.ScalarAsync<long>(
            "SELECT count(*) FROM ledger.audit_events WHERE operation = 'clearing.batch.prepared'"));
    }

    // ---------------------------------------------------------------- helpers

    private static ServiceProvider FaultyProvider(IdentityApiHost host, string failingOperation)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(host.RuntimeConnection);

        var original = services.Last(item => item.ServiceType == typeof(IAuditTrail));
        var factory = original.ImplementationFactory
            ?? throw new InvalidOperationException("IAuditTrail must be registered with a factory.");
        services.Remove(original);
        services.AddScoped<IAuditTrail>(provider =>
            new FailingAudit((IAuditTrail)factory(provider), failingOperation));

        return services.BuildServiceProvider();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        IdentityApiHost host, string user, HttpMethod method, string path, object? body = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await host.TokenAsync(user));

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        return await host.Client.SendAsync(request);
    }

    private static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private static async Task<ClearingBatchResponse> PrepareAsync(
        IdentityApiHost host, DateOnly date, HttpStatusCode expected = HttpStatusCode.Created, string user = "finance")
    {
        using var response = await SendAsync(host, user, HttpMethod.Post, "/v1/clearing-batches", new PrepareBatchRequest(date, "USD"));
        Assert.Equal(expected, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ClearingBatchResponse>(Json))!;
    }

    private static async Task<ClearingBatchResponse> ReadAsync(IdentityApiHost host, Guid id)
    {
        using var response = await SendAsync(host, "finance", HttpMethod.Get, $"/v1/clearing-batches/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ClearingBatchResponse>(Json))!;
    }

    private static async Task<ClearingBatchResponse> ExportAsync(
        IdentityApiHost host, Guid id, HttpStatusCode expected = HttpStatusCode.Created, string user = "finance")
    {
        using var response = await SendAsync(host, user, HttpMethod.Post, $"/v1/clearing-batches/{id}/export");
        Assert.Equal(expected, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ClearingBatchResponse>(Json))!;
    }

    private static async Task<(byte[] Bytes, string Sha256)> DownloadAsync(IdentityApiHost host, Guid id, string user = "finance")
    {
        using var response = await SendAsync(host, user, HttpMethod.Get, $"/v1/clearing-batches/{id}/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/csv", response.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);

        return (await response.Content.ReadAsByteArrayAsync(), response.Headers.GetValues("X-Content-SHA256").Single());
    }

    private static async Task<byte[]> SimulateAsync(IdentityApiHost host, Guid id)
    {
        using var response = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/clearing-batches/{id}/simulate-statement");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task<HttpResponseMessage> ConfirmAsync(
        IdentityApiHost host, string user, Guid id, byte[] statement, string contentType = "text/csv")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/clearing-batches/{id}/confirm-simulated")
        {
            Content = new ByteArrayContent(statement),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await host.TokenAsync(user));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        return await host.Client.SendAsync(request);
    }

    private static Task<long> CountAsync(IdentityApiHost host, string table) =>
        host.ScalarAsync<long>($"SELECT count(*) FROM ledger.{table}");

    private static async Task<decimal> CreditBalanceAsync(IdentityApiHost host, string accountCode) =>
        await host.ScalarAsync<long>($"""
            SELECT COALESCE(sum(CASE l.side WHEN 2 THEN l.amount_minor ELSE -l.amount_minor END), 0)::bigint
              FROM ledger.journal_lines l JOIN ledger.accounts a ON a.id = l.account_id
             WHERE a.code = '{accountCode}'
            """);

    private static async Task AssertNoClearingRowsAsync(IdentityApiHost host)
    {
        foreach (var table in ClearingTables)
        {
            Assert.Equal(0L, await CountAsync(host, table));
        }
    }

    private static async Task AssertSqlStateAsync(
    IdentityApiHost host, string sql, string sqlState, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? host.RuntimeConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(sqlState, error.SqlState);
    }

    private sealed record HoldRef(Guid Id);

    private static async Task<IReadOnlyList<HoldRef>> HoldsAsync(IdentityApiHost host, Guid settlementId)
    {
        var ids = await host.ScalarAsync<string>(
            $"SELECT string_agg(id::text, ',') FROM ledger.escrow_holds WHERE settlement_id = '{settlementId}'");

        return ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => new HoldRef(Guid.Parse(id))).ToArray();
    }

    private static async Task ReleaseAsync(IdentityApiHost host, Guid holdId)
    {
        using var response = await SendAsync(host, "scheduler", HttpMethod.Post, "/v1/escrow/releases");
        Assert.True(response.IsSuccessStatusCode, "release run: " + response.StatusCode);
        Assert.Equal(1L, await host.ScalarAsync<long>(
            $"SELECT count(*) FROM ledger.escrow_hold_transitions WHERE hold_id = '{holdId}' AND state = 'Released'"));
    }
}
