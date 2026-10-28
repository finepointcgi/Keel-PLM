-- Increment 1.6: change management (ADR-0019). Problem reports (PR), change requests (ECR), change notices (ECN) and change tasks (CT).
-- Change objects share one table (type column) because they share numbering, markings, lifecycle machinery, search, links and the
-- workflow gateway; tasks have their own shape and live in their own table. Nothing is ever deleted.
CREATE SCHEMA chg;

-- ---------------------------------------------------------------- lifecycles are data (shared tables of 0002)
-- is_final: the state ends the work (used for "open change" queries such as conflict detection).
ALTER TABLE plm.lifecycle_state ADD COLUMN is_final boolean NOT NULL DEFAULT false;

-- A revision created by a change that is abandoned before release is retired through the change process.
INSERT INTO plm.lifecycle_transition VALUES ('part-default', 'InWork', 'Obsolete', 'change');

INSERT INTO plm.lifecycle VALUES
    ('pr-default',  'Problem report lifecycle'),
    ('ecr-default', 'Change request lifecycle'),
    ('ecn-default', 'Change notice lifecycle'),
    ('ct-default',  'Change task lifecycle');

INSERT INTO plm.lifecycle_state (lifecycle_id, state, label, sort_order, is_initial, is_locked, is_final) VALUES
    ('pr-default', 'Open',        'Open',        10, true,  false, false),
    ('pr-default', 'Evaluating',  'Evaluating',  20, false, false, false),
    ('pr-default', 'Resolved',    'Resolved',    30, false, true,  true),
    ('pr-default', 'Rejected',    'Rejected',    40, false, true,  true),
    ('pr-default', 'Closed',      'Closed',      50, false, true,  true),
    ('ecr-default', 'Draft',       'Draft',        10, true,  false, false),
    ('ecr-default', 'UnderReview', 'Under review', 20, false, true,  false),
    ('ecr-default', 'Approved',    'Approved',     30, false, true,  false),
    ('ecr-default', 'Rejected',    'Rejected',     40, false, true,  true),
    ('ecr-default', 'Implemented', 'Implemented',  50, false, true,  true),
    ('ecr-default', 'Closed',      'Closed',       60, false, true,  true),
    ('ecn-default', 'Draft',       'Draft',        10, true,  false, false),
    ('ecn-default', 'UnderReview', 'Under review', 20, false, true,  false),
    ('ecn-default', 'Released',    'Released',     30, false, true,  false),
    ('ecn-default', 'Implemented', 'Implemented',  40, false, true,  true),
    ('ecn-default', 'Cancelled',   'Cancelled',    50, false, true,  true),
    ('ct-default', 'Open',       'Open',        10, true,  false, false),
    ('ct-default', 'InProgress', 'In progress', 20, false, false, false),
    ('ct-default', 'Done',       'Done',        30, false, true,  true),
    ('ct-default', 'Cancelled',  'Cancelled',   40, false, true,  true);

