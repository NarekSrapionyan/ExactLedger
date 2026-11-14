using el.Domain.Common;
using el.Domain.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace el.Infrastructure.Persistence.Configurations;

internal sealed class JournalLineRecordConfiguration : IEntityTypeConfiguration<JournalLineRecord>
{
    public void Configure(EntityTypeBuilder<JournalLineRecord> builder)
    {
        builder.ToTable("journal_lines", table =>
        {
            table.HasCheckConstraint("ck_journal_lines_line_number", "line_number >= 1");
            table.HasCheckConstraint("ck_journal_lines_side", Sql.InList("side", Enum.GetValues<EntrySide>()));

            // Individual amounts are positive BIGINT minor units within the Money range.
            table.HasCheckConstraint(
                "ck_journal_lines_amount",
                FormattableString.Invariant($"amount_minor BETWEEN 1 AND {Money.MaxMinorUnits}"));
        });

        builder.HasKey(line => new { line.EntryId, line.LineNumber }).HasName("pk_journal_lines");
        builder.Property(line => line.EntryId)
            .HasColumnName("entry_id")
            .HasConversion(ValueConverters.JournalEntryIdToGuid)
            .ValueGeneratedNever();
        builder.Property(line => line.LineNumber).HasColumnName("line_number").ValueGeneratedNever();
        builder.Property(line => line.AccountId)
            .HasColumnName("account_id")
            .HasConversion(ValueConverters.AccountIdToGuid);
        builder.Property(line => line.Side).HasColumnName("side");
        builder.Property(line => line.AmountMinor).HasColumnName("amount_minor").HasColumnType("bigint");
        builder.Property(line => line.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .HasConversion(ValueConverters.CurrencyCodeToText);

        // The line's currency must equal both its entry's and its account's currency.
        builder.HasOne<JournalEntryRecord>()
            .WithMany()
            .HasForeignKey(line => new { line.EntryId, line.Currency })
            .HasPrincipalKey(entry => new { entry.Id, entry.Currency })
            .HasConstraintName("fk_journal_lines_entry")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(line => new { line.AccountId, line.Currency })
            .HasPrincipalKey(account => new { account.Id, account.Currency })
            .HasConstraintName("fk_journal_lines_account")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(line => new { line.AccountId, line.Currency }).HasDatabaseName("ix_journal_lines_account");
    }
}
