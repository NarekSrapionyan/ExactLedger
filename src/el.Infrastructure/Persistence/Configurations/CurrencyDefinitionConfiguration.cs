using el.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace el.Infrastructure.Persistence.Configurations;

internal sealed class CurrencyDefinitionConfiguration : IEntityTypeConfiguration<CurrencyDefinition>
{
    public void Configure(EntityTypeBuilder<CurrencyDefinition> builder)
    {
        builder.ToTable("currency_definitions", table =>
        {
            table.HasCheckConstraint(
                "ck_currency_definitions_exponent",
                FormattableString.Invariant($"exponent BETWEEN {CurrencyDefinition.MinExponent} AND {CurrencyDefinition.MaxExponent}"));
            table.HasCheckConstraint(
                "ck_currency_definitions_version",
                FormattableString.Invariant($"version >= {CurrencyDefinition.MinVersion}"));
        });

        builder.HasKey(definition => new { definition.Code, definition.Version }).HasName("pk_currency_definitions");
        builder.Property(definition => definition.Code)
            .HasColumnName("code")
            .HasMaxLength(3)
            .HasConversion(ValueConverters.CurrencyCodeToText)
            .ValueGeneratedNever();
        builder.Property(definition => definition.Version).HasColumnName("version").ValueGeneratedNever();
        builder.Property(definition => definition.Exponent).HasColumnName("exponent");

        builder.HasOne<KnownCurrency>()
            .WithMany()
            .HasForeignKey(definition => definition.Code)
            .HasConstraintName("fk_currency_definitions_currency")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasData(CurrencyReferenceData.Catalog.Definitions);
    }
}
