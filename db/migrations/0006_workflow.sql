-- Increment 1.5: review workflow, electronic signatures and release (ADR-0006, ADR-0007, ADR-0018).
-- Also adds the user directory (identity.user_directory): identities live in the IdP, so the application keeps a
-- snapshot of each user it has seen, to offer participant pickers and to check eligibility before assigning a task.
CREATE SCHEMA identity;
CREATE SCHEMA wf;

-- ---------------------------------------------------------------- user directory
-- ASSUMPTION(A50): attributes are the snapshot from the user's last sign-in or API call. Eligibility is evaluated from
-- it when a participant is picked, and re-evaluated against the live token when the user signs.
CREATE TABLE identity.user_directory (
    sub                   text        PRIMARY KEY,
    username              text        NOT NULL,
    display_name          text        NOT NULL,
    org                   text,
    roles                 text[]      NOT NULL,
    us_person             boolean     NOT NULL,
    citizenship           text,
    programs              text[]      NOT NULL,
    export_authorizations text[]      NOT NULL,
    cui_categories        text[]      NOT NULL,
    first_seen            timestamptz NOT NULL,
    last_seen             timestamptz NOT NULL
);
CREATE INDEX user_directory_username_idx ON identity.user_directory (lower(username));
CREATE INDEX user_directory_name_idx ON identity.user_directory (lower(display_name));

-- ---------------------------------------------------------------- templates (versioned data, ADR-0006)
CREATE TABLE wf.template (
    id          text        NOT NULL CHECK (id ~ '^[a-z][a-z0-9-]{0,62}$'),
    version     int         NOT NULL CHECK (version >= 1),
    name        text        NOT NULL,
    description text,
    object_type text        NOT NULL,
    definition  jsonb       NOT NULL,
    is_active   boolean     NOT NULL,
    created_at  timestamptz NOT NULL,
    created_by  text        NOT NULL,
    PRIMARY KEY (id, version)
);
CREATE UNIQUE INDEX template_one_active ON wf.template (id) WHERE is_active;

-- A version's definition never changes; replacing a template inserts a new version and deactivates the old one.
CREATE FUNCTION wf.guard_template_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id <> OLD.id OR NEW.version <> OLD.version OR NEW.definition <> OLD.definition OR NEW.name <> OLD.name
       OR NEW.object_type <> OLD.object_type OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
        RAISE EXCEPTION 'workflow template versions are immutable; upload a new version' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER template_guard BEFORE UPDATE ON wf.template FOR EACH ROW EXECUTE FUNCTION wf.guard_template_update();

CREATE FUNCTION wf.reject_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows cannot be deleted', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER template_no_delete BEFORE DELETE ON wf.template FOR EACH ROW EXECUTE FUNCTION wf.reject_delete();

-- ---------------------------------------------------------------- signature meanings (data, A9)
CREATE TABLE wf.reason_code (
    code             text    PRIMARY KEY CHECK (code ~ '^[A-Za-z][A-Za-z0-9]{0,40}$'),
    label            text    NOT NULL,
    decision         text    NOT NULL CHECK (decision IN ('approve', 'reject')),
    requires_comment boolean NOT NULL,
    applies_to       text[]  NOT NULL,
    sort_order       int     NOT NULL,
    is_active        boolean NOT NULL DEFAULT true
);
INSERT INTO wf.reason_code (code, label, decision, requires_comment, applies_to, sort_order) VALUES
    ('Reviewed',             'Reviewed',               'approve', false, ARRAY['review'],             10),
    ('Approved',             'Approved',               'approve', false, ARRAY['approval'],           20),
    ('ApprovedWithComments', 'Approved with comments', 'approve', true,  ARRAY['review', 'approval'], 30),
    ('Rejected',             'Rejected',               'reject',  true,  ARRAY['review', 'approval'], 40);

