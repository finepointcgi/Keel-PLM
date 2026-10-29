-- Increment 8.1: quality processes (ADR-0036, docs/increments/8.1-quality.md). Forward-only, runs as keel_migrator.
--   * nonconformance reports (NCR) with containment actions and a signed material review board (MRB) disposition;
--   * corrective and preventive actions (CAPA) with 5-whys and cause-category analysis, action items, effectiveness verification and signed closure;
--   * deviations and waivers with part/serial/lot/quantity scope, a time window, optional customer approval and automatic expiry;
--   * three data-defined lifecycles (versioned families, view-only like the change lifecycles), review snapshots, links, history;
--   * workflow templates for the three reviews; the awareness tables accept the new object types (pattern-based and idempotent, order-independent with 0020).
-- Marked data: NCR, CAPA and deviation rows carry the marking columns of ADR-0004. Child rows (actions, analysis, scope, links, history) carry none:
-- they are readable only through the readable parent row.
CREATE SCHEMA qual;
GRANT USAGE ON SCHEMA qual TO keel_app;

CREATE SEQUENCE qual.ncr_seq START 1;
CREATE SEQUENCE qual.capa_seq START 1;
CREATE SEQUENCE qual.deviation_seq START 1;
CREATE SEQUENCE qual.action_seq START 1;
GRANT USAGE ON SEQUENCE qual.ncr_seq, qual.capa_seq, qual.deviation_seq, qual.action_seq TO keel_app;

-- ================================================================ lifecycles are data (shared tables of 0002 and 0009)
-- 'system': a transition only the system's own jobs take (a deviation or waiver that reached its end date).
DO $$
DECLARE k record;
BEGIN
    FOR k IN SELECT conname FROM pg_constraint WHERE conrelid = 'plm.lifecycle_transition'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%via%'
    LOOP EXECUTE format('ALTER TABLE plm.lifecycle_transition DROP CONSTRAINT %I', k.conname); END LOOP;
    ALTER TABLE plm.lifecycle_transition ADD CONSTRAINT lifecycle_transition_via_known CHECK (via IN ('manual', 'workflow', 'change', 'system'));
END;
$$;

INSERT INTO plm.lifecycle_family (family, object_type, name, current_version, is_system, editable) VALUES
    ('ncr-default',       'ncr',       'Nonconformance report lifecycle', 1, true, false),
    ('capa-default',      'capa',      'Corrective action lifecycle',     1, true, false),
    ('deviation-default', 'deviation', 'Deviation and waiver lifecycle',  1, true, false);
INSERT INTO plm.lifecycle (id, name, family, version) VALUES
    ('ncr-default', 'Nonconformance report lifecycle', 'ncr-default', 1),
    ('capa-default', 'Corrective action lifecycle', 'capa-default', 1),
    ('deviation-default', 'Deviation and waiver lifecycle', 'deviation-default', 1);
INSERT INTO plm.lifecycle_state (lifecycle_id, state, label, sort_order, is_initial, is_locked, is_final) VALUES
    ('ncr-default', 'Open',          'Open',           10, true,  false, false),
    ('ncr-default', 'UnderReview',   'MRB review',     20, false, true,  false),
    ('ncr-default', 'Dispositioned', 'Dispositioned',  30, false, true,  false),
    ('ncr-default', 'Closed',        'Closed',         40, false, true,  true),
    ('ncr-default', 'Cancelled',     'Cancelled',      50, false, true,  true),
    ('capa-default', 'Open',         'Open',           10, true,  false, false),
    ('capa-default', 'InProgress',   'In progress',    20, false, false, false),
    ('capa-default', 'Verification', 'Verification',   30, false, true,  false),
    ('capa-default', 'Closed',       'Closed',         40, false, true,  true),
    ('capa-default', 'Cancelled',    'Cancelled',      50, false, true,  true),
    ('deviation-default', 'Draft',            'Draft',             10, true,  false, false),
    ('deviation-default', 'UnderReview',      'Under review',      20, false, true,  false),
    ('deviation-default', 'AwaitingCustomer', 'Awaiting customer', 30, false, true,  false),
    ('deviation-default', 'Approved',         'Approved',          40, false, true,  false),
    ('deviation-default', 'Expired',          'Expired',           50, false, true,  true),
    ('deviation-default', 'Revoked',          'Revoked',           60, false, true,  true),
    ('deviation-default', 'Rejected',         'Rejected',          70, false, true,  true),
    ('deviation-default', 'Withdrawn',        'Withdrawn',         80, false, true,  true);
