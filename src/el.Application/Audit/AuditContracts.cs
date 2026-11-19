namespace el.Application.Audit;

/// <summary>Scoped caller metadata set by the authenticated HTTP boundary or a trusted job.</summary>
public sealed class AuditContext(string actorId, string correlationId)
{
    public string ActorId { get; set; } = actorId;
    public string CorrelationId { get; set; } = correlationId;
}

public sealed record AuditEvent(Guid Id, string ActorId, string Operation, string TargetId,
    DateTimeOffset OccurredAtUtc, string CorrelationId);

public interface IAuditTrail
{
    /// <summary>Adds metadata to the current transaction. No credentials or arbitrary payloads are accepted.</summary>
    Task RecordAsync(string operation, string targetId, CancellationToken cancellationToken);
}

public interface IAuditQueries
{
    Task<IReadOnlyList<AuditEvent>> ReadAsync(int offset, int limit, CancellationToken cancellationToken);
}