-- ---------------------------------------------------------------- instances, tokens, tasks
CREATE TABLE wf.instance (
    id                    uuid        PRIMARY KEY,
    template_id           text        NOT NULL,
    template_version      int         NOT NULL,
    -- The definition the instance runs, copied at start: replacing the template never changes a running review.
    definition            jsonb       NOT NULL,
    participants          jsonb       NOT NULL,
    object_type           text        NOT NULL,
    object_key            text        NOT NULL,
    object_id             uuid        NOT NULL,
    revision_id           uuid        NOT NULL,
    object_revision       text        NOT NULL,
    iteration_id          uuid        NOT NULL,
    iteration_label       text        NOT NULL,
    status                text        NOT NULL CHECK (status IN ('Active', 'Approved', 'Rejected', 'Cancelled')),
    comment               text,
    started_by            text        NOT NULL,
    started_by_username   text        NOT NULL,
    started_by_name       text        NOT NULL,
    started_at            timestamptz NOT NULL,
    completed_at          timestamptz,
    completed_by          text,
    completion_note       text,
    -- Marking snapshot at start (the column contract of ADR-0004), so list queries can use AccessPolicy.BuildReadFilter.
    -- Marked data is locked while a workflow runs, so the snapshot equals the live markings during the review.
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
    FOREIGN KEY (template_id, template_version) REFERENCES wf.template (id, version),
    CHECK ((status = 'Active') = (completed_at IS NULL))
);
-- One active review per object, enforced by the database as well as by the object lock.
CREATE UNIQUE INDEX instance_one_active ON wf.instance (object_type, object_id) WHERE status = 'Active';
CREATE INDEX instance_object_idx ON wf.instance (object_type, object_key, started_at DESC);
CREATE INDEX instance_status_idx ON wf.instance (status, started_at DESC);

CREATE FUNCTION wf.guard_instance_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status <> 'Active' THEN
        RAISE EXCEPTION 'a completed workflow instance is immutable' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.id <> OLD.id OR NEW.object_type <> OLD.object_type OR NEW.object_id <> OLD.object_id OR NEW.revision_id <> OLD.revision_id
       OR NEW.iteration_id <> OLD.iteration_id OR NEW.definition <> OLD.definition OR NEW.participants <> OLD.participants
       OR NEW.started_by <> OLD.started_by OR NEW.started_at <> OLD.started_at THEN
        RAISE EXCEPTION 'workflow instance identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER instance_guard BEFORE UPDATE ON wf.instance FOR EACH ROW EXECUTE FUNCTION wf.guard_instance_update();
CREATE TRIGGER instance_no_delete BEFORE DELETE ON wf.instance FOR EACH ROW EXECUTE FUNCTION wf.reject_delete();

-- A token marks where the instance is: one row per activated node. Serial tokens remember their step; Parallel tokens
-- their branch count, so the vote rule can be decided from persisted state alone.
CREATE TABLE wf.token (
    id           uuid        PRIMARY KEY,
    instance_id  uuid        NOT NULL REFERENCES wf.instance (id),
    node_id      text        NOT NULL,
    parent_id    uuid        REFERENCES wf.token (id),
    branch       int,
    step_index   int         NOT NULL DEFAULT 0,
    branch_count int         NOT NULL DEFAULT 0,
    status       text        NOT NULL CHECK (status IN ('Active', 'Completed', 'Rejected', 'Skipped')),
    created_at   timestamptz NOT NULL,
    completed_at timestamptz
);
CREATE INDEX token_instance_idx ON wf.token (instance_id);
CREATE TRIGGER token_no_delete BEFORE DELETE ON wf.token FOR EACH ROW EXECUTE FUNCTION wf.reject_delete();

CREATE TABLE wf.task (
    id                uuid        PRIMARY KEY,
    instance_id       uuid        NOT NULL REFERENCES wf.instance (id),
    token_id          uuid        NOT NULL REFERENCES wf.token (id),
    node_id           text        NOT NULL,
    name              text        NOT NULL,
    kind              text        NOT NULL CHECK (kind IN ('review', 'approval')),
    -- Null assignee = pool task: any holder of required_role who may read the object can complete it.
    assignee_sub      text,
    assignee_username text,
    assignee_name     text,
    required_role     text        NOT NULL,
    status            text        NOT NULL CHECK (status IN ('Open', 'Completed', 'Skipped', 'Cancelled')),
    decision          text        CHECK (decision IN ('approve', 'reject')),
    signature_id      uuid,
    due_at            timestamptz,
    created_at        timestamptz NOT NULL,
    completed_at      timestamptz,
    CHECK ((status = 'Completed') = (decision IS NOT NULL))
);
CREATE INDEX task_assignee_idx ON wf.task (assignee_sub) WHERE status = 'Open';
CREATE INDEX task_instance_idx ON wf.task (instance_id);

CREATE FUNCTION wf.guard_task_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status <> 'Open' THEN
        RAISE EXCEPTION 'a finished workflow task is immutable' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.id <> OLD.id OR NEW.instance_id <> OLD.instance_id OR NEW.token_id <> OLD.token_id OR NEW.node_id <> OLD.node_id
       OR NEW.kind <> OLD.kind OR NEW.required_role <> OLD.required_role OR NEW.created_at <> OLD.created_at THEN
        RAISE EXCEPTION 'workflow task identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER task_guard BEFORE UPDATE ON wf.task FOR EACH ROW EXECUTE FUNCTION wf.guard_task_update();
