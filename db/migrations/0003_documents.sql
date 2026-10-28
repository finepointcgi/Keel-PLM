-- Increment 1.3: documents with master / revision / iteration (ADR-0002) and vault files (ADR-0015).
-- File bytes live in the S3 vault under their SHA-256; these tables hold the metadata that names them.

-- ---------------------------------------------------------------- lifecycle (same states and transitions as parts)
INSERT INTO plm.lifecycle VALUES ('document-default', 'Standard document lifecycle');
INSERT INTO plm.lifecycle_state VALUES
    ('document-default', 'InWork',      'In Work',      10, true,  false),
    ('document-default', 'UnderReview', 'Under Review', 20, false, true),
    ('document-default', 'Released',    'Released',     30, false, true),
    ('document-default', 'Obsolete',    'Obsolete',     40, false, true);
INSERT INTO plm.lifecycle_transition VALUES
    ('document-default', 'InWork',      'UnderReview', 'manual'),
    ('document-default', 'UnderReview', 'InWork',      'manual'),
    ('document-default', 'UnderReview', 'Released',    'workflow'),
    ('document-default', 'Released',    'Obsolete',    'change');

-- ---------------------------------------------------------------- documents
CREATE SEQUENCE plm.document_number_seq START 1;

CREATE TABLE plm.document_master (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE,
    name                   text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    document_type          text        NOT NULL CHECK (document_type IN ('Specification', 'Drawing', 'CAD', 'Report', 'Procedure', 'Other')),
    lifecycle_id           text        NOT NULL REFERENCES plm.lifecycle (id),
    -- Marking columns: the contract used by AccessPolicy.BuildReadFilter (ADR-0004).
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
    search                 tsvector    GENERATED ALWAYS AS (
        setweight(to_tsvector('simple', number), 'A') || setweight(to_tsvector('english', name), 'B')) STORED
);
CREATE INDEX document_master_search_idx ON plm.document_master USING gin (search);
CREATE INDEX document_master_number_trgm ON plm.document_master USING gin (number gin_trgm_ops);
CREATE INDEX document_master_name_trgm ON plm.document_master USING gin (name gin_trgm_ops);
CREATE INDEX document_master_program_idx ON plm.document_master (program);

CREATE TABLE plm.document_revision (
    id               uuid        PRIMARY KEY,
    master_id        uuid        NOT NULL REFERENCES plm.document_master (id),
    revision         text        NOT NULL,
    lifecycle_state  text        NOT NULL,
    is_latest        boolean     NOT NULL,
    latest_iteration int         NOT NULL CHECK (latest_iteration >= 1),
    created_at       timestamptz NOT NULL,
    created_by       text        NOT NULL,
    state_changed_at timestamptz NOT NULL,
    state_changed_by text        NOT NULL,
    UNIQUE (master_id, revision),
    -- Lets links pin (revision, master) pairs with a composite foreign key (0004).
    UNIQUE (id, master_id)
);
CREATE UNIQUE INDEX document_revision_one_latest ON plm.document_revision (master_id) WHERE is_latest;
CREATE INDEX document_revision_state_idx ON plm.document_revision (lifecycle_state) WHERE is_latest;

-- Iterations are immutable once written (no UPDATE/DELETE, enforced below).
CREATE TABLE plm.document_iteration (
    id           uuid        PRIMARY KEY,
    revision_id  uuid        NOT NULL REFERENCES plm.document_revision (id),
    iteration    int         NOT NULL CHECK (iteration >= 1),
    description  text        NOT NULL DEFAULT '',
    comment      text,
    created_at   timestamptz NOT NULL,
    created_by   text        NOT NULL,
    UNIQUE (revision_id, iteration)
);

-- Files of an iteration: one optional primary plus attachments. The bytes are the vault object named by sha256.
CREATE TABLE plm.document_file (
    id            uuid        PRIMARY KEY,
    iteration_id  uuid        NOT NULL REFERENCES plm.document_iteration (id),
    role          text        NOT NULL CHECK (role IN ('primary', 'attachment')),
    file_name     text        NOT NULL CHECK (length(file_name) BETWEEN 1 AND 255),
    media_type    text        NOT NULL CHECK (length(media_type) BETWEEN 3 AND 255),
    size_bytes    bigint      NOT NULL CHECK (size_bytes >= 0),
    sha256        text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    added_at      timestamptz NOT NULL,
    added_by      text        NOT NULL
);
CREATE UNIQUE INDEX document_file_one_primary ON plm.document_file (iteration_id) WHERE role = 'primary';
CREATE UNIQUE INDEX document_file_unique_name ON plm.document_file (iteration_id, lower(file_name));
CREATE INDEX document_file_sha_idx ON plm.document_file (sha256);

-- At most one check-out per revision (primary key). Holds the private working copy until check-in.
CREATE TABLE plm.document_checkout (
    revision_id    uuid        PRIMARY KEY REFERENCES plm.document_revision (id),
    user_id        text        NOT NULL,
    user_name      text,
    checked_out_at timestamptz NOT NULL,
    description    text        NOT NULL
);

