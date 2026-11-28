using el.Application.Audit;
using el.Application.Settlements;
using Microsoft.Extensions.Options;

namespace el.Api.Escrow;

public sealed class EscrowScheduleOptions
{
    public const string SectionName = "Escrow:Schedule";
    public bool Enabled { get; init; } = true;
    public int IntervalSeconds { get; init; } = 60;
}

/// <summary>Runs the same atomic due-release use case as the manual Scheduler endpoint.</summary>
public sealed class EscrowScheduleRunner(IServiceScopeFactory scopes, ILogger<EscrowScheduleRunner> logger)
{
    public async Task<DueEscrowReleaseResult> RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var actor = scope.ServiceProvider.GetRequiredService<AuditContext>();
        actor.ActorId = "scheduler";
        actor.CorrelationId = Guid.NewGuid().ToString("N");
        var result = await scope.ServiceProvider.GetRequiredService<EscrowService>().ReleaseDueAsync(ct);
        logger.LogInformation("Scheduled escrow release: {Released} released, {Skipped} skipped.", result.Released.Count, result.Skipped.Count);
        return result;
    }
}

/// <summary>Releases due escrow at startup and periodically, without external HTTP calls.</summary>
public sealed class EscrowScheduleService(EscrowScheduleRunner runner, TimeProvider clock,
    IOptions<EscrowScheduleOptions> options, ILogger<EscrowScheduleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogError("Scheduled escrow release failed ({ErrorType}); retry on the next interval.", error.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.IntervalSeconds), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
