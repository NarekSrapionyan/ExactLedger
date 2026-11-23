namespace el.Infrastructure.Persistence.Migrations;

internal static class ClearingSafetySql
{
    private static readonly string[] HistoryTables =
    [
        "clearing_batches", "clearing_batch_entries", "payout_instructions",
        "clearing_batch_exports", "clearing_batch_confirmations", "payout_confirmations",
    ];

    internal static string Up => Tables + Functions + Guards;

    private const string Tables = """
        ALTER TABLE ledger.journal_entries ADD COLUMN sequence bigint GENERATED ALWAYS AS IDENTITY;
        CREATE UNIQUE INDEX ux_journal_entries_sequence ON ledger.journal_entries (sequence);

        CREATE TABLE ledger.payout_parties (
            party_id uuid NOT NULL,
            CONSTRAINT pk_payout_parties PRIMARY KEY (party_id),
            CONSTRAINT fk_payout_parties_party FOREIGN KEY (party_id) REFERENCES ledger.parties (id) ON DELETE RESTRICT
        );
        INSERT INTO ledger.payout_parties (party_id) VALUES
            ('625ef6d0-ce43-3186-d39e-d28c006d70c9'),
            ('2578a20d-3067-84db-0951-e4431482b4a5'),
            ('442d5ad4-508a-01b0-9c11-d73d65b18d1b');
        GRANT SELECT ON ledger.payout_parties TO exactledger_runtime;

        CREATE TABLE ledger.clearing_batches (
            id uuid NOT NULL,
            business_date date NOT NULL,
            currency varchar(3) NOT NULL,
            cut_at_utc timestamptz NOT NULL,
            cut_sequence bigint NOT NULL,
            entry_count integer NOT NULL,
            instruction_count integer NOT NULL,
            total_minor numeric(38,0) NOT NULL,
            creation_transaction bigint NOT NULL DEFAULT txid_current(),
            CONSTRAINT pk_clearing_batches PRIMARY KEY (id),
            CONSTRAINT ux_clearing_batches_date_currency UNIQUE (business_date, currency),
            CONSTRAINT fk_clearing_batches_currency FOREIGN KEY (currency) REFERENCES ledger.currencies (code) ON DELETE RESTRICT,
            CONSTRAINT ck_clearing_batch CHECK (cut_sequence >= 0 AND entry_count >= 0 AND instruction_count >= 0
                AND (entry_count > 0 OR instruction_count > 0) AND total_minor >= 0 AND isfinite(cut_at_utc))
        );

        CREATE TABLE ledger.clearing_batch_entries (
            journal_entry_id uuid NOT NULL,
            batch_id uuid NOT NULL,
            kind varchar(16) NOT NULL,
            CONSTRAINT pk_clearing_batch_entries PRIMARY KEY (journal_entry_id),
            CONSTRAINT fk_clearing_batch_entries_entry FOREIGN KEY (journal_entry_id) REFERENCES ledger.journal_entries (id) ON DELETE RESTRICT,
            CONSTRAINT fk_clearing_batch_entries_batch FOREIGN KEY (batch_id) REFERENCES ledger.clearing_batches (id) ON DELETE RESTRICT,
            CONSTRAINT ck_clearing_batch_entry CHECK (kind IN ('Settlement', 'Refund', 'EscrowRelease'))
        );
        CREATE INDEX ix_clearing_batch_entries_batch ON ledger.clearing_batch_entries (batch_id);

        CREATE TABLE ledger.payout_instructions (
            id uuid NOT NULL,
            batch_id uuid NOT NULL,
            party_id uuid NOT NULL,
            reference varchar(64) NOT NULL,
            currency varchar(3) NOT NULL,
            amount_minor bigint NOT NULL,
            CONSTRAINT pk_payout_instructions PRIMARY KEY (id),
            CONSTRAINT ux_payout_instruction_reference UNIQUE (reference),
            CONSTRAINT ux_payout_instruction_party UNIQUE (batch_id, party_id),
            CONSTRAINT fk_payout_instructions_batch FOREIGN KEY (batch_id) REFERENCES ledger.clearing_batches (id) ON DELETE RESTRICT,
            CONSTRAINT fk_payout_instructions_party FOREIGN KEY (party_id) REFERENCES ledger.payout_parties (party_id) ON DELETE RESTRICT,
            CONSTRAINT ck_payout_instruction CHECK (amount_minor BETWEEN 1 AND 99999999999 AND reference ~ '^[A-Z0-9.:_-]{1,64}$')
        );

        CREATE TABLE ledger.clearing_batch_exports (
            batch_id uuid NOT NULL,
            content bytea NOT NULL,
            content_sha256 varchar(64) NOT NULL,
            record_count integer NOT NULL,
            total_minor numeric(38,0) NOT NULL,
            currency varchar(3) NOT NULL,
            generated_at_utc timestamptz NOT NULL,
            CONSTRAINT pk_clearing_batch_exports PRIMARY KEY (batch_id),
            CONSTRAINT fk_clearing_batch_exports_batch FOREIGN KEY (batch_id) REFERENCES ledger.clearing_batches (id) ON DELETE RESTRICT,
            CONSTRAINT ck_clearing_export CHECK (octet_length(content) > 0 AND content_sha256 = encode(sha256(content), 'hex')
                AND record_count >= 0 AND total_minor >= 0 AND isfinite(generated_at_utc))
        );

        CREATE TABLE ledger.clearing_batch_confirmations (
            batch_id uuid NOT NULL,
            statement_sha256 varchar(64) NOT NULL,
            instruction_count integer NOT NULL,
            total_minor numeric(38,0) NOT NULL,
            confirmed_at_utc timestamptz NOT NULL,
            CONSTRAINT pk_clearing_batch_confirmations PRIMARY KEY (batch_id),
            CONSTRAINT fk_clearing_batch_confirmations_batch FOREIGN KEY (batch_id) REFERENCES ledger.clearing_batches (id) ON DELETE RESTRICT,
            CONSTRAINT ck_clearing_confirmation CHECK (statement_sha256 ~ '^[0-9a-f]{64}$' AND instruction_count >= 0
                AND total_minor >= 0 AND isfinite(confirmed_at_utc))
        );

        CREATE TABLE ledger.payout_confirmations (
            instruction_id uuid NOT NULL,
            batch_id uuid NOT NULL,
            journal_entry_id uuid NOT NULL,
            statement_row integer NOT NULL,
            CONSTRAINT pk_payout_confirmations PRIMARY KEY (instruction_id),
            CONSTRAINT ux_payout_confirmation_journal UNIQUE (journal_entry_id),
            CONSTRAINT fk_payout_confirmations_instruction FOREIGN KEY (instruction_id) REFERENCES ledger.payout_instructions (id) ON DELETE RESTRICT,
            CONSTRAINT fk_payout_confirmations_batch FOREIGN KEY (batch_id) REFERENCES ledger.clearing_batches (id) ON DELETE RESTRICT,
            CONSTRAINT fk_payout_confirmations_entry FOREIGN KEY (journal_entry_id) REFERENCES ledger.journal_entries (id) ON DELETE RESTRICT,
            CONSTRAINT ck_payout_confirmation CHECK (statement_row >= 2)
        );
        """;

