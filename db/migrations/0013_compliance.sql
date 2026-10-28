-- Compliance gaps (docs/increments/5.0-compliance.md, ADR-0026): upload malware scanning, vault garbage collection, SIEM forwarding.
-- Nothing here changes existing behaviour: every table is new. The application role keeps least privilege (insert-only where the
-- records are evidence, update only where state must advance).

-- ---------------------------------------------------------------- malware scanning of uploads (NIST 3.14.2)
-- One row per scan of a piece of content (the vault object named by sha256). Append-only: the newest row for a hash is its current
-- status, the older rows are the history auditors can ask for. Content is shared by documents, so the scan result belongs to the
-- content; the quarantine below belongs to the document.
CREATE TABLE plm.file_scan (
    id                  bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sha256              text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    size_bytes          bigint      NOT NULL CHECK (size_bytes >= 0),
    -- clean: a scanner looked at every byte and found nothing. infected: it found a signature (only written by rescans and imports:
    -- an infected upload is refused and stores nothing). unscanned: best-effort mode and the scanner failed. skipped: scanning is
    -- disabled, or the file is larger than the scanner can take in best-effort mode.
    status              text        NOT NULL CHECK (status IN ('clean', 'infected', 'unscanned', 'skipped')),
    scanner_name        text,
    scanner_version     text,
    signature_db_date   timestamptz,
    signature           text,
    reason              text,
    source              text        NOT NULL CHECK (source IN ('upload', 'rescan', 'import')),
    scanned_at          timestamptz NOT NULL
);
CREATE INDEX file_scan_sha_idx ON plm.file_scan (sha256, id DESC);
CREATE INDEX file_scan_status_idx ON plm.file_scan (status);

CREATE TRIGGER file_scan_immutable BEFORE UPDATE OR DELETE ON plm.file_scan
    FOR EACH ROW EXECUTE FUNCTION plm.reject_immutable_change();

-- A file found infected after it was stored is quarantined for the document that holds it: downloads of those bytes through that
-- document are refused (409) while the bytes and the audit trail stay. The flag is per document and content, not per vault object:
-- another document that happens to share the object is flagged only if its own rescan finds it. A release (false positive) is an
-- administrator action with a reason; the row is never deleted.
CREATE TABLE plm.file_quarantine (
    id               bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    master_id        uuid        NOT NULL REFERENCES plm.document_master (id),
    sha256           text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    signature        text,
    quarantined_at   timestamptz NOT NULL,
    quarantined_by   text        NOT NULL,
    released_at      timestamptz,
    released_by      text,
    release_reason   text,
    CONSTRAINT file_quarantine_release_complete CHECK ((released_at IS NULL) = (released_by IS NULL) AND (released_at IS NULL) = (release_reason IS NULL))
);
CREATE UNIQUE INDEX file_quarantine_active ON plm.file_quarantine (master_id, sha256) WHERE released_at IS NULL;

CREATE FUNCTION plm.guard_file_quarantine() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'quarantine records are never deleted' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.released_at IS NOT NULL THEN
        RAISE EXCEPTION 'quarantine record % is already released', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.id <> OLD.id OR NEW.master_id <> OLD.master_id OR NEW.sha256 <> OLD.sha256
       OR NEW.signature IS DISTINCT FROM OLD.signature OR NEW.quarantined_at <> OLD.quarantined_at OR NEW.quarantined_by <> OLD.quarantined_by THEN
        RAISE EXCEPTION 'only the release fields of a quarantine record can change' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER file_quarantine_guard BEFORE UPDATE OR DELETE ON plm.file_quarantine
    FOR EACH ROW EXECUTE FUNCTION plm.guard_file_quarantine();

-- Newest scan per stored file, so auditors can see scan coverage per file row (documents.file_scan_status).
CREATE VIEW plm.document_file_scan_status AS
SELECT f.id AS file_id, f.iteration_id, f.sha256, f.size_bytes,
       COALESCE(s.status, 'unscanned') AS scan_status,
       s.scanner_name, s.scanner_version, s.signature_db_date, s.scanned_at,
       EXISTS (SELECT 1 FROM plm.file_quarantine q
               JOIN plm.document_revision r ON r.master_id = q.master_id
               JOIN plm.document_iteration i ON i.revision_id = r.id AND i.id = f.iteration_id
               WHERE q.sha256 = f.sha256 AND q.released_at IS NULL) AS quarantined