INSERT INTO plm.lifecycle_transition (lifecycle_id, from_state, to_state, via) VALUES
    ('ncr-default', 'Open', 'UnderReview', 'workflow'),
    ('ncr-default', 'UnderReview', 'Dispositioned', 'workflow'),
    ('ncr-default', 'UnderReview', 'Open', 'workflow'),
    ('ncr-default', 'Dispositioned', 'Closed', 'manual'),
    ('ncr-default', 'Open', 'Cancelled', 'manual'),
    ('capa-default', 'Open', 'InProgress', 'manual'),
    ('capa-default', 'InProgress', 'Verification', 'workflow'),
    ('capa-default', 'Verification', 'Closed', 'workflow'),
    ('capa-default', 'Verification', 'InProgress', 'workflow'),
    ('capa-default', 'Open', 'Cancelled', 'manual'),
    ('capa-default', 'InProgress', 'Cancelled', 'manual'),
    ('deviation-default', 'Draft', 'UnderReview', 'workflow'),
    ('deviation-default', 'UnderReview', 'Approved', 'workflow'),
    ('deviation-default', 'UnderReview', 'AwaitingCustomer', 'workflow'),
    ('deviation-default', 'UnderReview', 'Draft', 'workflow'),
    ('deviation-default', 'AwaitingCustomer', 'Approved', 'manual'),
    ('deviation-default', 'AwaitingCustomer', 'Rejected', 'manual'),
    ('deviation-default', 'Approved', 'Expired', 'system'),
    ('deviation-default', 'Approved', 'Revoked', 'manual'),
    ('deviation-default', 'Draft', 'Withdrawn', 'manual');

CREATE FUNCTION qual.is_locked_state(p_lifecycle text, p_state text) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT COALESCE((SELECT is_locked FROM plm.lifecycle_state WHERE lifecycle_id = p_lifecycle AND state = p_state), true);
$$;
CREATE FUNCTION qual.is_final_state(p_lifecycle text, p_state text) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT COALESCE((SELECT is_final FROM plm.lifecycle_state WHERE lifecycle_id = p_lifecycle AND state = p_state), true);
$$;
CREATE FUNCTION qual.reject_modification() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'quality history and snapshots are never modified or deleted (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;

