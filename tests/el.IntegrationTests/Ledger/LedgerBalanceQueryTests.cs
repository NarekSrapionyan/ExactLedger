using System.Numerics;
using el.Application.Ledger;
using el.Domain.Common;
using el.Domain.Ledger;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace el.IntegrationTests.Ledger;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class LedgerBalanceQueryTests
{
    private readonly PostgreSqlFixture fixture;

    public LedgerBalanceQueryTests(PostgreSqlFixture fixture) => this.fixture = fixture;

    private async Task<LedgerTestHost> CreateHostAsync() =>
        await LedgerTestHost.CreateAsync(await fixture.CreateDatabaseAsync()); // A7

    private static async Task<T> QueryAsync<T>(LedgerTestHost host, Func<ILedgerBalanceQueries, Task<T>> query)
    {
        await using var scope = host.CreateScope();

        return await query(scope.ServiceProvider.GetRequiredService<ILedgerBalanceQueries>());
    }

    private static async Task PostAsync(LedgerTestHost host, JournalEntry entry)
    {
        await using var scope = host.CreateScope();

        var postingService = scope.ServiceProvider
            .GetRequiredService<JournalPostingService>();

        await postingService.PostAsync(entry, CancellationToken.None);
    }

    [Fact]
    public async Task An_unknown_account_has_no_balance()
    {
        await using var host = await CreateHostAsync();

        Assert.Null(await QueryAsync(host, q => q.GetAccountBalanceAsync(new AccountId(Guid.NewGuid()), CancellationToken.None)));
    }

    [Fact]
    public async Task An_account_without_postings_has_a_zero_balance()
    {
        await using var host = await CreateHostAsync();
        var data = new LedgerTestData(host);

        var balance = await QueryAsync(host, async q => await q.GetAccountBalanceAsync(await data.AccountIdAsync(LedgerTestData.ClearingUsd), CancellationToken.None));

        Assert.NotNull(balance);
        Assert.Equal(BigInteger.Zero, balance.DebitTotal);
        Assert.Equal(BigInteger.Zero, balance.CreditTotal);
        Assert.Equal(CurrencyReferenceData.Usd, balance.Currency);
    }

    [Fact]
    public async Task Debit_only_and_credit_only_activity_use_the_normal_sides()
    {
        await using var host = await CreateHostAsync();
        var data = new LedgerTestData(host);
        var clearing = await data.LoadAccountAsync(LedgerTestData.ClearingUsd);
        var revenue = await data.LoadAccountAsync(LedgerTestData.RevenueUsd);
        await PostAsync(host, data.BuildEntry("BE008.BAL.1", clearing, revenue, 25000));

        var debitSide = (await QueryAsync(host, q => q.GetAccountBalanceAsync(clearing.Id, CancellationToken.None)))!;
        var creditSide = (await QueryAsync(host, q => q.GetAccountBalanceAsync(revenue.Id, CancellationToken.None)))!;

        Assert.Equal(new BigInteger(25000), debitSide.DebitTotal);
        Assert.Equal(BigInteger.Zero, debitSide.CreditTotal);
        Assert.Equal(new BigInteger(25000), debitSide.OnNormalSide(EntrySide.Debit));
        Assert.Equal(new BigInteger(-25000), creditSide.NetDebit);
        Assert.Equal(new BigInteger(25000), creditSide.OnNormalSide(EntrySide.Credit));
    }

    [Fact]
    public async Task Offsetting_postings_net_to_zero_while_both_totals_are_kept()
    {
        await using var host = await CreateHostAsync();
        var data = new LedgerTestData(host);
        var clearing = await data.LoadAccountAsync(LedgerTestData.ClearingUsd);
        var revenue = await data.LoadAccountAsync(LedgerTestData.RevenueUsd);
        await PostAsync(host, data.BuildEntry("BE008.NET.1", clearing, revenue, 700));
        await PostAsync(host, data.BuildEntry("BE008.NET.2", revenue, clearing, 700));

        var balance = (await QueryAsync(host, q => q.GetAccountBalanceAsync(clearing.Id, CancellationToken.None)))!;

        Assert.Equal(new BigInteger(700), balance.DebitTotal);
        Assert.Equal(new BigInteger(700), balance.CreditTotal);
        Assert.Equal(BigInteger.Zero, balance.NetDebit);
    }

    [Fact]
    public async Task An_account_total_can_exceed_the_money_range()
    {
        await using var host = await CreateHostAsync();
        var data = new LedgerTestData(host);
        var clearing = await data.LoadAccountAsync(LedgerTestData.ClearingUsd);
        var revenue = await data.LoadAccountAsync(LedgerTestData.RevenueUsd);

        for (var i = 1; i <= 3; i++)
        {
            await PostAsync(host, data.BuildEntry(FormattableString.Invariant($"BE008.BIG.{i}"), clearing, revenue, Money.MaxMinorUnits));
        }

        var balance = (await QueryAsync(host, q => q.GetAccountBalanceAsync(clearing.Id, CancellationToken.None)))!;

        Assert.Equal(new BigInteger(Money.MaxMinorUnits) * 3, balance.DebitTotal);
        Assert.True(balance.DebitTotal > Money.MaxMinorUnits);
    }

    [Fact]
    public async Task A_closed_account_keeps_its_history()
    {
        await using var host = await CreateHostAsync();
        var data = new LedgerTestData(host);
        var clearing = await data.LoadAccountAsync(LedgerTestData.ClearingUsd);
        var revenue = await data.LoadAccountAsync(LedgerTestData.RevenueUsd);
        await PostAsync(host, data.BuildEntry("BE008.HIST", clearing, revenue, 500));

        await data.CloseAccountAsync(LedgerTestData.RevenueUsd);

        var balance = (await QueryAsync(host, q => q.GetAccountBalanceAsync(revenue.Id, CancellationToken.None)))!;
        Assert.Equal(new BigInteger(500), balance.CreditTotal);
    }

    [Fact]
    public async Task An_empty_ledger_has_an_empty_trial_balance()
    {
        await using var host = await CreateHostAsync();

        Assert.Empty(await QueryAsync(host, q => q.GetTrialBalanceAsync(null, CancellationToken.None)));
    }

    [Fact]
    public async Task Currencies_with_equal_numeric_totals_stay_separate_and_each_balances()
    {
        await using var host = await CreateHostAsync();
        var data = new LedgerTestData(host);
        await PostAsync(host, data.BuildEntry("BE008.TB.USD", await data.LoadAccountAsync(LedgerTestData.ClearingUsd), await data.LoadAccountAsync(LedgerTestData.RevenueUsd), 500));
        await PostAsync(host, data.BuildEntry("BE008.TB.JPY", await data.LoadAccountAsync(LedgerTestData.ClearingJpy), await data.LoadAccountAsync(LedgerTestData.RevenueJpy), 500));

        var all = await QueryAsync(host, q => q.GetTrialBalanceAsync(null, CancellationToken.None));
        var onlyUsd = await QueryAsync(host, q => q.GetTrialBalanceAsync(CurrencyReferenceData.Usd, CancellationToken.None));

        Assert.Equal(2, all.Count);

        var expectedOrder = new[] { "JPY", "USD" };
        Assert.Equal(expectedOrder, all.Select(t => t.Currency.ToString()).ToArray()); // ordinal order

        Assert.All(all, t =>
        {
            Assert.True(t.IsBalanced);
            Assert.Equal(new BigInteger(500), t.DebitTotal); // not 1000: no netting or merging
        });
        Assert.Equal(CurrencyReferenceData.Usd, Assert.Single(onlyUsd).Currency);
    }

    [Fact]
    public async Task Sums_of_bigint_values_are_exact_beyond_64_bits()
    {
        await using var host = await CreateHostAsync();
        await using var scope = host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ExactLedgerDbContext>();

        var total = await context.Database.QueryExactIntegerAsync(
            $"SELECT SUM(x) FROM (VALUES (9223372036854775807::bigint), (9223372036854775807::bigint)) AS t(x)");

        Assert.Equal(new BigInteger(long.MaxValue) * 2, total);
    }
}
