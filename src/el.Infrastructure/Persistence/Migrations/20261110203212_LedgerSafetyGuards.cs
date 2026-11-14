using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace el.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Database guards for journal history, independent of the domain checks:
    /// controlled finalization, deferred completed-entry validation, immutable posted history,
    /// and the least-privileged runtime role. Hand-written; it changes no EF model state.
    /// Functions are owned by the role that applies migrations (the schema owner), so
    /// SECURITY DEFINER functions run with the owner's rights.
    /// </summary>
    [DbContext(typeof(ExactLedgerDbContext))]
    [Migration("20261110203212_LedgerSafetyGuards")]
    public partial class LedgerSafetyGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Runtime privileges. The role is cluster-wide, so it is created only if missing.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'exactledger_runtime') THEN
                        CREATE ROLE exactledger_runtime NOLOGIN;
                    END IF;
                END
                $$;

                REVOKE ALL ON SCHEMA ledger FROM PUBLIC;
                GRANT USAGE ON SCHEMA ledger TO exactledger_runtime;
                GRANT SELECT ON ALL TABLES IN SCHEMA ledger TO exactledger_runtime;
                GRANT INSERT ON ledger.parties, ledger.accounts, ledger.journal_entries, ledger.journal_lines TO exactledger_runtime;
                GRANT UPDATE (status) ON ledger.parties, ledger.accounts TO exactledger_runtime;
                """);

            // Finalization is the only way to complete an entry; the runtime role cannot UPDATE entries.
            migrationBuilder.Sql("""
                CREATE FUNCTION ledger.finalize_journal_entry(p_entry_id uuid) RETURNS void
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    UPDATE ledger.journal_entries SET finalized = true WHERE id = p_entry_id AND NOT finalized;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'Journal entry % does not exist or is already finalized.', p_entry_id
                            USING ERRCODE = 'EL105';
                    END IF;
                END
                $$;

                REVOKE ALL ON FUNCTION ledger.finalize_journal_entry(uuid) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION ledger.finalize_journal_entry(uuid) TO exactledger_runtime;
                """);

            // No lines may be added to a finalized entry.
            migrationBuilder.Sql("""
                CREATE FUNCTION ledger.guard_journal_line_insert() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
                DECLARE
                    v_finalized boolean;
                BEGIN
                    SELECT finalized INTO v_finalized FROM ledger.journal_entries WHERE id = NEW.entry_id FOR SHARE;
                    IF v_finalized THEN
                        RAISE EXCEPTION 'Journal entry % is finalized; lines cannot be added.', NEW.entry_id
                            USING ERRCODE = 'EL101';
                    END IF;
                    RETURN NEW;
                END
                $$;

                CREATE TRIGGER journal_lines_guard_insert
                    BEFORE INSERT ON ledger.journal_lines
                    FOR EACH ROW EXECUTE FUNCTION ledger.guard_journal_line_insert();
                """);

            // Posted history is immutable for every role; only the finalization flag may change, once.
            migrationBuilder.Sql("""
                CREATE FUNCTION ledger.reject_history_change() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    RAISE EXCEPTION 'Posted journal history is immutable (% on %).', TG_OP, TG_TABLE_NAME
                        USING ERRCODE = 'EL104';
                END
                $$;

                CREATE FUNCTION ledger.guard_journal_entry_change() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    IF TG_OP = 'UPDATE'
                        AND NOT OLD.finalized AND NEW.finalized
                        AND (NEW.id, NEW.reference, NEW.description, NEW.currency, NEW.reversal_of)
                            IS NOT DISTINCT FROM (OLD.id, OLD.reference, OLD.description, OLD.currency, OLD.reversal_of)
                    THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'Posted journal history is immutable (% on journal_entries).', TG_OP
                        USING ERRCODE = 'EL104';
                END
                $$;

                CREATE TRIGGER journal_entries_immutable
                    BEFORE UPDATE OR DELETE ON ledger.journal_entries
                    FOR EACH ROW EXECUTE FUNCTION ledger.guard_journal_entry_change();
                CREATE TRIGGER journal_entries_no_truncate
                    BEFORE TRUNCATE ON ledger.journal_entries
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_history_change();
                CREATE TRIGGER journal_lines_immutable
                    BEFORE UPDATE OR DELETE ON ledger.journal_lines
                    FOR EACH ROW EXECUTE FUNCTION ledger.reject_history_change();
                CREATE TRIGGER journal_lines_no_truncate
                    BEFORE TRUNCATE ON ledger.journal_lines
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_history_change();
                """);

            // At commit, every touched entry must be finalized, have at least two lines and balance exactly.
            migrationBuilder.Sql("""
                CREATE FUNCTION ledger.assert_journal_entry_complete(p_entry_id uuid) RETURNS void
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
                DECLARE
                    v_finalized boolean;
                    v_lines bigint;
                    v_debits numeric;
                    v_credits numeric;
                BEGIN
                    SELECT finalized INTO v_finalized FROM ledger.journal_entries WHERE id = p_entry_id;
                    IF NOT FOUND THEN
                        RETURN;
                    END IF;
                    IF NOT v_finalized THEN
                        RAISE EXCEPTION 'Journal entry % was not finalized before commit.', p_entry_id
                            USING ERRCODE = 'EL103';
                    END IF;

                    SELECT count(*),
                           coalesce(sum(amount_minor) FILTER (WHERE side = 1), 0),
                           coalesce(sum(amount_minor) FILTER (WHERE side = 2), 0)
                      INTO v_lines, v_debits, v_credits
                      FROM ledger.journal_lines
                     WHERE entry_id = p_entry_id;

                    IF v_lines < 2 OR v_debits <> v_credits THEN
                        RAISE EXCEPTION 'Journal entry % is not balanced (lines %, debits %, credits %).',
                            p_entry_id, v_lines, v_debits, v_credits
                            USING ERRCODE = 'EL102';
                    END IF;
                END
                $$;

                CREATE FUNCTION ledger.validate_journal_entry_row() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    PERFORM ledger.assert_journal_entry_complete(NEW.id);
                    RETURN NULL;
                END
                $$;

                CREATE FUNCTION ledger.validate_journal_line_row() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    PERFORM ledger.assert_journal_entry_complete(NEW.entry_id);
                    RETURN NULL;
                END
                $$;

                CREATE CONSTRAINT TRIGGER journal_entries_complete
                    AFTER INSERT OR UPDATE ON ledger.journal_entries
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION ledger.validate_journal_entry_row();
                CREATE CONSTRAINT TRIGGER journal_lines_complete
                    AFTER INSERT ON ledger.journal_lines
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION ledger.validate_journal_line_row();

                REVOKE ALL ON FUNCTION
                    ledger.guard_journal_line_insert(),
                    ledger.reject_history_change(),
                    ledger.guard_journal_entry_change(),
                    ledger.assert_journal_entry_complete(uuid),
                    ledger.validate_journal_entry_row(),
                    ledger.validate_journal_line_row()
                FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The cluster-wide role is kept: other databases may use it.
            migrationBuilder.Sql("""
                DROP TRIGGER journal_lines_complete ON ledger.journal_lines;
                DROP TRIGGER journal_entries_complete ON ledger.journal_entries;
                DROP TRIGGER journal_lines_no_truncate ON ledger.journal_lines;
                DROP TRIGGER journal_lines_immutable ON ledger.journal_lines;
                DROP TRIGGER journal_entries_no_truncate ON ledger.journal_entries;
                DROP TRIGGER journal_entries_immutable ON ledger.journal_entries;
                DROP TRIGGER journal_lines_guard_insert ON ledger.journal_lines;

                DROP FUNCTION ledger.validate_journal_line_row();
                DROP FUNCTION ledger.validate_journal_entry_row();
                DROP FUNCTION ledger.assert_journal_entry_complete(uuid);
                DROP FUNCTION ledger.guard_journal_entry_change();
                DROP FUNCTION ledger.reject_history_change();
                DROP FUNCTION ledger.guard_journal_line_insert();
                DROP FUNCTION ledger.finalize_journal_entry(uuid);

                REVOKE ALL ON ALL TABLES IN SCHEMA ledger FROM exactledger_runtime;
                REVOKE ALL ON SCHEMA ledger FROM exactledger_runtime;
                """);
        }
    }
}
