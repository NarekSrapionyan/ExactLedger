using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace el.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IdentityAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    target_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identities",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    roles = table.Column<string[]>(type: "text[]", nullable: false),
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identities", x => x.id);
                    table.CheckConstraint("ck_identities_client", "NOT ('Integrator' = ANY(roles)) OR (client_id IS NOT NULL AND btrim(client_id) <> '')");
                    table.CheckConstraint("ck_identities_party", "NOT ('Payee' = ANY(roles)) OR party_id IS NOT NULL");
                    table.CheckConstraint("ck_identities_roles", "cardinality(roles) > 0 AND array_position(roles, NULL) IS NULL AND roles <@ ARRAY['Integrator','FinanceOperator','ConfigurationAdministrator','Payee','Auditor','Scheduler']::text[]");
                    table.CheckConstraint("ck_identities_username", "username ~ '^[a-z0-9][a-z0-9._-]{2,63}$'");
                    table.ForeignKey(
                        name: "FK_identities_parties_party_id",
                        column: x => x.party_id,
                        principalSchema: "ledger",
                        principalTable: "parties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_occurred_at_utc_id",
                schema: "ledger",
                table: "audit_events",
                columns: new[] { "occurred_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_identities_party_id",
                schema: "ledger",
                table: "identities",
                column: "party_id");

            migrationBuilder.CreateIndex(
                name: "IX_identities_username",
                schema: "ledger",
                table: "identities",
                column: "username",
                unique: true);

            // Identities are provisioned only through the trusted migration connection.
            // Business audit is append-only, including for the schema owner.
            migrationBuilder.Sql("""
                GRANT SELECT ON ledger.identities TO exactledger_runtime;
                GRANT SELECT, INSERT ON ledger.audit_events TO exactledger_runtime;
                CREATE TRIGGER audit_events_immutable
                    BEFORE UPDATE OR DELETE ON ledger.audit_events
                    FOR EACH ROW EXECUTE FUNCTION ledger.reject_history_change();
                CREATE TRIGGER audit_events_no_truncate
                    BEFORE TRUNCATE ON ledger.audit_events
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_history_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "identities",
                schema: "ledger");
        }
    }
}
