-- Increment 8.8: program and project management (ADR-0043, docs/increments/8.8-programs.md). Forward-only, runs as keel_migrator.
--   * the program master: one row per program marking code (the code IS markings.program of the row), so the page is marked and read-filtered like any object;
--     membership of programs stays with the identity provider and the ABAC policy: nothing here grants or widens access;
--   * the work breakdown (phases, milestones, tasks), finish-to-start dependencies, schedule baselines (immutable), slip log, deliverables as typed pointers;
--   * gate reviews (milestones with a checklist) approved through the workflow engine with e-signatures (database guard: an approving signature is required).
-- Marked data: programs, items and deliverables carry the marking columns of ADR-0004. Child rows (dependencies, checklist, baselines, log) carry none:
-- they are readable only through readable parents.
CREATE SCHEMA prg;
GRANT USAGE ON SCHEMA prg TO keel_app;
CREATE SEQUENCE prg.gate_seq START 1;
GRANT USAGE ON SEQUENCE prg.gate_seq TO keel_app;

CREATE FUNCTION prg.reject_modification() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'program baselines, logs and review snapshots are never modified or deleted (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;

-- ================================================================ program master
CREATE TABLE prg.program (
    id                     uuid        PRIMARY KEY,
    code                   text        NOT NULL UNIQUE CHECK (code ~ '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$'),
    name                   text        NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 200),
    description            text        NOT NULL DEFAULT '' CHECK (length(description) <= 8000),
    customer_ref           text        NOT NULL DEFAULT '' CHECK (length(customer_ref) <= 200),
    contract_ref           text        NOT NULL DEFAULT '' CHECK (length(contract_ref) <= 200),
    status                 text        NOT NULL DEFAULT 'Planning' CHECK (status IN ('Planning', 'Active', 'OnHold', 'Complete', 'Cancelled')),
    manager_sub            text        NOT NULL,
    manager_username       text        NOT NULL,
    manager_name           text        NOT NULL,
    start_date             date,
    end_date               date,
    actual_end_date        date,
    next_item_no           int         NOT NULL DEFAULT 1,
    next_deliverable_no    int         NOT NULL DEFAULT 1,
    next_baseline_no       int         NOT NULL DEFAULT 1,
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text        NOT NULL,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL,
    created_at             timestamptz NOT NULL,
    created_by             text        NOT NULL,
    updated_at             timestamptz NOT NULL,
    updated_by             text        NOT NULL,
    -- The program page is marked with its own code: only members of the program read it. Creating or editing it never changes who is a member.
    CONSTRAINT program_marked_with_its_code CHECK (program = code),
    CONSTRAINT program_dates CHECK (start_date IS NULL OR end_date IS NULL OR start_date <= end_date)
);
CREATE INDEX program_status_idx ON prg.program (status);
CREATE INDEX program_name_trgm ON prg.program USING gin (name gin_trgm_ops);
CREATE INDEX program_code_trgm ON prg.program USING gin (code gin_trgm_ops);

CREATE FUNCTION prg.guard_program() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id <> OLD.id OR NEW.code <> OLD.code OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by OR NEW.program <> OLD.program THEN
        RAISE EXCEPTION 'program identity and its marking code are immutable' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER program_update_guard BEFORE UPDATE ON prg.program FOR EACH ROW EXECUTE FUNCTION prg.guard_program();
CREATE TRIGGER program_no_delete BEFORE DELETE ON prg.program FOR EACH ROW EXECUTE FUNCTION prg.reject_modification();

