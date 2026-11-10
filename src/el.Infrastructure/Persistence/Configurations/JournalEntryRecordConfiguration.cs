using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace el.Infrastructure.Persistence.Configurations;

internal sealed class JournalEntryRecordConfiguration : IEntityTypeConfiguration<JournalEntryRecord>
{
    public void Configure(EntityTypeBuilder<JournalEntryRecord> builder)
    {
        builder.ToTable("journal_entries", table =>
        {
            table.HasCheckConstraint("ck_journal_entries_reference", "reference ~ '^[A-Z0-9.:_-]{1,100}$'");
            table.HasCheckConstraint("ck_journal_entries_description", "btrim(description) <> ''");
            table.HasCheckConstraint("ck_journal_entries_reversal", "reversal_of IS NULL OR reversal_of <> id");
        });

        builder.HasKey(entry => entry.Id).HasName("pk_journal_entries");
        builder.Property(entry => entry.Id)
            .HasColumnName("id")
            .HasConversion(ValueConverters.JournalEntryIdToGuid)
            .ValueGeneratedNever();
        builder.Property(entry => entry.Reference)
            .HasColumnName("reference")
            .HasMaxLength(Domain.Ledger.JournalReference.MaxLength)
            .HasConversion(ValueConverters.JournalReferenceToText);
        builder.Property(entry => entry.Description)
            .HasColumnName("description")
            .HasMaxLength(Domain.Ledger.JournalEntry.MaxDescriptionLength);
        builder.Property(entry => entry.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .HasConversion(ValueConverters.CurrencyCodeToText);
        builder.Property(entry => entry.ReversalOf)
            .HasColumnName("reversal_of")
            .HasConversion(ValueConverters.JournalEntryIdToGuid);
        builder.Property(entry => entry.Finalized).HasColumnName("finalized");

        // Lines and reversals reference (id, currency), so their currency must match the entry's.
        builder.HasAlternateKey(entry => new { entry.Id, entry.Currency }).HasName("ak_journal_entries_id_currency");

        builder.HasIndex(entry => entry.Reference).IsUnique().HasDatabaseName("ux_journal_entries_reference");

        // An entry can be reversed at most once.
        builder.HasIndex(entry => entry.ReversalOf)
            .IsUnique()
            .HasFilter("reversal_of IS NOT NULL")
            .HasDatabaseName("ux_journal_entries_reversal_of");

        builder.HasOne<KnownCurrency>()
            .WithMany()
            .HasForeignKey(entry => entry.Currency)
            .HasConstraintName("fk_journal_entries_currency")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<JournalEntryRecord>()
            .WithMany()
            .HasForeignKey(entry => new { entry.ReversalOf, entry.Currency })
            .HasPrincipalKey(entry => new { entry.Id, entry.Currency })
            .HasConstraintName("fk_journal_entries_reversal")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
