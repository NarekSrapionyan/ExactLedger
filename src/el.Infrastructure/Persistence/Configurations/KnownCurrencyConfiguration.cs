using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace el.Infrastructure.Persistence.Configurations;

internal sealed class KnownCurrencyConfiguration : IEntityTypeConfiguration<KnownCurrency>
{
    public void Configure(EntityTypeBuilder<KnownCurrency> builder)
    {
        builder.ToTable("currencies", table =>
            table.HasCheckConstraint("ck_currencies_code", "code ~ '^[A-Z]{3}$'"));

        builder.HasKey(currency => currency.Code).HasName("pk_currencies");
        builder.Property(currency => currency.Code)
            .HasColumnName("code")
            .HasMaxLength(3)
            .HasConversion(ValueConverters.CurrencyCodeToText)
            .ValueGeneratedNever();

        builder.HasData(SyntheticSeed.Currencies);
    }
}
