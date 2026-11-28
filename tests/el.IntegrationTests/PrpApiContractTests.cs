using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using el.Api.Escrow;
using el.Api.Ledger;
using el.Api.Settlements;
using el.Application.Settlements;
using el.Domain.Settlements;
using el.Infrastructure;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using static el.IntegrationTests.EscrowApiTests;

namespace el.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class PrpApiContractTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task Prp_payee_and_escrow_routes_match_existing_workflows_and_keep_ownership()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var first = await SubmitAsync(host, profile, "PRP-1", Today.AddDays(-10));
        var trade = first.Input with { TradeId = "PRP-2", Beneficiaries = [new("seller", SyntheticSeed.SellerTwo.Id.Value, "Remainder", 0, 0)] };
        using var posted = await SendAsync(host, "integrator", HttpMethod.Post, "/v1/settlements", trade, "prp-2");
        posted.EnsureSuccessStatusCode();
        using var own = await host.GetAsAsync("payee-seller-1", $"/v1/payees/{SyntheticSeed.SellerOne.Id.Value}/statement");
        own.EnsureSuccessStatusCode();
        using var original = await host.GetAsAsync("payee-seller-1", "/v1/reports/payee-statement");
        Assert.Equal(await original.Content.ReadAsStringAsync(), await own.Content.ReadAsStringAsync());
        using var foreign = await host.GetAsAsync("payee-seller-1", $"/v1/payees/{SyntheticSeed.SellerTwo.Id.Value}/statement");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var list = await host.GetAsAsync("finance", "/v1/escrow?currency=USD");
        var holds = (await list.Content.ReadFromJsonAsync<EscrowHoldResponse[]>(Json))!;
        Assert.Equal(2, holds.Length);
        using var payee = await host.GetAsAsync("payee-seller-1", "/v1/escrow?partyId=" + SyntheticSeed.SellerTwo.Id.Value);
        var ownHold = Assert.Single((await payee.Content.ReadFromJsonAsync<EscrowHoldResponse[]>(Json))!);
        Assert.Equal(SyntheticSeed.SellerOne.Id.Value, ownHold.PartyId);
        using var release = await SendAsync(host, "scheduler", HttpMethod.Post, $"/v1/escrow/{ownHold.Id}/release");
        release.EnsureSuccessStatusCode();
        using var retry = await SendAsync(host, "finance", HttpMethod.Post, $"/v1/escrow/holds/{ownHold.Id}/release");
        Assert.Equal(await release.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.escrow_hold_transitions"));
    }

    [Fact]
    public async Task Prp_ledger_accounts_and_trial_balance_use_posting_period_and_exact_opening_balances()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: false);
        var input = new SettlementInput("demo-client-1", "OLD", Today.AddDays(-10), "USD", 25000, "DEMO-NONE", profile.ProfileId,
            profile.CorporateCode, [new SettlementBeneficiary("seller", SyntheticSeed.SellerOne.Id.Value, "Remainder", 0, 0)]);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new SchedulerClock(Today.AddDays(-2)));
        services.AddInfrastructure(host.RuntimeConnection);
        await using (var provider = services.BuildServiceProvider())
        await using (var scope = provider.CreateAsyncScope())
            _ = await scope.ServiceProvider.GetRequiredService<SettlementService>().SubmitAsync(input, "old", CancellationToken.None);
        var trade = new TradeRequest("NEW", Today.AddDays(-10), "USD", Usd(25000), "DEMO-NONE", profile.ProfileId, profile.CorporateCode,
            [new("seller", SyntheticSeed.SellerOne.Id.Value, "Remainder", 0, 0)]);
        using var posted = await SendAsync(host, "integrator", HttpMethod.Post, "/v1/settlements", trade, "new");
        posted.EnsureSuccessStatusCode();
        var date = Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var response = await host.GetAsAsync("finance", $"/v1/ledger/accounts?currency=USD&from={date}&to={date}&limit=100");
        response.EnsureSuccessStatusCode();
        var rows = (await response.Content.ReadFromJsonAsync<AccountRegisterResponse[]>())!;
        var payable = rows.Single(row => row.Id == AccountId("PAYABLE.SELLER-1.USD"));
        Assert.Equal("212.45", payable.OpeningBalance.Amount);
        Assert.Equal("0.00", payable.DebitTotal.Amount);
        Assert.Equal("212.45", payable.CreditTotal.Amount);
        Assert.Equal("424.90", payable.ClosingBalance.Amount);
        Assert.Equal("Credit", payable.NormalSide);
        using var trial = await host.GetAsAsync("auditor", $"/v1/ledger/trial-balance?currency=USD&from={date}&to={date}");
        var balance = Assert.Single((await trial.Content.ReadFromJsonAsync<TrialBalanceResponse[]>())!);
        Assert.Equal("250.00", balance.DebitTotal.Amount);
        Assert.Equal(balance.DebitTotal, balance.CreditTotal);
        Assert.True(balance.IsBalanced);
        foreach (var user in new[] { "payee-seller-1", "integrator", "scheduler", "admin" })
        {
            using var denied = await host.GetAsAsync(user, "/v1/ledger/accounts");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var invalid = await host.GetAsAsync("finance", "/v1/ledger/accounts?from=2026-12-01&to=2026-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
