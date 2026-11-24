using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace el.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackendAcceptanceContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reason",
                schema: "ledger",
                table: "settlement_refunds",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
            migrationBuilder.Sql("""
                GRANT INSERT ON ledger.payout_parties TO exactledger_runtime;
                CREATE TRIGGER payout_parties_immutable BEFORE UPDATE OR DELETE ON ledger.payout_parties
                    FOR EACH ROW EXECUTE FUNCTION ledger.reject_history_change();
                CREATE TRIGGER payout_parties_no_truncate BEFORE TRUNCATE ON ledger.payout_parties
                    EXECUTE FUNCTION ledger.reject_history_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER payout_parties_immutable ON ledger.payout_parties;
                DROP TRIGGER payout_parties_no_truncate ON ledger.payout_parties;
                REVOKE INSERT ON ledger.payout_parties FROM exactledger_runtime;
                """);
            migrationBuilder.DropColumn(
                name: "reason",
                schema: "ledger",
                table: "settlement_refunds");
        }
    }
}