-- 'manual': a user with Modify rights takes it; 'workflow': only the review workflow (CCB for an ECR, release review for an ECN);
-- 'change': only as a consequence of a released change notice.
INSERT INTO plm.lifecycle_transition (lifecycle_id, from_state, to_state, via) VALUES
    ('pr-default', 'Open',       'Evaluating', 'manual'),
    ('pr-default', 'Evaluating', 'Open',       'manual'),
    ('pr-default', 'Evaluating', 'Resolved',   'manual'),
    ('pr-default', 'Open',       'Rejected',   'manual'),
    ('pr-default', 'Evaluating', 'Rejected',   'manual'),
    ('pr-default', 'Open',       'Closed',     'manual'),
    ('pr-default', 'Evaluating', 'Closed',     'manual'),
    ('pr-default', 'Resolved',   'Closed',     'manual'),
    ('pr-default', 'Rejected',   'Closed',     'manual'),
    ('ecr-default', 'Draft',       'UnderReview', 'workflow'),
    ('ecr-default', 'UnderReview', 'Approved',    'workflow'),
    ('ecr-default', 'UnderReview', 'Rejected',    'workflow'),
    ('ecr-default', 'UnderReview', 'Draft',       'workflow'),
    ('ecr-default', 'Rejected',    'Draft',       'manual'),
    ('ecr-default', 'Rejected',    'Closed',      'manual'),
    ('ecr-default', 'Draft',       'Closed',      'manual'),
    ('ecr-default', 'Approved',    'Closed',      'manual'),
    ('ecr-default', 'Approved',    'Implemented', 'change'),
    ('ecn-default', 'Draft',       'UnderReview', 'workflow'),
    ('ecn-default', 'UnderReview', 'Released',    'workflow'),
    ('ecn-default', 'UnderReview', 'Draft',       'workflow'),
    ('ecn-default', 'Draft',       'Cancelled',   'manual'),
    ('ecn-default', 'Released',    'Implemented', 'manual'),
    ('ct-default', 'Open',       'InProgress', 'manual'),
    ('ct-default', 'InProgress', 'Done',       'manual'),
    ('ct-default', 'Open',       'Done',       'manual'),
    ('ct-default', 'Open',       'Cancelled',  'manual'),
    ('ct-default', 'InProgress', 'Cancelled',  'manual');

-- Reason codes for leaving a lifecycle without completing it (data, like wf.reason_code).
CREATE TABLE chg.reason_code (
    scope      text    NOT NULL CHECK (scope IN ('pr.reject', 'pr.close', 'ecr.close', 'ecn.cancel')),
    code       text    NOT NULL CHECK (code ~ '^[A-Za-z][A-Za-z0-9]{0,40}$'),
    label      text    NOT NULL,
    sort_order int     NOT NULL,
    is_active  boolean NOT NULL DEFAULT true,
    PRIMARY KEY (scope, code)
);
INSERT INTO chg.reason_code (scope, code, label, sort_order) VALUES
    ('pr.reject', 'NotAProblem',      'Not a problem',          10),
    ('pr.reject', 'CannotReproduce',  'Cannot be reproduced',   20),
    ('pr.reject', 'Duplicate',        'Duplicate of another report', 30),
    ('pr.reject', 'OutOfScope',       'Out of scope',           40),
    ('pr.close',  'NoActionRequired', 'No action required',     10),
    ('pr.close',  'Duplicate',        'Duplicate of another report', 20),
    ('pr.close',  'Withdrawn',        'Withdrawn by the reporter', 30),
    ('pr.close',  'Superseded',       'Superseded by another report', 40),
    ('ecr.close', 'Withdrawn',        'Withdrawn by the requester', 10),
    ('ecr.close', 'Duplicate',        'Duplicate of another request', 20),
    ('ecr.close', 'Superseded',       'Superseded by another request', 30),
    ('ecr.close', 'NoLongerNeeded',   'No longer needed',       40),
    ('ecn.cancel', 'Superseded',      'Superseded by another notice', 10),
    ('ecn.cancel', 'NoLongerNeeded',  'No longer needed',       20),
    ('ecn.cancel', 'ScopeChange',     'Scope changed; start a new notice', 30),
    ('ecn.cancel', 'Withdrawn',       'Withdrawn by the requester', 40);

-- ---------------------------------------------------------------- helpers over the lifecycle data
CREATE FUNCTION chg.is_locked_state(p_lifecycle text, p_state text) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT COALESCE((SELECT is_locked FROM plm.lifecycle_state WHERE lifecycle_id = p_lifecycle AND state = p_state), true);
$$;
CREATE FUNCTION chg.is_final_state(p_lifecycle text, p_state text) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT COALESCE((SELECT is_final FROM plm.lifecycle_state WHERE lifecycle_id = p_lifecycle AND state = p_state), true);
$$;

CREATE FUNCTION chg.reject_modification() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'change records are never modified or deleted (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;

-- ---------------------------------------------------------------- change objects (PR, ECR, ECN)
CREATE SEQUENCE chg.pr_seq START 1;
CREATE SEQUENCE chg.ecr_seq START 1;
CREATE SEQUENCE chg.ecn_seq START 1;
CREATE SEQUENCE chg.ct_seq START 1;

