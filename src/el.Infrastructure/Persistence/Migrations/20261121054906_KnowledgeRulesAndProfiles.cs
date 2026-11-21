using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace el.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeRulesAndProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deduction_types",
                schema: "ledger",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deduction_types", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "fee_profiles",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    corporate_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    currency = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    commission_rate_units = table.Column<long>(type: "bigint", nullable: false),
                    processor_rate_units = table.Column<long>(type: "bigint", nullable: false),
                    processor_fixed_minor = table.Column<long>(type: "bigint", nullable: false),
                    processor_floor_minor = table.Column<long>(type: "bigint", nullable: true),
                    processor_cap_minor = table.Column<long>(type: "bigint", nullable: true),
                    rounding_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    escrow_rate_units = table.Column<long>(type: "bigint", nullable: false),
                    hold_days = table.Column<int>(type: "integer", nullable: false),
                    commission_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processor_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    is_synthetic = table.Column<bool>(type: "boolean", nullable: false),
                    published_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fee_profiles", x => x.id);
                    table.CheckConstraint("ck_profiles_amounts", "processor_fixed_minor BETWEEN 0 AND 99999999999 AND (processor_floor_minor IS NULL OR processor_floor_minor BETWEEN 0 AND 99999999999) AND (processor_cap_minor IS NULL OR processor_cap_minor BETWEEN 0 AND 99999999999) AND (processor_floor_minor IS NULL OR processor_cap_minor IS NULL OR processor_floor_minor <= processor_cap_minor)");
                    table.CheckConstraint("ck_profiles_interval", "effective_from < effective_to AND isfinite(effective_from) AND isfinite(effective_to)");
                    table.CheckConstraint("ck_profiles_publication", "(published_by IS NULL) = (published_at_utc IS NULL) AND (published_by IS NULL OR btrim(published_by) <> '')");
                    table.CheckConstraint("ck_profiles_values", "version > 0 AND profile_id <> '00000000-0000-0000-0000-000000000000' AND commission_rate_units BETWEEN 0 AND 100000000 AND processor_rate_units BETWEEN 0 AND 100000000 AND escrow_rate_units BETWEEN 0 AND 100000000 AND hold_days BETWEEN 0 AND 30 AND rounding_mode IN ('HalfEven','HalfAwayFromZero','TowardZero') AND btrim(client_id) <> '' AND btrim(corporate_code) <> '' AND btrim(source_note) <> ''");
                });

            migrationBuilder.CreateTable(
                name: "regions",
                schema: "ledger",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_regions", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "profile_fixtures",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    share_minor = table.Column<long>(type: "bigint", nullable: false),
                    expected_commission_minor = table.Column<long>(type: "bigint", nullable: false),
                    expected_processor_minor = table.Column<long>(type: "bigint", nullable: false),
                    expected_escrow_minor = table.Column<long>(type: "bigint", nullable: false),
                    expected_payable_minor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_fixtures", x => x.id);
                    table.CheckConstraint("ck_profile_fixture_amounts", "gross_minor BETWEEN 1 AND 99999999999 AND share_minor BETWEEN 0 AND gross_minor AND expected_commission_minor BETWEEN 0 AND 99999999999 AND expected_processor_minor BETWEEN 0 AND 99999999999 AND expected_escrow_minor BETWEEN 0 AND 99999999999 AND expected_payable_minor BETWEEN 0 AND 99999999999");
                    table.ForeignKey(
                        name: "FK_profile_fixtures_fee_profiles_profile_version_id",
                        column: x => x.profile_version_id,
                        principalSchema: "ledger",
                        principalTable: "fee_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deduction_rules",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    region_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    deduction_type_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    calculation_base = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rate_units = table.Column<long>(type: "bigint", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    beneficiary_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rounding_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    is_synthetic = table.Column<bool>(type: "boolean", nullable: false),
                    published_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deduction_rules", x => x.id);
                    table.CheckConstraint("ck_rules_interval", "effective_from < effective_to AND isfinite(effective_from) AND isfinite(effective_to)");
                    table.CheckConstraint("ck_rules_publication", "(published_by IS NULL) = (published_at_utc IS NULL) AND (published_by IS NULL OR btrim(published_by) <> '')");
                    table.CheckConstraint("ck_rules_values", "version > 0 AND rule_id <> '00000000-0000-0000-0000-000000000000' AND calculation_base = 'Gross' AND rate_units BETWEEN 0 AND 100000000 AND sequence BETWEEN 1 AND 1000 AND rounding_mode IN ('HalfEven','HalfAwayFromZero','TowardZero') AND btrim(source_note) <> ''");
                    table.ForeignKey(
                        name: "FK_deduction_rules_deduction_types_deduction_type_code",
                        column: x => x.deduction_type_code,
                        principalSchema: "ledger",
                        principalTable: "deduction_types",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deduction_rules_regions_region_code",
                        column: x => x.region_code,
                        principalSchema: "ledger",
                        principalTable: "regions",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rule_fixtures",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    expected_minor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rule_fixtures", x => x.id);
                    table.CheckConstraint("ck_rule_fixture_amounts", "gross_minor BETWEEN 1 AND 99999999999 AND expected_minor BETWEEN 0 AND 99999999999");
                    table.ForeignKey(
                        name: "FK_rule_fixtures_deduction_rules_rule_version_id",
                        column: x => x.rule_version_id,
                        principalSchema: "ledger",
                        principalTable: "deduction_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "ledger",
                table: "deduction_types",
                columns: new[] { "code", "description", "name" },
                values: new object[] { "DEMO-LEVY", "Educational gross-based deduction, not a statement of law.", "Synthetic levy" });

            migrationBuilder.InsertData(
                schema: "ledger",
                table: "fee_profiles",
                columns: new[] { "id", "client_id", "commission_account_id", "commission_rate_units", "corporate_code", "currency", "effective_from", "effective_to", "escrow_rate_units", "hold_days", "is_synthetic", "processor_account_id", "processor_cap_minor", "processor_fixed_minor", "processor_floor_minor", "processor_rate_units", "profile_id", "published_at_utc", "published_by", "rounding_mode", "source_note", "version" },
                values: new object[,]
                {
                    { new Guid("7ef63383-197f-3e20-cbc3-57c59e580988"), "demo-client-1", new Guid("10e31b89-58ae-d3bb-ef55-3f1a061d32f7"), 10000000L, "DEMO-STANDARD", "USD", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2027, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 10000000L, 7, true, new Guid("4dbf204a-03d6-9e42-b974-54fa668b1599"), 500L, 30L, 50L, 2900000L, new Guid("f61a3ce8-13c3-010d-d2b1-8a8e262e862b"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "synthetic-seed", "HalfEven", "Synthetic seller-borne processor fee; 10% escrow held for 7 days.", 1 },
                    { new Guid("81128bcc-bd7e-43c6-28cd-f14392948e32"), "demo-client-1", new Guid("10e31b89-58ae-d3bb-ef55-3f1a061d32f7"), 0L, "DEMO-SIMPLE", "USD", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2027, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 0L, 0, true, new Guid("4dbf204a-03d6-9e42-b974-54fa668b1599"), null, 0L, null, 1000000L, new Guid("9794939b-dbdf-58a2-3f21-9c268043e6a8"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "synthetic-seed", "HalfAwayFromZero", "Synthetic seller-borne processor fee; no escrow.", 1 }
                });

            migrationBuilder.InsertData(
                schema: "ledger",
                table: "regions",
                columns: new[] { "code", "name" },
                values: new object[,]
                {
                    { "DEMO-A", "Synthetic Region A" },
                    { "DEMO-B", "Synthetic Region B" },
                    { "DEMO-NONE", "Synthetic no-deduction region" }
                });

            migrationBuilder.InsertData(
                schema: "ledger",
                table: "deduction_rules",
                columns: new[] { "id", "beneficiary_account_id", "calculation_base", "currency", "deduction_type_code", "effective_from", "effective_to", "is_synthetic", "published_at_utc", "published_by", "rate_units", "region_code", "rounding_mode", "rule_id", "sequence", "source_note", "version" },
                values: new object[,]
                {
                    { new Guid("09fcda52-5221-011d-67c2-84a8bbb26e7d"), new Guid("25fc30ad-40f4-dee6-1e97-398e5edf7480"), "Gross", "USD", "DEMO-LEVY", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2027, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "synthetic-seed", 5000000L, "DEMO-B", "HalfEven", new Guid("d216babc-e76d-6ac3-1c9c-66208c3a97e2"), 1, "Synthetic teaching assumption; no real tax law is represented.", 1 },
                    { new Guid("0ef92b3c-214f-740a-52b9-6fa308e31e06"), new Guid("25fc30ad-40f4-dee6-1e97-398e5edf7480"), "Gross", "USD", "DEMO-LEVY", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2027, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "synthetic-seed", 10000000L, "DEMO-A", "HalfEven", new Guid("957f8844-39a1-453c-7ecd-e4b1bc4ff827"), 1, "Synthetic teaching assumption; no real tax law is represented.", 1 }
                });

            migrationBuilder.InsertData(
                schema: "ledger",
                table: "profile_fixtures",
                columns: new[] { "id", "expected_commission_minor", "expected_escrow_minor", "expected_payable_minor", "expected_processor_minor", "gross_minor", "note", "profile_version_id", "share_minor" },
                values: new object[,]
                {
                    { new Guid("17f5291b-d330-3a16-bf76-199f6ff13266"), 1000L, 800L, 7200L, 320L, 10000L, "Synthetic gross 100.00; beneficiary share 80.00.", new Guid("7ef63383-197f-3e20-cbc3-57c59e580988"), 8000L },
                    { new Guid("b91a2680-9191-be4c-91c3-2491bfd0a84b"), 0L, 0L, 8000L, 100L, 10000L, "Synthetic gross 100.00; beneficiary share 80.00.", new Guid("81128bcc-bd7e-43c6-28cd-f14392948e32"), 8000L }
                });

            migrationBuilder.InsertData(
                schema: "ledger",
                table: "rule_fixtures",
                columns: new[] { "id", "expected_minor", "gross_minor", "note", "rule_version_id" },
                values: new object[,]
                {
                    { new Guid("60f29052-ae43-029b-cb3a-61c82babc71a"), 500L, 10000L, "Synthetic USD 100.00 gross example.", new Guid("09fcda52-5221-011d-67c2-84a8bbb26e7d") },
                    { new Guid("610054e7-48ba-d432-e10a-1f33914d7255"), 1000L, 10000L, "Synthetic USD 100.00 gross example.", new Guid("0ef92b3c-214f-740a-52b9-6fa308e31e06") }
                });

            migrationBuilder.CreateIndex(
                name: "IX_deduction_rules_deduction_type_code",
                schema: "ledger",
                table: "deduction_rules",
                column: "deduction_type_code");

            migrationBuilder.CreateIndex(
                name: "IX_deduction_rules_region_code",
                schema: "ledger",
                table: "deduction_rules",
                column: "region_code");

            migrationBuilder.CreateIndex(
                name: "IX_deduction_rules_rule_id_version",
                schema: "ledger",
                table: "deduction_rules",
                columns: new[] { "rule_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fee_profiles_profile_id_version",
                schema: "ledger",
                table: "fee_profiles",
                columns: new[] { "profile_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_profile_fixtures_profile_version_id",
                schema: "ledger",
                table: "profile_fixtures",
                column: "profile_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_rule_fixtures_rule_version_id",
                schema: "ledger",
                table: "rule_fixtures",
                column: "rule_version_id");
            migrationBuilder.Sql(KnowledgeSafetySql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "profile_fixtures",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "rule_fixtures",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "fee_profiles",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "deduction_rules",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "deduction_types",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "regions",
                schema: "ledger");
            migrationBuilder.Sql(KnowledgeSafetySql.Down);
        }
    }
}
