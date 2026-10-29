-- Increment 8.6: records management (ADR-0041, docs/increments/8.6-records.md). Forward-only, runs as keel_migrator.
--   * retention schedules as versioned data (record classes with match criteria, retention period, trigger event, disposition, citation), published through the workflow engine;
--   * program events (program close, contract end) that retention periods can run from;
--   * a derived classification of objects (a cache: every destructive step re-evaluates it);
--   * legal holds with materialized membership; a hold overrides disposition in the service, in a database trigger and in the vault garbage collector's view of references;
--   * disposition requests (approved through the workflow engine), tombstones and signed certificates of destruction with later destruction confirmations;
--   * WORM-style archive packages (a signed manifest over a ZIP stored content-addressed in the vault).
-- Nothing here weakens an existing delete guard: tombstoned objects stay as rows, and additional triggers FREEZE them (no new iterations, check-outs or state changes).
CREATE SCHEMA rec;
GRANT USAGE ON SCHEMA rec TO keel_app;

CREATE SEQUENCE rec.hold_seq START 1;
CREATE SEQUENCE rec.disposition_seq START 1;
CREATE SEQUENCE rec.certificate_seq START 1;
CREATE SEQUENCE rec.archive_seq START 1;
GRANT USAGE ON SEQUENCE rec.hold_seq, rec.disposition_seq, rec.certificate_seq, rec.archive_seq TO keel_app;

CREATE FUNCTION rec.reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows are immutable (% refused)', TG_TABLE_NAME, TG_OP USING ERRCODE = 'insufficient_privilege';
END;
$$;

-- ================================================================ retention schedules (versioned data)
CREATE TABLE rec.schedule (
    id           uuid        PRIMARY KEY,
    version      int         NOT NULL UNIQUE CHECK (version >= 1),
    status       text        NOT NULL CHECK (status IN ('draft', 'in_review', 'published', 'superseded', 'discarded')),
    title        text        NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    notes        text        CHECK (notes IS NULL OR length(notes) <= 4000),
    owning_org   text        NOT NULL,
    revision     int         NOT NULL DEFAULT 1,
    created_at   timestamptz NOT NULL,
    created_by   text        NOT NULL,
    updated_at   timestamptz,
    published_at timestamptz,
    published_by text
);
CREATE UNIQUE INDEX schedule_one_published ON rec.schedule ((true)) WHERE status = 'published';
CREATE UNIQUE INDEX schedule_one_open_draft ON rec.schedule ((true)) WHERE status IN ('draft', 'in_review');

CREATE FUNCTION rec.guard_schedule() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'schedules are never deleted' USING ERRCODE = 'insufficient_privilege'; END IF;
    IF OLD.status = NEW.status THEN
        IF OLD.status <> 'draft' AND (NEW.title, NEW.notes) IS DISTINCT FROM (OLD.title, OLD.notes) THEN
            RAISE EXCEPTION 'only a draft schedule can be edited' USING ERRCODE = 'insufficient_privilege';
        END IF;
        RETURN NEW;
    END IF;
    IF (OLD.status, NEW.status) NOT IN (('draft', 'in_review'), ('in_review', 'draft'), ('in_review', 'published'), ('published', 'superseded'), ('draft', 'discarded')) THEN
        RAISE EXCEPTION 'schedule status cannot change from % to %', OLD.status, NEW.status USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF (NEW.title, NEW.notes, NEW.version, NEW.created_at, NEW.created_by) IS DISTINCT FROM (OLD.title, OLD.notes, OLD.version, OLD.created_at, OLD.created_by) THEN
        RAISE EXCEPTION 'a status change cannot also edit the schedule' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER schedule_guard BEFORE UPDATE OR DELETE ON rec.schedule FOR EACH ROW EXECUTE FUNCTION rec.guard_schedule();
CREATE TRIGGER schedule_no_truncate BEFORE TRUNCATE ON rec.schedule FOR EACH STATEMENT EXECUTE FUNCTION rec.reject_change();

