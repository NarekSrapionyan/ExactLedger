using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using el.Api.Contracts;
using el.Api.Knowledge;
using el.Api.Settlements;
using el.Application.Settlements;
using el.Domain.Common;
using el.Domain.Ledger;
using el.Domain.Settlements;
using el.Infrastructure;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace el.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class SettlementApiTests(PostgreSqlFixture database)
{
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new MoneyJsonConverter(CurrencyReferenceData.Catalog, 1)); return options;
    }
    private static Money Amount(long minor, string currency = "USD") => Money.FromMinorUnits(minor, CurrencyCode.Parse(currency));
    private static async Task<HttpResponseMessage> SendAsync(IdentityApiHost host, string user, HttpMethod method,
        string path, object? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await host.TokenAsync(user));
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return await host.Client.SendAsync(request);
    }
    private static async Task<TradeRequest> SetupAsync(IdentityApiHost host, string currency = "USD", bool regional = false)
    {
        var gross = currency == "JPY" ? 250 : currency == "KWD" ? 250000 : 25000;
        var fixedFee = currency == "JPY" ? 0 : currency == "KWD" ? 300 : 30;
        var commission = currency == "JPY" ? 30 : currency == "KWD" ? 30000 : 3000;
        var processor = currency == "JPY" ? 7 : currency == "KWD" ? 7550 : 755;
        var share = currency == "JPY" ? 205 : currency == "KWD" ? 204950 : 20495;
        var held = currency == "JPY" ? 20 : currency == "KWD" ? 20495 : 2050;
        Guid Account(string family) => SyntheticSeed.Accounts.Single(account => account.Code.Value == family + "." + currency).Id.Value;
        var profile = new ProfileRequest(Guid.NewGuid(), 1, "demo-client-1", "WORKED-" + currency, currency,
            12_000_000, 2_900_000, Amount(fixedFee, currency), null, null, "HalfEven", 10_000_000, 7,
            Account("REVENUE.COMMISSION"), Account("PAYABLE.PROCESSOR"), From, From.AddYears(1), "Synthetic PRP worked example", true,
            [new("Independent expected components", Amount(gross,currency), Amount(share,currency), Amount(commission,currency),
                Amount(processor,currency), Amount(held,currency), Amount(share-held,currency))]);
        using var create = await SendAsync(host, "admin", HttpMethod.Post, "/v1/fee-profiles", profile);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var saved = (await create.Content.ReadFromJsonAsync<ProfileResponse>(Json))!;
        using var publish = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/fee-profiles/{saved.Id}/publish");
        Assert.Equal(HttpStatusCode.NoContent, publish.StatusCode);
        var region = "DEMO-NONE";
        if (regional)
        {
            region = "WORKED-2";
            using var addRegion = await SendAsync(host, "admin", HttpMethod.Post, "/v1/regions", new RegionRequest(region, "Synthetic 2% region"));
            Assert.Equal(HttpStatusCode.Created, addRegion.StatusCode);
            var rule = new RuleRequest(Guid.NewGuid(), 1, region, "DEMO-LEVY", "Gross", 2_000_000, 1, Account("PAYABLE.TAX"),
                currency, "HalfEven", From, From.AddYears(1), "Synthetic 2% gross deduction, not actual tax law", true,
                [new("Independent 2% fixture", Amount(25000), Amount(500))]);
            using var add = await SendAsync(host, "admin", HttpMethod.Post, "/v1/rules", rule);
            Assert.Equal(HttpStatusCode.Created, add.StatusCode);
            var savedRule = (await add.Content.ReadFromJsonAsync<RuleResponse>(Json))!;
            using var publishRule = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/rules/{savedRule.Id}/publish");
            Assert.Equal(HttpStatusCode.NoContent, publishRule.StatusCode);
        }
        return new("T-1", From.AddMonths(5), currency, Amount(gross,currency), region, profile.ProfileId, profile.CorporateCode,
            [new("affiliate", SyntheticSeed.Affiliate.Id.Value, "Affiliate", 0, 3_000_000), new("seller", SyntheticSeed.SellerOne.Id.Value, "Remainder", 0, 0)]);
    }
    private static async Task<FullSettlementResponse> SubmitAsync(IdentityApiHost host, TradeRequest trade, string key = "command-1")
    {
        using var response = await SendAsync(host, "integrator", HttpMethod.Post, "/v1/settlements", trade, key);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<FullSettlementResponse>(Json))!;
    }

    [Theory]
    [InlineData(false, "184.45", "20.50")]
    [InlineData(true, "179.95", "20.00")]
    public async Task PRP_250_USD_examples_are_exact_through_HTTP_posting_holds_and_replay(bool regional, string payable, string escrow)
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var trade = await SetupAsync(host, regional: regional);
        var result = await SubmitAsync(host, trade);
        Assert.Equal(755, Assert.Single(result.Allocations, row => row.Component == "Processor").Amount.MinorUnits);
        Assert.Equal(3000, Assert.Single(result.Allocations, row => row.Component == "Commission").Amount.MinorUnits);
        Assert.Equal(750, Assert.Single(result.Allocations, row => row.Component == "Affiliate").Amount.MinorUnits);
        Assert.Equal(payable, MoneyText.Format(Assert.Single(result.Allocations, row => row.Component == "Payable").Amount, CurrencyReferenceData.Catalog.Resolve(CurrencyReferenceData.Usd,1)));
        Assert.Equal(escrow, MoneyText.Format(Assert.Single(result.Allocations, row => row.Component == "Escrow").Amount, CurrencyReferenceData.Catalog.Resolve(CurrencyReferenceData.Usd,1)));
        Assert.Equal(25000, result.Allocations.Sum(row => row.Amount.MinorUnits));
        Assert.Equal(regional ? 1 : 0, result.Snapshot.Rules.Count);
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries WHERE finalized"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.escrow_holds WHERE state = 'Held' AND released_minor = 0"));
        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation IN ('journal.posted','settlement.created')"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.idempotency_records"));
        Assert.Equal(0m, await host.ScalarAsync<decimal>("SELECT sum(CASE side WHEN 1 THEN amount_minor ELSE -amount_minor END) FROM ledger.journal_lines"));
        using var replay = await SendAsync(host, "auditor", HttpMethod.Get, $"/v1/settlements/{result.Id}/replay");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.Content.ReadFromJsonAsync<SettlementReplayResponse>())!.Matches);
        var retry = await SubmitAsync(host, trade with { Beneficiaries = trade.Beneficiaries.Reverse().ToArray() });
        Assert.Equal(JsonSerializer.Serialize(result,Json), JsonSerializer.Serialize(retry,Json));
    }

    [Theory]
    [InlineData("JPY", 185)]
    [InlineData("KWD", 184455)]
    public async Task Supported_currency_precision_is_preserved_without_FX(string currency, long payable)
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var result = await SubmitAsync(host, await SetupAsync(host,currency));
        Assert.Equal(payable, Assert.Single(result.Allocations, row => row.Component == "Payable").Amount.MinorUnits);
        Assert.Equal(result.Input.Gross.MinorUnits, result.Allocations.Sum(row => row.Amount.MinorUnits));
    }

    [Fact]
    public async Task Ten_parallel_identical_commands_post_once_conflicts_and_new_keys_cannot_repost_a_trade()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var trade = await SetupAsync(host); _ = await host.TokenAsync("integrator");
        var results = await Task.WhenAll(Enumerable.Range(0,10).Select(_ => SubmitAsync(host,trade)));
        Assert.Single(results.Select(result => result.Id).Distinct());
        Assert.Single(results.Select(result => JsonSerializer.Serialize(result,Json)).Distinct());
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries"));
        using var changed = await SendAsync(host,"integrator",HttpMethod.Post,"/v1/settlements",trade with { TradeId = "T-2" },"command-1");
        await ErrorAsync(changed,HttpStatusCode.Conflict,"IDEMPOTENCY_CONFLICT");
        using var newKey = await SendAsync(host,"integrator",HttpMethod.Post,"/v1/settlements",trade,"command-2");
        await ErrorAsync(newKey,HttpStatusCode.Conflict,"TRADE_ALREADY_SETTLED");
        var second = await SubmitAsync(host,trade with { TradeId = "T-2" },"command-2");
        Assert.NotEqual(results[0].Id,second.Id);
        Assert.Equal(results[0].ResultHash,second.ResultHash); // IDs, posted time and trade label do not enter result hash.
    }

    [Fact]
    public async Task Stored_ownership_and_role_rules_apply_to_real_settlement_and_trace_routes()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var trade = await SetupAsync(host); var result = await SubmitAsync(host,trade);
        foreach (var suffix in new[] { "", "/trace" })
        {
            foreach (var user in new[] { "integrator", "finance", "admin", "auditor" })
            { using var read = await SendAsync(host,user,HttpMethod.Get,$"/v1/settlements/{result.Id}{suffix}"); Assert.Equal(HttpStatusCode.OK,read.StatusCode); }
            foreach (var user in new[] { "integrator-other", "payee-seller-2" })
            { using var denied = await SendAsync(host,user,HttpMethod.Get,$"/v1/settlements/{result.Id}{suffix}?partyId={SyntheticSeed.SellerOne.Id.Value}"); Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode); }
            using var payee = await SendAsync(host,"payee-seller-1",HttpMethod.Get,$"/v1/settlements/{result.Id}{suffix}");
            Assert.Equal(HttpStatusCode.OK,payee.StatusCode);
            var text = await payee.Content.ReadAsStringAsync();
            foreach (var field in new[] { "partyId", "accountId", "clientId", "snapshot", "input", "resultHash", "numerator", "payoutReference" }) Assert.DoesNotContain('"'+field+'"',text);
            Assert.DoesNotContain(SyntheticSeed.Affiliate.Id.Value.ToString(),text);
            var own = JsonSerializer.Deserialize<PayeeSettlementResponse>(text,Json)!;
            Assert.Equal(20495,own.Allocations.Sum(row => row.Amount.MinorUnits));
        }
        using var scheduler = await SendAsync(host,"scheduler",HttpMethod.Get,$"/v1/settlements/{result.Id}");
        Assert.Equal(HttpStatusCode.Forbidden,scheduler.StatusCode);
        using var financePost = await SendAsync(host,"finance",HttpMethod.Post,"/v1/settlements",trade,"finance-key");
        Assert.Equal(HttpStatusCode.Forbidden,financePost.StatusCode);
        using var anonymous = await host.Client.GetAsync($"/v1/settlements/{result.Id}"); Assert.Equal(HttpStatusCode.Unauthorized,anonymous.StatusCode);
    }

    [Fact]
    public async Task Publishing_adjacent_configuration_changes_new_trades_but_not_stored_results_or_replay()
    {
        await using var host = await IdentityApiHost.CreateAsync(database); var trade = await SetupAsync(host);
        var original = await SubmitAsync(host,trade);
        var replacement = original.Snapshot.Profile.Content with
        {
            Version = 2, EffectiveFrom = From.AddYears(1), EffectiveTo = From.AddYears(2), CommissionRateUnits = 20_000_000,
            SourceNote = "Synthetic version 2 commission change",
            Fixtures = [new("Independent version 2 example",Amount(25000),Amount(18495),Amount(5000),Amount(755),Amount(1850),Amount(16645))],
        };
        using var create = await SendAsync(host,"admin",HttpMethod.Post,"/v1/fee-profiles",replacement);
        Assert.Equal(HttpStatusCode.Created,create.StatusCode);
        var saved = (await create.Content.ReadFromJsonAsync<ProfileResponse>(Json))!;
        using var publish = await SendAsync(host,"admin",HttpMethod.Post,$"/v1/fee-profiles/{saved.Id}/publish");
        Assert.Equal(HttpStatusCode.NoContent,publish.StatusCode);
        var next = await SubmitAsync(host,trade with { TradeId = "T-2", OccurredAt = From.AddYears(1) },"command-2");
        Assert.Equal(2,next.Snapshot.Profile.Content.Version);
        Assert.Equal(16645,Assert.Single(next.Allocations,row => row.Component == "Payable").Amount.MinorUnits);
        Assert.NotEqual(original.ResultHash,next.ResultHash);
        var retry = await SubmitAsync(host,trade);
        Assert.Equal(JsonSerializer.Serialize(original,Json),JsonSerializer.Serialize(retry,Json));
        using var replay = await SendAsync(host,"auditor",HttpMethod.Get,$"/v1/settlements/{original.Id}/replay");
        Assert.True((await replay.Content.ReadFromJsonAsync<SettlementReplayResponse>())!.Matches);
        await host.ExecuteAsync($"UPDATE ledger.parties SET status = 2 WHERE id = '{SyntheticSeed.SellerOne.Id.Value}'");
        var disabledRetry = await SubmitAsync(host,trade);
        Assert.Equal(original.Id,disabledRetry.Id); // Recorded commands survive current provisioning/configuration changes.
    }

    [Fact]
    public async Task Command_keys_and_permanent_trade_identity_are_scoped_by_the_authenticated_client()
    {
        await using var host = await IdentityApiHost.CreateAsync(database); var trade = await SetupAsync(host);
        var first = await SubmitAsync(host,trade);
        var secondProfile = first.Snapshot.Profile.Content with { ProfileId = Guid.NewGuid(), ClientId = "demo-client-2", Fixtures =
            [new("Synthetic second client",Amount(25000),Amount(20495),Amount(3000),Amount(755),Amount(2050),Amount(18445))] };
        using var create = await SendAsync(host,"admin",HttpMethod.Post,"/v1/fee-profiles",secondProfile);
        var draft = (await create.Content.ReadFromJsonAsync<ProfileResponse>(Json))!;
        using var publish = await SendAsync(host,"admin",HttpMethod.Post,$"/v1/fee-profiles/{draft.Id}/publish");
        Assert.Equal(HttpStatusCode.NoContent,publish.StatusCode);
        using var second = await SendAsync(host,"integrator-other",HttpMethod.Post,"/v1/settlements",trade with { ProfileId = secondProfile.ProfileId },"command-1");
        Assert.Equal(HttpStatusCode.Created,second.StatusCode);
        var result = (await second.Content.ReadFromJsonAsync<FullSettlementResponse>(Json))!;
        Assert.Equal("demo-client-2",result.ClientId); Assert.NotEqual(first.Id,result.Id);
        Assert.Equal(2L,await host.ScalarAsync<long>("SELECT count(*) FROM ledger.settlements WHERE trade_id = 'T-1'"));
    }

    [Fact]
    public async Task Invalid_key_parties_input_or_currency_commit_no_business_state()
    {
        await using var host = await IdentityApiHost.CreateAsync(database); var trade = await SetupAsync(host);
        using var noKey = await SendAsync(host,"integrator",HttpMethod.Post,"/v1/settlements",trade);
        await ErrorAsync(noKey,HttpStatusCode.BadRequest,"INVALID_IDEMPOTENCY_KEY");
        foreach (var invalid in new[]
        {
            trade with { Gross = Amount(0) }, trade with { Currency = "JPY" }, trade with { RegionCode = "UNKNOWN" },
            trade with { Beneficiaries = [new("seller",Guid.NewGuid(),"Remainder",0,0)] },
            trade with { Beneficiaries = [new("affiliate",SyntheticSeed.Affiliate.Id.Value,"Affiliate",0,100_000_000),trade.Beneficiaries[1]] },
        })
        { using var bad = await SendAsync(host,"integrator",HttpMethod.Post,"/v1/settlements",invalid,"invalid-key"); Assert.Equal(HttpStatusCode.BadRequest,bad.StatusCode); }
        await host.ExecuteAsync($"UPDATE ledger.parties SET status = 2 WHERE id = '{SyntheticSeed.SellerOne.Id.Value}'");
        using var inactive = await SendAsync(host,"integrator",HttpMethod.Post,"/v1/settlements",trade,"inactive-key");
        await ErrorAsync(inactive,HttpStatusCode.BadRequest,"INVALID_PARTY");
        foreach (var table in new[] { "settlements", "settlement_allocations", "escrow_holds", "journal_entries", "idempotency_records" })
            Assert.Equal(0L,await host.ScalarAsync<long>($"SELECT count(*) FROM ledger.{table}"));
    }

    [Fact]
    public async Task PostgreSQL_rejects_history_mutation_and_appends_after_commit()
    {
        await using var host = await IdentityApiHost.CreateAsync(database); _ = await SubmitAsync(host,await SetupAsync(host));
        foreach (var table in new[] { "settlements", "settlement_allocations", "escrow_holds", "idempotency_records" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync($"DELETE FROM ledger.{table}")); Assert.Equal(LedgerDatabase.HistoryImmutable,error.SqlState);
            var truncate = await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync($"TRUNCATE ledger.{table} CASCADE")); Assert.Equal(LedgerDatabase.HistoryImmutable,truncate.SqlState);
        }
        var append = await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync("""
            INSERT INTO ledger.settlement_allocations(settlement_id,ordinal,line_id,party_id,account_id,currency,component,amount_minor)
            SELECT settlement_id,999,line_id,party_id,account_id,currency,component,amount_minor FROM ledger.settlement_allocations LIMIT 1
            """)); Assert.Equal("EL301",append.SqlState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_after_journal_or_at_deferred_integrity_check_rolls_back_everything_and_does_not_poison_the_key(bool corrupt)
    {
        await using var host = await IdentityApiHost.CreateAsync(database); var trade = await SetupAsync(host);
        var services = new ServiceCollection().AddInfrastructure(host.RuntimeConnection);
        var descriptor = services.Last(item => item.ServiceType == typeof(ISettlementStore));
        services.AddScoped<ISettlementStore>(provider => new FaultStore((ISettlementStore)ActivatorUtilities.CreateInstance(provider,descriptor.ImplementationType!),
            provider.GetRequiredService<ExactLedgerDbContext>(),corrupt));
        await using var provider = services.BuildServiceProvider(); await using var scope = provider.CreateAsyncScope();
        var input = new SettlementInput("demo-client-1",trade.TradeId,trade.OccurredAt,trade.Currency,trade.Gross.MinorUnits,trade.RegionCode,trade.ProfileId,trade.CorporateCode,
            trade.Beneficiaries.Select(row => new SettlementBeneficiary(row.LineId,row.PartyId,row.Kind,row.Weight,row.RateUnits)).ToArray());
        var error = await Record.ExceptionAsync(() => scope.ServiceProvider.GetRequiredService<SettlementService>().SubmitAsync(input,"command-1",default));
        if (corrupt) Assert.Equal("EL302",Assert.IsType<PostgresException>(error).SqlState); else Assert.IsType<InjectedFailure>(error);
        foreach (var table in new[] { "settlements", "settlement_allocations", "escrow_holds", "journal_entries", "idempotency_records" })
            Assert.Equal(0L,await host.ScalarAsync<long>($"SELECT count(*) FROM ledger.{table}"));
        Assert.Equal(0L,await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation IN ('journal.posted','settlement.created')"));
        _ = await SubmitAsync(host,trade);
    }
    private sealed class InjectedFailure : Exception;
    private sealed class FaultStore(ISettlementStore inner, ExactLedgerDbContext db, bool corrupt) : ISettlementStore
    {
        public Task<StoredSettlementCommand?> FindCommandAsync(string client,string key,CancellationToken ct) => inner.FindCommandAsync(client,key,ct);
        public Task<bool> TradeExistsAsync(string client,string trade,CancellationToken ct) => inner.TradeExistsAsync(client,trade,ct);
        public Task<IReadOnlyList<Account>> AccountsAsync(string currency,CancellationToken ct) => inner.AccountsAsync(currency,ct);
        public Task EnsurePartiesActiveAsync(IReadOnlyCollection<Guid> ids,CancellationToken ct) => inner.EnsurePartiesActiveAsync(ids,ct);
        public Task<SettledTrade?> GetAsync(Guid id,CancellationToken ct) => inner.GetAsync(id,ct);
        public async Task AddAsync(SettledTrade result,string key,string hash,CancellationToken ct)
        {
            if (!corrupt) throw new InjectedFailure();
            await inner.AddAsync(result,key,hash,ct);
            db.ChangeTracker.Entries().Single(entry => entry.Metadata.GetTableName() == "settlements").Property("GrossMinor").CurrentValue = result.Input.GrossMinor+1;
        }
    }
    private static async Task ErrorAsync(HttpResponseMessage response,HttpStatusCode status,string code)
    {
        Assert.Equal(status,response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code,json.RootElement.GetProperty("code").GetString()); Assert.True(json.RootElement.TryGetProperty("correlationId",out _));
    }
}