-- ================================================================ work breakdown: phases, milestones and tasks
CREATE TABLE prg.item (
    id                     uuid        PRIMARY KEY,
    program_id             uuid        NOT NULL REFERENCES prg.program (id),
    key                    text        NOT NULL CHECK (key ~ '^(PH|MS|TK)-[0-9]{3,}$'),
    kind                   text        NOT NULL CHECK (kind IN ('Phase', 'Milestone', 'Task')),
    parent_id              uuid        REFERENCES prg.item (id),
    sort_order             int         NOT NULL DEFAULT 0,
    title                  text        NOT NULL CHECK (length(btrim(title)) BETWEEN 1 AND 200),
    description            text        NOT NULL DEFAULT '' CHECK (length(description) <= 4000),
    owner_sub              text,
    owner_username         text,
    owner_name             text,
    planned_start          date        NOT NULL,
    planned_end            date        NOT NULL,
    actual_start           date,
    actual_end             date,
    status                 text        NOT NULL DEFAULT 'NotStarted' CHECK (status IN ('NotStarted', 'InProgress', 'Blocked', 'Complete', 'Cancelled')),
    percent_complete       int         NOT NULL DEFAULT 0 CHECK (percent_complete BETWEEN 0 AND 100),
    gate_type              text        CHECK (gate_type IS NULL OR gate_type IN ('SRR', 'PDR', 'CDR', 'TRR', 'FRR', 'PRR', 'Other')),
    gate_number            text        UNIQUE CHECK (gate_number IS NULL OR gate_number ~ '^GR-[0-9]{6,}$'),
    gate_state             text        CHECK (gate_state IS NULL OR gate_state IN ('Draft', 'UnderReview', 'Approved')),
    submission_no          int         NOT NULL DEFAULT 0 CHECK (submission_no >= 0),
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
    due_notified_at        timestamptz,
    overdue_notified_at    timestamptz,
    created_at             timestamptz NOT NULL,
    created_by             text        NOT NULL,
    updated_at             timestamptz NOT NULL,
    updated_by             text        NOT NULL,
    UNIQUE (program_id, key),
    CONSTRAINT item_dates CHECK (planned_start <= planned_end AND (actual_start IS NULL OR actual_end IS NULL OR actual_start <= actual_end)),
    CONSTRAINT item_milestone_is_a_day CHECK (kind <> 'Milestone' OR planned_start = planned_end),
    CONSTRAINT item_complete CHECK (status <> 'Complete' OR (percent_complete = 100 AND actual_end IS NOT NULL)),
    CONSTRAINT item_gate_only_milestone CHECK (gate_number IS NULL OR kind = 'Milestone'),
    CONSTRAINT item_gate_fields CHECK ((gate_number IS NULL) = (gate_state IS NULL) AND (gate_number IS NULL OR gate_type IS NOT NULL)),
    CONSTRAINT item_gate_complete_needs_approval CHECK (gate_number IS NULL OR status <> 'Complete' OR gate_state = 'Approved'),
    CONSTRAINT item_marked_with_program CHECK (program IS NOT NULL)
);
CREATE INDEX item_program_idx ON prg.item (program_id, sort_order, planned_start);
CREATE INDEX item_owner_idx ON prg.item (owner_sub) WHERE status NOT IN ('Complete', 'Cancelled');
CREATE INDEX item_due_idx ON prg.item (planned_end) WHERE status NOT IN ('Complete', 'Cancelled');
CREATE INDEX item_title_trgm ON prg.item USING gin (title gin_trgm_ops);