CREATE TABLE chg.change (
    id                     uuid        PRIMARY KEY,
    change_type            text        NOT NULL CHECK (change_type IN ('PR', 'ECR', 'ECN')),
    number                 text        NOT NULL UNIQUE,
    title                  text        NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    description            text        NOT NULL DEFAULT '' CHECK (length(description) <= 8000),
    priority               text        NOT NULL CHECK (priority IN ('Low', 'Medium', 'High', 'Critical')),
    -- Class I / II (A8). Required on an ECN; an ECR is classified before it goes to the CCB; a PR has none.
    change_class           text        CHECK (change_class IN ('I', 'II')),
    reason                 text        NOT NULL DEFAULT '' CHECK (length(reason) <= 4000),
    requester_sub          text        NOT NULL,
    requester_username     text        NOT NULL,
    requester_name         text        NOT NULL,
    -- PR -> ECR -> ECN: the object this one was created from (a PR may spawn several ECRs, an ECR several ECNs).
    source_id              uuid        REFERENCES chg.change (id),
    lifecycle_id           text        NOT NULL,
    lifecycle_state        text        NOT NULL,
    -- Number of times the object went to review; each submission freezes a snapshot that the signatures cover.
    submission_no          int         NOT NULL DEFAULT 0 CHECK (submission_no >= 0),
    close_reason_code      text,
    close_reason           text,
    -- Marking columns: the contract used by AccessPolicy.BuildReadFilter (ADR-0004). They dominate every listed object (ADR-0019).
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
    CONSTRAINT change_number_format CHECK (number ~ ('^' || change_type || '-[0-9]{6,}$')),
    CONSTRAINT change_lifecycle_matches_type CHECK (lifecycle_id = lower(change_type) || '-default'),
    CONSTRAINT change_pr_has_no_class_or_source CHECK (change_type <> 'PR' OR (change_class IS NULL AND source_id IS NULL)),
    CONSTRAINT change_ecn_is_classified CHECK (change_type <> 'ECN' OR change_class IS NOT NULL),
    -- A8: a Class I change notice always descends from a change request (approved by the CCB, see the insert trigger).
    CONSTRAINT change_class_i_needs_ecr CHECK (change_type <> 'ECN' OR change_class <> 'I' OR source_id IS NOT NULL),
    -- A change request the CCB reviewed (or approved) is always classified.
    CONSTRAINT change_reviewed_ecr_is_classified CHECK (change_type <> 'ECR' OR lifecycle_state NOT IN ('UnderReview', 'Approved', 'Implemented') OR change_class IS NOT NULL)
);
CREATE INDEX change_type_state_idx ON chg.change (change_type, lifecycle_state, created_at DESC);
CREATE INDEX change_source_idx ON chg.change (source_id);
CREATE INDEX change_program_idx ON chg.change (program);
CREATE INDEX change_number_trgm ON chg.change USING gin (number gin_trgm_ops);
CREATE INDEX change_title_trgm ON chg.change USING gin (title gin_trgm_ops);

CREATE FUNCTION chg.guard_change_insert() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    p chg.change;
BEGIN
    IF NEW.source_id IS NOT NULL THEN
        SELECT * INTO p FROM chg.change WHERE id = NEW.source_id;
        IF NEW.change_type = 'ECR' AND p.change_type IS DISTINCT FROM 'PR' THEN
            RAISE EXCEPTION 'a change request can only be created from a problem report' USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.change_type = 'ECN' AND p.change_type IS DISTINCT FROM 'ECR' THEN
            RAISE EXCEPTION 'a change notice can only be created from a change request' USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.change_type = 'ECN' AND NEW.change_class = 'I' AND (p.change_class IS DISTINCT FROM 'I' OR p.lifecycle_state <> 'Approved') THEN
            RAISE EXCEPTION 'a Class I change notice needs a Class I change request approved by the CCB' USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER change_insert_guard BEFORE INSERT ON chg.change FOR EACH ROW EXECUTE FUNCTION chg.guard_change_insert();