-- Shared guard: the lifecycle data decides which transitions exist; a state that only a signed review reaches needs an approving signature.
CREATE FUNCTION qual.guard_transition(p_kind text, p_number text, p_lifecycle text, p_from text, p_to text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_via text;
BEGIN
    IF p_from = p_to THEN RETURN; END IF;
    SELECT via INTO v_via FROM plm.lifecycle_transition WHERE lifecycle_id = p_lifecycle AND from_state = p_from AND to_state = p_to;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'lifecycle % has no transition from % to %', p_lifecycle, p_from, p_to USING ERRCODE = 'check_violation';
    END IF;
    IF v_via = 'workflow' AND p_to IN ('Dispositioned', 'Closed', 'Approved', 'AwaitingCustomer')
       AND NOT EXISTS (SELECT 1 FROM wf.signature s WHERE s.object_type = p_kind AND s.object_key = p_number AND s.decision = 'approve') THEN
        RAISE EXCEPTION '% % cannot reach % without an approving signature', p_kind, p_number, p_to USING ERRCODE = 'check_violation';
    END IF;
END;
$$;

-- ================================================================ nonconformance reports
CREATE TABLE qual.ncr (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE CHECK (number ~ '^NCR-[0-9]{6,}$'),
    title                  text        NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    description            text        NOT NULL DEFAULT '' CHECK (length(description) <= 8000),
    severity               text        NOT NULL CHECK (severity IN ('Minor', 'Major', 'Critical')),
    detected_at_stage      text        NOT NULL DEFAULT 'Other' CHECK (detected_at_stage IN ('Receiving', 'InProcess', 'Final', 'Field', 'Audit', 'Other')),
    part_master_id         uuid        NOT NULL,
    part_number            text        NOT NULL,
    part_revision_id       uuid        NOT NULL,
    part_revision          text        NOT NULL,
    lot_number             text        CHECK (lot_number IS NULL OR length(lot_number) BETWEEN 1 AND 100),
    serial_number          text        CHECK (serial_number IS NULL OR length(serial_number) BETWEEN 1 AND 100),
    -- An opaque reference to a supplier record (the sourcing module owns suppliers). Never resolved here.
    supplier_ref           text        CHECK (supplier_ref IS NULL OR length(supplier_ref) BETWEEN 1 AND 200),
    quantity               numeric(18,4) NOT NULL CHECK (quantity > 0),
    unit                   text        NOT NULL DEFAULT 'EA' CHECK (length(unit) BETWEEN 1 AND 10),
    proposed_disposition   text        CHECK (proposed_disposition IS NULL OR proposed_disposition IN ('UseAsIs', 'Rework', 'Repair', 'Scrap', 'ReturnToSupplier')),
    disposition_rationale  text        NOT NULL DEFAULT '' CHECK (length(disposition_rationale) <= 4000),
    -- The disposition the MRB approved (set when the review completes); the execution note is written when it was carried out.
    disposition            text        CHECK (disposition IS NULL OR disposition IN ('UseAsIs', 'Rework', 'Repair', 'Scrap', 'ReturnToSupplier')),
    disposition_note       text        CHECK (disposition_note IS NULL OR length(disposition_note) <= 4000),
    cancel_reason          text        CHECK (cancel_reason IS NULL OR length(cancel_reason) <= 1000),
    lifecycle_id           text        NOT NULL,
    lifecycle_state        text        NOT NULL,
    submission_no          int         NOT NULL DEFAULT 0 CHECK (submission_no >= 0),
    reporter_sub           text        NOT NULL,
    reporter_username      text        NOT NULL,
    reporter_name          text        NOT NULL,
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL,
    created_at             timestamptz NOT NULL,
    created_by             text        NOT NULL,
    state_changed_at       timestamptz NOT NULL,
    state_changed_by       text        NOT NULL,
    FOREIGN KEY (lifecycle_id, lifecycle_state) REFERENCES plm.lifecycle_state (lifecycle_id, state),
    CONSTRAINT ncr_lifecycle_family CHECK (split_part(lifecycle_id, '~v', 1) = 'ncr-default'),
    CONSTRAINT ncr_review_has_proposal CHECK (lifecycle_state NOT IN ('UnderReview', 'Dispositioned', 'Closed') OR proposed_disposition IS NOT NULL),
    CONSTRAINT ncr_dispositioned_has_disposition CHECK (lifecycle_state NOT IN ('Dispositioned', 'Closed') OR disposition IS NOT NULL),
    CONSTRAINT ncr_closed_has_note CHECK (lifecycle_state <> 'Closed' OR length(btrim(COALESCE(disposition_note, ''))) > 0),
    CONSTRAINT ncr_cancelled_has_reason CHECK (lifecycle_state <> 'Cancelled' OR length(btrim(COALESCE(cancel_reason, ''))) > 0)
);
CREATE INDEX ncr_state_idx ON qual.ncr (lifecycle_state, created_at DESC);
CREATE INDEX ncr_part_idx ON qual.ncr (part_master_id);
CREATE INDEX ncr_program_idx ON qual.ncr (program);
CREATE INDEX ncr_number_trgm ON qual.ncr USING gin (number gin_trgm_ops);
CREATE INDEX ncr_title_trgm ON qual.ncr USING gin (title gin_trgm_ops);

CREATE FUNCTION qual.guard_ncr() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id <> OLD.id OR NEW.number <> OLD.number OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by OR NEW.reporter_sub <> OLD.reporter_sub
       OR NEW.lifecycle_id <> OLD.lifecycle_id THEN
        RAISE EXCEPTION 'nonconformance identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    IF qual.is_locked_state(OLD.lifecycle_id, OLD.lifecycle_state) AND (
           NEW.title <> OLD.title OR NEW.description <> OLD.description OR NEW.severity <> OLD.severity OR NEW.detected_at_stage <> OLD.detected_at_stage
        OR NEW.part_master_id <> OLD.part_master_id OR NEW.part_revision_id <> OLD.part_revision_id OR NEW.lot_number IS DISTINCT FROM OLD.lot_number
        OR NEW.serial_number IS DISTINCT FROM OLD.serial_number OR NEW.supplier_ref IS DISTINCT FROM OLD.supplier_ref OR NEW.quantity <> OLD.quantity OR NEW.unit <> OLD.unit
        OR NEW.proposed_disposition IS DISTINCT FROM OLD.proposed_disposition OR NEW.disposition_rationale <> OLD.disposition_rationale
        OR NEW.export_jurisdiction <> OLD.export_jurisdiction OR NEW.eccn IS DISTINCT FROM OLD.eccn
        OR NEW.usml_category IS DISTINCT FROM OLD.usml_category OR NEW.cui_categories <> OLD.cui_categories
        OR NEW.distribution_statement <> OLD.distribution_statement OR NEW.controlling_office IS DISTINCT FROM OLD.controlling_office
        OR NEW.distribution_reason IS DISTINCT FROM OLD.distribution_reason OR NEW.program IS DISTINCT FROM OLD.program
        OR NEW.owning_org <> OLD.owning_org OR NEW.export_authorizations <> OLD.export_authorizations) THEN
        RAISE EXCEPTION 'NCR % is locked in state %', OLD.number, OLD.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.disposition IS NOT NULL AND NEW.disposition IS DISTINCT FROM OLD.disposition THEN
        RAISE EXCEPTION 'an approved disposition never changes' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.submission_no < OLD.submission_no THEN RAISE EXCEPTION 'submission numbers only increase' USING ERRCODE = 'check_violation'; END IF;
    PERFORM qual.guard_transition('ncr', OLD.number, OLD.lifecycle_id, OLD.lifecycle_state, NEW.lifecycle_state);
    RETURN NEW;
END;
$$;
CREATE TRIGGER ncr_update_guard BEFORE UPDATE ON qual.ncr FOR EACH ROW EXECUTE FUNCTION qual.guard_ncr();
CREATE TRIGGER ncr_no_delete BEFORE DELETE ON qual.ncr FOR EACH ROW EXECUTE FUNCTION qual.reject_modification();