    private const string Functions = """
        CREATE FUNCTION ledger.guard_clearing_append() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM ledger.clearing_batches WHERE id = NEW.batch_id AND creation_transaction = txid_current()) THEN
                RAISE EXCEPTION 'Cannot append to a committed clearing batch' USING ERRCODE = 'EL501';
            END IF;
            RETURN NEW;
        END;
        $$;

        CREATE FUNCTION ledger.check_clearing_batch() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.entry_count <> (SELECT count(*) FROM ledger.clearing_batch_entries WHERE batch_id = NEW.id)
               OR NEW.instruction_count <> (SELECT count(*) FROM ledger.payout_instructions WHERE batch_id = NEW.id)
               OR NEW.total_minor <> (SELECT COALESCE(sum(amount_minor), 0) FROM ledger.payout_instructions WHERE batch_id = NEW.id)
               OR EXISTS (SELECT 1 FROM ledger.payout_instructions WHERE batch_id = NEW.id AND currency <> NEW.currency)
               OR EXISTS (
                    SELECT 1 FROM ledger.clearing_batch_entries be JOIN ledger.journal_entries e ON e.id = be.journal_entry_id
                     WHERE be.batch_id = NEW.id
                       AND (NOT e.finalized OR e.currency <> NEW.currency OR e.sequence > NEW.cut_sequence
                            OR NOT (CASE be.kind
                                WHEN 'Settlement' THEN EXISTS (SELECT 1 FROM ledger.settlements s WHERE s.journal_entry_id = e.id)
                                WHEN 'Refund' THEN EXISTS (SELECT 1 FROM ledger.settlement_refunds r WHERE r.journal_entry_id = e.id)
                                ELSE EXISTS (SELECT 1 FROM ledger.escrow_hold_transitions t WHERE t.journal_entry_id = e.id AND t.state = 'Released')
                            END)))
               OR (SELECT count(*) FROM ledger.clearing_batches b WHERE b.currency = NEW.currency
                      AND NOT EXISTS (SELECT 1 FROM ledger.clearing_batch_confirmations c WHERE c.batch_id = b.id)) > 1
               OR EXISTS (
                    SELECT 1
                      FROM (SELECT i.party_id, sum(i.amount_minor) AS reserved FROM ledger.payout_instructions i
                             WHERE i.currency = NEW.currency
                               AND NOT EXISTS (SELECT 1 FROM ledger.payout_confirmations c WHERE c.instruction_id = i.id)
                             GROUP BY i.party_id) r
                      JOIN ledger.accounts a ON a.party_id = r.party_id AND a.type = 2 AND a.currency = NEW.currency
                     WHERE r.reserved > COALESCE((SELECT sum(CASE l.side WHEN 2 THEN l.amount_minor ELSE -l.amount_minor END)
                                                    FROM ledger.journal_lines l JOIN ledger.journal_entries je ON je.id = l.entry_id
                                                   WHERE l.account_id = a.id AND je.finalized), 0)) THEN
                RAISE EXCEPTION 'Clearing batch % is inconsistent with the ledger', NEW.id USING ERRCODE = 'EL502';
            END IF;
            RETURN NULL;
        END;
        $$;

        CREATE FUNCTION ledger.check_clearing_export() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            b ledger.clearing_batches%ROWTYPE;
        BEGIN
            SELECT * INTO b FROM ledger.clearing_batches WHERE id = NEW.batch_id;
            IF NEW.record_count <> b.instruction_count OR NEW.total_minor <> b.total_minor OR NEW.currency <> b.currency THEN
                RAISE EXCEPTION 'Clearing export of batch % does not match its batch', NEW.batch_id USING ERRCODE = 'EL503';
            END IF;
            RETURN NULL;
        END;
        $$;

        CREATE FUNCTION ledger.check_clearing_confirmation() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            b ledger.clearing_batches%ROWTYPE;
        BEGIN
            SELECT * INTO b FROM ledger.clearing_batches WHERE id = NEW.batch_id;
            IF NOT EXISTS (SELECT 1 FROM ledger.clearing_batch_exports WHERE batch_id = NEW.batch_id)
               OR NEW.instruction_count <> b.instruction_count OR NEW.total_minor <> b.total_minor
               OR (SELECT count(*) FROM ledger.payout_confirmations WHERE batch_id = NEW.batch_id) <> b.instruction_count
               OR EXISTS (SELECT 1 FROM ledger.payout_instructions i WHERE i.batch_id = NEW.batch_id
                            AND NOT EXISTS (SELECT 1 FROM ledger.payout_confirmations c WHERE c.instruction_id = i.id)) THEN
                RAISE EXCEPTION 'Confirmation of batch % is not backed by an export and every instruction', NEW.batch_id USING ERRCODE = 'EL504';
            END IF;
            RETURN NULL;
        END;
        $$;

        CREATE FUNCTION ledger.check_payout_confirmation() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            i ledger.payout_instructions%ROWTYPE;
        BEGIN
            SELECT * INTO i FROM ledger.payout_instructions WHERE id = NEW.instruction_id;
            IF i.batch_id <> NEW.batch_id
               OR NOT EXISTS (SELECT 1 FROM ledger.clearing_batch_confirmations WHERE batch_id = NEW.batch_id)
               OR NOT EXISTS (SELECT 1 FROM ledger.journal_entries e WHERE e.id = NEW.journal_entry_id AND e.finalized
                                AND e.currency = i.currency AND e.reversal_of IS NULL AND e.reference = 'PAYOUT:' || i.reference)
               OR (SELECT count(*) FROM ledger.journal_lines WHERE entry_id = NEW.journal_entry_id) <> 2
               OR NOT EXISTS (SELECT 1 FROM ledger.journal_lines l JOIN ledger.accounts a ON a.id = l.account_id
                               WHERE l.entry_id = NEW.journal_entry_id AND l.side = 1 AND l.amount_minor = i.amount_minor
                                 AND a.type = 2 AND a.party_id = i.party_id AND a.currency = i.currency)
               OR NOT EXISTS (SELECT 1 FROM ledger.journal_lines l JOIN ledger.accounts a ON a.id = l.account_id
                               WHERE l.entry_id = NEW.journal_entry_id AND l.side = 2 AND l.amount_minor = i.amount_minor
                                 AND a.type = 1 AND a.currency = i.currency) THEN
                RAISE EXCEPTION 'Payout confirmation of instruction % is not an exact Dr Payable / Cr Clearing posting', NEW.instruction_id USING ERRCODE = 'EL504';
            END IF;
            RETURN NULL;
        END;
        $$;

        CREATE TRIGGER clearing_batch_entries_append BEFORE INSERT ON ledger.clearing_batch_entries
            FOR EACH ROW EXECUTE FUNCTION ledger.guard_clearing_append();
        CREATE TRIGGER payout_instructions_append BEFORE INSERT ON ledger.payout_instructions
            FOR EACH ROW EXECUTE FUNCTION ledger.guard_clearing_append();
        CREATE CONSTRAINT TRIGGER clearing_batch_valid AFTER INSERT ON ledger.clearing_batches
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ledger.check_clearing_batch();
        CREATE CONSTRAINT TRIGGER clearing_export_valid AFTER INSERT ON ledger.clearing_batch_exports
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ledger.check_clearing_export();
        CREATE CONSTRAINT TRIGGER clearing_confirmation_valid AFTER INSERT ON ledger.clearing_batch_confirmations
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ledger.check_clearing_confirmation();
        CREATE CONSTRAINT TRIGGER payout_confirmation_valid AFTER INSERT ON ledger.payout_confirmations
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ledger.check_payout_confirmation();
        """;

