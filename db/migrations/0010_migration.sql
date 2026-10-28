-- Phase 3: migration from Teamcenter / Windchill (ADR-0023).
-- Imported rows keep their original timestamps and authors; this file adds the bookkeeping that ties every imported row to
-- the batch that created it and to its source identity, plus the id map that makes loading idempotent and resumable.
-- Existing behaviour for natively created rows is unchanged: every new column is nullable.
CREATE SCHEMA mig;

-- A batch is one execution of `keel-import load`. Batches are never deleted (nothing in Keel is), so the load is
-- gated by a successful dry-run report (package + mapping hashes).
CREATE TABLE mig.batch (
    id                uuid        PRIMARY KEY,
    name              text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    source_system     text        NOT NULL CHECK (length(source_system) BETWEEN 1 AND 64),
    package_sha256    text        NOT NULL CHECK (package_sha256 ~ '^[0-9a-f]{64}$'),
    mapping_sha256    text        NOT NULL CHECK (mapping_sha256 ~ '^[0-9a-f]{64}$'),
    dry_run_hash      text        CHECK (dry_run_hash ~ '^[0-9a-f]{64}$'),
    audit_granularity text        NOT NULL CHECK (audit_granularity IN ('batch', 'object')),
    quarantine_mode   boolean     NOT NULL,
    status            text        NOT NULL CHECK (status IN ('Running', 'Completed', 'CompletedWithIssues', 'Failed')),
    started_at        timestamptz NOT NULL,
    started_by        text        NOT NULL,
    completed_at      timestamptz,
    summary           jsonb       NOT NULL DEFAULT '{}'::jsonb
);
CREATE INDEX batch_started_idx ON mig.batch (started_at DESC);

-- source system + entity type + source id -> Keel id. source_hash covers the mapped object, so a changed source
-- (or a changed mapping) for an object that is already imported is detected on the next run and reported as a conflict.
CREATE TABLE mig.id_map (
    source_system text        NOT NULL,
    entity_type   text        NOT NULL CHECK (entity_type IN ('part', 'part_revision', 'document', 'document_revision', 'change')),
    source_id     text        NOT NULL,
    keel_id       uuid        NOT NULL,
    batch_id      uuid        NOT NULL REFERENCES mig.batch (id),
    source_hash   text        NOT NULL CHECK (source_hash ~ '^[0-9a-f]{64}$'),
    imported_at   timestamptz NOT NULL,
    PRIMARY KEY (source_system, entity_type, source_id)
);
CREATE INDEX id_map_batch_idx ON mig.id_map (batch_id, entity_type);
CREATE INDEX id_map_keel_idx ON mig.id_map (keel_id);

-- Objects a batch skipped (quarantine mode) or could not load, with the reason.
CREATE TABLE mig.quarantine (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    batch_id    uuid        NOT NULL REFERENCES mig.batch (id),
    entity_type text        NOT NULL,
    source_id   text        NOT NULL,
    code        text        NOT NULL,
    message     text        NOT NULL,
    recorded_at timestamptz NOT NULL
);
CREATE INDEX quarantine_batch_idx ON mig.quarantine (batch_id);

-- Stored reports (load, reconcile). Content is counts, source ids and numbers only.
CREATE TABLE mig.report (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    batch_id    uuid        NOT NULL REFERENCES mig.batch (id),
    kind        text        NOT NULL CHECK (kind IN ('load', 'reconcile')),
    content     jsonb       NOT NULL,
    created_at  timestamptz NOT NULL
);
CREATE INDEX report_batch_idx ON mig.report (batch_id, id);

CREATE FUNCTION mig.reject_modification() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'migration records are never modified or deleted (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER id_map_immutable BEFORE UPDATE OR DELETE ON mig.id_map FOR EACH ROW EXECUTE FUNCTION mig.reject_modification();
CREATE TRIGGER quarantine_immutable BEFORE UPDATE OR DELETE ON mig.quarantine FOR EACH ROW EXECUTE FUNCTION mig.reject_modification();
CREATE TRIGGER report_immutable BEFORE UPDATE OR DELETE ON mig.report FOR EACH ROW EXECUTE FUNCTION mig.reject_modification();

-- A batch is updated once, from Running to its final status; it is never deleted.
CREATE FUNCTION mig.guard_batch() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'migration batches are never deleted' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.status <> 'Running' THEN
        RAISE EXCEPTION 'batch % is finished (%)', OLD.id, OLD.status USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.id <> OLD.id OR NEW.package_sha256 <> OLD.package_sha256 OR NEW.mapping_sha256 <> OLD.mapping_sha256
       OR NEW.started_at <> OLD.started_at OR NEW.started_by <> OLD.started_by THEN
        RAISE EXCEPTION 'batch identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER batch_guard BEFORE UPDATE OR DELETE ON mig.batch FOR EACH ROW EXECUTE FUNCTION mig.guard_batch();

