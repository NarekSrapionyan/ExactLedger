using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace el.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SettlementRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "settlement_refunds",
                schema: "ledger",
                columns: table => new
                {
                    settlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    refunded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settlement_refunds", x => x.settlement_id);
                    table.CheckConstraint("ck_settlement_refund", "amount_minor BETWEEN 1 AND 99999999999 AND length(client_id) BETWEEN 1 AND 100 AND length(idempotency_key) BETWEEN 1 AND 128 AND request_hash ~ '^[0-9a-f]{64}$' AND isfinite(refunded_at_utc)");
                    table.ForeignKey(
                        name: "FK_settlement_refunds_settlements_settlement_id_currency",
                        columns: x => new { x.settlement_id, x.currency },
                        principalSchema: "ledger",
                        principalTable: "settlements",
                        principalColumns: new[] { "id", "currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_settlement_refunds_settlement_id_currency",
                schema: "ledger",
                table: "settlement_refunds",
                columns: new[] { "settlement_id", "currency" });

            migrationBuilder.CreateIndex(
                name: "ux_settlement_refund_journal",
                schema: "ledger",
                table: "settlement_refunds",
                column: "journal_entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_settlement_refund_key",
                schema: "ledger",
                table: "settlement_refunds",
                columns: new[] { "client_id", "idempotency_key" },
                unique: true);

            migrationBuilder.Sql(RefundSafetySql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RefundSafetySql.Down);

            migrationBuilder.DropTable(
                name: "settlement_refunds",
                schema: "ledger");
        }
    }
}
