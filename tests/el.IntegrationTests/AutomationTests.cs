using System.Diagnostics;
using System.Net.Http.Json;
using el.Api.Clearing;
using el.Api.Escrow;
using el.Application.Audit;
using el.Application.Settlements;
using el.Api.Settlements;
using el.Infrastructure;
using el.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static el.IntegrationTests.EscrowApiTests;

namespace el.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class AutomationTests(PostgreSqlFixture database)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_configuration_controls_both_automatic_jobs(bool enabled)
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var clock = new SchedulerClock(Today);
        await using (var factory = host.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Clearing:Schedule:Enabled"] = enabled.ToString(), ["Escrow:Schedule:Enabled"] = enabled.ToString(),
            }));
            builder.ConfigureServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); });
        }))
        {
            using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
            var jobs = factory.Services.GetServices<IHostedService>();
            Assert.Equal(enabled, factory.Services.GetRequiredService<IOptions<EscrowScheduleOptions>>().Value.Enabled);
            Assert.Equal(enabled, factory.Services.GetRequiredService<IOptions<ClearingScheduleOptions>>().Value.Enabled);
            if (enabled) await UntilAsync(() => Task.FromResult(clock.ScheduledCount == 2));
            else
            {
                await jobs.OfType<EscrowScheduleService>().Single().ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
                await jobs.OfType<ClearingScheduleService>().Single().ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation='escrow.release-run'"));
            }
        }
        Assert.Equal(0, clock.ScheduledCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hosted_escrow_job_releases_due_holds_and_retries_a_failed_transaction(bool failFirst)
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "AUTO-1", Today.AddDays(-10), "auto-1");
        var clock = new SchedulerClock(Today);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging();
        services.AddInfrastructure(host.RuntimeConnection);
        services.AddSingleton<EscrowScheduleRunner>();
        var fault = new OneFailure(failFirst);
        if (failFirst)
        {
            var original = services.Last(service => service.ServiceType == typeof(IAuditTrail));
            services.Remove(original);
            services.AddScoped<IAuditTrail>(provider => new FaultAudit((IAuditTrail)original.ImplementationFactory!(provider), fault));
        }
        await using var provider = services.BuildServiceProvider();
        using var worker = new EscrowScheduleService(provider.GetRequiredService<EscrowScheduleRunner>(), clock,
            Options.Create(new EscrowScheduleOptions()), NullLogger<EscrowScheduleService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await UntilAsync(() => Task.FromResult(clock.ScheduledCount > 0));
        if (failFirst)
        {
            Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.escrow_hold_transitions"));
            Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries"));
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        await UntilAsync(async () => await ReleasesAsync(host) == 1);
        await UntilAsync(() => Task.FromResult(clock.ScheduledCount > 0));
        _ = await SubmitAsync(host, profile, "AUTO-2", Today.AddDays(-10), "auto-2");
        clock.Advance(TimeSpan.FromMinutes(1));
        await UntilAsync(async () => await ReleasesAsync(host) == 2);
        Assert.Equal(4L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries"));
        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation='escrow.released' AND actor_id='scheduler'"));
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(0, clock.ScheduledCount);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2L, await ReleasesAsync(host));
    }

    [Fact]
    public async Task Daily_cut_releases_due_escrow_before_freezing_the_watermark()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        _ = await SubmitAsync(host, profile, "DAY-1", Today.AddDays(-10));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new SchedulerClock(Today));
        services.AddLogging();
        services.AddInfrastructure(host.RuntimeConnection);
        services.AddSingleton<EscrowScheduleRunner>();
        services.AddSingleton<ClearingScheduleRunner>();
        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<ClearingScheduleRunner>();
        Assert.Equal(1, await runner.RunAsync(CancellationToken.None));
        Assert.Equal(0, await runner.RunAsync(CancellationToken.None));
        Assert.Equal(1L, await ReleasesAsync(host));
        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.clearing_batch_entries"));
        Assert.Equal(21245m, await host.ScalarAsync<decimal>("SELECT total_minor FROM ledger.clearing_batches"));
        Assert.True(await host.ScalarAsync<bool>("SELECT cut_sequence >= (SELECT max(sequence) FROM ledger.journal_entries) FROM ledger.clearing_batches"));
    }

    [Fact]
    public async Task Bounded_due_selection_does_not_starve_eligible_holds_behind_inactive_parties()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var profile = await PublishProfileAsync(host, escrow: true);
        var first = await SubmitAsync(host, profile, "BLOCKED", Today.AddDays(-10));
        var trade = first.Input with { TradeId = "ELIGIBLE", OccurredAt = Today.AddDays(-9),
            Beneficiaries = [new("seller", el.Infrastructure.Persistence.SyntheticSeed.SellerTwo.Id.Value, "Remainder", 0, 0)] };
        using var posted = await SendAsync(host, "integrator", HttpMethod.Post, "/v1/settlements", trade, "eligible");
        posted.EnsureSuccessStatusCode();
        var second = (await posted.Content.ReadFromJsonAsync<FullSettlementResponse>(Json))!;
        var eligibleHold = Assert.Single(await HoldsAsync(host, second.Id));
        await host.ExecuteAsync($"UPDATE ledger.parties SET status=2 WHERE id='{el.Infrastructure.Persistence.SyntheticSeed.SellerOne.Id.Value}'");
        await using var provider = new ServiceCollection().AddInfrastructure(host.RuntimeConnection).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var ids = await scope.ServiceProvider.GetRequiredService<IEscrowStore>().DueHoldIdsAsync(Today, 1, CancellationToken.None);
        Assert.Equal(eligibleHold.Id, Assert.Single(ids));
    }

    private static Task<long> ReleasesAsync(IdentityApiHost host) => host.ScalarAsync<long>("SELECT count(*) FROM ledger.escrow_hold_transitions WHERE state='Released'");
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(10), "Scheduled work did not complete.");
            await Task.Delay(20);
        }
    }
    private sealed class OneFailure(bool fail) { internal bool Fail = fail; }
    private sealed class FaultAudit(IAuditTrail inner, OneFailure fault) : IAuditTrail
    {
        public Task RecordAsync(string operation, string targetId, CancellationToken ct)
        {
            if (operation == "escrow.released" && fault.Fail) { fault.Fail = false; throw new InvalidOperationException("Injected failure"); }
            return inner.RecordAsync(operation, targetId, ct);
        }
    }
}
