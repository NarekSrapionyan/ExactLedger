using el.Domain.Ledger;
using el.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace el.Infrastructure.Persistence.Configurations;

internal sealed class IdentityConfiguration : IEntityTypeConfiguration<IdentityRecord>
{
    public void Configure(EntityTypeBuilder<IdentityRecord> builder)
    {
        builder.ToTable("identities", table =>
        {
            table.HasCheckConstraint("ck_identities_username", "username ~ '^[a-z0-9][a-z0-9._-]{2,63}$'");
            table.HasCheckConstraint("ck_identities_roles", "cardinality(roles) > 0 AND array_position(roles, NULL) IS NULL AND roles <@ ARRAY['Integrator','FinanceOperator','ConfigurationAdministrator','Payee','Auditor','Scheduler']::text[]");
            table.HasCheckConstraint("ck_identities_client", "NOT ('Integrator' = ANY(roles)) OR (client_id IS NOT NULL AND btrim(client_id) <> '')");
            table.HasCheckConstraint("ck_identities_party", "NOT ('Payee' = ANY(roles)) OR party_id IS NOT NULL");
        });
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(row => row.Username).HasColumnName("username").HasMaxLength(64);
        builder.HasIndex(row => row.Username).IsUnique();
        builder.Property(row => row.PasswordHash).HasColumnName("password_hash").HasMaxLength(1000);
        builder.Property(row => row.Roles).HasColumnName("roles");
        builder.Property(row => row.ClientId).HasColumnName("client_id").HasMaxLength(100);
        builder.Property(row => row.PartyId).HasColumnName("party_id").HasConversion(ValueConverters.PartyIdToGuid);
        builder.Property(row => row.Enabled).HasColumnName("enabled");
        builder.HasOne<Party>().WithMany().HasForeignKey(row => row.PartyId).OnDelete(DeleteBehavior.Restrict);
    }
}
