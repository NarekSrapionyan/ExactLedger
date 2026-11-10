using el.Domain.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace el.Infrastructure.Persistence.Configurations;

internal sealed class PartyConfiguration : IEntityTypeConfiguration<Party>
{
    public void Configure(EntityTypeBuilder<Party> builder)
    {
        builder.ToTable("parties", table =>
        {
            table.HasCheckConstraint("ck_parties_name", "btrim(name) <> ''");
            table.HasCheckConstraint("ck_parties_status", Sql.InList("status", Enum.GetValues<PartyStatus>()));
        });

        builder.HasKey(party => party.Id).HasName("pk_parties");
        builder.Property(party => party.Id)
            .HasColumnName("id")
            .HasConversion(ValueConverters.PartyIdToGuid)
            .ValueGeneratedNever();
        builder.Property(party => party.Name).HasColumnName("name").HasMaxLength(Party.MaxNameLength);
        builder.Property(party => party.Status).HasColumnName("status");

        builder.HasData(SyntheticSeed.Parties);
    }
}