CREATE TRIGGER task_no_delete BEFORE DELETE ON wf.task FOR EACH ROW EXECUTE FUNCTION wf.reject_delete();

-- ---------------------------------------------------------------- electronic signatures (A9, ADR-0007)
CREATE TABLE wf.signature (
    id                  uuid        PRIMARY KEY,
    instance_id         uuid        NOT NULL REFERENCES wf.instance (id),
    task_id             uuid        NOT NULL UNIQUE REFERENCES wf.task (id),
    signer_sub          text        NOT NULL,
    signer_username     text        NOT NULL,
    signer_display_name text        NOT NULL,
    signer_org          text,
    meaning_code        text        NOT NULL REFERENCES wf.reason_code (code),
    meaning_label       text        NOT NULL,
    decision            text        NOT NULL CHECK (decision IN ('approve', 'reject')),
    comment             text,
    signed_at           timestamptz NOT NULL,
    auth_time           timestamptz,
    object_type         text        NOT NULL,
    object_key          text        NOT NULL,
    object_revision     text        NOT NULL,
    iteration_id        uuid        NOT NULL,
    iteration_label     text        NOT NULL,
    marking_banner      text        NOT NULL,
    markings            jsonb       NOT NULL,
    -- SHA-256 over the canonical content of the signed iteration (description, attributes, markings).
    content_hash        bytea       NOT NULL CHECK (octet_length(content_hash) = 32),
    -- SHA-256 over the canonical signed statement: identity, iteration, content hash, meaning, signer, time.
    signature_hash      bytea       NOT NULL CHECK (octet_length(signature_hash) = 32)
);
CREATE INDEX signature_instance_idx ON wf.signature (instance_id, signed_at);
CREATE INDEX signature_object_idx ON wf.signature (object_type, object_key, signed_at DESC);

CREATE FUNCTION wf.reject_modification() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'signature and history records are immutable (% on %)', TG_OP, TG_TABLE_NAME
        USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER signature_immutable BEFORE UPDATE OR DELETE ON wf.signature FOR EACH ROW EXECUTE FUNCTION wf.reject_modification();
CREATE TRIGGER signature_no_truncate BEFORE TRUNCATE ON wf.signature FOR EACH STATEMENT EXECUTE FUNCTION wf.reject_modification();

-- Human-readable workflow history for the UI (the tamper-evident record is the audit chain).
CREATE TABLE wf.event (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    instance_id uuid        NOT NULL REFERENCES wf.instance (id),
    at          timestamptz NOT NULL,
    actor_sub   text        NOT NULL,
    actor_name  text        NOT NULL,
    action      text        NOT NULL,
    detail      text
);
CREATE INDEX event_instance_idx ON wf.event (instance_id, id);
CREATE TRIGGER event_immutable BEFORE UPDATE OR DELETE ON wf.event FOR EACH ROW EXECUTE FUNCTION wf.reject_modification();
CREATE TRIGGER event_no_truncate BEFORE TRUNCATE ON wf.event FOR EACH STATEMENT EXECUTE FUNCTION wf.reject_modification();

-- ---------------------------------------------------------------- seed data
-- The seeded template is ordinary data: administrators replace it by uploading a new version through the API.
INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('part-release-review', 1, 'Part release review',
        'Serial technical review by one reviewer, then parallel approval where every chosen approver must approve, then release.',
        'part', $tpl$
{
  "id": "part-release-review",
  "name": "Part release review",
  "description": "Serial technical review by one reviewer, then parallel approval where every chosen approver must approve, then release.",
  "objectType": "part",
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

-- Review states belong to the workflow engine: the manual path no longer enters or leaves Under Review
-- (release was already workflow-only). Administrators can still correct a state with the audited "set state" override.
UPDATE plm.lifecycle_transition SET via = 'workflow'
WHERE lifecycle_id = 'part-default' AND ((from_state = 'InWork' AND to_state = 'UnderReview') OR (from_state = 'UnderReview' AND to_state = 'InWork'));

-- ---------------------------------------------------------------- privileges (least privilege for keel_app)
GRANT USAGE ON SCHEMA identity, wf TO keel_app;
GRANT SELECT, INSERT, UPDATE ON identity.user_directory TO keel_app;
GRANT SELECT, INSERT, UPDATE ON wf.template, wf.instance, wf.token, wf.task TO keel_app;
GRANT SELECT ON wf.reason_code TO keel_app;
GRANT SELECT, INSERT ON wf.signature, wf.event TO keel_app;