-- ---------------------------------------------------------------- imported rows (nullable: native rows are untouched)
-- imported_batch_id / source_id: provenance. legacy_properties: every source property that has no Keel attribute, so nothing is dropped.
-- legacy_revision: the source's own revision label when it differs from the ASME Y14.35 label assigned in source order.
ALTER TABLE plm.part_master        ADD COLUMN imported_batch_id uuid REFERENCES mig.batch (id), ADD COLUMN source_id text, ADD COLUMN legacy_properties jsonb;
ALTER TABLE plm.part_revision      ADD COLUMN imported_batch_id uuid REFERENCES mig.batch (id), ADD COLUMN source_id text, ADD COLUMN legacy_revision text, ADD COLUMN legacy_properties jsonb;
ALTER TABLE plm.part_iteration     ADD COLUMN imported_batch_id uuid REFERENCES mig.batch (id), ADD COLUMN legacy_properties jsonb;
ALTER TABLE plm.document_master    ADD COLUMN imported_batch_id uuid REFERENCES mig.batch (id), ADD COLUMN source_id text, ADD COLUMN legacy_properties jsonb;
ALTER TABLE plm.document_revision  ADD COLUMN imported_batch_id uuid REFERENCES mig.batch (id), ADD COLUMN source_id text, ADD COLUMN legacy_revision text, ADD COLUMN legacy_properties jsonb;
ALTER TABLE plm.document_iteration ADD COLUMN imported_batch_id uuid REFERENCES mig.batch (id), ADD COLUMN legacy_properties jsonb;
ALTER TABLE chg.change             ADD COLUMN imported_batch_id uuid REFERENCES mig.batch (id), ADD COLUMN legacy_number text, ADD COLUMN legacy_properties jsonb;
CREATE INDEX part_master_batch_idx ON plm.part_master (imported_batch_id) WHERE imported_batch_id IS NOT NULL;
CREATE INDEX part_revision_batch_idx ON plm.part_revision (imported_batch_id) WHERE imported_batch_id IS NOT NULL;
CREATE INDEX part_iteration_batch_idx ON plm.part_iteration (imported_batch_id) WHERE imported_batch_id IS NOT NULL;
CREATE INDEX document_master_batch_idx ON plm.document_master (imported_batch_id) WHERE imported_batch_id IS NOT NULL;
CREATE INDEX document_revision_batch_idx ON plm.document_revision (imported_batch_id) WHERE imported_batch_id IS NOT NULL;
CREATE INDEX document_iteration_batch_idx ON plm.document_iteration (imported_batch_id) WHERE imported_batch_id IS NOT NULL;
CREATE INDEX change_batch_idx ON chg.change (imported_batch_id) WHERE imported_batch_id IS NOT NULL;

-- Provenance never changes once written (the identity guards of 0002/0003/0007 cover number, id and creator; this covers the new column).
CREATE FUNCTION mig.guard_provenance() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.imported_batch_id IS DISTINCT FROM OLD.imported_batch_id THEN
        RAISE EXCEPTION 'import provenance is immutable' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER part_master_provenance BEFORE UPDATE ON plm.part_master FOR EACH ROW EXECUTE FUNCTION mig.guard_provenance();
CREATE TRIGGER part_revision_provenance BEFORE UPDATE ON plm.part_revision FOR EACH ROW EXECUTE FUNCTION mig.guard_provenance();
CREATE TRIGGER document_master_provenance BEFORE UPDATE ON plm.document_master FOR EACH ROW EXECUTE FUNCTION mig.guard_provenance();
CREATE TRIGGER document_revision_provenance BEFORE UPDATE ON plm.document_revision FOR EACH ROW EXECUTE FUNCTION mig.guard_provenance();
CREATE TRIGGER change_provenance BEFORE UPDATE ON chg.change FOR EACH ROW EXECUTE FUNCTION mig.guard_provenance();

-- The import runs as keel_app (no superuser, no trigger disabling): it needs only these rights.
GRANT USAGE ON SCHEMA mig TO keel_app;
GRANT SELECT, INSERT, UPDATE ON mig.batch TO keel_app;
GRANT SELECT, INSERT ON mig.id_map, mig.quarantine, mig.report TO keel_app;
GRANT USAGE ON SEQUENCE mig.quarantine_id_seq, mig.report_id_seq TO keel_app;
