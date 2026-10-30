-- Increment 9.5: cross-document compare and redline review (ADR-0053, docs/increments/9.5-redline.md). Forward-only, runs as keel_migrator.
--   * rdl.review: one shared redline review per ORDERED pair of document iterations (left = base, right = changed). It records which two stored files (by SHA-256)
--     were compared and under which diff/extractor version, so a comment is always about known bytes. The comparison itself is never stored: it is recomputed from
--     the vault bytes on every view (deterministic), so no copy of document text exists outside the vault.
--   * rdl.annotation: reviewer comments anchored to one changed range of a review (the n-th change of the word diff, with a digest of that change so a comment can
--     be recognised as detached if the diff ever changes). Threads are a root comment plus replies; a root can be resolved and reopened.
-- Marking model: NO marking columns. A review and its comments are marked data derived from BOTH documents, so they inherit dynamically: the service reads or writes
-- them only for a caller who may read AND download both documents under their live attributes, and the one list query joins both masters through
-- AccessPolicy.BuildReadFilter. When either document's markings change the comments follow; nothing is copied.
CREATE SCHEMA rdl;
GRANT USAGE ON SCHEMA rdl TO keel_app;

CREATE TABLE rdl.review (
    id                uuid        PRIMARY KEY,
    left_master_id    uuid        NOT NULL REFERENCES plm.document_master (id),
    left_iteration_id uuid        NOT NULL REFERENCES plm.document_iteration (id),
    right_master_id   uuid        NOT NULL REFERENCES plm.document_master (id),
    right_iteration_id uuid       NOT NULL REFERENCES plm.document_iteration (id),
    left_sha256       text        NOT NULL CHECK (left_sha256 ~ '^[0-9a-f]{64}$'),
    right_sha256      text        NOT NULL CHECK (right_sha256 ~ '^[0-9a-f]{64}$'),
    diff_version      int         NOT NULL CHECK (diff_version >= 1),
    extractor_version int         NOT NULL CHECK (extractor_version >= 1),
    created_at        timestamptz NOT NULL,
    created_by        text        NOT NULL CHECK (length(created_by) BETWEEN 1 AND 200),
    created_by_name   text        NOT NULL CHECK (length(created_by_name) BETWEEN 1 AND 200),
    CONSTRAINT review_distinct CHECK (left_iteration_id <> right_iteration_id),
    CONSTRAINT review_pair UNIQUE (left_iteration_id, right_iteration_id)
);
CREATE INDEX review_left_idx  ON rdl.review (left_master_id);
CREATE INDEX review_right_idx ON rdl.review (right_master_id);

-- A review row is a fact about two stored files: it never changes and is never deleted.
CREATE TRIGGER review_immutable BEFORE UPDATE OR DELETE ON rdl.review
    FOR EACH ROW EXECUTE FUNCTION plm.reject_immutable_change();
CREATE TRIGGER review_no_truncate BEFORE TRUNCATE ON rdl.review FOR EACH STATEMENT EXECUTE FUNCTION plm.reject_immutable_change();

-- Each side must be an iteration of the stated document, and the stated hash must be the hash of that iteration's primary file.
CREATE FUNCTION rdl.guard_review() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM plm.document_iteration i JOIN plm.document_revision r ON r.id = i.revision_id
                   JOIN plm.document_file f ON f.iteration_id = i.id AND f.role = 'primary'
                   WHERE i.id = NEW.left_iteration_id AND r.master_id = NEW.left_master_id AND f.sha256 = NEW.left_sha256) THEN
        RAISE EXCEPTION 'the left side must be an iteration of the left document with that primary file hash' USING ERRCODE = 'check_violation';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM plm.document_iteration i JOIN plm.document_revision r ON r.id = i.revision_id
                   JOIN plm.document_file f ON f.iteration_id = i.id AND f.role = 'primary'
                   WHERE i.id = NEW.right_iteration_id AND r.master_id = NEW.right_master_id AND f.sha256 = NEW.right_sha256) THEN
        RAISE EXCEPTION 'the right side must be an iteration of the right document with that primary file hash' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER review_guard BEFORE INSERT ON rdl.review FOR EACH ROW EXECUTE FUNCTION rdl.guard_review();

