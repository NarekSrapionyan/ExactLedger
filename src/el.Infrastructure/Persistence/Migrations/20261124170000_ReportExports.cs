using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace el.Infrastructure.Persistence.Migrations;

/// <summary>SQL-backed report artifacts; no EF model/snapshot change.</summary>
[DbContext(typeof(ExactLedgerDbContext))]
[Migration("20261124170000_ReportExports")]
public sealed class ReportExports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE ledger.report_exports (
            id uuid PRIMARY KEY,
            kind varchar(32) NOT NULL CHECK (kind IN ('journal-register', 'payee-statement', 'account-balances', 'trial-balance', 'payout-instructions', 'batch-summary')),
            party_id uuid REFERENCES ledger.parties(id) ON DELETE RESTRICT,
            actor_id varchar(128) NOT NULL,
            idempotency_key varchar(128) NOT NULL,
            request_hash varchar(64) NOT NULL CHECK (request_hash ~ '^[0-9a-f]{64}$'),
            generated_at_utc timestamptz NOT NULL CHECK (isfinite(generated_at_utc)),
            content_sha256 varchar(64) NOT NULL,
            record_count integer NOT NULL CHECK (record_count BETWEEN 0 AND 100000),
            totals_json jsonb NOT NULL CHECK (jsonb_typeof(totals_json) = 'array'),
            content bytea NOT NULL,
            CONSTRAINT ux_report_export_command UNIQUE (actor_id, idempotency_key),
            CONSTRAINT ck_report_export_hash CHECK (octet_length(content) > 0 AND content_sha256 = encode(sha256(content), 'hex'))
        );
        GRANT SELECT, INSERT ON ledger.report_exports TO exactledger_runtime;
        CREATE TRIGGER report_exports_immutable BEFORE UPDATE OR DELETE ON ledger.report_exports
            FOR EACH ROW EXECUTE FUNCTION ledger.reject_history_change();
        CREATE TRIGGER report_exports_no_truncate BEFORE TRUNCATE ON ledger.report_exports
            EXECUTE FUNCTION ledger.reject_history_change();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE ledger.report_exports;");
}
