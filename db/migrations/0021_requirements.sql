-- Requirements and traceability (ADR-0038). A requirement specification is a versioned object (revision, iteration, release by review with e-signatures);
-- its requirements are permanent identities with one immutable version row per iteration, each carrying its own markings. Traceability links join a requirement to
-- another requirement, a part, a document or a change; verification records are immutable and a formal one carries an e-signature. Forward-only; nothing is deleted.
CREATE SCHEMA req;
GRANT USAGE ON SCHEMA req TO keel_app;

-- ================================================================ link types (administrator data)
-- The source of a link is always a requirement. coverage_role says what a link of this type means for coverage: an allocation (the requirement is implemented by the target)
-- or a verification plan (the target is test evidence or a specification that verifies it).
CREATE TABLE req.link_type (
    code          text        PRIMARY KEY CHECK (code ~ '^[a-z][a-z0-9-]{1,39}$'),
    label         text        NOT NULL CHECK (length(label) BETWEEN 1 AND 60),
    reverse_label text        NOT NULL CHECK (length(reverse_label) BETWEEN 1 AND 60),
    target_kind   text        NOT NULL CHECK (target_kind IN ('requirement', 'part', 'document', 'change')),
    coverage_role text        NOT NULL DEFAULT 'none' CHECK (coverage_role IN ('none', 'allocation', 'verification')),
    is_system     boolean     NOT NULL DEFAULT false,
    is_active     boolean     NOT NULL DEFAULT true,
    sort_order    int         NOT NULL DEFAULT 100,
    updated_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    text        NOT NULL DEFAULT 'system'
);
INSERT INTO req.link_type (code, label, reverse_label, target_kind, coverage_role, is_system, sort_order) VALUES
    ('derives-from',  'Derives from',  'Parent of derived requirement', 'requirement', 'none',         true, 10),
    ('refines',       'Refines',       'Refined by',                    'requirement', 'none',         true, 20),
    ('allocated-to',  'Allocated to',  'Implements requirement',        'part',        'allocation',   true, 30),
    ('verified-by',   'Verified by',   'Verifies requirement',          'document',    'verification', true, 40),
    ('changed-by',    'Changed by',    'Changes requirement',           'change',      'none',         true, 50);

INSERT INTO cfg.numbering (object_type, prefix, width, sequence_name, prefix_editable) VALUES ('reqspec', 'RS-', 6, 'req.spec_number_seq', true);
CREATE SEQUENCE req.spec_number_seq START 1;
GRANT USAGE ON SEQUENCE req.spec_number_seq TO keel_app;

-- ================================================================ lifecycle (data, view-only in the administration UI)
INSERT INTO plm.lifecycle_family (family, object_type, name, current_version, is_system, editable) VALUES
    ('reqspec-default', 'reqspec', 'Requirement specification lifecycle', 1, true, false);
INSERT INTO plm.lifecycle (id, name, family, version, status) VALUES ('reqspec-default', 'Requirement specification lifecycle', 'reqspec-default', 1, 'published');
INSERT INTO plm.lifecycle_state (lifecycle_id, state, label, sort_order, is_initial, is_locked, is_final) VALUES
    ('reqspec-default', 'InWork',      'In Work',      10, true,  false, false),
    ('reqspec-default', 'UnderReview', 'Under Review', 20, false, true,  false),
    ('reqspec-default', 'Released',    'Released',     30, false, true,  false),
    ('reqspec-default', 'Obsolete',    'Obsolete',     40, false, true,  true);
INSERT INTO plm.lifecycle_transition (lifecycle_id, from_state, to_state, via) VALUES
    ('reqspec-default', 'InWork',      'UnderReview', 'workflow'),
    ('reqspec-default', 'UnderReview', 'InWork',      'workflow'),
    ('reqspec-default', 'UnderReview', 'Released',    'workflow'),
    ('reqspec-default', 'Released',    'Obsolete',    'manual'),
    ('reqspec-default', 'InWork',      'Obsolete',    'manual');

INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('reqspec-release-review', 1, 'Requirement specification release review',
        'Technical review by one reviewer, then parallel approval where every chosen approver must approve, then release.',
        'reqspec', $tpl$
{
  "id": "reqspec-release-review",
  "name": "Requirement specification release review",
  "description": "Technical review by one reviewer, then parallel approval where every chosen approver must approve, then release.",
  "objectType": "reqspec",
  "slots": [
    { "name": "reviewer",  "label": "Technical reviewer", "role": "plm-viewer",   "min": 1, "max": 1 },
    { "name": "approvers", "label": "Approvers",          "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "Specification release review", "steps": ["tech-review", "approval", "release"] },
    { "id": "tech-review", "type": "Task",  "name": "Technical review", "kind": "review",
      "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 5 },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" },
      "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "release",  "type": "End",      "name": "Release", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

-- ================================================================ the specification
CREATE TABLE req.spec (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE CHECK (length(number) BETWEEN 2 AND 40),
    name                   text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    product                text        CHECK (product IS NULL OR length(product) BETWEEN 1 AND 120),
    description            text        NOT NULL DEFAULT '' CHECK (length(description) <= 4000),
    lifecycle_id           text        NOT NULL REFERENCES plm.lifecycle (id),
    next_key               int         NOT NULL DEFAULT 1 CHECK (next_key >= 1),
    -- Marking columns: the contract used by AccessPolicy.BuildReadFilter (ADR-0004). Every requirement's markings dominate these.
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
CREATE INDEX spec_program_idx ON req.spec (program);
CREATE INDEX spec_number_trgm ON req.spec USING gin (number gin_trgm_ops);
CREATE INDEX spec_name_trgm ON req.spec USING gin (name gin_trgm_ops);

CREATE TABLE req.spec_revision (
    id               uuid        PRIMARY KEY,
    spec_id          uuid        NOT NULL REFERENCES req.spec (id),
    revision         text        NOT NULL,
    lifecycle_state  text        NOT NULL,
    is_latest        boolean     NOT NULL,
    latest_iteration int         NOT NULL CHECK (latest_iteration >= 1),
    created_at       timestamptz NOT NULL,
    created_by       text        NOT NULL,
    state_changed_at timestamptz NOT NULL,
    state_changed_by text        NOT NULL,
    state_reason     text,
    UNIQUE (spec_id, revision)
);
CREATE UNIQUE INDEX spec_revision_one_latest ON req.spec_revision (spec_id) WHERE is_latest;

-- Iterations are immutable. The content hash covers every requirement version of the iteration.
CREATE TABLE req.spec_iteration (
    id                uuid        PRIMARY KEY,
    revision_id       uuid        NOT NULL REFERENCES req.spec_revision (id),
    iteration         int         NOT NULL CHECK (iteration >= 1),
    comment           text        CHECK (comment IS NULL OR length(comment) <= 1000),
    content_sha256    text        NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    requirement_count int         NOT NULL CHECK (requirement_count >= 0),
    created_at        timestamptz NOT NULL,
    created_by        text        NOT NULL,
    UNIQUE (revision_id, iteration)
);

-- ================================================================ requirements
-- A requirement keeps its identity (and its key, R-0001 ...) for ever; it is never deleted, only withdrawn. Its content lives in the version rows.
CREATE TABLE req.requirement (
    id         uuid        PRIMARY KEY,
    spec_id    uuid        NOT NULL REFERENCES req.spec (id),
    key        text        NOT NULL CHECK (key ~ '^R-[0-9]{4,}$'),
    created_at timestamptz NOT NULL,
    created_by text        NOT NULL,
    UNIQUE (spec_id, key),
    UNIQUE (id, spec_id)
);

CREATE TABLE req.requirement_version (
    id                  uuid        PRIMARY KEY,
    iteration_id        uuid        NOT NULL REFERENCES req.spec_iteration (id),
    requirement_id      uuid        NOT NULL REFERENCES req.requirement (id),
    parent_requirement_id uuid      REFERENCES req.requirement (id),
    ordinal             int         NOT NULL CHECK (ordinal >= 1),
    title               text        NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    body                text        NOT NULL CHECK (length(body) BETWEEN 1 AND 8000),
    req_type            text        NOT NULL CHECK (req_type IN ('functional', 'performance', 'interface', 'environmental', 'safety')),
    priority            text        NOT NULL CHECK (priority IN ('low', 'medium', 'high', 'critical')),
    verification_method text        NOT NULL CHECK (verification_method IN ('test', 'analysis', 'inspection', 'demonstration')),
    rationale           text        NOT NULL DEFAULT '' CHECK (length(rationale) <= 4000),
    status              text        NOT NULL CHECK (status IN ('draft', 'approved', 'deferred', 'withdrawn')),
    -- SHA-256 over the content fields (not the position): what a link or a verification was reviewed against.
    content_sha256      text        NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
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
    created_at          timestamptz NOT NULL,
    created_by          text        NOT NULL,
    UNIQUE (iteration_id, requirement_id),
    CHECK (parent_requirement_id IS NULL OR parent_requirement_id <> requirement_id)
);
CREATE INDEX requirement_version_req_idx ON req.requirement_version (requirement_id);
CREATE INDEX requirement_version_parent_idx ON req.requirement_version (iteration_id, parent_requirement_id, ordinal);

-- The requirements of each specification's latest iteration: what coverage, trace and the part, document and change pages read.
CREATE VIEW req.current_requirement AS
SELECT v.*, r.spec_id AS spec_id, r.id AS spec_revision_id, r.revision AS spec_revision, r.lifecycle_state AS spec_state, i.iteration AS spec_iteration
FROM req.requirement_version v
JOIN req.spec_iteration i ON i.id = v.iteration_id
JOIN req.spec_revision r ON r.id = i.revision_id AND r.is_latest AND i.iteration = r.latest_iteration;
GRANT SELECT ON req.current_requirement TO keel_app;

-- ================================================================ traceability links
CREATE TABLE req.link (
    id                    uuid        PRIMARY KEY,
    link_type             text        NOT NULL REFERENCES req.link_type (code),
    source_requirement_id uuid        NOT NULL REFERENCES req.requirement (id),
    target_kind           text        NOT NULL CHECK (target_kind IN ('requirement', 'part', 'document', 'change')),
    target_requirement_id uuid        REFERENCES req.requirement (id),
    target_number         text        CHECK (target_number IS NULL OR length(target_number) BETWEEN 1 AND 64),
    -- The revision of the part or document the link was last reviewed against; the link is suspect once the target has a newer revision.
    reviewed_target_revision text     CHECK (reviewed_target_revision IS NULL OR length(reviewed_target_revision) <= 20),
    reviewed_source_sha   text        NOT NULL CHECK (reviewed_source_sha ~ '^[0-9a-f]{64}$'),
    reviewed_target_sha   text        CHECK (reviewed_target_sha IS NULL OR reviewed_target_sha ~ '^[0-9a-f]{64}$'),
    status                text        NOT NULL DEFAULT 'ok' CHECK (status IN ('ok', 'suspect')),
    suspect_reason        text        CHECK (suspect_reason IS NULL OR suspect_reason IN ('source_changed', 'target_changed', 'target_revised')),
    suspect_at            timestamptz,
    created_at            timestamptz NOT NULL,
    created_by            text        NOT NULL,
    reviewed_at           timestamptz,
    reviewed_by           text,
    removed_at            timestamptz,
    removed_by            text,
    CONSTRAINT link_target_shape CHECK (
        (target_kind = 'requirement' AND target_requirement_id IS NOT NULL AND target_number IS NULL AND target_requirement_id <> source_requirement_id)
        OR (target_kind <> 'requirement' AND target_requirement_id IS NULL AND target_number IS NOT NULL)),
    CONSTRAINT link_suspect_shape CHECK ((status = 'suspect') = (suspect_reason IS NOT NULL))
);
CREATE UNIQUE INDEX link_open_key ON req.link (source_requirement_id, link_type, COALESCE(target_requirement_id::text, target_kind || ':' || target_number)) WHERE removed_at IS NULL;
CREATE INDEX link_target_number_idx ON req.link (target_kind, target_number) WHERE removed_at IS NULL;
CREATE INDEX link_target_req_idx ON req.link (target_requirement_id) WHERE removed_at IS NULL;
CREATE INDEX link_source_idx ON req.link (source_requirement_id) WHERE removed_at IS NULL;

-- ================================================================ verification
CREATE TABLE req.verification (
    id                 uuid        PRIMARY KEY,
    requirement_id     uuid        NOT NULL REFERENCES req.requirement (id),
    spec_revision      text        NOT NULL,
    requirement_sha256 text        NOT NULL CHECK (requirement_sha256 ~ '^[0-9a-f]{64}$'),
    method             text        NOT NULL CHECK (method IN ('test', 'analysis', 'inspection', 'demonstration')),
    result             text        NOT NULL CHECK (result IN ('pass', 'fail', 'waived')),
    formal             boolean     NOT NULL,
    -- Notes and the waiver reason are marked data (the requirement's markings): never in audit details, notifications or logs.
    notes              text        NOT NULL DEFAULT '' CHECK (length(notes) <= 2000),
    waiver_reason      text        CHECK (waiver_reason IS NULL OR length(waiver_reason) BETWEEN 1 AND 2000),
    performed_at       timestamptz NOT NULL,
    performed_by       text        NOT NULL,
    performed_by_sub   text        NOT NULL,
    CONSTRAINT verification_waiver CHECK ((result = 'waived') = (waiver_reason IS NOT NULL))
);
CREATE INDEX verification_req_idx ON req.verification (requirement_id, performed_at DESC);

CREATE TABLE req.verification_evidence (
    verification_id   uuid NOT NULL REFERENCES req.verification (id),
    document_number   text NOT NULL CHECK (length(document_number) BETWEEN 1 AND 64),
    document_revision text CHECK (document_revision IS NULL OR length(document_revision) <= 20),
    PRIMARY KEY (verification_id, document_number)
);
CREATE INDEX verification_evidence_doc_idx ON req.verification_evidence (document_number);

CREATE TABLE req.verification_signature (
    id                  uuid        PRIMARY KEY,
    verification_id     uuid        NOT NULL UNIQUE REFERENCES req.verification (id),
    signer_sub          text        NOT NULL,
    signer_username     text        NOT NULL,
    signer_display_name text        NOT NULL,
    meaning             text        NOT NULL CHECK (length(meaning) BETWEEN 1 AND 200),
    signed_at           timestamptz NOT NULL,
    auth_time           timestamptz NOT NULL,
    spec_number         text        NOT NULL,
    requirement_key     text        NOT NULL,
    spec_revision       text        NOT NULL,
    marking_banner      text        NOT NULL,
    content_hash        bytea       NOT NULL CHECK (octet_length(content_hash) = 32),
    signature_hash      bytea       NOT NULL CHECK (octet_length(signature_hash) = 32)
);

-- What a release review signed: the iteration content hash, bound when the review starts.
CREATE TABLE req.review_binding (
    id            uuid        PRIMARY KEY,
    revision_id   uuid        NOT NULL REFERENCES req.spec_revision (id),
    iteration_id  uuid        NOT NULL REFERENCES req.spec_iteration (id),
    review_sha256 text        NOT NULL CHECK (review_sha256 ~ '^[0-9a-f]{64}$'),
    bound_at      timestamptz NOT NULL,
    bound_by      text        NOT NULL
);
CREATE INDEX review_binding_revision_idx ON req.review_binding (revision_id, bound_at DESC);

-- ================================================================ guards (defence in depth; the service enforces the same rules with messages)
CREATE FUNCTION req.reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'requirement records are immutable (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER iteration_immutable BEFORE UPDATE OR DELETE ON req.spec_iteration FOR EACH ROW EXECUTE FUNCTION req.reject_change();
CREATE TRIGGER requirement_version_immutable BEFORE UPDATE OR DELETE ON req.requirement_version FOR EACH ROW EXECUTE FUNCTION req.reject_change();
CREATE TRIGGER requirement_immutable BEFORE UPDATE OR DELETE ON req.requirement FOR EACH ROW EXECUTE FUNCTION req.reject_change();
CREATE TRIGGER verification_immutable BEFORE UPDATE OR DELETE ON req.verification FOR EACH ROW EXECUTE FUNCTION req.reject_change();
CREATE TRIGGER evidence_immutable BEFORE UPDATE OR DELETE ON req.verification_evidence FOR EACH ROW EXECUTE FUNCTION req.reject_change();
CREATE TRIGGER signature_immutable BEFORE UPDATE OR DELETE ON req.verification_signature FOR EACH ROW EXECUTE FUNCTION req.reject_change();
CREATE TRIGGER binding_immutable BEFORE UPDATE OR DELETE ON req.review_binding FOR EACH ROW EXECUTE FUNCTION req.reject_change();
CREATE TRIGGER verification_no_truncate BEFORE TRUNCATE ON req.verification FOR EACH STATEMENT EXECUTE FUNCTION req.reject_change();
CREATE TRIGGER signature_no_truncate BEFORE TRUNCATE ON req.verification_signature FOR EACH STATEMENT EXECUTE FUNCTION req.reject_change();
CREATE TRIGGER revision_no_delete BEFORE DELETE ON req.spec_revision FOR EACH ROW EXECUTE FUNCTION req.reject_change();
CREATE TRIGGER spec_no_delete BEFORE DELETE ON req.spec FOR EACH ROW EXECUTE FUNCTION req.reject_change();
CREATE TRIGGER link_no_delete BEFORE DELETE ON req.link FOR EACH ROW EXECUTE FUNCTION req.reject_change();

-- A link's identity never changes; only its review state and removal do.
CREATE FUNCTION req.guard_link() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id <> OLD.id OR NEW.link_type <> OLD.link_type OR NEW.source_requirement_id <> OLD.source_requirement_id OR NEW.target_kind <> OLD.target_kind
       OR NEW.target_requirement_id IS DISTINCT FROM OLD.target_requirement_id OR NEW.target_number IS DISTINCT FROM OLD.target_number
       OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
        RAISE EXCEPTION 'a traceability link keeps its ends and type' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.removed_at IS NOT NULL THEN
        RAISE EXCEPTION 'a removed link cannot change' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER link_guard BEFORE UPDATE ON req.link FOR EACH ROW EXECUTE FUNCTION req.guard_link();

-- The system link types keep their code, target kind and role.
CREATE FUNCTION req.guard_link_type() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'link types are retired, never deleted' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.is_system AND (NEW.code <> OLD.code OR NEW.target_kind <> OLD.target_kind OR NEW.coverage_role <> OLD.coverage_role OR NOT NEW.is_active OR NOT NEW.is_system) THEN
        RAISE EXCEPTION 'system link types cannot be retired or redefined' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.code <> OLD.code OR NEW.target_kind <> OLD.target_kind THEN
        RAISE EXCEPTION 'a link type keeps its code and target kind' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER link_type_guard BEFORE UPDATE OR DELETE ON req.link_type FOR EACH ROW EXECUTE FUNCTION req.guard_link_type();

CREATE FUNCTION req.is_locked_state(p_spec uuid, p_state text) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT COALESCE((SELECT s.is_locked FROM req.spec m JOIN plm.lifecycle_state s ON s.lifecycle_id = m.lifecycle_id AND s.state = p_state WHERE m.id = p_spec), true);
$$;

CREATE FUNCTION req.guard_spec() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW.id <> OLD.id OR NEW.number <> OLD.number OR NEW.lifecycle_id <> OLD.lifecycle_id OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by) THEN
        RAISE EXCEPTION 'specification identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER spec_guard BEFORE UPDATE ON req.spec FOR EACH ROW EXECUTE FUNCTION req.guard_spec();

-- A locked revision keeps its content pointers; a release needs a review bound to its latest iteration.
CREATE FUNCTION req.guard_revision_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF req.is_locked_state(OLD.spec_id, OLD.lifecycle_state)
       AND (NEW.latest_iteration <> OLD.latest_iteration OR NEW.revision <> OLD.revision OR NEW.spec_id <> OLD.spec_id) THEN
        RAISE EXCEPTION 'specification revision % is locked in state %', OLD.revision, OLD.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.lifecycle_state <> OLD.lifecycle_state THEN
        IF NOT EXISTS (SELECT 1 FROM req.spec m JOIN plm.lifecycle_transition t ON t.lifecycle_id = m.lifecycle_id
                       WHERE m.id = OLD.spec_id AND t.from_state = OLD.lifecycle_state AND t.to_state = NEW.lifecycle_state) THEN
            RAISE EXCEPTION 'the specification lifecycle has no transition from % to %', OLD.lifecycle_state, NEW.lifecycle_state USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.lifecycle_state = 'Released' AND NOT EXISTS (
            SELECT 1 FROM req.review_binding b JOIN req.spec_iteration i ON i.id = b.iteration_id
            WHERE b.revision_id = NEW.id AND i.iteration = NEW.latest_iteration) THEN
            RAISE EXCEPTION 'a specification is released only after a review was bound to its latest iteration' USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER revision_guard BEFORE UPDATE ON req.spec_revision FOR EACH ROW EXECUTE FUNCTION req.guard_revision_update();

CREATE FUNCTION req.guard_locked_revision() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_spec  uuid;
    v_state text;
BEGIN
    SELECT spec_id, lifecycle_state INTO v_spec, v_state FROM req.spec_revision WHERE id = NEW.revision_id;
    IF req.is_locked_state(v_spec, v_state) THEN
        RAISE EXCEPTION 'specification revision is in locked state %', v_state USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER iteration_locked BEFORE INSERT ON req.spec_iteration FOR EACH ROW EXECUTE FUNCTION req.guard_locked_revision();

CREATE FUNCTION req.guard_version_insert() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_spec  uuid;
    v_state text;
    v_req_spec uuid;
BEGIN
    SELECT r.spec_id, r.lifecycle_state INTO v_spec, v_state FROM req.spec_iteration i JOIN req.spec_revision r ON r.id = i.revision_id WHERE i.id = NEW.iteration_id;
    IF req.is_locked_state(v_spec, v_state) THEN
        RAISE EXCEPTION 'specification revision is in locked state %: its requirements cannot change', v_state USING ERRCODE = 'check_violation';
    END IF;
    SELECT spec_id INTO v_req_spec FROM req.requirement WHERE id = NEW.requirement_id;
    IF v_req_spec IS DISTINCT FROM v_spec THEN
        RAISE EXCEPTION 'a requirement belongs to one specification' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.parent_requirement_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM req.requirement WHERE id = NEW.parent_requirement_id AND spec_id = v_spec) THEN
        RAISE EXCEPTION 'the parent requirement belongs to another specification' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER version_insert_guard BEFORE INSERT ON req.requirement_version FOR EACH ROW EXECUTE FUNCTION req.guard_version_insert();

-- A formal verification exists only with its e-signature, and an informal one never has one.
CREATE FUNCTION req.check_formal_signature() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.formal AND NOT EXISTS (SELECT 1 FROM req.verification_signature WHERE verification_id = NEW.id) THEN
        RAISE EXCEPTION 'a formal verification needs an e-signature' USING ERRCODE = 'check_violation';
    END IF;
    IF NOT NEW.formal AND EXISTS (SELECT 1 FROM req.verification_signature WHERE verification_id = NEW.id) THEN
        RAISE EXCEPTION 'an informal verification has no e-signature' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER verification_formal_check AFTER INSERT ON req.verification
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION req.check_formal_signature();

-- ================================================================ awareness tables accept the new object type
-- Same approach as migration 0020: the object type lists of the workspace and notification tables are found by their definition and replaced by a pattern
-- (already done when sourcing was applied; then nothing matches and this is a no-op).
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
END;
$$;

-- ================================================================ privileges (least privilege)
GRANT SELECT, INSERT, UPDATE ON req.link_type, req.spec, req.spec_revision, req.link TO keel_app;
GRANT SELECT, INSERT ON req.spec_iteration, req.requirement, req.requirement_version, req.verification, req.verification_evidence, req.verification_signature, req.review_binding TO keel_app;