CREATE FUNCTION prg.guard_item() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id <> OLD.id OR NEW.program_id <> OLD.program_id OR NEW.key <> OLD.key OR NEW.kind <> OLD.kind OR NEW.created_at <> OLD.created_at
       OR NEW.gate_number IS DISTINCT FROM OLD.gate_number OR NEW.gate_type IS DISTINCT FROM OLD.gate_type THEN
        RAISE EXCEPTION 'schedule item identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.gate_state IN ('UnderReview', 'Approved') AND NEW.gate_state IS NOT DISTINCT FROM OLD.gate_state AND (
           NEW.title <> OLD.title OR NEW.description <> OLD.description OR NEW.planned_start <> OLD.planned_start OR NEW.planned_end <> OLD.planned_end
        OR NEW.export_jurisdiction <> OLD.export_jurisdiction OR NEW.cui_categories <> OLD.cui_categories OR NEW.distribution_statement <> OLD.distribution_statement
        OR NEW.program IS DISTINCT FROM OLD.program OR NEW.owning_org <> OLD.owning_org OR NEW.export_authorizations <> OLD.export_authorizations) THEN
        RAISE EXCEPTION 'gate review % is % and is frozen', OLD.gate_number, OLD.gate_state USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.gate_state IS DISTINCT FROM OLD.gate_state THEN
        IF NOT ((OLD.gate_state = 'Draft' AND NEW.gate_state = 'UnderReview') OR (OLD.gate_state = 'UnderReview' AND NEW.gate_state IN ('Draft', 'Approved'))) THEN
            RAISE EXCEPTION 'gate review % cannot go from % to %', OLD.gate_number, OLD.gate_state, NEW.gate_state USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.gate_state = 'Approved' AND NOT EXISTS (SELECT 1 FROM wf.signature s WHERE s.object_type = 'gate' AND s.object_key = OLD.gate_number AND s.decision = 'approve') THEN
            RAISE EXCEPTION 'gate review % cannot be approved without an approving signature', OLD.gate_number USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.gate_state = 'UnderReview' AND EXISTS (SELECT 1 FROM prg.checklist c WHERE c.item_id = OLD.id AND c.required AND c.status = 'Open') THEN
            RAISE EXCEPTION 'gate review % still has open checklist items', OLD.gate_number USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    IF NEW.submission_no < OLD.submission_no THEN RAISE EXCEPTION 'submission numbers only increase' USING ERRCODE = 'check_violation'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER item_update_guard BEFORE UPDATE ON prg.item FOR EACH ROW EXECUTE FUNCTION prg.guard_item();
CREATE TRIGGER item_no_delete BEFORE DELETE ON prg.item FOR EACH ROW EXECUTE FUNCTION prg.reject_modification();

-- ================================================================ dependencies (finish to start, optional lag in days)
CREATE TABLE prg.dependency (
    id             uuid        PRIMARY KEY,
    program_id     uuid        NOT NULL REFERENCES prg.program (id),
    predecessor_id uuid        NOT NULL REFERENCES prg.item (id),
    successor_id   uuid        NOT NULL REFERENCES prg.item (id),
    lag_days       int         NOT NULL DEFAULT 0 CHECK (lag_days BETWEEN -365 AND 365),
    created_at     timestamptz NOT NULL,
    created_by     text        NOT NULL,
    UNIQUE (predecessor_id, successor_id),
    CONSTRAINT dependency_not_self CHECK (predecessor_id <> successor_id)
);
CREATE INDEX dependency_program_idx ON prg.dependency (program_id);
CREATE INDEX dependency_successor_idx ON prg.dependency (successor_id);

CREATE FUNCTION prg.guard_dependency() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE p prg.item; s prg.item;
BEGIN
    SELECT * INTO p FROM prg.item WHERE id = NEW.predecessor_id;
    SELECT * INTO s FROM prg.item WHERE id = NEW.successor_id;
    IF p.program_id <> NEW.program_id OR s.program_id <> NEW.program_id THEN
        RAISE EXCEPTION 'a dependency joins two items of the same program' USING ERRCODE = 'check_violation';
    END IF;
    IF p.kind = 'Phase' OR s.kind = 'Phase' THEN
        RAISE EXCEPTION 'dependencies join tasks and milestones, not phases' USING ERRCODE = 'check_violation';
    END IF;
    -- no cycle: the predecessor must not be reachable from the successor
    IF EXISTS (
        WITH RECURSIVE reach(id) AS (
            SELECT NEW.successor_id
            UNION
            SELECT d.successor_id FROM prg.dependency d JOIN reach r ON d.predecessor_id = r.id)
        SELECT 1 FROM reach WHERE id = NEW.predecessor_id) THEN
        RAISE EXCEPTION 'this dependency would make the schedule circular' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER dependency_guard BEFORE INSERT ON prg.dependency FOR EACH ROW EXECUTE FUNCTION prg.guard_dependency();