CREATE TABLE rdl.annotation (
    id              uuid        PRIMARY KEY,
    review_id       uuid        NOT NULL REFERENCES rdl.review (id),
    parent_id       uuid        REFERENCES rdl.annotation (id),
    -- The n-th change (0-based) of the word diff of the review, its kind, and a SHA-256 over its deleted and inserted text (see WordDiff.ChangeDigest).
    change_index    int         NOT NULL CHECK (change_index >= 0),
    change_kind     text        NOT NULL CHECK (change_kind IN ('insert', 'delete', 'replace')),
    change_digest   text        NOT NULL CHECK (change_digest ~ '^[0-9a-f]{64}$'),
    body            text        NOT NULL CHECK (length(btrim(body)) BETWEEN 1 AND 4000),
    author_sub      text        NOT NULL CHECK (length(author_sub) BETWEEN 1 AND 200),
    author_name     text        NOT NULL CHECK (length(author_name) BETWEEN 1 AND 200),
    created_at      timestamptz NOT NULL,
    -- Resolution belongs to a thread (a root comment). A reopened thread has all three NULL again; the audit log keeps every resolve and reopen.
    resolved_at     timestamptz,
    resolved_by     text,
    resolved_by_name text,
    CONSTRAINT resolved_together CHECK ((resolved_at IS NULL) = (resolved_by IS NULL) AND (resolved_at IS NULL) = (resolved_by_name IS NULL)),
    CONSTRAINT resolved_only_on_root CHECK (parent_id IS NULL OR resolved_at IS NULL)
);
CREATE INDEX annotation_review_idx ON rdl.annotation (review_id, change_index, created_at, id);
CREATE INDEX annotation_open_idx ON rdl.annotation (review_id) WHERE parent_id IS NULL AND resolved_at IS NULL;

-- A comment is immutable; only a root's resolution columns may change; nothing is deleted. Replies go one level deep, to a root of the same review and change.
CREATE FUNCTION rdl.guard_annotation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.resolved_at IS NOT NULL THEN
            RAISE EXCEPTION 'a comment cannot be created resolved' USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.parent_id IS NOT NULL AND NOT EXISTS (
               SELECT 1 FROM rdl.annotation p WHERE p.id = NEW.parent_id AND p.parent_id IS NULL AND p.review_id = NEW.review_id AND p.change_index = NEW.change_index) THEN
            RAISE EXCEPTION 'a reply must answer a top-level comment of the same review and change' USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    ELSIF TG_OP = 'UPDATE' THEN
        IF NEW.id <> OLD.id OR NEW.review_id <> OLD.review_id OR NEW.parent_id IS DISTINCT FROM OLD.parent_id OR NEW.change_index <> OLD.change_index
           OR NEW.change_kind <> OLD.change_kind OR NEW.change_digest <> OLD.change_digest OR NEW.body <> OLD.body
           OR NEW.author_sub <> OLD.author_sub OR NEW.author_name <> OLD.author_name OR NEW.created_at <> OLD.created_at
           OR OLD.parent_id IS NOT NULL THEN
            RAISE EXCEPTION 'redline comments are immutable (only the resolution of a thread may change)' USING ERRCODE = 'insufficient_privilege';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'redline comments are never deleted (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER annotation_guard BEFORE INSERT OR UPDATE OR DELETE ON rdl.annotation FOR EACH ROW EXECUTE FUNCTION rdl.guard_annotation();
CREATE TRIGGER annotation_no_truncate BEFORE TRUNCATE ON rdl.annotation FOR EACH STATEMENT EXECUTE FUNCTION rdl.guard_annotation();

GRANT SELECT, INSERT ON rdl.review TO keel_app;
GRANT SELECT, INSERT, UPDATE ON rdl.annotation TO keel_app;