-- ================================================================ corrective and preventive actions
CREATE TABLE qual.capa (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE CHECK (number ~ '^CAPA-[0-9]{6,}$'),
    title                  text        NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    description            text        NOT NULL DEFAULT '' CHECK (length(description) <= 8000),
    capa_type              text        NOT NULL CHECK (capa_type IN ('Corrective', 'Preventive')),
    source                 text        NOT NULL CHECK (source IN ('Ncr', 'Audit', 'Customer', 'Process', 'Other')),
    priority               text        NOT NULL CHECK (priority IN ('Low', 'Medium', 'High', 'Critical')),
    owner_sub              text        NOT NULL,
    owner_username         text        NOT NULL,
    owner_name             text        NOT NULL,
    target_date            date,
    root_cause             text        NOT NULL DEFAULT '' CHECK (length(root_cause) <= 4000),
    verification_method    text        NOT NULL DEFAULT '' CHECK (length(verification_method) <= 2000),
    verification_criteria  text        NOT NULL DEFAULT '' CHECK (length(verification_criteria) <= 2000),
    verification_due       date,
    verification_result    text        NOT NULL DEFAULT '' CHECK (length(verification_result) <= 4000),
    verification_effective boolean,
    verification_by        text,
    verification_at        timestamptz,
    cancel_reason          text        CHECK (cancel_reason IS NULL OR length(cancel_reason) <= 1000),
    lifecycle_id           text        NOT NULL,
    lifecycle_state        text        NOT NULL,
    submission_no          int         NOT NULL DEFAULT 0 CHECK (submission_no >= 0),
    requester_sub          text        NOT NULL,
    requester_username     text        NOT NULL,
    requester_name         text        NOT NULL,
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL,
    created_at             timestamptz NOT NULL,
    created_by             text        NOT NULL,
    state_changed_at       timestamptz NOT NULL,
    state_changed_by       text        NOT NULL,
    FOREIGN KEY (lifecycle_id, lifecycle_state) REFERENCES plm.lifecycle_state (lifecycle_id, state),
    CONSTRAINT capa_lifecycle_family CHECK (split_part(lifecycle_id, '~v', 1) = 'capa-default'),
    CONSTRAINT capa_verified_before_review CHECK (lifecycle_state NOT IN ('Verification', 'Closed') OR (verification_effective IS TRUE AND length(btrim(root_cause)) > 0 AND length(btrim(verification_result)) > 0)),
    CONSTRAINT capa_cancelled_has_reason CHECK (lifecycle_state <> 'Cancelled' OR length(btrim(COALESCE(cancel_reason, ''))) > 0)
);
CREATE INDEX capa_state_idx ON qual.capa (lifecycle_state, created_at DESC);
CREATE INDEX capa_program_idx ON qual.capa (program);
CREATE INDEX capa_number_trgm ON qual.capa USING gin (number gin_trgm_ops);
CREATE INDEX capa_title_trgm ON qual.capa USING gin (title gin_trgm_ops);

-- Actions are created after their parent; the CAPA guard refers to them, so the table comes first (below) and the function is created here.
CREATE TABLE qual.action (
    id                 uuid        PRIMARY KEY,
    number             text        NOT NULL UNIQUE CHECK (number ~ '^ACT-[0-9]{6,}$'),
    parent_kind        text        NOT NULL CHECK (parent_kind IN ('ncr', 'capa')),
    parent_id          uuid        NOT NULL,
    action_type        text        NOT NULL CHECK (action_type IN ('Containment', 'Correction', 'Corrective', 'Preventive')),
    description        text        NOT NULL CHECK (length(btrim(description)) BETWEEN 1 AND 2000),
    owner_sub          text        NOT NULL,
    owner_username     text        NOT NULL,
    owner_name         text        NOT NULL,
    due_date           date,
    status             text        NOT NULL DEFAULT 'Open' CHECK (status IN ('Open', 'InProgress', 'Done', 'Cancelled')),
    completion_note    text        CHECK (completion_note IS NULL OR length(completion_note) <= 2000),
    cancel_reason      text        CHECK (cancel_reason IS NULL OR length(cancel_reason) <= 1000),
    overdue_notified_at timestamptz,
    created_at         timestamptz NOT NULL,
    created_by         text        NOT NULL,
    started_at         timestamptz,
    completed_at       timestamptz,
    completed_by       text,
    CONSTRAINT action_done_has_note CHECK (status <> 'Done' OR length(btrim(COALESCE(completion_note, ''))) > 0),
    CONSTRAINT action_cancelled_has_reason CHECK (status <> 'Cancelled' OR length(btrim(COALESCE(cancel_reason, ''))) > 0),
    CONSTRAINT action_type_matches_parent CHECK ((parent_kind = 'ncr' AND action_type IN ('Containment', 'Correction')) OR (parent_kind = 'capa' AND action_type IN ('Corrective', 'Preventive')))
);
CREATE INDEX action_parent_idx ON qual.action (parent_kind, parent_id);
CREATE INDEX action_owner_idx ON qual.action (owner_sub) WHERE status IN ('Open', 'InProgress');
CREATE INDEX action_due_idx ON qual.action (due_date) WHERE status IN ('Open', 'InProgress');

