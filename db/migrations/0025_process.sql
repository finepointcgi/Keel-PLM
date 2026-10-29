-- Increment 8.7: manufacturing process data (ADR-0042, docs/increments/8.7-process.md). Forward-only, runs as keel_migrator.
--   * work centers and resources per plant (administrator data);
--   * tools, fixtures and gauges with calibration history and expiry notices;
--   * process plans per MBOM: revisions and iterations whose content (routings, operations, component assignments, work instruction steps, attachments, tool references)
--     is one JSON document per iteration, immutable once checked in; release through the workflow engine with release locks enforced by triggers;
--   * workflow template for plan release; object type lists of the awareness tables accept the new kinds (pattern, idempotent).
-- Marked data: plans and tools carry the marking columns of ADR-0004. Work centers carry none (plant reference data).
CREATE SCHEMA IF NOT EXISTS proc;
GRANT USAGE ON SCHEMA proc TO keel_app;

CREATE SEQUENCE proc.plan_seq START 1;
CREATE SEQUENCE proc.tool_seq START 1;
GRANT USAGE ON SEQUENCE proc.plan_seq, proc.tool_seq TO keel_app;

-- ================================================================ work centers and resources
CREATE TABLE proc.work_center (
    id            uuid        PRIMARY KEY,
    plant         text        NOT NULL REFERENCES mbom.plant (code),
    code          text        NOT NULL CHECK (code ~ '^[A-Z0-9][A-Z0-9._-]{0,19}$'),
    name          text        NOT NULL CHECK (length(name) BETWEEN 1 AND 120),
    resource_kind text        NOT NULL CHECK (resource_kind IN ('work_center', 'machine', 'station', 'labor')),
    is_active     boolean     NOT NULL DEFAULT true,
    updated_at    timestamptz NOT NULL,
    updated_by    text        NOT NULL,
    UNIQUE (plant, code)
);

-- ================================================================ tools, fixtures and gauges
CREATE TABLE proc.tool (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE CHECK (length(number) BETWEEN 2 AND 40),
    name                   text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    kind                   text        NOT NULL CHECK (kind IN ('tool', 'fixture', 'gauge')),
    plant                  text        NOT NULL REFERENCES mbom.plant (code),
    description            text        CHECK (description IS NULL OR length(description) <= 1000),
    requires_calibration   boolean     NOT NULL DEFAULT false,
    interval_days          int         CHECK (interval_days IS NULL OR interval_days BETWEEN 1 AND 3660),
    last_calibrated_on     date,
    due_on                 date,
    status                 text        NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'retired')),
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
    revision               int         NOT NULL DEFAULT 1,
    created_at             timestamptz NOT NULL,
    created_by             text        NOT NULL,
    updated_at             timestamptz,
    updated_by             text,
    CONSTRAINT tool_calibration_shape CHECK (NOT requires_calibration OR interval_days IS NOT NULL)
);
CREATE INDEX tool_due_idx ON proc.tool (due_on) WHERE requires_calibration AND status = 'active';

CREATE TABLE proc.tool_calibration (
    id            uuid        PRIMARY KEY,
    tool_id       uuid        NOT NULL REFERENCES proc.tool (id),
    calibrated_on date        NOT NULL,
    next_due_on   date        NOT NULL CHECK (next_due_on >= calibrated_on),
    reference     text        CHECK (reference IS NULL OR length(reference) <= 100),
    note          text        CHECK (note IS NULL OR length(note) <= 500),
    recorded_at   timestamptz NOT NULL,
    recorded_by   text        NOT NULL
);
CREATE INDEX tool_calibration_tool_idx ON proc.tool_calibration (tool_id, calibrated_on DESC);

-- One row per tool, stage and due date: makes the expiry job idempotent.
CREATE TABLE proc.tool_notice (
    tool_id     uuid        NOT NULL REFERENCES proc.tool (id),
    stage       text        NOT NULL CHECK (stage IN ('d30', 'd14', 'd7', 'overdue')),
    due_on      date        NOT NULL,
    noticed_at  timestamptz NOT NULL,
    PRIMARY KEY (tool_id, stage, due_on)
);

CREATE FUNCTION proc.guard_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'proc.% is append-only', TG_TABLE_NAME USING ERRCODE = 'integrity_constraint_violation';
END;
$$;
CREATE TRIGGER tool_calibration_append_only BEFORE UPDATE OR DELETE ON proc.tool_calibration FOR EACH ROW EXECUTE FUNCTION proc.guard_append_only();
CREATE TRIGGER tool_calibration_no_truncate BEFORE TRUNCATE ON proc.tool_calibration FOR EACH STATEMENT EXECUTE FUNCTION proc.guard_append_only();

-- ================================================================ process plans
CREATE TABLE proc.plan (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE CHECK (length(number) BETWEEN 2 AND 40),
    mbom_id                uuid        NOT NULL UNIQUE REFERENCES mbom.mbom (id),
    mbom_number            text        NOT NULL UNIQUE,
    part_number            text        NOT NULL,
    part_name              text        NOT NULL,
    plant                  text        NOT NULL REFERENCES mbom.plant (code),
    description            text        NOT NULL DEFAULT '' CHECK (length(description) <= 4000),
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
    created_by             text        NOT NULL
);

