using System.Text.RegularExpressions;
using el.Domain.Common;
using el.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace el.Infrastructure.Persistence.Configurations;

internal static class KnowledgeColumns
{
    internal static void Map<T>(EntityTypeBuilder<T> builder) where T : class
    {
        foreach (var property in typeof(T).GetProperties())
        {
            var column = Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
            builder.Property(property.Name).HasColumnName(column);
            if (property.PropertyType == typeof(string)) builder.Property(property.Name).HasMaxLength(
                property.Name is "SourceNote" or "Note" or "Description" ? 2000 : property.Name == "Name" ? 200 : property.Name is "ClientId" or "PublishedBy" ? 100 : 64);
        }
    }
    internal const string Interval = "effective_from < effective_to AND isfinite(effective_from) AND isfinite(effective_to)";
    internal const string Publication = "(published_by IS NULL) = (published_at_utc IS NULL) AND (published_by IS NULL OR btrim(published_by) <> '')";
    internal const string Amount = " BETWEEN 0 AND 99999999999";
}

internal sealed class RegionConfiguration : IEntityTypeConfiguration<Region>
{
    public void Configure(EntityTypeBuilder<Region> b)
    {
        b.ToTable("regions"); b.HasKey(x => x.Code); KnowledgeColumns.Map(b); b.HasData(KnowledgeSeed.Regions);
    }
}
internal sealed class DeductionTypeConfiguration : IEntityTypeConfiguration<DeductionType>
{
    public void Configure(EntityTypeBuilder<DeductionType> b)
    {
        b.ToTable("deduction_types"); b.HasKey(x => x.Code); KnowledgeColumns.Map(b); b.HasData(KnowledgeSeed.Types);
    }
}
internal sealed class DeductionRuleConfiguration : IEntityTypeConfiguration<DeductionRule>
{
    public void Configure(EntityTypeBuilder<DeductionRule> b)
    {
        b.ToTable("deduction_rules", table =>
        {
            table.HasCheckConstraint("ck_rules_interval", KnowledgeColumns.Interval);
            table.HasCheckConstraint("ck_rules_publication", KnowledgeColumns.Publication);
            table.HasCheckConstraint("ck_rules_values", "version > 0 AND rule_id <> '00000000-0000-0000-0000-000000000000' AND calculation_base = 'Gross' AND rate_units BETWEEN 0 AND 100000000 AND sequence BETWEEN 1 AND 1000 AND rounding_mode IN ('HalfEven','HalfAwayFromZero','TowardZero') AND btrim(source_note) <> ''");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); KnowledgeColumns.Map(b);
        b.Property(x => x.RoundingMode).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(x => new { x.RuleId, x.Version }).IsUnique();
        b.HasOne<Region>().WithMany().HasForeignKey(x => x.RegionCode).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DeductionType>().WithMany().HasForeignKey(x => x.DeductionTypeCode).OnDelete(DeleteBehavior.Restrict);
        b.HasData(KnowledgeSeed.Rules);
    }
}
internal sealed class CorporateFeeProfileConfiguration : IEntityTypeConfiguration<CorporateFeeProfile>
{
    public void Configure(EntityTypeBuilder<CorporateFeeProfile> b)
    {
        b.ToTable("fee_profiles", table =>
        {
            table.HasCheckConstraint("ck_profiles_interval", KnowledgeColumns.Interval);
            table.HasCheckConstraint("ck_profiles_publication", KnowledgeColumns.Publication);
            table.HasCheckConstraint("ck_profiles_values", "version > 0 AND profile_id <> '00000000-0000-0000-0000-000000000000' AND commission_rate_units BETWEEN 0 AND 100000000 AND processor_rate_units BETWEEN 0 AND 100000000 AND escrow_rate_units BETWEEN 0 AND 100000000 AND hold_days BETWEEN 0 AND 30 AND rounding_mode IN ('HalfEven','HalfAwayFromZero','TowardZero') AND btrim(client_id) <> '' AND btrim(corporate_code) <> '' AND btrim(source_note) <> ''");
            table.HasCheckConstraint("ck_profiles_amounts", "processor_fixed_minor" + KnowledgeColumns.Amount + " AND (processor_floor_minor IS NULL OR processor_floor_minor" + KnowledgeColumns.Amount + ") AND (processor_cap_minor IS NULL OR processor_cap_minor" + KnowledgeColumns.Amount + ") AND (processor_floor_minor IS NULL OR processor_cap_minor IS NULL OR processor_floor_minor <= processor_cap_minor)");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); KnowledgeColumns.Map(b);
        b.Property(x => x.RoundingMode).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(x => new { x.ProfileId, x.Version }).IsUnique(); b.HasData(KnowledgeSeed.Profiles);
    }
}
internal sealed class RuleFixtureConfiguration : IEntityTypeConfiguration<RuleFixture>
{
    public void Configure(EntityTypeBuilder<RuleFixture> b)
    {
        b.ToTable("rule_fixtures", table => table.HasCheckConstraint("ck_rule_fixture_amounts", "gross_minor BETWEEN 1 AND 99999999999 AND expected_minor" + KnowledgeColumns.Amount));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); KnowledgeColumns.Map(b);
        b.HasOne<DeductionRule>().WithMany().HasForeignKey(x => x.RuleVersionId).OnDelete(DeleteBehavior.Restrict);
        b.HasData(KnowledgeSeed.RuleFixtures);
    }
}
internal sealed class ProfileFixtureConfiguration : IEntityTypeConfiguration<ProfileFixture>
{
    public void Configure(EntityTypeBuilder<ProfileFixture> b)
    {
        b.ToTable("profile_fixtures", table => table.HasCheckConstraint("ck_profile_fixture_amounts",
            "gross_minor BETWEEN 1 AND 99999999999 AND share_minor BETWEEN 0 AND gross_minor AND " + string.Join(" AND ", new[] { "expected_commission_minor", "expected_processor_minor", "expected_escrow_minor", "expected_payable_minor" }.Select(column => column + KnowledgeColumns.Amount))));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); KnowledgeColumns.Map(b);
        b.HasOne<CorporateFeeProfile>().WithMany().HasForeignKey(x => x.ProfileVersionId).OnDelete(DeleteBehavior.Restrict);
        b.HasData(KnowledgeSeed.ProfileFixtures);
    }
}