CREATE TABLE rec.record_class (
    id                  uuid    PRIMARY KEY,
    schedule_id         uuid    NOT NULL REFERENCES rec.schedule (id),
    code                text    NOT NULL CHECK (code ~ '^[A-Z0-9][A-Z0-9._-]{1,29}$'),
    name                text    NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    description         text    CHECK (description IS NULL OR length(description) <= 2000),
    priority            int     NOT NULL DEFAULT 100 CHECK (priority BETWEEN 1 AND 10000),
    -- Match criteria. An empty array means "any". An object matches when every non-empty criterion holds.
    match_kinds         text[]  NOT NULL CHECK (match_kinds <@ ARRAY['document', 'part']),
    match_types         text[]  NOT NULL,
    match_programs      text[]  NOT NULL,
    match_jurisdictions text[]  NOT NULL CHECK (match_jurisdictions <@ ARRAY['None', 'Ear', 'Itar']),
    match_cui           text[]  NOT NULL,
    match_statements    text[]  NOT NULL CHECK (match_statements <@ ARRAY['A', 'B', 'C', 'D', 'E', 'F']),
    retention_months    int     NOT NULL CHECK (retention_months BETWEEN 0 AND 1200),
    trigger_event       text    NOT NULL CHECK (trigger_event IN ('release', 'obsolescence', 'program_close', 'contract_end')),
    disposition         text    NOT NULL CHECK (disposition IN ('destroy', 'transfer_archive', 'review')),
    citation            text    CHECK (citation IS NULL OR length(citation) <= 500),
    notes               text    CHECK (notes IS NULL OR length(notes) <= 2000),
    UNIQUE (schedule_id, code)
);
CREATE INDEX record_class_schedule_idx ON rec.record_class (schedule_id, priority);

CREATE FUNCTION rec.guard_class() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE s uuid; st text;
BEGIN
    s := CASE WHEN TG_OP = 'DELETE' THEN OLD.schedule_id ELSE NEW.schedule_id END;
    SELECT status INTO st FROM rec.schedule WHERE id = s;
    IF st IS DISTINCT FROM 'draft' THEN RAISE EXCEPTION 'record classes can change only while the schedule is a draft' USING ERRCODE = 'insufficient_privilege'; END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;
CREATE TRIGGER record_class_guard BEFORE INSERT OR UPDATE OR DELETE ON rec.record_class FOR EACH ROW EXECUTE FUNCTION rec.guard_class();
CREATE TRIGGER record_class_no_truncate BEFORE TRUNCATE ON rec.record_class FOR EACH STATEMENT EXECUTE FUNCTION rec.reject_change();

-- Events a retention period can run from (append-only; the latest row per program and event counts).
CREATE TABLE rec.program_event (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    program     text        NOT NULL CHECK (length(program) BETWEEN 1 AND 64),
    event       text        NOT NULL CHECK (event IN ('program_close', 'contract_end')),
    occurred_on date        NOT NULL,
    recorded_at timestamptz NOT NULL,
    recorded_by text        NOT NULL
);
CREATE INDEX program_event_idx ON rec.program_event (program, event, id DESC);
CREATE TRIGGER program_event_immutable BEFORE UPDATE OR DELETE ON rec.program_event FOR EACH ROW EXECUTE FUNCTION rec.reject_change();
CREATE TRIGGER program_event_no_truncate BEFORE TRUNCATE ON rec.program_event FOR EACH STATEMENT EXECUTE FUNCTION rec.reject_change();
CREATE VIEW rec.program_event_current AS
    SELECT DISTINCT ON (program, event) program, event, occurred_on, recorded_at, recorded_by FROM rec.program_event ORDER BY program, event, id DESC;

-- Derived: which class an object falls in, from which date it may be disposed of. A cache; destructive steps re-evaluate (never trusted).
CREATE TABLE rec.classification (
    object_kind  text        NOT NULL CHECK (object_kind IN ('document', 'part')),
    object_id    uuid        NOT NULL,
    schedule_id  uuid        NOT NULL REFERENCES rec.schedule (id),
    class_code   text        NOT NULL,
    trigger_event text       NOT NULL,
    trigger_date date        NOT NULL,
    eligible_on  date        NOT NULL,
    disposition  text        NOT NULL,
    evaluated_at timestamptz NOT NULL,
    PRIMARY KEY (object_kind, object_id)
);
CREATE INDEX classification_eligible_idx ON rec.classification (eligible_on);

