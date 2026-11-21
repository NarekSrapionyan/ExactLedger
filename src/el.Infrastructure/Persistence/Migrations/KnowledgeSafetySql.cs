namespace el.Infrastructure.Persistence.Migrations;

internal static class KnowledgeSafetySql
{
    internal const string Up = """
        ALTER TABLE ledger.deduction_rules ADD CONSTRAINT fk_rule_beneficiary
            FOREIGN KEY (beneficiary_account_id) REFERENCES ledger.accounts(id);
        ALTER TABLE ledger.deduction_rules ADD CONSTRAINT fk_rule_currency
            FOREIGN KEY (currency) REFERENCES ledger.currencies(code);
        ALTER TABLE ledger.fee_profiles ADD CONSTRAINT fk_profile_commission
            FOREIGN KEY (commission_account_id) REFERENCES ledger.accounts(id);
        ALTER TABLE ledger.fee_profiles ADD CONSTRAINT fk_profile_processor
            FOREIGN KEY (processor_account_id) REFERENCES ledger.accounts(id);
        ALTER TABLE ledger.fee_profiles ADD CONSTRAINT fk_profile_currency
            FOREIGN KEY (currency) REFERENCES ledger.currencies(code);

        CREATE FUNCTION ledger.knowledge_account_valid(account_id uuid, currency_code text, required_type integer)
        RETURNS boolean LANGUAGE sql STABLE AS $$
            SELECT EXISTS(SELECT FROM ledger.accounts a LEFT JOIN ledger.parties p ON p.id = a.party_id
                WHERE a.id = account_id AND a.currency = currency_code AND a.type = required_type
                  AND a.status = 1 AND (a.party_id IS NULL OR p.status = 1));
        $$;

        CREATE FUNCTION ledger.guard_knowledge_version() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE previous_version integer; previous_end timestamptz;
        BEGIN
            -- Same key as MonetaryOperationLock: serialize publication with monetary operations.
            PERFORM pg_advisory_xact_lock(4993451089857960241);
            IF TG_OP <> 'INSERT' THEN
                IF OLD.published_at_utc IS NOT NULL THEN
                    RAISE EXCEPTION 'Published configuration is immutable' USING ERRCODE = 'EL201';
                END IF;
                IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
            END IF;
            IF NEW.published_at_utc IS NULL THEN RETURN NEW; END IF;
            IF TG_TABLE_NAME = 'deduction_rules' THEN
                IF NOT ledger.knowledge_account_valid(NEW.beneficiary_account_id, NEW.currency, 2) OR
                   NOT EXISTS(SELECT FROM ledger.rule_fixtures WHERE rule_version_id = NEW.id) THEN
                    RAISE EXCEPTION 'Invalid rule mapping or missing fixtures' USING ERRCODE = 'EL202';
                END IF;
                SELECT version, effective_to INTO previous_version, previous_end FROM ledger.deduction_rules
                    WHERE rule_id = NEW.rule_id AND published_at_utc IS NOT NULL AND id <> NEW.id ORDER BY version DESC LIMIT 1;
                IF EXISTS(SELECT FROM ledger.deduction_rules WHERE rule_id = NEW.rule_id AND published_at_utc IS NOT NULL AND id <> NEW.id
                    AND (region_code <> NEW.region_code OR deduction_type_code <> NEW.deduction_type_code OR currency <> NEW.currency)) OR
                   EXISTS(SELECT FROM ledger.deduction_rules WHERE id <> NEW.id AND published_at_utc IS NOT NULL
                    AND region_code = NEW.region_code AND effective_from < NEW.effective_to AND NEW.effective_from < effective_to
                    AND (deduction_type_code = NEW.deduction_type_code OR sequence = NEW.sequence)) THEN
                    RAISE EXCEPTION 'Ambiguous regional publication' USING ERRCODE = 'EL202';
                END IF;
            ELSE
                IF NOT ledger.knowledge_account_valid(NEW.commission_account_id, NEW.currency, 4) OR
                   NOT ledger.knowledge_account_valid(NEW.processor_account_id, NEW.currency, 2) OR
                   NOT EXISTS(SELECT FROM ledger.profile_fixtures WHERE profile_version_id = NEW.id) THEN
                    RAISE EXCEPTION 'Invalid profile mapping or missing fixtures' USING ERRCODE = 'EL202';
                END IF;
                SELECT version, effective_to INTO previous_version, previous_end FROM ledger.fee_profiles
                    WHERE profile_id = NEW.profile_id AND published_at_utc IS NOT NULL AND id <> NEW.id ORDER BY version DESC LIMIT 1;
                IF EXISTS(SELECT FROM ledger.fee_profiles WHERE profile_id = NEW.profile_id AND published_at_utc IS NOT NULL AND id <> NEW.id
                    AND (client_id <> NEW.client_id OR corporate_code <> NEW.corporate_code OR currency <> NEW.currency)) OR
                   EXISTS(SELECT FROM ledger.fee_profiles WHERE id <> NEW.id AND published_at_utc IS NOT NULL
                    AND client_id = NEW.client_id AND corporate_code = NEW.corporate_code AND currency = NEW.currency
                    AND effective_from < NEW.effective_to AND NEW.effective_from < effective_to) THEN
                    RAISE EXCEPTION 'Ambiguous profile publication' USING ERRCODE = 'EL202';
                END IF;
            END IF;
            IF NEW.version <> COALESCE(previous_version, 0) + 1 OR (previous_end IS NOT NULL AND NEW.effective_from <> previous_end) THEN
                RAISE EXCEPTION 'Replacement versions must be consecutive and adjacent' USING ERRCODE = 'EL202';
            END IF;
            RETURN NEW;
        END;
        $$;

        CREATE FUNCTION ledger.guard_knowledge_fixture() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE old_parent uuid; new_parent uuid; immutable boolean;
        BEGIN
            PERFORM pg_advisory_xact_lock(4993451089857960241);
            IF TG_TABLE_NAME = 'rule_fixtures' THEN
                IF TG_OP <> 'INSERT' THEN old_parent := OLD.rule_version_id; END IF;
                IF TG_OP <> 'DELETE' THEN new_parent := NEW.rule_version_id; END IF;
                SELECT EXISTS(SELECT FROM ledger.deduction_rules WHERE id IN (old_parent, new_parent) AND published_at_utc IS NOT NULL) INTO immutable;
            ELSE
                IF TG_OP <> 'INSERT' THEN old_parent := OLD.profile_version_id; END IF;
                IF TG_OP <> 'DELETE' THEN new_parent := NEW.profile_version_id; END IF;
                SELECT EXISTS(SELECT FROM ledger.fee_profiles WHERE id IN (old_parent, new_parent) AND published_at_utc IS NOT NULL) INTO immutable;
            END IF;
            IF immutable THEN RAISE EXCEPTION 'Published fixtures are immutable' USING ERRCODE = 'EL201'; END IF;
            IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
            RETURN NEW;
        END;
        $$;

        CREATE TRIGGER rules_guard BEFORE INSERT OR UPDATE OR DELETE ON ledger.deduction_rules FOR EACH ROW EXECUTE FUNCTION ledger.guard_knowledge_version();
        CREATE TRIGGER profiles_guard BEFORE INSERT OR UPDATE OR DELETE ON ledger.fee_profiles FOR EACH ROW EXECUTE FUNCTION ledger.guard_knowledge_version();
        CREATE TRIGGER rule_fixtures_guard BEFORE INSERT OR UPDATE OR DELETE ON ledger.rule_fixtures FOR EACH ROW EXECUTE FUNCTION ledger.guard_knowledge_fixture();
        CREATE TRIGGER profile_fixtures_guard BEFORE INSERT OR UPDATE OR DELETE ON ledger.profile_fixtures FOR EACH ROW EXECUTE FUNCTION ledger.guard_knowledge_fixture();
        CREATE TRIGGER rules_no_truncate BEFORE TRUNCATE ON ledger.deduction_rules EXECUTE FUNCTION ledger.reject_history_change();
        CREATE TRIGGER profiles_no_truncate BEFORE TRUNCATE ON ledger.fee_profiles EXECUTE FUNCTION ledger.reject_history_change();
        CREATE TRIGGER rule_fixtures_no_truncate BEFORE TRUNCATE ON ledger.rule_fixtures EXECUTE FUNCTION ledger.reject_history_change();
        CREATE TRIGGER profile_fixtures_no_truncate BEFORE TRUNCATE ON ledger.profile_fixtures EXECUTE FUNCTION ledger.reject_history_change();

        GRANT SELECT, INSERT ON ledger.regions, ledger.deduction_types TO exactledger_runtime;
        GRANT SELECT, INSERT, UPDATE ON ledger.deduction_rules, ledger.fee_profiles TO exactledger_runtime;
        GRANT SELECT, INSERT, UPDATE, DELETE ON ledger.rule_fixtures, ledger.profile_fixtures TO exactledger_runtime;
        """;

    internal const string Down = """
        DROP FUNCTION ledger.guard_knowledge_fixture();
        DROP FUNCTION ledger.guard_knowledge_version();
        DROP FUNCTION ledger.knowledge_account_valid(uuid, text, integer);
        """;
}
