using el.Application.Audit;
using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Persistence;

internal sealed class EfAuditTrail(ExactLedgerDbContext database, AuditContext context, TimeProvider clock) : IAuditTrail, IAuditQueries
{
    public Task RecordAsync(string operation, string targetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Business audit must be recorded inside the business transaction.");

        Validate(context.ActorId, 100, nameof(context.ActorId));
        Validate(context.CorrelationId, 64, nameof(context.CorrelationId));
        Validate(operation, 100, nameof(operation));
        Validate(targetId, 200, nameof(targetId));
        database.Add(new AuditEvent(Guid.NewGuid(), context.ActorId, operation, targetId,
            clock.GetUtcNow().ToUniversalTime(), context.CorrelationId));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AuditEvent>> ReadAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        return await database.Set<AuditEvent>().AsNoTracking().OrderByDescending(row => row.OccurredAtUtc)
            .ThenByDescending(row => row.Id).Skip(offset).Take(limit).ToListAsync(cancellationToken);
    }

    private static void Validate(string value, int maximum, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > maximum) throw new ArgumentOutOfRangeException(name);
    }
}