-- A "review" disposition that decided to keep an object longer.
CREATE TABLE rec.review_deferral (
    id          uuid        PRIMARY KEY,
    object_kind text        NOT NULL CHECK (object_kind IN ('document', 'part')),
    object_id   uuid        NOT NULL,
    retain_until date       NOT NULL,
    decided_at  timestamptz NOT NULL,
    decided_by  text        NOT NULL
);
CREATE INDEX review_deferral_idx ON rec.review_deferral (object_kind, object_id, retain_until DESC);
CREATE TRIGGER review_deferral_immutable BEFORE UPDATE OR DELETE ON rec.review_deferral FOR EACH ROW EXECUTE FUNCTION rec.reject_change();

-- ================================================================ legal holds
-- A hold record carries no markings: it names no object and its text is entered by records staff (the UI warns against marked content). Which objects it
-- covers is shown to a reader only through the read filter of each object, and never counted.
CREATE TABLE rec.hold (
    id           uuid        PRIMARY KEY,
    number       text        NOT NULL UNIQUE,
    name         text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    reason       text        NOT NULL CHECK (length(reason) BETWEEN 1 AND 2000),
    scope_kind   text        NOT NULL CHECK (scope_kind IN ('objects', 'program', 'query')),
    scope        jsonb       NOT NULL,
    owning_org   text        NOT NULL,
    in_release   boolean     NOT NULL DEFAULT false,
    revision     int         NOT NULL DEFAULT 1,
    placed_at    timestamptz NOT NULL,
    placed_by    text        NOT NULL,
    released_at  timestamptz,
    released_by  text,
    release_note text        CHECK (release_note IS NULL OR length(release_note) <= 1000),
    CONSTRAINT hold_release_shape CHECK ((released_at IS NULL) = (released_by IS NULL))
);
CREATE INDEX hold_active_idx ON rec.hold (placed_at) WHERE released_at IS NULL;

CREATE FUNCTION rec.guard_hold() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'holds are never deleted' USING ERRCODE = 'insufficient_privilege'; END IF;
    IF OLD.released_at IS NOT NULL THEN RAISE EXCEPTION 'a released hold is final' USING ERRCODE = 'insufficient_privilege'; END IF;
    IF (NEW.number, NEW.name, NEW.reason, NEW.scope_kind, NEW.scope, NEW.placed_at, NEW.placed_by) IS DISTINCT FROM (OLD.number, OLD.name, OLD.reason, OLD.scope_kind, OLD.scope, OLD.placed_at, OLD.placed_by) THEN
        RAISE EXCEPTION 'a hold''s identity and scope cannot change' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER hold_guard BEFORE UPDATE OR DELETE ON rec.hold FOR EACH ROW EXECUTE FUNCTION rec.guard_hold();
CREATE TRIGGER hold_no_truncate BEFORE TRUNCATE ON rec.hold FOR EACH STATEMENT EXECUTE FUNCTION rec.reject_change();

CREATE TABLE rec.hold_member (
    hold_id     uuid        NOT NULL REFERENCES rec.hold (id),
    object_kind text        NOT NULL CHECK (object_kind IN ('document', 'part')),
    object_id   uuid        NOT NULL,
    added_at    timestamptz NOT NULL,
    added_by    text        NOT NULL CHECK (added_by IN ('placement', 'reevaluation')),
    PRIMARY KEY (hold_id, object_kind, object_id)
);
CREATE INDEX hold_member_object_idx ON rec.hold_member (object_kind, object_id);
CREATE TRIGGER hold_member_immutable BEFORE UPDATE OR DELETE ON rec.hold_member FOR EACH ROW EXECUTE FUNCTION rec.reject_change();
CREATE TRIGGER hold_member_no_truncate BEFORE TRUNCATE ON rec.hold_member FOR EACH STATEMENT EXECUTE FUNCTION rec.reject_change();

