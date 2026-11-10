using el.Domain.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace el.Infrastructure.Persistence.Configurations;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        var partyOwnedTypes = Enum.GetValues<AccountType>().Where(AccountTypes.IsPartyOwned);

        builder.ToTable("accounts", table =>
        {
            table.HasCheckConstraint("ck_accounts_code", "code ~ '^[A-Z0-9._-]{1,64}$'");
            table.HasCheckConstraint("ck_accounts_type", Sql.InList("type", Enum.GetValues<AccountType>()));
            table.HasCheckConstraint("ck_accounts_status", Sql.InList("status", Enum.GetValues<AccountStatus>()));

            // Payable and escrow accounts belong to exactly one party; system accounts to none.
            table.HasCheckConstraint("ck_accounts_party", $"({Sql.InList("type", partyOwnedTypes)}) = (party_id IS NOT NULL)");
        });

        builder.HasKey(account => account.Id).HasName("pk_accounts");
        builder.Property(account => account.Id)
            .HasColumnName("id")
            .HasConversion(ValueConverters.AccountIdToGuid)
            .ValueGeneratedNever();
        builder.Property(account => account.Code)
            .HasColumnName("code")
            .HasMaxLength(AccountCode.MaxLength)
            .HasConversion(ValueConverters.AccountCodeToText);
        builder.Property(account => account.Type).HasColumnName("type");
        builder.Property(account => account.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .HasConversion(ValueConverters.CurrencyCodeToText);
        builder.Property(account => account.PartyId)
            .HasColumnName("party_id")
            .HasConversion(ValueConverters.PartyIdToGuid);
        builder.Property(account => account.Status).HasColumnName("status");

        builder.HasIndex(account => account.Code).IsUnique().HasDatabaseName("ux_accounts_code");

        // One payable and one escrow account per party and currency.
        builder.HasIndex(account => new { account.PartyId, account.Type, account.Currency })
            .IsUnique()
            .HasFilter("party_id IS NOT NULL")
            .HasDatabaseName("ux_accounts_party_type_currency");

        builder.HasOne<Party>()
            .WithMany()
            .HasForeignKey(account => account.PartyId)
            .HasConstraintName("fk_accounts_party")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<KnownCurrency>()
            .WithMany()
            .HasForeignKey(account => account.Currency)
            .HasConstraintName("fk_accounts_currency")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasData(SyntheticSeed.Accounts);
    }
}