CREATE FUNCTION chg.guard_change_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id <> OLD.id OR NEW.number <> OLD.number OR NEW.change_type <> OLD.change_type OR NEW.source_id IS DISTINCT FROM OLD.source_id
       OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by OR NEW.requester_sub <> OLD.requester_sub
       OR NEW.lifecycle_id <> OLD.lifecycle_id THEN
        RAISE EXCEPTION 'change identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    -- Content is frozen in locked states (under review, approved, released, closed ...): a released change notice never changes.
    IF chg.is_locked_state(OLD.lifecycle_id, OLD.lifecycle_state) AND (
           NEW.title <> OLD.title OR NEW.description <> OLD.description OR NEW.priority <> OLD.priority
        OR NEW.change_class IS DISTINCT FROM OLD.change_class OR NEW.reason <> OLD.reason
        OR NEW.export_jurisdiction <> OLD.export_jurisdiction OR NEW.eccn IS DISTINCT FROM OLD.eccn
        OR NEW.usml_category IS DISTINCT FROM OLD.usml_category OR NEW.cui_categories <> OLD.cui_categories
        OR NEW.distribution_statement <> OLD.distribution_statement OR NEW.controlling_office IS DISTINCT FROM OLD.controlling_office
        OR NEW.distribution_reason IS DISTINCT FROM OLD.distribution_reason OR NEW.program IS DISTINCT FROM OLD.program
        OR NEW.owning_org <> OLD.owning_org OR NEW.export_authorizations <> OLD.export_authorizations) THEN
        RAISE EXCEPTION '% % is locked in state %', OLD.change_type, OLD.number, OLD.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.lifecycle_state <> OLD.lifecycle_state AND NOT EXISTS (
        SELECT 1 FROM plm.lifecycle_transition WHERE lifecycle_id = OLD.lifecycle_id AND from_state = OLD.lifecycle_state AND to_state = NEW.lifecycle_state) THEN
        RAISE EXCEPTION 'lifecycle % has no transition from % to %', OLD.lifecycle_id, OLD.lifecycle_state, NEW.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.submission_no < OLD.submission_no THEN
        RAISE EXCEPTION 'submission numbers only increase' USING ERRCODE = 'check_violation';
    END IF;
    -- An ECN is implemented only when its tasks are done (cancelled tasks do not count).
    IF NEW.lifecycle_state = 'Implemented' AND OLD.lifecycle_state <> 'Implemented' AND NEW.change_type = 'ECN' AND EXISTS (
        SELECT 1 FROM chg.change_task t WHERE t.ecn_id = NEW.id AND t.lifecycle_state IN ('Open', 'InProgress')) THEN
        RAISE EXCEPTION 'change notice % still has open change tasks', NEW.number USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER change_update_guard BEFORE UPDATE ON chg.change FOR EACH ROW EXECUTE FUNCTION chg.guard_change_update();
CREATE TRIGGER change_no_delete BEFORE DELETE ON chg.change FOR EACH ROW EXECUTE FUNCTION chg.reject_modification();