CREATE FUNCTION rec.is_held(p_kind text, p_id uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT EXISTS (SELECT 1 FROM rec.hold_member m JOIN rec.hold h ON h.id = m.hold_id WHERE m.object_kind = p_kind AND m.object_id = p_id AND h.released_at IS NULL);
$$;

-- ================================================================ disposition requests
CREATE TABLE rec.disposition (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE,
    status                 text        NOT NULL CHECK (status IN ('draft', 'in_review', 'approved', 'rejected', 'executed', 'cancelled')),
    note                   text        CHECK (note IS NULL OR length(note) <= 1000),
    revision               int         NOT NULL DEFAULT 1,
    created_at             timestamptz NOT NULL,
    created_by             text        NOT NULL,
    updated_at             timestamptz,
    executed_at            timestamptz,
    executed_by            text,
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL
);
CREATE INDEX disposition_status_idx ON rec.disposition (status, created_at);

CREATE TABLE rec.disposition_item (
    id               uuid        PRIMARY KEY,
    disposition_id   uuid        NOT NULL REFERENCES rec.disposition (id),
    object_kind      text        NOT NULL CHECK (object_kind IN ('document', 'part')),
    object_id        uuid        NOT NULL,
    object_number    text        NOT NULL,
    class_code       text        NOT NULL,
    schedule_version int         NOT NULL,
    action           text        NOT NULL CHECK (action IN ('destroy', 'transfer_archive', 'review')),
    eligible_on      date        NOT NULL,
    -- Filled when the request is executed.
    result           text        CHECK (result IN ('destroyed', 'transferred', 'retained', 'skipped')),
    result_code      text,
    UNIQUE (disposition_id, object_kind, object_id)
);
CREATE INDEX disposition_item_object_idx ON rec.disposition_item (object_kind, object_id);

CREATE FUNCTION rec.guard_disposition() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'disposition requests are never deleted' USING ERRCODE = 'insufficient_privilege'; END IF;
    IF OLD.status IN ('executed', 'rejected', 'cancelled') THEN RAISE EXCEPTION 'a % disposition request is final', OLD.status USING ERRCODE = 'insufficient_privilege'; END IF;
    IF OLD.status <> NEW.status AND (OLD.status, NEW.status) NOT IN
       (('draft', 'in_review'), ('in_review', 'draft'), ('in_review', 'approved'), ('in_review', 'rejected'), ('draft', 'cancelled'), ('approved', 'executed'), ('approved', 'cancelled')) THEN
        RAISE EXCEPTION 'disposition status cannot change from % to %', OLD.status, NEW.status USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF (NEW.number, NEW.created_at, NEW.created_by, NEW.export_jurisdiction, NEW.cui_categories, NEW.program, NEW.owning_org) IS DISTINCT FROM
       (OLD.number, OLD.created_at, OLD.created_by, OLD.export_jurisdiction, OLD.cui_categories, OLD.program, OLD.owning_org) THEN
        RAISE EXCEPTION 'a request''s identity and markings cannot change' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER disposition_guard BEFORE UPDATE OR DELETE ON rec.disposition FOR EACH ROW EXECUTE FUNCTION rec.guard_disposition();

CREATE FUNCTION rec.guard_disposition_item() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE st text;
BEGIN
    SELECT status INTO st FROM rec.disposition WHERE id = CASE WHEN TG_OP = 'DELETE' THEN OLD.disposition_id ELSE NEW.disposition_id END;
    IF TG_OP = 'UPDATE' THEN
        IF st <> 'approved' OR OLD.result IS NOT NULL OR (NEW.disposition_id, NEW.object_kind, NEW.object_id, NEW.class_code, NEW.action, NEW.schedule_version, NEW.eligible_on) IS DISTINCT FROM
           (OLD.disposition_id, OLD.object_kind, OLD.object_id, OLD.class_code, OLD.action, OLD.schedule_version, OLD.eligible_on) THEN
            RAISE EXCEPTION 'an item can only receive its result, once, while the request is approved' USING ERRCODE = 'insufficient_privilege';
        END IF;
        RETURN NEW;
    END IF;
    IF st <> 'draft' THEN RAISE EXCEPTION 'items can change only while the request is a draft' USING ERRCODE = 'insufficient_privilege'; END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;
CREATE TRIGGER disposition_item_guard BEFORE INSERT OR UPDATE OR DELETE ON rec.disposition_item FOR EACH ROW EXECUTE FUNCTION rec.guard_disposition_item();

-- ================================================================ certificates of destruction and tombstones
CREATE TABLE rec.certificate (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE,
    disposition_id         uuid        NOT NULL UNIQUE REFERENCES rec.disposition (id),
    -- The exact text that was hashed and signed (canonical JSON). The hash and signature are over these bytes.
    body                   text        NOT NULL,
    body_sha256            bytea       NOT NULL CHECK (octet_length(body_sha256) = 32),
    key_id                 text        NOT NULL,
    signature              bytea       NOT NULL,
    issued_at              timestamptz NOT NULL,
    issued_by              text        NOT NULL,
    object_count           int         NOT NULL CHECK (object_count >= 1),
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL
);
CREATE TRIGGER certificate_immutable BEFORE UPDATE OR DELETE ON rec.certificate FOR EACH ROW EXECUTE FUNCTION rec.reject_change();
CREATE TRIGGER certificate_no_truncate BEFORE TRUNCATE ON rec.certificate FOR EACH STATEMENT EXECUTE FUNCTION rec.reject_change();

CREATE TABLE rec.tombstone (
    id             uuid        PRIMARY KEY,
    object_kind    text        NOT NULL CHECK (object_kind IN ('document', 'part')),
    object_id      uuid        NOT NULL,
    object_number  text        NOT NULL,
    disposition_id uuid        NOT NULL REFERENCES rec.disposition (id),
    certificate_id uuid        NOT NULL REFERENCES rec.certificate (id),
    action         text        NOT NULL CHECK (action IN ('destroy', 'transfer_archive')),
    tombstoned_at  timestamptz NOT NULL,
    tombstoned_by  text        NOT NULL,
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
    UNIQUE (object_kind, object_id)
);
CREATE INDEX tombstone_certificate_idx ON rec.tombstone (certificate_id);

-- The only way to create a tombstone: an approved request that lists the object, the object is not held, and the object exists. A hold placed at any time
-- before this statement commits is seen here (holds and tombstones serialize on the advisory lock the service takes; the trigger is the backstop).
CREATE FUNCTION rec.guard_tombstone() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'tombstones are immutable' USING ERRCODE = 'insufficient_privilege'; END IF;
    IF NOT EXISTS (SELECT 1 FROM rec.disposition d JOIN rec.disposition_item i ON i.disposition_id = d.id
                   WHERE d.id = NEW.disposition_id AND d.status = 'approved' AND i.object_kind = NEW.object_kind AND i.object_id = NEW.object_id AND i.action = NEW.action) THEN
        RAISE EXCEPTION 'a tombstone needs an approved disposition request that lists the object' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF rec.is_held(NEW.object_kind, NEW.object_id) THEN
        RAISE EXCEPTION 'an object under legal hold cannot be tombstoned' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER tombstone_guard BEFORE INSERT OR UPDATE OR DELETE ON rec.tombstone FOR EACH ROW EXECUTE FUNCTION rec.guard_tombstone();
CREATE TRIGGER tombstone_no_truncate BEFORE TRUNCATE ON rec.tombstone FOR EACH STATEMENT EXECUTE FUNCTION rec.reject_change();

-- Evidence that content was really gone (the vault garbage collector deleted it) at a later time, signed like the certificate.
CREATE TABLE rec.destruction_confirmation (
    id             uuid        PRIMARY KEY,
    certificate_id uuid        NOT NULL REFERENCES rec.certificate (id),
    sha256         text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    vault_absent   boolean     NOT NULL,
    still_referenced boolean   NOT NULL,
    confirmed_at   timestamptz NOT NULL,
    confirmed_by   text        NOT NULL,
    key_id         text        NOT NULL,
    signature      bytea       NOT NULL
);
CREATE INDEX destruction_confirmation_idx ON rec.destruction_confirmation (certificate_id, sha256, confirmed_at DESC);
CREATE TRIGGER destruction_confirmation_immutable BEFORE UPDATE OR DELETE ON rec.destruction_confirmation FOR EACH ROW EXECUTE FUNCTION rec.reject_change();
CREATE TRIGGER destruction_confirmation_no_truncate BEFORE TRUNCATE ON rec.destruction_confirmation FOR EACH STATEMENT EXECUTE FUNCTION rec.reject_change();

-- Content of a document iteration whose document was tombstoned, and no hold covers it: the vault garbage collector no longer counts the file row as a
-- reference (VaultReferences registers this predicate). Content shared with another live row stays, because that row still references it.
CREATE FUNCTION rec.iteration_content_released(p_iteration uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT EXISTS (SELECT 1 FROM plm.document_iteration i
                   JOIN plm.document_revision rv ON rv.id = i.revision_id
                   JOIN rec.tombstone t ON t.object_kind = 'document' AND t.object_id = rv.master_id
                   WHERE i.id = p_iteration AND NOT rec.is_held('document', rv.master_id));
$$;

-- ---------------------------------------------------------------- tombstoned objects are frozen (additive guards; no existing trigger changes)
CREATE FUNCTION rec.is_tombstoned(p_kind text, p_id uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT EXISTS (SELECT 1 FROM rec.tombstone WHERE object_kind = p_kind AND object_id = p_id);
$$;
CREATE FUNCTION rec.guard_frozen_document_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF rec.is_tombstoned('document', NEW.master_id) THEN RAISE EXCEPTION 'the document was disposed of under a retention schedule and is frozen' USING ERRCODE = 'insufficient_privilege'; END IF;
    RETURN NEW;
END;
$$;
CREATE FUNCTION rec.guard_frozen_part_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF rec.is_tombstoned('part', NEW.master_id) THEN RAISE EXCEPTION 'the part was disposed of under a retention schedule and is frozen' USING ERRCODE = 'insufficient_privilege'; END IF;
    RETURN NEW;
END;
$$;
CREATE FUNCTION rec.guard_frozen_document_child() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM plm.document_revision rv WHERE rv.id = NEW.revision_id AND rec.is_tombstoned('document', rv.master_id)) THEN
        RAISE EXCEPTION 'the document was disposed of under a retention schedule and is frozen' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
CREATE FUNCTION rec.guard_frozen_part_child() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM plm.part_revision rv WHERE rv.id = NEW.revision_id AND rec.is_tombstoned('part', rv.master_id)) THEN
        RAISE EXCEPTION 'the part was disposed of under a retention schedule and is frozen' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER zz_rec_frozen BEFORE INSERT OR UPDATE ON plm.document_revision FOR EACH ROW EXECUTE FUNCTION rec.guard_frozen_document_revision();
CREATE TRIGGER zz_rec_frozen BEFORE INSERT OR UPDATE ON plm.part_revision FOR EACH ROW EXECUTE FUNCTION rec.guard_frozen_part_revision();
CREATE TRIGGER zz_rec_frozen BEFORE INSERT ON plm.document_iteration FOR EACH ROW EXECUTE FUNCTION rec.guard_frozen_document_child();
CREATE TRIGGER zz_rec_frozen BEFORE INSERT ON plm.part_iteration FOR EACH ROW EXECUTE FUNCTION rec.guard_frozen_part_child();
CREATE TRIGGER zz_rec_frozen BEFORE INSERT ON plm.document_checkout FOR EACH ROW EXECUTE FUNCTION rec.guard_frozen_document_child();
CREATE TRIGGER zz_rec_frozen BEFORE INSERT ON plm.part_checkout FOR EACH ROW EXECUTE FUNCTION rec.guard_frozen_part_child();

-- ================================================================ WORM-style archive packages
CREATE TABLE rec.archive (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE,
    title                  text        NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    -- The package: a ZIP stored in the vault under its SHA-256 (the key). The manifest inside is signed; its hash and signature are repeated here.
    package_sha256         text        NOT NULL CHECK (package_sha256 ~ '^[0-9a-f]{64}$'),
    package_size           bigint      NOT NULL CHECK (package_size > 0),
    manifest_sha256        text        NOT NULL CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
    key_id                 text        NOT NULL,
    signature              bytea       NOT NULL,
    object_count           int         NOT NULL CHECK (object_count >= 1),
    file_count             int         NOT NULL CHECK (file_count >= 0),
    -- What protects the package from change: 'application' (database triggers, no delete path, garbage collector registration) or the S3 Object Lock mode applied.
    worm_mode              text        NOT NULL CHECK (worm_mode IN ('application', 's3_governance', 's3_compliance')),
    retain_until           timestamptz,
    created_at             timestamptz NOT NULL,
    created_by             text        NOT NULL,
    disposition_id         uuid        REFERENCES rec.disposition (id),
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL
);
CREATE INDEX archive_package_idx ON rec.archive (package_sha256);
CREATE TRIGGER archive_immutable BEFORE UPDATE OR DELETE ON rec.archive FOR EACH ROW EXECUTE FUNCTION rec.reject_change();
CREATE TRIGGER archive_no_truncate BEFORE TRUNCATE ON rec.archive FOR EACH STATEMENT EXECUTE FUNCTION rec.reject_change();

CREATE TABLE rec.archive_object (
    archive_id    uuid NOT NULL REFERENCES rec.archive (id),
    object_kind   text NOT NULL CHECK (object_kind IN ('document', 'part')),
    object_id     uuid NOT NULL,
    object_number text NOT NULL,
    revisions     int  NOT NULL,
    PRIMARY KEY (archive_id, object_kind, object_id)
);
CREATE INDEX archive_object_idx ON rec.archive_object (object_kind, object_id);
CREATE TRIGGER archive_object_immutable BEFORE UPDATE OR DELETE ON rec.archive_object FOR EACH ROW EXECUTE FUNCTION rec.reject_change();
CREATE TRIGGER archive_object_no_truncate BEFORE TRUNCATE ON rec.archive_object FOR EACH STATEMENT EXECUTE FUNCTION rec.reject_change();

-- ================================================================ workflow templates (ordinary data, replaceable by administrators)
INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('retention-schedule-publication', 1, 'Retention schedule publication',
        'One reviewer, then parallel approval where every chosen approver must sign. The signatures cover the schedule and every record class it contains.',
        'retention_schedule', $tpl$
{
  "id": "retention-schedule-publication",
  "name": "Retention schedule publication",
  "description": "One reviewer, then parallel approval where every chosen approver must sign. The signatures cover the schedule and every record class it contains.",
  "objectType": "retention_schedule",
  "slots": [
    { "name": "reviewer",  "label": "Records reviewer", "role": "plm-viewer",   "min": 1, "max": 1 },
    { "name": "approvers", "label": "Approvers",        "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "Schedule publication", "steps": ["review", "approval", "release"] },
    { "id": "review",   "type": "Task",     "name": "Review", "kind": "review", "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 5 },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" }, "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "release",  "type": "End",      "name": "Publish", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('legal-hold-release', 1, 'Legal hold release',
        'An approver signs to release the hold. The signature covers the hold, its scope and a digest of its membership.',
        'legal_hold', $tpl$
{
  "id": "legal-hold-release",
  "name": "Legal hold release",
  "description": "An approver signs to release the hold. The signature covers the hold, its scope and a digest of its membership.",
  "objectType": "legal_hold",
  "slots": [
    { "name": "approvers", "label": "Approvers", "role": "plm-approver", "min": 1, "max": 5 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "Hold release", "steps": ["approval", "release"] },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" }, "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "release",  "type": "End",      "name": "Release the hold", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('disposition-approval', 1, 'Disposition approval',
        'One reviewer, then parallel approval where every chosen approver must sign. The signatures cover the list of objects, their classes and the schedule version.',
        'disposition', $tpl$
{
  "id": "disposition-approval",
  "name": "Disposition approval",
  "description": "One reviewer, then parallel approval where every chosen approver must sign. The signatures cover the list of objects, their classes and the schedule version.",
  "objectType": "disposition",
  "slots": [
    { "name": "reviewer",  "label": "Records reviewer", "role": "plm-viewer",   "min": 1, "max": 1 },
    { "name": "approvers", "label": "Approvers",        "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "Disposition approval", "steps": ["review", "approval", "release"] },
    { "id": "review",   "type": "Task",     "name": "Review", "kind": "review", "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 5 },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" }, "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "release",  "type": "End",      "name": "Approve", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

-- ================================================================ privileges (least privilege: no table is deletable, records stay)
GRANT SELECT, INSERT, UPDATE ON rec.schedule, rec.hold, rec.disposition TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON rec.record_class TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON rec.disposition_item TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON rec.classification TO keel_app;
GRANT SELECT, INSERT ON rec.program_event, rec.review_deferral, rec.hold_member, rec.certificate, rec.tombstone, rec.destruction_confirmation, rec.archive, rec.archive_object TO keel_app;
GRANT SELECT ON rec.program_event_current TO keel_app;
GRANT EXECUTE ON FUNCTION rec.is_held(text, uuid), rec.is_tombstoned(text, uuid), rec.iteration_content_released(uuid) TO keel_app;