CREATE TABLE proc.plan_revision (
    id                 uuid        PRIMARY KEY,
    plan_id            uuid        NOT NULL REFERENCES proc.plan (id),
    revision           text        NOT NULL CHECK (revision ~ '^[A-Z]{1,3}$'),
    lifecycle_state    text        NOT NULL CHECK (lifecycle_state IN ('InWork', 'UnderReview', 'Released', 'Obsolete')),
    state_changed_at   timestamptz NOT NULL,
    state_changed_by   text        NOT NULL,
    is_latest          boolean     NOT NULL,
    latest_iteration   int         NOT NULL CHECK (latest_iteration >= 1),
    -- The released MBOM revision this revision of the plan is written against. A newer released MBOM revision makes the plan stale (flagged, never changed).
    bound_mbom_revision text       NOT NULL,
    bound_mbom_sha     text        NOT NULL CHECK (bound_mbom_sha ~ '^[0-9a-f]{64}$'),
    bound_at           timestamptz NOT NULL,
    bound_by           text        NOT NULL,
    created_at         timestamptz NOT NULL,
    created_by         text        NOT NULL,
    UNIQUE (plan_id, revision)
);
CREATE UNIQUE INDEX plan_revision_latest ON proc.plan_revision (plan_id) WHERE is_latest;

CREATE TABLE proc.plan_iteration (
    id             uuid        PRIMARY KEY,
    revision_id    uuid        NOT NULL REFERENCES proc.plan_revision (id),
    iteration      int         NOT NULL CHECK (iteration >= 1),
    is_open        boolean     NOT NULL,
    opened_by      text,
    opened_by_name text,
    content        jsonb       NOT NULL,
    content_sha256 text        CHECK (content_sha256 IS NULL OR content_sha256 ~ '^[0-9a-f]{64}$'),
    comment        text        CHECK (comment IS NULL OR length(comment) <= 1000),
    created_at     timestamptz NOT NULL,
    created_by     text        NOT NULL,
    UNIQUE (revision_id, iteration),
    CONSTRAINT iteration_open_shape CHECK ((is_open AND opened_by IS NOT NULL) OR (NOT is_open AND content_sha256 IS NOT NULL))
);
CREATE UNIQUE INDEX plan_iteration_one_open ON proc.plan_iteration (revision_id) WHERE is_open;

-- Which tools an iteration uses (rebuilt whenever the working copy is saved): where-used, calibration warnings.
CREATE TABLE proc.tool_use (
    iteration_id uuid NOT NULL REFERENCES proc.plan_iteration (id) ON DELETE CASCADE,
    tool_id      uuid NOT NULL REFERENCES proc.tool (id),
    PRIMARY KEY (iteration_id, tool_id)
);
CREATE INDEX tool_use_tool_idx ON proc.tool_use (tool_id);

-- The binding made when a review starts: the digest of the checked-in content and the MBOM revision it was written against.
CREATE TABLE proc.review_binding (
    id              uuid        PRIMARY KEY,
    revision_id     uuid        NOT NULL REFERENCES proc.plan_revision (id),
    iteration_id    uuid        NOT NULL REFERENCES proc.plan_iteration (id),
    review_sha256   text        NOT NULL CHECK (review_sha256 ~ '^[0-9a-f]{64}$'),
    mbom_revision   text        NOT NULL,
    bound_at        timestamptz NOT NULL,
    bound_by        text        NOT NULL
);
CREATE TRIGGER review_binding_append_only BEFORE UPDATE OR DELETE ON proc.review_binding FOR EACH ROW EXECUTE FUNCTION proc.guard_append_only();

-- ---------------------------------------------------------------- release locks and immutability
CREATE FUNCTION proc.guard_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'plan revisions are never deleted' USING ERRCODE = 'integrity_constraint_violation'; END IF;
    IF NEW.plan_id <> OLD.plan_id OR NEW.revision <> OLD.revision THEN RAISE EXCEPTION 'plan revision identity is immutable' USING ERRCODE = 'integrity_constraint_violation'; END IF;
    IF (NEW.bound_mbom_revision <> OLD.bound_mbom_revision OR NEW.bound_mbom_sha <> OLD.bound_mbom_sha) AND NOT (OLD.lifecycle_state = 'InWork' AND NEW.lifecycle_state = 'InWork') THEN
        RAISE EXCEPTION 'the MBOM binding of a locked plan revision cannot change' USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF NEW.lifecycle_state <> OLD.lifecycle_state AND NOT (
        (OLD.lifecycle_state = 'InWork' AND NEW.lifecycle_state = 'UnderReview') OR
        (OLD.lifecycle_state = 'UnderReview' AND NEW.lifecycle_state IN ('InWork', 'Released')) OR
        (OLD.lifecycle_state = 'Released' AND NEW.lifecycle_state = 'Obsolete')) THEN
        RAISE EXCEPTION 'plan revision cannot move from % to %', OLD.lifecycle_state, NEW.lifecycle_state USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF NEW.latest_iteration <> OLD.latest_iteration AND OLD.lifecycle_state <> 'InWork' THEN
        RAISE EXCEPTION 'a locked plan revision gets no new iteration' USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER plan_revision_guard BEFORE UPDATE OR DELETE ON proc.plan_revision FOR EACH ROW EXECUTE FUNCTION proc.guard_revision();