-- ---------------------------------------------------------------- affected and resulting objects
-- One row per object revision a change lists. A 'revise' row on an ECN also names the resulting revision (the new revision created
-- in work and bound to the notice). Rows are never deleted: removing an object stamps removed_at.
CREATE TABLE chg.affected_object (
    id                    uuid        PRIMARY KEY,
    change_id             uuid        NOT NULL REFERENCES chg.change (id),
    object_type           text        NOT NULL CHECK (object_type IN ('part')),
    master_id             uuid        NOT NULL,
    object_key            text        NOT NULL,
    object_name           text        NOT NULL,
    revision_id           uuid        NOT NULL,
    revision              text        NOT NULL,
    change_action         text        NOT NULL CHECK (change_action IN ('revise', 'obsolete', 'none')),
    resulting_revision_id uuid,
    resulting_revision    text,
    -- True while an open ECN holds this revision for revising or obsoleting. At most one holder per revision (unique index below).
    binding               boolean     NOT NULL DEFAULT false,
    added_at              timestamptz NOT NULL,
    added_by              text        NOT NULL,
    removed_at            timestamptz,
    removed_by            text,
    CONSTRAINT affected_resulting_pair CHECK ((resulting_revision_id IS NULL) = (resulting_revision IS NULL)),
    CONSTRAINT affected_resulting_only_for_revise CHECK (change_action = 'revise' OR resulting_revision_id IS NULL),
    CONSTRAINT affected_binding_needs_action CHECK (NOT binding OR change_action IN ('revise', 'obsolete')),
    CONSTRAINT affected_removed_not_bound CHECK (removed_at IS NULL OR NOT binding)
);
CREATE UNIQUE INDEX affected_once_per_change ON chg.affected_object (change_id, object_type, revision_id) WHERE removed_at IS NULL;
CREATE UNIQUE INDEX affected_one_open_binding ON chg.affected_object (object_type, revision_id) WHERE binding;
CREATE INDEX affected_master_idx ON chg.affected_object (object_type, master_id) WHERE removed_at IS NULL;
CREATE INDEX affected_resulting_idx ON chg.affected_object (resulting_revision_id) WHERE resulting_revision_id IS NOT NULL;

CREATE FUNCTION chg.guard_affected_insert() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    c chg.change;
BEGIN
    SELECT * INTO c FROM chg.change WHERE id = NEW.change_id;
    IF chg.is_locked_state(c.lifecycle_id, c.lifecycle_state) THEN
        RAISE EXCEPTION '% % is % and its affected objects are frozen', c.change_type, c.number, c.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    IF c.change_type = 'PR' AND NEW.change_action <> 'none' THEN
        RAISE EXCEPTION 'a problem report only references objects' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.binding AND c.change_type <> 'ECN' THEN
        RAISE EXCEPTION 'only a change notice holds a revision' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER affected_insert_guard BEFORE INSERT ON chg.affected_object FOR EACH ROW EXECUTE FUNCTION chg.guard_affected_insert();

-- Content never changes; only removal (while the change is open for editing) and the release of the binding are allowed.
CREATE FUNCTION chg.guard_affected_update() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    c chg.change;
BEGIN
    IF NEW.id <> OLD.id OR NEW.change_id <> OLD.change_id OR NEW.object_type <> OLD.object_type OR NEW.master_id <> OLD.master_id
       OR NEW.object_key <> OLD.object_key OR NEW.object_name <> OLD.object_name OR NEW.revision_id <> OLD.revision_id OR NEW.revision <> OLD.revision
       OR NEW.change_action <> OLD.change_action OR NEW.resulting_revision_id IS DISTINCT FROM OLD.resulting_revision_id
       OR NEW.resulting_revision IS DISTINCT FROM OLD.resulting_revision OR NEW.added_at <> OLD.added_at OR NEW.added_by <> OLD.added_by THEN
        RAISE EXCEPTION 'affected objects are immutable; remove and add again' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.binding AND NOT OLD.binding THEN
        RAISE EXCEPTION 'a binding is never re-established' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.removed_at IS NOT NULL AND (NEW.removed_at IS DISTINCT FROM OLD.removed_at OR NEW.removed_by IS DISTINCT FROM OLD.removed_by) THEN
        RAISE EXCEPTION 'a removed affected object stays removed' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.removed_at IS NULL AND NEW.removed_at IS NOT NULL THEN
        SELECT * INTO c FROM chg.change WHERE id = NEW.change_id;
        IF chg.is_locked_state(c.lifecycle_id, c.lifecycle_state) THEN
            RAISE EXCEPTION '% % is % and its affected objects are frozen', c.change_type, c.number, c.lifecycle_state USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER affected_update_guard BEFORE UPDATE ON chg.affected_object FOR EACH ROW EXECUTE FUNCTION chg.guard_affected_update();
