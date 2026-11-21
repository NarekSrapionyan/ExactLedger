using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace el.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SettlementWaterfall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "settlements",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "text", nullable: false),
                    trade_id = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    posted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    result_hash = table.Column<string>(type: "text", nullable: false),
                    creation_transaction = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "txid_current()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settlements", x => x.id);
                    table.UniqueConstraint("AK_settlements_id_currency", x => new { x.id, x.currency });
                    table.CheckConstraint("ck_settlement_values", "gross_minor BETWEEN 1 AND 99999999999 AND length(client_id) BETWEEN 1 AND 100 AND length(trade_id) BETWEEN 1 AND 100 AND result_hash ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "ledger",
                columns: table => new
                {
                    client_id = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    settlement_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => new { x.client_id, x.operation, x.key });
                    table.CheckConstraint("ck_settlement_command", "operation = 'settlement.create' AND length(key) BETWEEN 1 AND 128 AND request_hash ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_idempotency_records_settlements_settlement_id",
                        column: x => x.settlement_id,
                        principalSchema: "ledger",
                        principalTable: "settlements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "settlement_allocations",
                schema: "ledger",
                columns: table => new
                {
                    settlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    line_id = table.Column<string>(type: "text", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    component = table.Column<string>(type: "text", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settlement_allocations", x => new { x.settlement_id, x.ordinal });
                    table.CheckConstraint("ck_settlement_allocation", "ordinal >= 0 AND amount_minor BETWEEN 0 AND 99999999999 AND component IN ('Regional','Processor','Commission','Affiliate','Payable','Escrow')");
                    table.ForeignKey(
                        name: "FK_settlement_allocations_settlements_settlement_id_currency",
                        columns: x => new { x.settlement_id, x.currency },
                        principalSchema: "ledger",
                        principalTable: "settlements",
                        principalColumns: new[] { "id", "currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "escrow_holds",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    settlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocation_ordinal = table.Column<int>(type: "integer", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    escrow_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payable_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    original_held_minor = table.Column<long>(type: "bigint", nullable: false),
                    released_minor = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    due_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_escrow_holds", x => x.id);
                    table.CheckConstraint("ck_initial_escrow_hold", "original_held_minor BETWEEN 1 AND 99999999999 AND released_minor = 0 AND state = 'Held' AND isfinite(due_at_utc)");
                    table.ForeignKey(
                        name: "FK_escrow_holds_settlement_allocations_settlement_id_allocatio~",
                        columns: x => new { x.settlement_id, x.allocation_ordinal },
                        principalSchema: "ledger",
                        principalTable: "settlement_allocations",
                        principalColumns: new[] { "settlement_id", "ordinal" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_escrow_holds_due_at_utc",
                schema: "ledger",
                table: "escrow_holds",
                column: "due_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_escrow_holds_settlement_id_allocation_ordinal",
                schema: "ledger",
                table: "escrow_holds",
                columns: new[] { "settlement_id", "allocation_ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_settlement_id",
                schema: "ledger",
                table: "idempotency_records",
                column: "settlement_id");

            migrationBuilder.CreateIndex(
                name: "IX_settlement_allocations_party_id",
                schema: "ledger",
                table: "settlement_allocations",
                column: "party_id");

            migrationBuilder.CreateIndex(
                name: "IX_settlement_allocations_settlement_id_currency",
                schema: "ledger",
                table: "settlement_allocations",
                columns: new[] { "settlement_id", "currency" });

            migrationBuilder.CreateIndex(
                name: "IX_settlements_client_id_trade_id",
                schema: "ledger",
                table: "settlements",
                columns: new[] { "client_id", "trade_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_settlements_journal_entry_id",
                schema: "ledger",
                table: "settlements",
                column: "journal_entry_id",
                unique: true);
            migrationBuilder.Sql(SettlementSafetySql.Up);
            migrationBuilder.Sql(SettlementSafetySql.Guards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "escrow_holds",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "settlement_allocations",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "settlements",
                schema: "ledger");
            migrationBuilder.Sql(SettlementSafetySql.Down);
        }
    }
}