-- The working copy's file list. Check-in copies these rows into document_file of the new iteration;
-- the bytes are content-addressed, so nothing is copied in the vault.
CREATE TABLE plm.document_checkout_file (
    id            uuid        PRIMARY KEY,
    revision_id   uuid        NOT NULL REFERENCES plm.document_checkout (revision_id) ON DELETE CASCADE,
    role          text        NOT NULL CHECK (role IN ('primary', 'attachment')),
    file_name     text        NOT NULL CHECK (length(file_name) BETWEEN 1 AND 255),
    media_type    text        NOT NULL CHECK (length(media_type) BETWEEN 3 AND 255),
    size_bytes    bigint      NOT NULL CHECK (size_bytes >= 0),
    sha256        text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    added_at      timestamptz NOT NULL,
    added_by      text        NOT NULL
);
CREATE UNIQUE INDEX document_checkout_file_one_primary ON plm.document_checkout_file (revision_id) WHERE role = 'primary';
CREATE UNIQUE INDEX document_checkout_file_unique_name ON plm.document_checkout_file (revision_id, lower(file_name));

-- ---------------------------------------------------------------- release locking (defence in depth, ADR-0002)
CREATE FUNCTION plm.document_revision_locked(p_revision uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT s.is_locked FROM plm.document_revision r
    JOIN plm.document_master m ON m.id = r.master_id
    JOIN plm.lifecycle_state s ON s.lifecycle_id = m.lifecycle_id AND s.state = r.lifecycle_state
    WHERE r.id = p_revision;
$$;

-- Generic immutability guard (the 0002 function's message names parts).
CREATE FUNCTION plm.reject_immutable_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows are immutable', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER document_iteration_immutable BEFORE UPDATE OR DELETE ON plm.document_iteration
    FOR EACH ROW EXECUTE FUNCTION plm.reject_immutable_change();
CREATE TRIGGER document_file_immutable BEFORE UPDATE OR DELETE ON plm.document_file
    FOR EACH ROW EXECUTE FUNCTION plm.reject_immutable_change();

-- Rows keyed by revision_id (iterations, check-outs, working files) cannot be written while the revision is locked.
CREATE FUNCTION plm.guard_locked_document_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF plm.document_revision_locked(NEW.revision_id) THEN
        RAISE EXCEPTION 'document revision is in a locked state (%)', TG_TABLE_NAME USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER document_iteration_locked BEFORE INSERT ON plm.document_iteration
    FOR EACH ROW EXECUTE FUNCTION plm.guard_locked_document_revision();
CREATE TRIGGER document_checkout_locked BEFORE INSERT OR UPDATE ON plm.document_checkout
    FOR EACH ROW EXECUTE FUNCTION plm.guard_locked_document_revision();
CREATE TRIGGER document_checkout_file_locked BEFORE INSERT OR UPDATE ON plm.document_checkout_file
    FOR EACH ROW EXECUTE FUNCTION plm.guard_locked_document_revision();

-- Files may only be added to an iteration while its revision is unlocked (i.e. during check-in or create).
CREATE FUNCTION plm.guard_locked_document_file() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF plm.document_revision_locked((SELECT revision_id FROM plm.document_iteration WHERE id = NEW.iteration_id)) THEN
        RAISE EXCEPTION 'document revision is in a locked state (%)', TG_TABLE_NAME USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER document_file_locked BEFORE INSERT ON plm.document_file
    FOR EACH ROW EXECUTE FUNCTION plm.guard_locked_document_file();

-- A revision in a locked state may only change lifecycle state or lose is_latest. Content pointers are frozen.
CREATE FUNCTION plm.guard_document_revision_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF plm.document_revision_locked(OLD.id)
       AND (NEW.latest_iteration <> OLD.latest_iteration OR NEW.revision <> OLD.revision OR NEW.master_id <> OLD.master_id) THEN
        RAISE EXCEPTION 'document revision % is locked in state %', OLD.revision, OLD.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER document_revision_locked BEFORE UPDATE ON plm.document_revision
    FOR EACH ROW EXECUTE FUNCTION plm.guard_document_revision_update();

-- Documents are never deleted; they are obsoleted through change control.
CREATE TRIGGER document_revision_no_delete BEFORE DELETE ON plm.document_revision
    FOR EACH ROW EXECUTE FUNCTION plm.reject_delete();
CREATE TRIGGER document_master_no_delete BEFORE DELETE ON plm.document_master
    FOR EACH ROW EXECUTE FUNCTION plm.reject_delete();

-- Identity of a master never changes.
CREATE FUNCTION plm.guard_document_master_identity() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id <> OLD.id OR NEW.number <> OLD.number OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
        RAISE EXCEPTION 'document identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER document_master_identity BEFORE UPDATE ON plm.document_master
    FOR EACH ROW EXECUTE FUNCTION plm.guard_document_master_identity();

GRANT SELECT, INSERT, UPDATE ON plm.document_master, plm.document_revision TO keel_app;
GRANT SELECT, INSERT ON plm.document_iteration, plm.document_file TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON plm.document_checkout, plm.document_checkout_file TO keel_app;
GRANT USAGE ON SEQUENCE plm.document_number_seq TO keel_app;