CREATE TRIGGER affected_no_delete BEFORE DELETE ON chg.affected_object FOR EACH ROW EXECUTE FUNCTION chg.reject_modification();

-- ---------------------------------------------------------------- change tasks
CREATE TABLE chg.change_task (
    id                uuid        PRIMARY KEY,
    number            text        NOT NULL UNIQUE CHECK (number ~ '^CT-[0-9]{6,}$'),
    ecn_id            uuid        NOT NULL REFERENCES chg.change (id),
    title             text        NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    description       text        NOT NULL DEFAULT '' CHECK (length(description) <= 4000),
    assignee_sub      text        NOT NULL,
    assignee_username text        NOT NULL,
    assignee_name     text        NOT NULL,
    due_date          date,
    lifecycle_id      text        NOT NULL DEFAULT 'ct-default' CHECK (lifecycle_id = 'ct-default'),
    lifecycle_state   text        NOT NULL DEFAULT 'Open',
    completion_note   text,
    cancel_reason     text,
    created_at        timestamptz NOT NULL,
    created_by        text        NOT NULL,
    started_at        timestamptz,
    completed_at      timestamptz,
    completed_by      text,
    state_changed_at  timestamptz NOT NULL,
    FOREIGN KEY (lifecycle_id, lifecycle_state) REFERENCES plm.lifecycle_state (lifecycle_id, state),
    CONSTRAINT task_done_has_note CHECK (lifecycle_state <> 'Done' OR length(btrim(COALESCE(completion_note, ''))) > 0),
    CONSTRAINT task_cancelled_has_reason CHECK (lifecycle_state <> 'Cancelled' OR length(btrim(COALESCE(cancel_reason, ''))) > 0)
);
CREATE INDEX change_task_ecn_idx ON chg.change_task (ecn_id);
CREATE INDEX change_task_assignee_idx ON chg.change_task (assignee_sub) WHERE lifecycle_state IN ('Open', 'InProgress');

CREATE FUNCTION chg.guard_task_insert() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    c chg.change;
BEGIN
    SELECT * INTO c FROM chg.change WHERE id = NEW.ecn_id;
    IF c.change_type IS DISTINCT FROM 'ECN' THEN
        RAISE EXCEPTION 'change tasks belong to change notices' USING ERRCODE = 'check_violation';
    END IF;
    IF chg.is_final_state(c.lifecycle_id, c.lifecycle_state) THEN
        RAISE EXCEPTION 'change notice % is % and takes no new tasks', c.number, c.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER task_insert_guard BEFORE INSERT ON chg.change_task FOR EACH ROW EXECUTE FUNCTION chg.guard_task_insert();

CREATE FUNCTION chg.guard_task_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF chg.is_final_state(OLD.lifecycle_id, OLD.lifecycle_state) THEN
        RAISE EXCEPTION 'a finished change task is immutable' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.id <> OLD.id OR NEW.number <> OLD.number OR NEW.ecn_id <> OLD.ecn_id OR NEW.assignee_sub <> OLD.assignee_sub
       OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
        RAISE EXCEPTION 'change task identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.lifecycle_state <> OLD.lifecycle_state AND NOT EXISTS (
        SELECT 1 FROM plm.lifecycle_transition WHERE lifecycle_id = OLD.lifecycle_id AND from_state = OLD.lifecycle_state AND to_state = NEW.lifecycle_state) THEN
        RAISE EXCEPTION 'lifecycle % has no transition from % to %', OLD.lifecycle_id, OLD.lifecycle_state, NEW.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER task_update_guard BEFORE UPDATE ON chg.change_task FOR EACH ROW EXECUTE FUNCTION chg.guard_task_update();
CREATE TRIGGER task_no_delete BEFORE DELETE ON chg.change_task FOR EACH ROW EXECUTE FUNCTION chg.reject_modification();