FROM plm.document_file f
LEFT JOIN LATERAL (SELECT * FROM plm.file_scan x WHERE x.sha256 = f.sha256 ORDER BY x.id DESC LIMIT 1) s ON true;

GRANT SELECT, INSERT ON plm.file_scan TO keel_app;
GRANT SELECT, INSERT, UPDATE ON plm.file_quarantine TO keel_app;
GRANT SELECT ON plm.document_file_scan_status TO keel_app;

-- ---------------------------------------------------------------- vault garbage collection
CREATE SCHEMA vault;
GRANT USAGE ON SCHEMA vault TO keel_app;

-- An upload that is about to rely on a vault object (new, or already there) leaves a short-lived marker under the same advisory lock
-- the sweeper takes before it deletes that key, so identical bytes uploaded during a sweep can never lose their object (ADR-0026).
CREATE TABLE vault.pending_upload (
    sha256      text        PRIMARY KEY CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    marked_at   timestamptz NOT NULL,
    expires_at  timestamptz NOT NULL
);
CREATE INDEX pending_upload_expiry_idx ON vault.pending_upload (expires_at);

-- One row per sweep (dry run or delete): counts and the report hash, kept for the admin page. The audit log carries the same
-- numbers; this table adds the sample keys and the continuation token.
CREATE TABLE vault.gc_run (
    id                uuid        PRIMARY KEY,
    started_at        timestamptz NOT NULL,
    finished_at       timestamptz NOT NULL,
    started_by        text        NOT NULL,
    mode              text        NOT NULL CHECK (mode IN ('dry-run', 'delete')),
    grace_hours       integer     NOT NULL CHECK (grace_hours >= 24),
    max_deletions     integer     NOT NULL CHECK (max_deletions >= 0),
    objects_listed    bigint      NOT NULL,
    objects_referenced bigint     NOT NULL,
    candidates        bigint      NOT NULL,
    deleted           bigint      NOT NULL,
    bytes_candidates  bigint      NOT NULL,
    bytes_deleted     bigint      NOT NULL,
    report_hash       text        NOT NULL CHECK (report_hash ~ '^[0-9a-f]{64}$'),
    continuation      text,
    complete          boolean     NOT NULL,
    sample_keys       text[]      NOT NULL
);
CREATE INDEX gc_run_started_idx ON vault.gc_run (started_at DESC);
CREATE TRIGGER gc_run_immutable BEFORE UPDATE OR DELETE ON vault.gc_run
    FOR EACH ROW EXECUTE FUNCTION plm.reject_immutable_change();

GRANT SELECT, INSERT, UPDATE, DELETE ON vault.pending_upload TO keel_app;
GRANT SELECT, INSERT ON vault.gc_run TO keel_app;

-- ---------------------------------------------------------------- SIEM forwarding (NIST 3.3.x, ADR-0005, A20)
CREATE SCHEMA siem;
GRANT USAGE ON SCHEMA siem TO keel_app;

-- Delivery position per destination and stream ('audit' = audit.event by seq, 'checkpoint' = audit.checkpoint by id). Forwarding is
-- at-least-once and ordered: the cursor advances only after the destination accepted the batch. last_hash is the chain hash at
-- last_seq, so a restart still verifies continuity and a tail deleted after it was forwarded is detected. chain_break_* records a
-- break the forwarder found in what it read; it persists across restarts and keeps the health check degraded until an operator clears it.
CREATE TABLE siem.cursor (
    destination      text        NOT NULL CHECK (length(destination) BETWEEN 1 AND 64),
    stream           text        NOT NULL CHECK (stream IN ('audit', 'checkpoint')),
    last_seq         bigint      NOT NULL DEFAULT 0 CHECK (last_seq >= 0),
    last_hash        bytea       NOT NULL DEFAULT decode(repeat('00', 32), 'hex') CHECK (octet_length(last_hash) = 32),
    forwarded_total  bigint      NOT NULL DEFAULT 0,
    updated_at       timestamptz NOT NULL,
    chain_break_seq  bigint,
    chain_break_at   timestamptz,
    chain_break_note text,
    PRIMARY KEY (destination, stream)
);

GRANT SELECT, INSERT, UPDATE ON siem.cursor TO keel_app;