CREATE FUNCTION qual.guard_capa() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id <> OLD.id OR NEW.number <> OLD.number OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by OR NEW.requester_sub <> OLD.requester_sub
       OR NEW.lifecycle_id <> OLD.lifecycle_id THEN
        RAISE EXCEPTION 'corrective action identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    IF qual.is_locked_state(OLD.lifecycle_id, OLD.lifecycle_state) AND (
           NEW.title <> OLD.title OR NEW.description <> OLD.description OR NEW.capa_type <> OLD.capa_type OR NEW.source <> OLD.source OR NEW.priority <> OLD.priority
        OR NEW.owner_sub <> OLD.owner_sub OR NEW.target_date IS DISTINCT FROM OLD.target_date OR NEW.root_cause <> OLD.root_cause
        OR NEW.verification_method <> OLD.verification_method OR NEW.verification_criteria <> OLD.verification_criteria OR NEW.verification_due IS DISTINCT FROM OLD.verification_due
        OR NEW.verification_result <> OLD.verification_result OR NEW.verification_effective IS DISTINCT FROM OLD.verification_effective
        OR NEW.export_jurisdiction <> OLD.export_jurisdiction OR NEW.eccn IS DISTINCT FROM OLD.eccn
        OR NEW.usml_category IS DISTINCT FROM OLD.usml_category OR NEW.cui_categories <> OLD.cui_categories
        OR NEW.distribution_statement <> OLD.distribution_statement OR NEW.controlling_office IS DISTINCT FROM OLD.controlling_office
        OR NEW.distribution_reason IS DISTINCT FROM OLD.distribution_reason OR NEW.program IS DISTINCT FROM OLD.program
        OR NEW.owning_org <> OLD.owning_org OR NEW.export_authorizations <> OLD.export_authorizations) THEN
        RAISE EXCEPTION 'CAPA % is locked in state %', OLD.number, OLD.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.submission_no < OLD.submission_no THEN RAISE EXCEPTION 'submission numbers only increase' USING ERRCODE = 'check_violation'; END IF;
    PERFORM qual.guard_transition('capa', OLD.number, OLD.lifecycle_id, OLD.lifecycle_state, NEW.lifecycle_state);
    -- Review and closure need every action finished.
    IF NEW.lifecycle_state IN ('Verification', 'Closed') AND OLD.lifecycle_state <> NEW.lifecycle_state AND EXISTS (
        SELECT 1 FROM qual.action a WHERE a.parent_kind = 'capa' AND a.parent_id = NEW.id AND a.status IN ('Open', 'InProgress')) THEN
        RAISE EXCEPTION 'CAPA % still has open actions', NEW.number USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER capa_update_guard BEFORE UPDATE ON qual.capa FOR EACH ROW EXECUTE FUNCTION qual.guard_capa();
CREATE TRIGGER capa_no_delete BEFORE DELETE ON qual.capa FOR EACH ROW EXECUTE FUNCTION qual.reject_modification();

-- Root cause analysis: a chain of whys and cause categories (fishbone), editable while the CAPA is editable.
CREATE TABLE qual.rca_why (
    id        uuid PRIMARY KEY,
    capa_id   uuid NOT NULL REFERENCES qual.capa (id),
    seq       int  NOT NULL CHECK (seq BETWEEN 1 AND 8),
    why_text  text NOT NULL CHECK (length(btrim(why_text)) BETWEEN 1 AND 1000),
    evidence  text NOT NULL DEFAULT '' CHECK (length(evidence) <= 1000),
    UNIQUE (capa_id, seq)
);
CREATE TABLE qual.rca_cause (
    id          uuid PRIMARY KEY,
    capa_id     uuid NOT NULL REFERENCES qual.capa (id),
    category    text NOT NULL CHECK (category IN ('Man', 'Machine', 'Method', 'Material', 'Measurement', 'Environment')),
    description text NOT NULL CHECK (length(btrim(description)) BETWEEN 1 AND 1000),
    is_root     boolean NOT NULL DEFAULT false
);
CREATE INDEX rca_cause_capa_idx ON qual.rca_cause (capa_id);

CREATE FUNCTION qual.guard_capa_child() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE c qual.capa; v_id uuid;
BEGIN
    v_id := COALESCE(NEW.capa_id, OLD.capa_id);
    SELECT * INTO c FROM qual.capa WHERE id = v_id;
    IF qual.is_locked_state(c.lifecycle_id, c.lifecycle_state) THEN
        RAISE EXCEPTION 'CAPA % is % and its analysis is frozen', c.number, c.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    RETURN COALESCE(NEW, OLD);
END;
$$;
CREATE TRIGGER rca_why_guard BEFORE INSERT OR UPDATE OR DELETE ON qual.rca_why FOR EACH ROW EXECUTE FUNCTION qual.guard_capa_child();
CREATE TRIGGER rca_cause_guard BEFORE INSERT OR UPDATE OR DELETE ON qual.rca_cause FOR EACH ROW EXECUTE FUNCTION qual.guard_capa_child();

-- ================================================================ action items (NCR containment, CAPA corrective and preventive actions)
CREATE FUNCTION qual.guard_action() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_lc text; v_state text;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF OLD.status IN ('Done', 'Cancelled') THEN RAISE EXCEPTION 'a finished action is immutable' USING ERRCODE = 'check_violation'; END IF;
        IF NEW.id <> OLD.id OR NEW.number <> OLD.number OR NEW.parent_kind <> OLD.parent_kind OR NEW.parent_id <> OLD.parent_id OR NEW.owner_sub <> OLD.owner_sub
           OR NEW.created_at <> OLD.created_at THEN
            RAISE EXCEPTION 'action identity is immutable' USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.parent_kind = 'ncr' THEN SELECT lifecycle_id, lifecycle_state INTO v_lc, v_state FROM qual.ncr WHERE id = NEW.parent_id;
    ELSE SELECT lifecycle_id, lifecycle_state INTO v_lc, v_state FROM qual.capa WHERE id = NEW.parent_id; END IF;
    IF v_lc IS NULL THEN RAISE EXCEPTION 'the action names no record' USING ERRCODE = 'check_violation'; END IF;
    IF qual.is_final_state(v_lc, v_state) OR (NEW.parent_kind = 'capa' AND qual.is_locked_state(v_lc, v_state)) THEN
        RAISE EXCEPTION 'the record is % and takes no new actions', v_state USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER action_guard BEFORE INSERT OR UPDATE ON qual.action FOR EACH ROW EXECUTE FUNCTION qual.guard_action();