-- ---------------------------------------------------------------- history and review snapshots (immutable)
CREATE TABLE chg.history (
    id         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    change_id  uuid        NOT NULL REFERENCES chg.change (id),
    task_id    uuid        REFERENCES chg.change_task (id),
    at         timestamptz NOT NULL,
    actor_sub  text        NOT NULL,
    actor_name text        NOT NULL,
    action     text        NOT NULL,
    detail     text
);
CREATE INDEX history_change_idx ON chg.history (change_id, id);
CREATE TRIGGER history_immutable BEFORE UPDATE OR DELETE ON chg.history FOR EACH ROW EXECUTE FUNCTION chg.reject_modification();
CREATE TRIGGER history_no_truncate BEFORE TRUNCATE ON chg.history FOR EACH STATEMENT EXECUTE FUNCTION chg.reject_modification();

-- What a review (and its signatures) covered: the change's content and the resulting iterations, frozen when it was submitted.
-- The id is derived from (change, submission number) so the workflow engine can bind to it before the submission is written.
CREATE TABLE chg.change_submission (
    id              uuid        PRIMARY KEY,
    change_id       uuid        NOT NULL REFERENCES chg.change (id),
    submission_no   int         NOT NULL CHECK (submission_no >= 1),
    snapshot        text        NOT NULL,
    snapshot_sha256 bytea       NOT NULL CHECK (octet_length(snapshot_sha256) = 32),
    created_at      timestamptz NOT NULL,
    created_by      text        NOT NULL,
    UNIQUE (change_id, submission_no)
);
CREATE TRIGGER submission_immutable BEFORE UPDATE OR DELETE ON chg.change_submission FOR EACH ROW EXECUTE FUNCTION chg.reject_modification();
CREATE TRIGGER submission_no_truncate BEFORE TRUNCATE ON chg.change_submission FOR EACH STATEMENT EXECUTE FUNCTION chg.reject_modification();

-- ---------------------------------------------------------------- workflow templates (ordinary data of 0006)
INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('ecr-ccb-review', 1, 'ECR CCB review',
        'The chosen Change Control Board members approve in parallel; every member must approve.',
        'ecr', $tpl$
{
  "id": "ecr-ccb-review",
  "name": "ECR CCB review",
  "description": "The chosen Change Control Board members approve in parallel; every member must approve.",
  "objectType": "ecr",
  "slots": [
    { "name": "ccb", "label": "CCB members", "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "CCB review", "steps": ["ccb-approval", "approved"] },
    { "id": "ccb-approval", "type": "Parallel", "name": "CCB approval", "rule": { "type": "All" },
      "fanOut": { "slot": "ccb", "kind": "approval" }, "dueDays": 7 },
    { "id": "approved", "type": "End",      "name": "Approved", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system'),
('ecn-release-review', 1, 'ECN release review',
        'Serial technical review by one reviewer, then parallel approval where every chosen approver must approve, then the change notice releases all resulting revisions at once.',
        'ecn', $tpl$
{
  "id": "ecn-release-review",
  "name": "ECN release review",
  "description": "Serial technical review by one reviewer, then parallel approval where every chosen approver must approve, then the change notice releases all resulting revisions at once.",
  "objectType": "ecn",
  "slots": [
    { "name": "reviewer",  "label": "Technical reviewer", "role": "plm-viewer",   "min": 1, "max": 1 },
    { "name": "approvers", "label": "Approvers",          "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "Release review", "steps": ["tech-review", "approval", "release"] },
    { "id": "tech-review", "type": "Task",  "name": "Technical review", "kind": "review",
      "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 5 },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" },
      "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "release",  "type": "End",      "name": "Release", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

-- ---------------------------------------------------------------- privileges (least privilege for keel_app)
GRANT USAGE ON SCHEMA chg TO keel_app;
GRANT SELECT ON chg.reason_code TO keel_app;
GRANT SELECT, INSERT, UPDATE ON chg.change, chg.affected_object, chg.change_task TO keel_app;
GRANT SELECT, INSERT ON chg.history, chg.change_submission TO keel_app;
GRANT USAGE ON SEQUENCE chg.pr_seq, chg.ecr_seq, chg.ecn_seq, chg.ct_seq TO keel_app;
