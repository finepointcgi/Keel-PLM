-- Increment 1.3: part <-> document links (ADR-0016). Links belong to a part ITERATION, like BOM usage links
-- (ADR-0003), so a released part iteration keeps pointing at exactly what it pointed at when it was checked in.
--   describes  : the document describes the part (e.g. its drawing); pins a document revision.
--   references : a document the part conforms to (e.g. a specification); points at the master and resolves at
--                read time (latest Released revision, else latest revision; assumption A23).

CREATE TABLE plm.part_document_link (
    id                   uuid        PRIMARY KEY,
    part_iteration_id    uuid        NOT NULL REFERENCES plm.part_iteration (id),
    link_type            text        NOT NULL CHECK (link_type IN ('describes', 'references')),
    document_master_id   uuid        NOT NULL REFERENCES plm.document_master (id),
    document_revision_id uuid,
    created_at           timestamptz NOT NULL,
    created_by           text        NOT NULL,
    CHECK ((link_type = 'describes') = (document_revision_id IS NOT NULL)),
    -- The pinned revision must be a revision of the linked master.
    FOREIGN KEY (document_revision_id, document_master_id) REFERENCES plm.document_revision (id, master_id),
    UNIQUE (part_iteration_id, link_type, document_master_id)
);
CREATE INDEX part_document_link_document_idx ON plm.part_document_link (document_master_id);

-- Links of a part's working copy (one per check-out). Check-in copies them into the new iteration.
CREATE TABLE plm.part_checkout_document_link (
    id                   uuid        PRIMARY KEY,
    revision_id          uuid        NOT NULL REFERENCES plm.part_checkout (revision_id) ON DELETE CASCADE,
    link_type            text        NOT NULL CHECK (link_type IN ('describes', 'references')),
    document_master_id   uuid        NOT NULL REFERENCES plm.document_master (id),
    document_revision_id uuid,
    created_at           timestamptz NOT NULL,
    created_by           text        NOT NULL,
    CHECK ((link_type = 'describes') = (document_revision_id IS NOT NULL)),
    FOREIGN KEY (document_revision_id, document_master_id) REFERENCES plm.document_revision (id, master_id),
    UNIQUE (revision_id, link_type, document_master_id)
);

-- Iteration links are part of an immutable iteration.
CREATE TRIGGER part_document_link_immutable BEFORE UPDATE OR DELETE ON plm.part_document_link
    FOR EACH ROW EXECUTE FUNCTION plm.reject_immutable_change();

-- New iteration links only while the part revision is unlocked (i.e. during check-in).
CREATE FUNCTION plm.guard_locked_part_link() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_master uuid;
    v_state  text;
BEGIN
    SELECT r.master_id, r.lifecycle_state INTO v_master, v_state
    FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id WHERE i.id = NEW.part_iteration_id;
    IF plm.is_locked_state(v_master, v_state) THEN
        RAISE EXCEPTION 'part revision is in locked state % (%)', v_state, TG_TABLE_NAME USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER part_document_link_locked BEFORE INSERT ON plm.part_document_link
    FOR EACH ROW EXECUTE FUNCTION plm.guard_locked_part_link();
CREATE TRIGGER part_checkout_document_link_locked BEFORE INSERT OR UPDATE ON plm.part_checkout_document_link
    FOR EACH ROW EXECUTE FUNCTION plm.guard_locked_revision();

GRANT SELECT, INSERT ON plm.part_document_link TO keel_app;
GRANT SELECT, INSERT, DELETE ON plm.part_checkout_document_link TO keel_app;