CREATE FUNCTION proc.guard_iteration() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE st text;
BEGIN
    IF TG_OP = 'INSERT' THEN
        SELECT lifecycle_state INTO st FROM proc.plan_revision WHERE id = NEW.revision_id;
        IF st <> 'InWork' THEN RAISE EXCEPTION 'a locked plan revision gets no new iteration' USING ERRCODE = 'integrity_constraint_violation'; END IF;
        RETURN NEW;
    END IF;
    IF TG_OP = 'DELETE' THEN
        IF NOT OLD.is_open OR OLD.iteration = 1 THEN RAISE EXCEPTION 'only an open working iteration after the first can be removed' USING ERRCODE = 'integrity_constraint_violation'; END IF;
        RETURN OLD;
    END IF;
    IF NOT OLD.is_open THEN RAISE EXCEPTION 'a checked-in plan iteration is immutable' USING ERRCODE = 'integrity_constraint_violation'; END IF;
    IF NEW.id <> OLD.id OR NEW.revision_id <> OLD.revision_id OR NEW.iteration <> OLD.iteration THEN RAISE EXCEPTION 'plan iteration identity is immutable' USING ERRCODE = 'integrity_constraint_violation'; END IF;
    SELECT lifecycle_state INTO st FROM proc.plan_revision WHERE id = NEW.revision_id;
    IF st <> 'InWork' THEN RAISE EXCEPTION 'a locked plan revision cannot be edited' USING ERRCODE = 'integrity_constraint_violation'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER plan_iteration_guard BEFORE INSERT OR UPDATE OR DELETE ON proc.plan_iteration FOR EACH ROW EXECUTE FUNCTION proc.guard_iteration();

CREATE FUNCTION proc.guard_plan() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'process plans are never deleted' USING ERRCODE = 'integrity_constraint_violation'; END IF;
    IF NEW.mbom_id <> OLD.mbom_id OR NEW.mbom_number <> OLD.mbom_number OR NEW.number <> OLD.number OR NEW.plant <> OLD.plant OR NEW.part_number <> OLD.part_number THEN
        RAISE EXCEPTION 'process plan identity is immutable' USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER plan_guard BEFORE UPDATE OR DELETE ON proc.plan FOR EACH ROW EXECUTE FUNCTION proc.guard_plan();

-- ================================================================ workflow template (ordinary data, replaceable by administrators)
INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('process-plan-release', 1, 'Process plan release',
        'Review by one manufacturing engineer, then parallel approval where every chosen approver must approve. The signatures cover the routings, operations, component assignments and work instructions of the plan.',
        'process_plan', $tpl$
{
  "id": "process-plan-release",
  "name": "Process plan release",
  "description": "Review by one manufacturing engineer, then parallel approval where every chosen approver must approve. The signatures cover the routings, operations, component assignments and work instructions of the plan.",
  "objectType": "process_plan",
  "slots": [
    { "name": "reviewer",  "label": "Manufacturing reviewer", "role": "plm-viewer",   "min": 1, "max": 1 },
    { "name": "approvers", "label": "Approvers",              "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "Process plan release", "steps": ["review", "approval", "release"] },
    { "id": "review",   "type": "Task",     "name": "Review", "kind": "review",
      "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 5 },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" },
      "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "release",  "type": "End",      "name": "Release", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

-- ================================================================ awareness tables accept the new object types
-- Constraints are found by definition; those already replaced by the pattern (migration 0020) are left alone, so the order of other modules' additions does not matter.
DO $$
DECLARE c record;
BEGIN
    FOR c IN
        SELECT n.nspname AS sch, t.relname AS tbl, k.conname AS con
        FROM pg_constraint k JOIN pg_class t ON t.oid = k.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE k.contype = 'c' AND n.nspname IN ('ws', 'ntf') AND pg_get_constraintdef(k.oid) LIKE '%object_type%' AND pg_get_constraintdef(k.oid) NOT LIKE '%~%'
    LOOP
        EXECUTE format('ALTER TABLE %I.%I DROP CONSTRAINT %I', c.sch, c.tbl, c.con);
        EXECUTE format('ALTER TABLE %I.%I ADD CONSTRAINT %I CHECK (object_type ~ ''^[a-z][a-z_]{1,30}$'')', c.sch, c.tbl, c.tbl || '_object_type_known');
    END LOOP;
END;
$$;

-- ================================================================ privileges (least privilege)
GRANT SELECT, INSERT, UPDATE ON proc.work_center, proc.tool, proc.plan, proc.plan_revision TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON proc.plan_iteration TO keel_app;
GRANT SELECT, INSERT, DELETE ON proc.tool_use TO keel_app;
GRANT SELECT, INSERT ON proc.tool_calibration, proc.tool_notice, proc.review_binding TO keel_app;
