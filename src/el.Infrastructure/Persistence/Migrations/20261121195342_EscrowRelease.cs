using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace el.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EscrowRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "escrow_hold_transitions",
                schema: "ledger",
                columns: table => new
                {
                    hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_escrow_hold_transitions", x => x.hold_id);
                    table.CheckConstraint("ck_escrow_hold_transition", "state IN ('Released','Refunded') AND amount_minor BETWEEN 1 AND 99999999999 AND isfinite(occurred_at_utc)");
                    table.ForeignKey(
                        name: "FK_escrow_hold_transitions_escrow_holds_hold_id",
                        column: x => x.hold_id,
                        principalSchema: "ledger",
                        principalTable: "escrow_holds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_escrow_release_journal",
                schema: "ledger",
                table: "escrow_hold_transitions",
                column: "journal_entry_id",
                unique: true,
                filter: "state = 'Released'");

            migrationBuilder.Sql(EscrowSafetySql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(EscrowSafetySql.Down);

            migrationBuilder.DropTable(
                name: "escrow_hold_transitions",
                schema: "ledger");
        }
    }
}