CREATE TRIGGER action_no_delete BEFORE DELETE ON qual.action FOR EACH ROW EXECUTE FUNCTION qual.reject_modification();

-- ================================================================ deviations and waivers
CREATE TABLE qual.deviation (
    id                         uuid        PRIMARY KEY,
    number                     text        NOT NULL UNIQUE CHECK (number ~ '^DEV-[0-9]{6,}$'),
    dev_kind                   text        NOT NULL CHECK (dev_kind IN ('Deviation', 'Waiver')),
    title                      text        NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    description                text        NOT NULL DEFAULT '' CHECK (length(description) <= 8000),
    requirement_ref            text        NOT NULL DEFAULT '' CHECK (length(requirement_ref) <= 500),
    customer_approval_required boolean     NOT NULL DEFAULT false,
    customer_approval_ref      text        CHECK (customer_approval_ref IS NULL OR length(customer_approval_ref) BETWEEN 1 AND 300),
    customer_approval_date     date,
    customer_approval_by       text,
    valid_from                 date        NOT NULL,
    valid_to                   date        NOT NULL,
    close_reason               text        CHECK (close_reason IS NULL OR length(close_reason) <= 1000),
    expiry_warned_at           timestamptz,
    lifecycle_id               text        NOT NULL,
    lifecycle_state            text        NOT NULL,
    submission_no              int         NOT NULL DEFAULT 0 CHECK (submission_no >= 0),
    requester_sub              text        NOT NULL,
    requester_username         text        NOT NULL,
    requester_name             text        NOT NULL,
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL,
    created_at                 timestamptz NOT NULL,
    created_by                 text        NOT NULL,
    state_changed_at           timestamptz NOT NULL,
    state_changed_by           text        NOT NULL,
    FOREIGN KEY (lifecycle_id, lifecycle_state) REFERENCES plm.lifecycle_state (lifecycle_id, state),
    CONSTRAINT deviation_lifecycle_family CHECK (split_part(lifecycle_id, '~v', 1) = 'deviation-default'),
    CONSTRAINT deviation_window CHECK (valid_from <= valid_to),
    CONSTRAINT deviation_approved_has_customer CHECK (lifecycle_state NOT IN ('Approved', 'Expired') OR NOT customer_approval_required OR customer_approval_ref IS NOT NULL),
    CONSTRAINT deviation_revoked_has_reason CHECK (lifecycle_state <> 'Revoked' OR length(btrim(COALESCE(close_reason, ''))) > 0)
);
CREATE INDEX deviation_state_idx ON qual.deviation (lifecycle_state, valid_to);
CREATE INDEX deviation_program_idx ON qual.deviation (program);
CREATE INDEX deviation_number_trgm ON qual.deviation USING gin (number gin_trgm_ops);
CREATE INDEX deviation_title_trgm ON qual.deviation USING gin (title gin_trgm_ops);

CREATE FUNCTION qual.guard_deviation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id <> OLD.id OR NEW.number <> OLD.number OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by OR NEW.requester_sub <> OLD.requester_sub
       OR NEW.lifecycle_id <> OLD.lifecycle_id THEN
        RAISE EXCEPTION 'deviation identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    IF qual.is_locked_state(OLD.lifecycle_id, OLD.lifecycle_state) AND (
           NEW.title <> OLD.title OR NEW.description <> OLD.description OR NEW.dev_kind <> OLD.dev_kind OR NEW.requirement_ref <> OLD.requirement_ref
        OR NEW.customer_approval_required <> OLD.customer_approval_required OR NEW.valid_from <> OLD.valid_from OR NEW.valid_to <> OLD.valid_to
        OR NEW.export_jurisdiction <> OLD.export_jurisdiction OR NEW.eccn IS DISTINCT FROM OLD.eccn
        OR NEW.usml_category IS DISTINCT FROM OLD.usml_category OR NEW.cui_categories <> OLD.cui_categories
        OR NEW.distribution_statement <> OLD.distribution_statement OR NEW.controlling_office IS DISTINCT FROM OLD.controlling_office
        OR NEW.distribution_reason IS DISTINCT FROM OLD.distribution_reason OR NEW.program IS DISTINCT FROM OLD.program
        OR NEW.owning_org <> OLD.owning_org OR NEW.export_authorizations <> OLD.export_authorizations) THEN
        RAISE EXCEPTION 'deviation % is locked in state %', OLD.number, OLD.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.submission_no < OLD.submission_no THEN RAISE EXCEPTION 'submission numbers only increase' USING ERRCODE = 'check_violation'; END IF;
    PERFORM qual.guard_transition('deviation', OLD.number, OLD.lifecycle_id, OLD.lifecycle_state, NEW.lifecycle_state);
    RETURN NEW;
END;
$$;
CREATE TRIGGER deviation_update_guard BEFORE UPDATE ON qual.deviation FOR EACH ROW EXECUTE FUNCTION qual.guard_deviation();
CREATE TRIGGER deviation_no_delete BEFORE DELETE ON qual.deviation FOR EACH ROW EXECUTE FUNCTION qual.reject_modification();

