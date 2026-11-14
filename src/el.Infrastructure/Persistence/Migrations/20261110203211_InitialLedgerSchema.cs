using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace el.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedgerSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ledger");

            migrationBuilder.CreateTable(
                name: "currencies",
                schema: "ledger",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_currencies", x => x.code);
                    table.CheckConstraint("ck_currencies_code", "code ~ '^[A-Z]{3}$'");
                });

            migrationBuilder.CreateTable(
                name: "parties",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parties", x => x.id);
                    table.CheckConstraint("ck_parties_name", "btrim(name) <> ''");
                    table.CheckConstraint("ck_parties_status", "status IN (1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "currency_definitions",
                schema: "ledger",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    exponent = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_currency_definitions", x => new { x.code, x.version });
                    table.CheckConstraint("ck_currency_definitions_exponent", "exponent BETWEEN 0 AND 4");
                    table.CheckConstraint("ck_currency_definitions_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_currency_definitions_currency",
                        column: x => x.code,
                        principalSchema: "ledger",
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    reversal_of = table.Column<Guid>(type: "uuid", nullable: true),
                    finalized = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entries", x => x.id);
                    table.UniqueConstraint("ak_journal_entries_id_currency", x => new { x.id, x.currency });
                    table.CheckConstraint("ck_journal_entries_description", "btrim(description) <> ''");
                    table.CheckConstraint("ck_journal_entries_reference", "reference ~ '^[A-Z0-9.:_-]{1,100}$'");
                    table.CheckConstraint("ck_journal_entries_reversal", "reversal_of IS NULL OR reversal_of <> id");
                    table.ForeignKey(
                        name: "fk_journal_entries_currency",
                        column: x => x.currency,
                        principalSchema: "ledger",
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_journal_entries_reversal",
                        columns: x => new { x.reversal_of, x.currency },
                        principalSchema: "ledger",
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.UniqueConstraint("ak_accounts_id_currency", x => new { x.id, x.currency });
                    table.CheckConstraint("ck_accounts_code", "code ~ '^[A-Z0-9._-]{1,64}$'");
                    table.CheckConstraint("ck_accounts_party", "(type IN (2, 3)) = (party_id IS NOT NULL)");
                    table.CheckConstraint("ck_accounts_status", "status IN (1, 2)");
                    table.CheckConstraint("ck_accounts_type", "type IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "fk_accounts_currency",
                        column: x => x.currency,
                        principalSchema: "ledger",
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_accounts_party",
                        column: x => x.party_id,
                        principalSchema: "ledger",
                        principalTable: "parties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "journal_lines",
                schema: "ledger",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    side = table.Column<int>(type: "integer", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_lines", x => new { x.entry_id, x.line_number });
                    table.CheckConstraint("ck_journal_lines_amount", "amount_minor BETWEEN 1 AND 99999999999");
                    table.CheckConstraint("ck_journal_lines_line_number", "line_number >= 1");
                    table.CheckConstraint("ck_journal_lines_side", "side IN (1, 2)");
                    table.ForeignKey(
                        name: "fk_journal_lines_account",
                        columns: x => new { x.account_id, x.currency },
                        principalSchema: "ledger",
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "currency" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_journal_lines_entry",
                        columns: x => new { x.entry_id, x.currency },
                        principalSchema: "ledger",
                        principalTable: "journal_entries",
                        principalColumns: new[] { "id", "currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "ledger",
                table: "currencies",
                column: "code",
                values: new object[]
                {
                    "JPY",
                    "KWD",
                    "USD"
                });

            migrationBuilder.InsertData(
                schema: "ledger",
                table: "parties",
                columns: new[] { "id", "name", "status" },
                values: new object[,]
                {
                    { new Guid("2578a20d-3067-84db-0951-e4431482b4a5"), "Synthetic Seller Two", 1 },
                    { new Guid("442d5ad4-508a-01b0-9c11-d73d65b18d1b"), "Synthetic Affiliate", 1 },
                    { new Guid("625ef6d0-ce43-3186-d39e-d28c006d70c9"), "Synthetic Seller One", 1 },
                    { new Guid("7257501e-2296-aa1f-c407-38b4c913b68a"), "Synthetic Card Processor", 1 },
                    { new Guid("a186ddf9-f897-7bf9-33bf-8904fda4e7d8"), "Synthetic Tax Authority", 1 }
                });

            migrationBuilder.InsertData(
                schema: "ledger",
                table: "accounts",
                columns: new[] { "id", "code", "currency", "party_id", "status", "type" },
                values: new object[,]
                {
                    { new Guid("0349cbec-1717-10e7-75d8-21c5cb8a2a7a"), "CLEARING.BANK.JPY", "JPY", null, 1, 1 },
                    { new Guid("04948343-30c6-0fbd-eb45-b87906fb68a7"), "ESCROW.SELLER-1.JPY", "JPY", new Guid("625ef6d0-ce43-3186-d39e-d28c006d70c9"), 1, 3 },
                    { new Guid("0dd8b77e-42ea-7191-f24e-163c07f7acbe"), "ESCROW.AFFILIATE-1.KWD", "KWD", new Guid("442d5ad4-508a-01b0-9c11-d73d65b18d1b"), 1, 3 },
                    { new Guid("10e31b89-58ae-d3bb-ef55-3f1a061d32f7"), "REVENUE.COMMISSION.USD", "USD", null, 1, 4 },
                    { new Guid("1370e9ff-8759-9fc1-c615-19ccb8aea76c"), "ESCROW.AFFILIATE-1.JPY", "JPY", new Guid("442d5ad4-508a-01b0-9c11-d73d65b18d1b"), 1, 3 },
                    { new Guid("14983c4f-15e6-919e-65b1-7e8cb73ca4b6"), "PAYABLE.SELLER-2.JPY", "JPY", new Guid("2578a20d-3067-84db-0951-e4431482b4a5"), 1, 2 },
                    { new Guid("21b4ea0d-8bd2-9cae-9cf5-c470cf5ddc26"), "PAYABLE.SELLER-1.JPY", "JPY", new Guid("625ef6d0-ce43-3186-d39e-d28c006d70c9"), 1, 2 },
                    { new Guid("25fc30ad-40f4-dee6-1e97-398e5edf7480"), "PAYABLE.TAX.USD", "USD", new Guid("a186ddf9-f897-7bf9-33bf-8904fda4e7d8"), 1, 2 },
                    { new Guid("2ac99e17-8006-35c5-89a4-30ca8166e468"), "PAYABLE.SELLER-2.KWD", "KWD", new Guid("2578a20d-3067-84db-0951-e4431482b4a5"), 1, 2 },
                    { new Guid("36e8d90b-09f9-51ca-ad4c-56620219a8c9"), "PAYABLE.PROCESSOR.JPY", "JPY", new Guid("7257501e-2296-aa1f-c407-38b4c913b68a"), 1, 2 },
                    { new Guid("385a4b76-affb-afd5-2bde-0b85f569981a"), "ESCROW.AFFILIATE-1.USD", "USD", new Guid("442d5ad4-508a-01b0-9c11-d73d65b18d1b"), 1, 3 },
                    { new Guid("3ad92bd3-5b77-7cbc-4b13-62fd3b9b16d4"), "ESCROW.SELLER-2.JPY", "JPY", new Guid("2578a20d-3067-84db-0951-e4431482b4a5"), 1, 3 },
                    { new Guid("3cf2dab0-f0da-5a81-5956-3515bc042254"), "PAYABLE.SELLER-1.KWD", "KWD", new Guid("625ef6d0-ce43-3186-d39e-d28c006d70c9"), 1, 2 },
                    { new Guid("3d3f27ff-17c5-92a5-92be-1de6411e030a"), "PAYABLE.SELLER-2.USD", "USD", new Guid("2578a20d-3067-84db-0951-e4431482b4a5"), 1, 2 },
                    { new Guid("4dbf204a-03d6-9e42-b974-54fa668b1599"), "PAYABLE.PROCESSOR.USD", "USD", new Guid("7257501e-2296-aa1f-c407-38b4c913b68a"), 1, 2 },
                    { new Guid("7bbad810-cb78-6c7b-6dac-34aa69c3ede4"), "ESCROW.SELLER-1.USD", "USD", new Guid("625ef6d0-ce43-3186-d39e-d28c006d70c9"), 1, 3 },
                    { new Guid("7bdb7cf6-8057-6550-0371-43f66ccf4c1c"), "PAYABLE.PROCESSOR.KWD", "KWD", new Guid("7257501e-2296-aa1f-c407-38b4c913b68a"), 1, 2 },
                    { new Guid("930e9a8b-fcb7-9b37-bd85-09f067c09f73"), "REVENUE.COMMISSION.KWD", "KWD", null, 1, 4 },
                    { new Guid("979f6c83-428e-7b00-c775-250f4709df1c"), "CLEARING.BANK.USD", "USD", null, 1, 1 },
                    { new Guid("a7d02e5c-7b18-99e0-bd1a-170aa92107fc"), "PAYABLE.AFFILIATE-1.KWD", "KWD", new Guid("442d5ad4-508a-01b0-9c11-d73d65b18d1b"), 1, 2 },
                    { new Guid("b5bef136-7f87-24a3-7b21-485905bb47aa"), "CLEARING.BANK.KWD", "KWD", null, 1, 1 },
                    { new Guid("b6469300-8800-00c0-a679-27ead0e87935"), "ESCROW.SELLER-2.KWD", "KWD", new Guid("2578a20d-3067-84db-0951-e4431482b4a5"), 1, 3 },
                    { new Guid("c41f35d2-9894-9651-5901-a5a3b10f0974"), "ESCROW.SELLER-2.USD", "USD", new Guid("2578a20d-3067-84db-0951-e4431482b4a5"), 1, 3 },
                    { new Guid("c85e30c9-8bfa-a075-8863-2ceaba8544a7"), "PAYABLE.TAX.KWD", "KWD", new Guid("a186ddf9-f897-7bf9-33bf-8904fda4e7d8"), 1, 2 },
                    { new Guid("ca8901ad-f6c7-c695-6bb1-40bfe6c8c28d"), "PAYABLE.SELLER-1.USD", "USD", new Guid("625ef6d0-ce43-3186-d39e-d28c006d70c9"), 1, 2 },
                    { new Guid("cafdf206-dc87-6458-1eb1-ef69aa256331"), "PAYABLE.TAX.JPY", "JPY", new Guid("a186ddf9-f897-7bf9-33bf-8904fda4e7d8"), 1, 2 },
                    { new Guid("cea4fa90-910a-41a9-0529-7253d9058d8f"), "PAYABLE.AFFILIATE-1.USD", "USD", new Guid("442d5ad4-508a-01b0-9c11-d73d65b18d1b"), 1, 2 },
                    { new Guid("d218e3fd-d88d-1e7f-ab1c-e90c7509bfbb"), "REVENUE.COMMISSION.JPY", "JPY", null, 1, 4 },
                    { new Guid("d9f30591-79b9-c0e6-4fa6-eafc0f615e69"), "PAYABLE.AFFILIATE-1.JPY", "JPY", new Guid("442d5ad4-508a-01b0-9c11-d73d65b18d1b"), 1, 2 },
                    { new Guid("f8d1f95e-150c-9641-b32b-bc1ff4f1bc9e"), "ESCROW.SELLER-1.KWD", "KWD", new Guid("625ef6d0-ce43-3186-d39e-d28c006d70c9"), 1, 3 }
                });

            migrationBuilder.InsertData(
                schema: "ledger",
                table: "currency_definitions",
                columns: new[] { "code", "version", "exponent" },
                values: new object[,]
                {
                    { "JPY", 1, 0 },
                    { "KWD", 1, 3 },
                    { "USD", 1, 2 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_currency",
                schema: "ledger",
                table: "accounts",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "ux_accounts_code",
                schema: "ledger",
                table: "accounts",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_accounts_party_type_currency",
                schema: "ledger",
                table: "accounts",
                columns: new[] { "party_id", "type", "currency" },
                unique: true,
                filter: "party_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_currency",
                schema: "ledger",
                table: "journal_entries",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_reversal_of_currency",
                schema: "ledger",
                table: "journal_entries",
                columns: new[] { "reversal_of", "currency" });

            migrationBuilder.CreateIndex(
                name: "ux_journal_entries_reference",
                schema: "ledger",
                table: "journal_entries",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_journal_entries_reversal_of",
                schema: "ledger",
                table: "journal_entries",
                column: "reversal_of",
                unique: true,
                filter: "reversal_of IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_journal_lines_account",
                schema: "ledger",
                table: "journal_lines",
                columns: new[] { "account_id", "currency" });

            migrationBuilder.CreateIndex(
                name: "IX_journal_lines_entry_id_currency",
                schema: "ledger",
                table: "journal_lines",
                columns: new[] { "entry_id", "currency" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "currency_definitions",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "journal_lines",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "journal_entries",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "parties",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "currencies",
                schema: "ledger");
        }
    }
}