    private static string Guards => string.Join("\n", HistoryTables.Select(table =>
        "GRANT SELECT, INSERT ON ledger." + table + " TO exactledger_runtime;\n"
        + "CREATE TRIGGER " + table + "_immutable BEFORE UPDATE OR DELETE ON ledger." + table
        + " FOR EACH ROW EXECUTE FUNCTION ledger.reject_history_change();\n"
        + "CREATE TRIGGER " + table + "_no_truncate BEFORE TRUNCATE ON ledger." + table
        + " EXECUTE FUNCTION ledger.reject_history_change();"));

    internal const string Down = """
        DROP TABLE ledger.payout_confirmations;
        DROP TABLE ledger.clearing_batch_confirmations;
        DROP TABLE ledger.clearing_batch_exports;
        DROP TABLE ledger.payout_instructions;
        DROP TABLE ledger.clearing_batch_entries;
        DROP TABLE ledger.clearing_batches;
        DROP TABLE ledger.payout_parties;
        DROP FUNCTION ledger.check_payout_confirmation();
        DROP FUNCTION ledger.check_clearing_confirmation();
        DROP FUNCTION ledger.check_clearing_export();
        DROP FUNCTION ledger.check_clearing_batch();
        DROP FUNCTION ledger.guard_clearing_append();
        ALTER TABLE ledger.journal_entries DROP COLUMN sequence;
        """;
}