-- Scope: one row per part (optionally one revision), with serials (a list and/or a numeric range), lots and a quantity ceiling. Effectivity-style: no
-- serial, lot or quantity restriction means every unit of the part inside the time window.
CREATE TABLE qual.deviation_scope (
    id               uuid PRIMARY KEY,
    deviation_id     uuid NOT NULL REFERENCES qual.deviation (id),
    part_master_id   uuid NOT NULL,
    part_number      text NOT NULL,
    part_revision_id uuid,
    part_revision    text,
    serial_from      bigint CHECK (serial_from IS NULL OR serial_from >= 0),
    serial_to        bigint CHECK (serial_to IS NULL OR serial_to >= 0),
    serials          text[] NOT NULL DEFAULT '{}' CHECK (cardinality(serials) <= 500),
    lots             text[] NOT NULL DEFAULT '{}' CHECK (cardinality(lots) <= 200),
    max_quantity     numeric(18,4) CHECK (max_quantity IS NULL OR max_quantity > 0),
    CONSTRAINT scope_revision_pair CHECK ((part_revision_id IS NULL) = (part_revision IS NULL)),
    CONSTRAINT scope_serial_range CHECK (serial_from IS NULL OR serial_to IS NULL OR serial_from <= serial_to)
);
CREATE UNIQUE INDEX deviation_scope_once ON qual.deviation_scope (deviation_id, part_master_id, COALESCE(part_revision_id, '00000000-0000-0000-0000-000000000000'::uuid));
CREATE INDEX deviation_scope_part_idx ON qual.deviation_scope (part_master_id);

CREATE FUNCTION qual.guard_deviation_scope() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE d qual.deviation; v_id uuid;
BEGIN
    v_id := COALESCE(NEW.deviation_id, OLD.deviation_id);
    SELECT * INTO d FROM qual.deviation WHERE id = v_id;
    IF qual.is_locked_state(d.lifecycle_id, d.lifecycle_state) THEN
        RAISE EXCEPTION 'deviation % is % and its scope is frozen', d.number, d.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    RETURN COALESCE(NEW, OLD);
END;
$$;
CREATE TRIGGER deviation_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON qual.deviation_scope FOR EACH ROW EXECUTE FUNCTION qual.guard_deviation_scope();

-- ================================================================ links between quality records, changes and parts
-- to_key is the business number of the target (an NCR, CAPA, deviation, change or part number). The service checks that the actor may read it.
CREATE TABLE qual.link (
    id         uuid        PRIMARY KEY,
    from_kind  text        NOT NULL CHECK (from_kind IN ('ncr', 'capa', 'deviation')),
    from_id    uuid        NOT NULL,
    to_kind    text        NOT NULL CHECK (to_kind IN ('ncr', 'capa', 'deviation', 'change', 'part')),
    to_key     text        NOT NULL CHECK (length(to_key) BETWEEN 1 AND 64),
    added_at   timestamptz NOT NULL,
    added_by   text        NOT NULL,
    removed_at timestamptz,
    removed_by text
);
CREATE UNIQUE INDEX link_once ON qual.link (from_kind, from_id, to_kind, to_key) WHERE removed_at IS NULL;
CREATE INDEX link_target_idx ON qual.link (to_kind, to_key) WHERE removed_at IS NULL;

-- ================================================================ history and review snapshots (immutable)
CREATE TABLE qual.history (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    object_kind text        NOT NULL CHECK (object_kind IN ('ncr', 'capa', 'deviation')),
    object_id   uuid        NOT NULL,
    at          timestamptz NOT NULL,
    actor_sub   text        NOT NULL,
    actor_name  text        NOT NULL,
    action      text        NOT NULL,
    detail      text
);
CREATE INDEX history_object_idx ON qual.history (object_kind, object_id, id);
CREATE TRIGGER history_immutable BEFORE UPDATE OR DELETE ON qual.history FOR EACH ROW EXECUTE FUNCTION qual.reject_modification();
CREATE TRIGGER history_no_truncate BEFORE TRUNCATE ON qual.history FOR EACH STATEMENT EXECUTE FUNCTION qual.reject_modification();

CREATE TABLE qual.submission (
    id              uuid        PRIMARY KEY,
    object_kind     text        NOT NULL CHECK (object_kind IN ('ncr', 'capa', 'deviation')),
    object_id       uuid        NOT NULL,
    submission_no   int         NOT NULL CHECK (submission_no >= 1),
    snapshot        text        NOT NULL,
    snapshot_sha256 bytea       NOT NULL CHECK (octet_length(snapshot_sha256) = 32),
    created_at      timestamptz NOT NULL,
    created_by      text        NOT NULL,
    UNIQUE (object_kind, object_id, submission_no)
);
CREATE TRIGGER submission_immutable BEFORE UPDATE OR DELETE ON qual.submission FOR EACH ROW EXECUTE FUNCTION qual.reject_modification();
CREATE TRIGGER submission_no_truncate BEFORE TRUNCATE ON qual.submission FOR EACH STATEMENT EXECUTE FUNCTION qual.reject_modification();

