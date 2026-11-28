using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace el.Infrastructure.Persistence.Configurations;

internal static class SettlementColumns
{
    internal static void Map<T>(EntityTypeBuilder<T> b) where T : class
    {
        foreach (var property in typeof(T).GetProperties())
            b.Property(property.Name).HasColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }
}
internal sealed class SettlementConfiguration : IEntityTypeConfiguration<SettlementRecord>
{
    public void Configure(EntityTypeBuilder<SettlementRecord> b)
    {
        b.ToTable("settlements", table => table.HasCheckConstraint("ck_settlement_values", "gross_minor BETWEEN 1 AND 99999999999 AND length(client_id) BETWEEN 1 AND 100 AND length(trade_id) BETWEEN 1 AND 100 AND result_hash ~ '^[0-9a-f]{64}$'"));
        b.HasKey(row => row.Id); b.Property(row => row.Id).ValueGeneratedNever(); SettlementColumns.Map(b);
        b.Property<long>("CreationTransaction").HasColumnName("creation_transaction").HasDefaultValueSql("txid_current()");
        b.HasIndex(row => new { row.ClientId, row.TradeId }).IsUnique();
        b.HasIndex(row => row.JournalEntryId).IsUnique(); b.HasAlternateKey(row => new { row.Id, row.Currency });
        b.Property(row => row.InputJson).HasColumnType("jsonb"); b.Property(row => row.SnapshotJson).HasColumnType("jsonb"); b.Property(row => row.ResultJson).HasColumnType("jsonb");
    }
}
internal sealed class SettlementAllocationConfiguration : IEntityTypeConfiguration<SettlementAllocationRecord>
{
    public void Configure(EntityTypeBuilder<SettlementAllocationRecord> b)
    {
        b.ToTable("settlement_allocations", table => table.HasCheckConstraint("ck_settlement_allocation", "ordinal >= 0 AND amount_minor BETWEEN 0 AND 99999999999 AND component IN ('Regional','Processor','Commission','Affiliate','Payable','Escrow')"));
        b.HasKey(row => new { row.SettlementId, row.Ordinal }); SettlementColumns.Map(b);
        b.HasOne<SettlementRecord>().WithMany().HasForeignKey(row => new { row.SettlementId, row.Currency }).HasPrincipalKey(row => new { row.Id, row.Currency }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(row => row.PartyId);
    }
}
internal sealed class EscrowHoldConfiguration : IEntityTypeConfiguration<EscrowHoldRecord>
{
    public void Configure(EntityTypeBuilder<EscrowHoldRecord> b)
    {
        b.ToTable("escrow_holds", table => table.HasCheckConstraint("ck_initial_escrow_hold", "original_held_minor BETWEEN 1 AND 99999999999 AND released_minor = 0 AND state = 'Held' AND isfinite(due_at_utc)"));
        b.HasKey(row => row.Id); b.Property(row => row.Id).ValueGeneratedNever(); SettlementColumns.Map(b);
        b.HasOne<SettlementAllocationRecord>().WithMany().HasForeignKey(row => new { row.SettlementId, row.AllocationOrdinal }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(row => new { row.SettlementId, row.AllocationOrdinal }).IsUnique(); b.HasIndex(row => row.DueAtUtc);
    }
}
internal sealed class SettlementCommandConfiguration : IEntityTypeConfiguration<SettlementCommandRecord>
{
    public void Configure(EntityTypeBuilder<SettlementCommandRecord> b)
    {
        b.ToTable("idempotency_records", table => table.HasCheckConstraint("ck_settlement_command", "operation = 'settlement.create' AND length(key) BETWEEN 1 AND 128 AND request_hash ~ '^[0-9a-f]{64}$'"));
        b.HasKey(row => new { row.ClientId, row.Operation, row.Key }); SettlementColumns.Map(b);
        b.HasOne<SettlementRecord>().WithMany().HasForeignKey(row => row.SettlementId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class EscrowHoldTransitionConfiguration : IEntityTypeConfiguration<EscrowHoldTransitionRecord>
{
    public void Configure(EntityTypeBuilder<EscrowHoldTransitionRecord> b)
    {
        b.ToTable("escrow_hold_transitions", table => table.HasCheckConstraint("ck_escrow_hold_transition",
            "state IN ('Released','Refunded') AND amount_minor BETWEEN 1 AND 99999999999 AND isfinite(occurred_at_utc)"));
        // One transition per hold: a hold is released or refunded at most once.
        b.HasKey(row => row.HoldId); b.Property(row => row.HoldId).ValueGeneratedNever(); SettlementColumns.Map(b);
        b.HasOne<EscrowHoldRecord>().WithMany().HasForeignKey(row => row.HoldId).OnDelete(DeleteBehavior.Restrict);
        // Each release has its own journal entry; refunds (BE-012.2) may share one reversal entry.
        b.HasIndex(row => row.JournalEntryId).IsUnique().HasFilter("state = 'Released'").HasDatabaseName("ux_escrow_release_journal");
    }
}
internal sealed class SettlementRefundConfiguration : IEntityTypeConfiguration<SettlementRefundRecord>
{
    public void Configure(EntityTypeBuilder<SettlementRefundRecord> b)
    {
        b.ToTable("settlement_refunds", table => table.HasCheckConstraint("ck_settlement_refund",
            "amount_minor BETWEEN 1 AND 99999999999 AND length(client_id) BETWEEN 1 AND 100 AND length(idempotency_key) BETWEEN 1 AND 128 " +
            "AND request_hash ~ '^[0-9a-f]{64}$' AND isfinite(refunded_at_utc)"));
        // One refund per settlement.
        b.HasKey(row => row.SettlementId); b.Property(row => row.SettlementId).ValueGeneratedNever(); SettlementColumns.Map(b);
        b.Property(row => row.Reason).HasMaxLength(500);
        b.HasOne<SettlementRecord>().WithMany().HasForeignKey(row => new { row.SettlementId, row.Currency })
            .HasPrincipalKey(row => new { row.Id, row.Currency }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(row => new { row.ClientId, row.IdempotencyKey }).IsUnique().HasDatabaseName("ux_settlement_refund_key");
        b.HasIndex(row => row.JournalEntryId).IsUnique().HasDatabaseName("ux_settlement_refund_journal");
    }
}