-- ================================================================ schedule baselines (immutable snapshots of planned dates) and the slip log
CREATE TABLE prg.baseline (
    id          uuid        PRIMARY KEY,
    program_id  uuid        NOT NULL REFERENCES prg.program (id),
    seq         int         NOT NULL CHECK (seq >= 1),
    name        text        NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 200),
    created_at  timestamptz NOT NULL,
    created_by  text        NOT NULL,
    item_count  int         NOT NULL,
    UNIQUE (program_id, seq)
);
-- Only dates and ids: no title or other marked text is copied. A viewer sees the baseline date of an item only through the item they can read.
CREATE TABLE prg.baseline_item (
    baseline_id   uuid NOT NULL REFERENCES prg.baseline (id),
    item_id       uuid NOT NULL REFERENCES prg.item (id),
    planned_start date NOT NULL,
    planned_end   date NOT NULL,
    PRIMARY KEY (baseline_id, item_id)
);
CREATE TRIGGER baseline_immutable BEFORE UPDATE OR DELETE ON prg.baseline FOR EACH ROW EXECUTE FUNCTION prg.reject_modification();
CREATE TRIGGER baseline_item_immutable BEFORE UPDATE OR DELETE ON prg.baseline_item FOR EACH ROW EXECUTE FUNCTION prg.reject_modification();
CREATE TRIGGER baseline_no_truncate BEFORE TRUNCATE ON prg.baseline FOR EACH STATEMENT EXECUTE FUNCTION prg.reject_modification();
CREATE TRIGGER baseline_item_no_truncate BEFORE TRUNCATE ON prg.baseline_item FOR EACH STATEMENT EXECUTE FUNCTION prg.reject_modification();

CREATE TABLE prg.schedule_change (
    id         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    program_id uuid        NOT NULL REFERENCES prg.program (id),
    item_id    uuid        NOT NULL REFERENCES prg.item (id),
    at         timestamptz NOT NULL,
    actor_sub  text        NOT NULL,
    actor_name text        NOT NULL,
    old_start  date        NOT NULL,
    old_end    date        NOT NULL,
    new_start  date        NOT NULL,
    new_end    date        NOT NULL,
    slip_days  int         NOT NULL,
    reason     text        NOT NULL DEFAULT '' CHECK (length(reason) <= 1000),
    CONSTRAINT slip_needs_reason CHECK (slip_days <= 0 OR length(btrim(reason)) > 0)
);
CREATE INDEX schedule_change_item_idx ON prg.schedule_change (item_id, id);
CREATE TRIGGER schedule_change_immutable BEFORE UPDATE OR DELETE ON prg.schedule_change FOR EACH ROW EXECUTE FUNCTION prg.reject_modification();
CREATE TRIGGER schedule_change_no_truncate BEFORE TRUNCATE ON prg.schedule_change FOR EACH STATEMENT EXECUTE FUNCTION prg.reject_modification();

-- ================================================================ gate review checklists and review snapshots
CREATE TABLE prg.checklist (
    id          uuid        PRIMARY KEY,
    item_id     uuid        NOT NULL REFERENCES prg.item (id),
    seq         int         NOT NULL CHECK (seq >= 1),
    text        text        NOT NULL CHECK (length(btrim(text)) BETWEEN 1 AND 500),
    required    boolean     NOT NULL DEFAULT true,
    status      text        NOT NULL DEFAULT 'Open' CHECK (status IN ('Open', 'Met', 'NotApplicable')),
    note        text        NOT NULL DEFAULT '' CHECK (length(note) <= 1000),
    checked_by  text,
    checked_at  timestamptz,
    UNIQUE (item_id, seq)
);
CREATE FUNCTION prg.guard_checklist() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_id uuid; v_state text;
BEGIN
    v_id := COALESCE(NEW.item_id, OLD.item_id);
    SELECT gate_state INTO v_state FROM prg.item WHERE id = v_id;
    IF v_state IS NULL THEN RAISE EXCEPTION 'only a gate review has a checklist' USING ERRCODE = 'check_violation'; END IF;
    IF v_state <> 'Draft' THEN RAISE EXCEPTION 'the gate review is % and its checklist is frozen', v_state USING ERRCODE = 'check_violation'; END IF;
    RETURN COALESCE(NEW, OLD);
END;
$$;
CREATE TRIGGER checklist_guard BEFORE INSERT OR UPDATE OR DELETE ON prg.checklist FOR EACH ROW EXECUTE FUNCTION prg.guard_checklist();

