using el.Application.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace el.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(row => row.ActorId).HasColumnName("actor_id").HasMaxLength(100);
        builder.Property(row => row.Operation).HasColumnName("operation").HasMaxLength(100);
        builder.Property(row => row.TargetId).HasColumnName("target_id").HasMaxLength(200);
        builder.Property(row => row.OccurredAtUtc).HasColumnName("occurred_at_utc");
        builder.Property(row => row.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
        builder.HasIndex(row => new { row.OccurredAtUtc, row.Id });
    }
}
