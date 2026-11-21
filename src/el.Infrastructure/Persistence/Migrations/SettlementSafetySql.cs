namespace el.Infrastructure.Persistence.Migrations;

internal static class SettlementSafetySql
{
    internal const string Up = """
        ALTER TABLE ledger.settlements ADD CONSTRAINT fk_settlement_journal FOREIGN KEY (journal_entry_id) REFERENCES ledger.journal_entries(id);
        ALTER TABLE ledger.settlement_allocations ADD CONSTRAINT fk_allocation_account FOREIGN KEY (account_id, currency) REFERENCES ledger.accounts(id, currency);
        ALTER TABLE ledger.settlement_allocations ADD CONSTRAINT fk_allocation_party FOREIGN KEY (party_id) REFERENCES ledger.parties(id);
        ALTER TABLE ledger.escrow_holds ADD CONSTRAINT fk_hold_escrow FOREIGN KEY (escrow_account_id, currency) REFERENCES ledger.accounts(id, currency);
        ALTER TABLE ledger.escrow_holds ADD CONSTRAINT fk_hold_payable FOREIGN KEY (payable_account_id, currency) REFERENCES ledger.accounts(id, currency);
        ALTER TABLE ledger.escrow_holds ADD CONSTRAINT fk_hold_party FOREIGN KEY (party_id) REFERENCES ledger.parties(id);

        CREATE FUNCTION ledger.guard_settlement_append() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NOT EXISTS(SELECT FROM ledger.settlements WHERE id = NEW.settlement_id AND creation_transaction = txid_current()) THEN
                RAISE EXCEPTION 'Cannot append to committed settlement history' USING ERRCODE = 'EL301';
            END IF;
            RETURN NEW;
        END;
        $$;

        CREATE FUNCTION ledger.check_settlement_complete() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF (SELECT COALESCE(sum(amount_minor), 0) FROM ledger.settlement_allocations WHERE settlement_id = NEW.id) <> NEW.gross_minor OR
               NOT EXISTS(SELECT FROM ledger.idempotency_records WHERE settlement_id = NEW.id AND client_id = NEW.client_id) OR
               NOT EXISTS(SELECT FROM ledger.journal_entries WHERE id = NEW.journal_entry_id AND finalized AND currency = NEW.currency) THEN
                RAISE EXCEPTION 'Incomplete settlement transaction' USING ERRCODE = 'EL302';
            END IF;
            IF (SELECT count(*) FROM ledger.journal_lines WHERE entry_id = NEW.journal_entry_id AND side = 1) <> 1 OR
               NOT EXISTS(SELECT FROM ledger.journal_lines l JOIN ledger.accounts a ON a.id = l.account_id
                 WHERE l.entry_id = NEW.journal_entry_id AND l.side = 1 AND l.amount_minor = NEW.gross_minor AND a.type = 1) OR
               EXISTS(SELECT FROM
                   (SELECT account_id, sum(amount_minor) AS amount FROM ledger.settlement_allocations WHERE settlement_id = NEW.id GROUP BY account_id) a
                   FULL JOIN (SELECT account_id, sum(amount_minor) AS amount FROM ledger.journal_lines WHERE entry_id = NEW.journal_entry_id AND side = 2 GROUP BY account_id) j USING(account_id)
                   WHERE COALESCE(a.amount, 0) <> COALESCE(j.amount, 0)) THEN
                RAISE EXCEPTION 'Settlement allocations differ from journal' USING ERRCODE = 'EL302';
            END IF;
            IF EXISTS(SELECT FROM ledger.settlement_allocations l JOIN ledger.accounts a ON a.id = l.account_id
                WHERE l.settlement_id = NEW.id AND (a.party_id IS DISTINCT FROM l.party_id OR
                 a.type <> CASE l.component WHEN 'Commission' THEN 4 WHEN 'Escrow' THEN 3 ELSE 2 END)) OR
               EXISTS(SELECT FROM ledger.settlement_allocations a LEFT JOIN ledger.escrow_holds h
                 ON h.settlement_id = a.settlement_id AND h.allocation_ordinal = a.ordinal
                 WHERE a.settlement_id = NEW.id AND a.component = 'Escrow' AND a.amount_minor > 0 AND
                  (h.id IS NULL OR h.original_held_minor <> a.amount_minor OR h.party_id IS DISTINCT FROM a.party_id OR h.escrow_account_id <> a.account_id)) OR
               EXISTS(SELECT FROM ledger.escrow_holds h JOIN ledger.settlement_allocations a
                 ON a.settlement_id = h.settlement_id AND a.ordinal = h.allocation_ordinal JOIN ledger.accounts p ON p.id = h.payable_account_id
                 WHERE h.settlement_id = NEW.id AND (a.component <> 'Escrow' OR a.amount_minor <> h.original_held_minor OR
                  p.type <> 2 OR p.party_id IS DISTINCT FROM h.party_id OR h.currency <> NEW.currency OR h.due_at_utc < NEW.occurred_at)) THEN
                RAISE EXCEPTION 'Invalid settlement beneficiary or escrow mapping' USING ERRCODE = 'EL302';
            END IF;
            RETURN NULL;
        END;
        $$;
        CREATE CONSTRAINT TRIGGER settlement_complete AFTER INSERT ON ledger.settlements
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ledger.check_settlement_complete();
        """;
    internal static string Guards => string.Join("\n", new[] { "settlements", "settlement_allocations", "escrow_holds", "idempotency_records" }.Select(table => $"""
        GRANT SELECT, INSERT ON ledger.{table} TO exactledger_runtime;
        CREATE TRIGGER {table}_immutable BEFORE UPDATE OR DELETE ON ledger.{table} FOR EACH ROW EXECUTE FUNCTION ledger.reject_history_change();
        CREATE TRIGGER {table}_no_truncate BEFORE TRUNCATE ON ledger.{table} EXECUTE FUNCTION ledger.reject_history_change();
        """)) + string.Join("\n", new[] { "settlement_allocations", "escrow_holds", "idempotency_records" }.Select(table => $"""
        CREATE TRIGGER {table}_append BEFORE INSERT ON ledger.{table} FOR EACH ROW EXECUTE FUNCTION ledger.guard_settlement_append();
        """));
    internal const string Down = """
        DROP FUNCTION ledger.check_settlement_complete();
        DROP FUNCTION ledger.guard_settlement_append();
        """;
}
