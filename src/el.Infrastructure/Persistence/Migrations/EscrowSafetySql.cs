namespace el.Infrastructure.Persistence.Migrations;

/// <summary>
/// Database guards for escrow hold transitions (BE-012.1): insert-only, one per hold, and a
/// deferred check that each release posts Dr Escrow / Cr Payable for exactly the held amount,
/// no earlier than the hold's due time. The status view derives each hold's effective state.
/// </summary>
internal static class EscrowSafetySql
{
    internal const string Up = """
        ALTER TABLE ledger.escrow_hold_transitions ADD CONSTRAINT fk_escrow_transition_journal
            FOREIGN KEY (journal_entry_id) REFERENCES ledger.journal_entries(id);

        CREATE FUNCTION ledger.check_escrow_hold_transition() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            h ledger.escrow_holds%ROWTYPE;
        BEGIN
            IF NEW.state <> 'Released' THEN
                RAISE EXCEPTION 'Unsupported escrow hold transition %', NEW.state USING ERRCODE = 'EL401';
            END IF;
            SELECT * INTO h FROM ledger.escrow_holds WHERE id = NEW.hold_id;
            IF NEW.amount_minor <> h.original_held_minor OR NEW.currency <> h.currency OR NEW.occurred_at_utc < h.due_at_utc OR
               NOT EXISTS(SELECT FROM ledger.journal_entries e WHERE e.id = NEW.journal_entry_id AND e.finalized
                   AND e.currency = NEW.currency AND e.reversal_of IS NULL) OR
               (SELECT count(*) FROM ledger.journal_lines WHERE entry_id = NEW.journal_entry_id) <> 2 OR
               NOT EXISTS(SELECT FROM ledger.journal_lines WHERE entry_id = NEW.journal_entry_id
                   AND side = 1 AND account_id = h.escrow_account_id AND amount_minor = NEW.amount_minor) OR
               NOT EXISTS(SELECT FROM ledger.journal_lines WHERE entry_id = NEW.journal_entry_id
                   AND side = 2 AND account_id = h.payable_account_id AND amount_minor = NEW.amount_minor) THEN
                RAISE EXCEPTION 'Escrow release % does not match its hold and journal', NEW.hold_id USING ERRCODE = 'EL402';
            END IF;
            RETURN NULL;
        END;
        $$;
        CREATE CONSTRAINT TRIGGER escrow_hold_transition_valid AFTER INSERT ON ledger.escrow_hold_transitions
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ledger.check_escrow_hold_transition();

        GRANT SELECT, INSERT ON ledger.escrow_hold_transitions TO exactledger_runtime;
        CREATE TRIGGER escrow_hold_transitions_immutable BEFORE UPDATE OR DELETE ON ledger.escrow_hold_transitions
            FOR EACH ROW EXECUTE FUNCTION ledger.reject_history_change();
        CREATE TRIGGER escrow_hold_transitions_no_truncate BEFORE TRUNCATE ON ledger.escrow_hold_transitions
            EXECUTE FUNCTION ledger.reject_history_change();

        CREATE VIEW ledger.escrow_hold_status AS
            SELECT h.id, h.settlement_id, h.party_id, h.currency, h.original_held_minor,
                   CASE WHEN t.state = 'Released' THEN h.original_held_minor ELSE 0 END AS released_minor,
                   COALESCE(t.state, 'Held') AS state, h.due_at_utc,
                   t.journal_entry_id AS transition_journal_entry_id, t.occurred_at_utc AS transition_at_utc
              FROM ledger.escrow_holds h
              LEFT JOIN ledger.escrow_hold_transitions t ON t.hold_id = h.id;
        GRANT SELECT ON ledger.escrow_hold_status TO exactledger_runtime;
        """;

    internal const string Down = """
        DROP VIEW ledger.escrow_hold_status;
        DROP TRIGGER escrow_hold_transition_valid ON ledger.escrow_hold_transitions;
        DROP FUNCTION ledger.check_escrow_hold_transition();
        """;
}
