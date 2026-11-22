namespace el.Infrastructure.Persistence.Migrations;

/// <summary>
/// Database guards for full refunds (BE-012.2), independent of the application: a refund is a
/// finalized reversal linked to the settlement's entry, mirrors every original line exactly with the
/// opposite side, refunds the full gross, and marks every hold of the settlement Refunded with the
/// same entry. Escrow transitions accept Refunded only when such a refund exists, and a hold of a
/// refunded settlement can no longer be released.
/// </summary>
internal static class RefundSafetySql
{
    internal const string Up = """
        ALTER TABLE ledger.settlement_refunds ADD CONSTRAINT fk_settlement_refund_journal
            FOREIGN KEY (journal_entry_id) REFERENCES ledger.journal_entries(id);

        CREATE OR REPLACE FUNCTION ledger.check_escrow_hold_transition() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            h ledger.escrow_holds%ROWTYPE;
        BEGIN
            SELECT * INTO h FROM ledger.escrow_holds WHERE id = NEW.hold_id;
            IF NEW.amount_minor <> h.original_held_minor OR NEW.currency <> h.currency THEN
                RAISE EXCEPTION 'Escrow transition % does not match its hold', NEW.hold_id USING ERRCODE = 'EL402';
            END IF;
            IF NEW.state = 'Released' THEN
                IF NEW.occurred_at_utc < h.due_at_utc OR
                   EXISTS(SELECT FROM ledger.settlement_refunds WHERE settlement_id = h.settlement_id) OR
                   NOT EXISTS(SELECT FROM ledger.journal_entries e WHERE e.id = NEW.journal_entry_id AND e.finalized
                       AND e.currency = NEW.currency AND e.reversal_of IS NULL) OR
                   (SELECT count(*) FROM ledger.journal_lines WHERE entry_id = NEW.journal_entry_id) <> 2 OR
                   NOT EXISTS(SELECT FROM ledger.journal_lines WHERE entry_id = NEW.journal_entry_id
                       AND side = 1 AND account_id = h.escrow_account_id AND amount_minor = NEW.amount_minor) OR
                   NOT EXISTS(SELECT FROM ledger.journal_lines WHERE entry_id = NEW.journal_entry_id
                       AND side = 2 AND account_id = h.payable_account_id AND amount_minor = NEW.amount_minor) THEN
                    RAISE EXCEPTION 'Escrow release % does not match its hold and journal', NEW.hold_id USING ERRCODE = 'EL402';
                END IF;
            ELSIF NEW.state = 'Refunded' THEN
                IF NOT EXISTS(SELECT FROM ledger.settlement_refunds r
                       WHERE r.settlement_id = h.settlement_id AND r.journal_entry_id = NEW.journal_entry_id) THEN
                    RAISE EXCEPTION 'Escrow refund % has no matching settlement refund', NEW.hold_id USING ERRCODE = 'EL402';
                END IF;
            ELSE
                RAISE EXCEPTION 'Unsupported escrow hold transition %', NEW.state USING ERRCODE = 'EL401';
            END IF;
            RETURN NULL;
        END;
        $$;

        CREATE FUNCTION ledger.check_settlement_refund() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            s ledger.settlements%ROWTYPE;
        BEGIN
            SELECT * INTO s FROM ledger.settlements WHERE id = NEW.settlement_id;
            IF NEW.client_id <> s.client_id OR NEW.currency <> s.currency OR NEW.amount_minor <> s.gross_minor OR
               NOT EXISTS(SELECT FROM ledger.journal_entries e WHERE e.id = NEW.journal_entry_id AND e.finalized
                   AND e.currency = s.currency AND e.reversal_of = s.journal_entry_id) OR
               EXISTS(
                   (SELECT account_id, CASE side WHEN 1 THEN 2 ELSE 1 END AS side, amount_minor, count(*) AS lines
                      FROM ledger.journal_lines WHERE entry_id = s.journal_entry_id GROUP BY 1, 2, 3
                    EXCEPT
                    SELECT account_id, side, amount_minor, count(*)
                      FROM ledger.journal_lines WHERE entry_id = NEW.journal_entry_id GROUP BY 1, 2, 3)
                   UNION ALL
                   (SELECT account_id, side, amount_minor, count(*)
                      FROM ledger.journal_lines WHERE entry_id = NEW.journal_entry_id GROUP BY 1, 2, 3
                    EXCEPT
                    SELECT account_id, CASE side WHEN 1 THEN 2 ELSE 1 END, amount_minor, count(*)
                      FROM ledger.journal_lines WHERE entry_id = s.journal_entry_id GROUP BY 1, 2, 3)) OR
               EXISTS(SELECT FROM ledger.escrow_holds h LEFT JOIN ledger.escrow_hold_transitions t ON t.hold_id = h.id
                   WHERE h.settlement_id = s.id AND
                         (t.hold_id IS NULL OR t.state <> 'Refunded' OR t.journal_entry_id <> NEW.journal_entry_id)) THEN
                RAISE EXCEPTION 'Refund of settlement % is not an exact linked reversal', NEW.settlement_id USING ERRCODE = 'EL403';
            END IF;
            RETURN NULL;
        END;
        $$;
        CREATE CONSTRAINT TRIGGER settlement_refund_valid AFTER INSERT ON ledger.settlement_refunds
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ledger.check_settlement_refund();

        GRANT SELECT, INSERT ON ledger.settlement_refunds TO exactledger_runtime;
        CREATE TRIGGER settlement_refunds_immutable BEFORE UPDATE OR DELETE ON ledger.settlement_refunds
            FOR EACH ROW EXECUTE FUNCTION ledger.reject_history_change();
        CREATE TRIGGER settlement_refunds_no_truncate BEFORE TRUNCATE ON ledger.settlement_refunds
            EXECUTE FUNCTION ledger.reject_history_change();
        """;

    // Restores the BE-012.1 release-only transition check.
    internal const string Down = """
        DROP TRIGGER settlement_refund_valid ON ledger.settlement_refunds;
        DROP FUNCTION ledger.check_settlement_refund();

        CREATE OR REPLACE FUNCTION ledger.check_escrow_hold_transition() RETURNS trigger LANGUAGE plpgsql AS $$
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
        """;
}