-- ================================================================ workflow templates (ordinary data, replaceable by administrators)
INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by) VALUES
('ncr-mrb-review', 1, 'NCR material review board',
 'A quality reviewer checks the report, then the chosen material review board members approve the disposition in parallel; every member must approve. The signatures cover the report and the proposed disposition.',
 'ncr', $tpl$
{
  "id": "ncr-mrb-review", "name": "NCR material review board",
  "description": "A quality reviewer checks the report, then the chosen material review board members approve the disposition in parallel; every member must approve. The signatures cover the report and the proposed disposition.",
  "objectType": "ncr",
  "slots": [
    { "name": "reviewer", "label": "Quality reviewer", "role": "plm-viewer", "min": 1, "max": 1 },
    { "name": "mrb", "label": "MRB members", "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow", "type": "Serial", "name": "MRB review", "steps": ["quality-review", "mrb-approval", "approved"] },
    { "id": "quality-review", "type": "Task", "name": "Quality review", "kind": "review", "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 3 },
    { "id": "mrb-approval", "type": "Parallel", "name": "MRB approval", "rule": { "type": "All" }, "fanOut": { "slot": "mrb", "kind": "approval" }, "dueDays": 5 },
    { "id": "approved", "type": "End", "name": "Dispositioned", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system'),
('capa-verification-review', 1, 'CAPA effectiveness verification',
 'The chosen verifiers sign that the actions were completed and were effective; every verifier must approve. The signatures cover the analysis, actions and verification result.',
 'capa', $tpl$
{
  "id": "capa-verification-review", "name": "CAPA effectiveness verification",
  "description": "The chosen verifiers sign that the actions were completed and were effective; every verifier must approve. The signatures cover the analysis, actions and verification result.",
  "objectType": "capa",
  "slots": [ { "name": "verifiers", "label": "Verifiers", "role": "plm-approver", "min": 1, "max": 5 } ],
  "start": "flow",
  "nodes": [
    { "id": "flow", "type": "Serial", "name": "Verification", "steps": ["verify", "closed"] },
    { "id": "verify", "type": "Parallel", "name": "Verification sign-off", "rule": { "type": "All" }, "fanOut": { "slot": "verifiers", "kind": "approval" }, "dueDays": 7 },
    { "id": "closed", "type": "End", "name": "Closed", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system'),
('deviation-approval', 1, 'Deviation and waiver approval',
 'The chosen approvers approve the deviation or waiver in parallel; every approver must approve. The signatures cover the scope and the time window.',
 'deviation', $tpl$
{
  "id": "deviation-approval", "name": "Deviation and waiver approval",
  "description": "The chosen approvers approve the deviation or waiver in parallel; every approver must approve. The signatures cover the scope and the time window.",
  "objectType": "deviation",
  "slots": [ { "name": "approvers", "label": "Approvers", "role": "plm-approver", "min": 1, "max": 10 } ],
  "start": "flow",
  "nodes": [
    { "id": "flow", "type": "Serial", "name": "Approval", "steps": ["approval", "approved"] },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" }, "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "approved", "type": "End", "name": "Approved", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

-- ================================================================ awareness tables accept the new object types (pattern-based, idempotent, order-independent with 0020)
DO $$
DECLARE c record;
BEGIN
    FOR c IN
        SELECT n.nspname AS sch, t.relname AS tbl, k.conname AS con
        FROM pg_constraint k JOIN pg_class t ON t.oid = k.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE k.contype = 'c' AND n.nspname IN ('ws', 'ntf') AND pg_get_constraintdef(k.oid) LIKE '%object_type%' AND pg_get_constraintdef(k.oid) LIKE '%mbom%'
    LOOP
        EXECUTE format('ALTER TABLE %I.%I DROP CONSTRAINT %I', c.sch, c.tbl, c.con);
        EXECUTE format('ALTER TABLE %I.%I ADD CONSTRAINT %I CHECK (object_type ~ ''^[a-z][a-z_]{1,30}$'')', c.sch, c.tbl, c.tbl || '_object_type_known');
    END LOOP;
    FOR c IN
        SELECT n.nspname AS sch, t.relname AS tbl, k.conname AS con
        FROM pg_constraint k JOIN pg_class t ON t.oid = k.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE k.contype = 'c' AND n.nspname = 'ws' AND pg_get_constraintdef(k.oid) LIKE '%scope%' AND pg_get_constraintdef(k.oid) LIKE '%mbom%'
    LOOP
        EXECUTE format('ALTER TABLE %I.%I DROP CONSTRAINT %I', c.sch, c.tbl, c.con);
        EXECUTE format('ALTER TABLE %I.%I ADD CONSTRAINT %I CHECK (scope ~ ''^[a-z][a-z_]{1,30}$'')', c.sch, c.tbl, c.tbl || '_scope_known');
    END LOOP;
END;
$$;

-- ================================================================ privileges (least privilege for keel_app)
GRANT SELECT, INSERT, UPDATE ON qual.ncr, qual.capa, qual.deviation, qual.action TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON qual.rca_why, qual.rca_cause, qual.deviation_scope TO keel_app;
GRANT SELECT, INSERT, UPDATE ON qual.link TO keel_app;
GRANT SELECT, INSERT ON qual.history, qual.submission TO keel_app;
