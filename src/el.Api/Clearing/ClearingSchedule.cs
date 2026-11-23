using System.Globalization;
using el.Application.Abstractions;
using el.Application.Audit;
using el.Application.Clearing;
using el.Domain.Common;
using Microsoft.Extensions.Options;

namespace el.Api.Clearing;

/// <summary>Clearing:Schedule:Enabled turns the daily cut on; TimeUtc (HH:mm, default 00:05) sets when it runs.</summary>
public sealed class ClearingScheduleOptions
{
    public const string SectionName = "Clearing:Schedule";

    public bool Enabled { get; init; }

    public string TimeUtc { get; init; } = "00:05";

    public bool TryGetTime(out TimeOnly time) =>
        TimeOnly.TryParseExact(TimeUtc, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}

/// <summary>Prepares the previous UTC day for every supported currency as the "scheduler" actor.</summary>
public sealed class ClearingScheduleRunner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ClearingScheduleRunner> logger)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var businessDate = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddDays(-1));
        var prepared = 0;

        foreach (var code in CurrencyReferenceData.Catalog.Definitions.Select(definition => definition.Code).Distinct())
        {
            await using var scope = scopes.CreateAsyncScope();
            var actor = scope.ServiceProvider.GetRequiredService<AuditContext>();
            actor.ActorId = "scheduler";
            actor.CorrelationId = Guid.NewGuid().ToString("N");

            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<ClearingService>().PrepareAsync(businessDate, code.Value, ct);

                if (result.Outcome == ClearingOutcome.Prepared)
                {
                    prepared++;
                }
            }
            catch (ClearingException error)
            {
                logger.LogWarning("Scheduled clearing skipped ({Code}) for {Currency} {BusinessDate}.", error.Code, code.Value, businessDate);
            }
            catch (LedgerLockTimeoutException)
            {
                logger.LogWarning("Scheduled clearing for {Currency} {BusinessDate} could not take the ledger lock.", code.Value, businessDate);
            }
        }

        return prepared;
    }
}

public sealed class ClearingScheduleService(ClearingScheduleRunner runner, TimeProvider clock,
    IOptions<ClearingScheduleOptions> options, ILogger<ClearingScheduleService> logger) : BackgroundService
{
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeOnly time)
    {
        var today = new DateTimeOffset(DateOnly.FromDateTime(now.UtcDateTime).ToDateTime(time), TimeSpan.Zero);

        return today > now ? today : today.AddDays(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Value.TryGetTime(out var time);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow();

            try
            {
                await Task.Delay(NextRun(now, time) - now, clock, stoppingToken);
                await runner.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError("Scheduled clearing run failed ({ErrorType}).", exception.GetType().Name);
            }
        }
    }
}