CREATE TABLE prg.submission (
    id              uuid        PRIMARY KEY,
    item_id         uuid        NOT NULL REFERENCES prg.item (id),
    submission_no   int         NOT NULL CHECK (submission_no >= 1),
    snapshot        text        NOT NULL,
    snapshot_sha256 bytea       NOT NULL CHECK (octet_length(snapshot_sha256) = 32),
    created_at      timestamptz NOT NULL,
    created_by      text        NOT NULL,
    UNIQUE (item_id, submission_no)
);
CREATE TRIGGER submission_immutable BEFORE UPDATE OR DELETE ON prg.submission FOR EACH ROW EXECUTE FUNCTION prg.reject_modification();
CREATE TRIGGER submission_no_truncate BEFORE TRUNCATE ON prg.submission FOR EACH STATEMENT EXECUTE FUNCTION prg.reject_modification();

-- ================================================================ deliverables: typed pointers to Keel objects (kind and business number only; the state is read live)
CREATE TABLE prg.deliverable (
    id                     uuid        PRIMARY KEY,
    program_id             uuid        NOT NULL REFERENCES prg.program (id),
    number                 text        NOT NULL CHECK (number ~ '^D-[0-9]{3,}$'),
    item_id                uuid        REFERENCES prg.item (id),
    title                  text        NOT NULL CHECK (length(btrim(title)) BETWEEN 1 AND 200),
    object_kind            text        NOT NULL CHECK (object_kind IN ('part', 'document', 'change', 'baseline', 'reqspec', 'ncr', 'capa', 'deviation', 'mbom')),
    object_key             text        NOT NULL CHECK (length(object_key) BETWEEN 1 AND 64),
    due_date               date        NOT NULL,
    owner_sub              text,
    owner_username         text,
    owner_name             text,
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
    removed_at             timestamptz,
    removed_by             text,
    created_at             timestamptz NOT NULL,
    created_by             text        NOT NULL,
    UNIQUE (program_id, number),
    CONSTRAINT deliverable_marked_with_program CHECK (program IS NOT NULL)
);
CREATE UNIQUE INDEX deliverable_once ON prg.deliverable (program_id, object_kind, object_key, COALESCE(item_id, '00000000-0000-0000-0000-000000000000'::uuid)) WHERE removed_at IS NULL;
CREATE INDEX deliverable_due_idx ON prg.deliverable (program_id, due_date) WHERE removed_at IS NULL;
CREATE INDEX deliverable_target_idx ON prg.deliverable (object_kind, object_key) WHERE removed_at IS NULL;

-- ================================================================ workflow template for gate reviews (ordinary data, replaceable by administrators)
INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by) VALUES
('gate-review-approval', 1, 'Gate review approval',
 'The chosen approvers sign that the gate review criteria are met; every approver must approve. The signatures cover the gate, its checklist and the readiness of its deliverables as submitted.',
 'gate', $tpl$
{
  "id": "gate-review-approval", "name": "Gate review approval",
  "description": "The chosen approvers sign that the gate review criteria are met; every approver must approve. The signatures cover the gate, its checklist and the readiness of its deliverables as submitted.",
  "objectType": "gate",
  "slots": [ { "name": "approvers", "label": "Review board", "role": "plm-approver", "min": 1, "max": 12 } ],
  "start": "flow",
  "nodes": [
    { "id": "flow", "type": "Serial", "name": "Gate approval", "steps": ["approval", "approved"] },
    { "id": "approval", "type": "Parallel", "name": "Review board approval", "rule": { "type": "All" }, "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 7 },
    { "id": "approved", "type": "End", "name": "Gate passed", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

-- ================================================================ awareness tables accept the new object types (pattern-based, idempotent, order-independent with 0025 and 0027)
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
GRANT SELECT, INSERT, UPDATE ON prg.program, prg.item, prg.deliverable TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON prg.checklist TO keel_app;
GRANT SELECT, INSERT ON prg.dependency, prg.baseline, prg.baseline_item, prg.schedule_change, prg.submission TO keel_app;
GRANT DELETE ON prg.dependency TO keel_app;
