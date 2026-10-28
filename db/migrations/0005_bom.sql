-- Increment 1.4: multi-level BOM (ADR-0003, ADR-0017). Usage links belong to a parent part ITERATION and point at a child
-- part MASTER; the child version is resolved at read time by a configuration spec.

-- Which usage-link structure a row belongs to. Only the engineering BOM exists in Phase 1; the manufacturing BOM arrives in Phase 2.
CREATE TABLE plm.bom_usage (
    id                    uuid          PRIMARY KEY,
    parent_iteration_id   uuid          NOT NULL REFERENCES plm.part_iteration (id),
    child_master_id       uuid          NOT NULL REFERENCES plm.part_master (id),
    quantity              numeric(18,6) NOT NULL CHECK (quantity > 0),
    unit                  text          NOT NULL,
    find_number           int           NOT NULL CHECK (find_number > 0),
    reference_designators text[]        NOT NULL DEFAULT '{}',
    notes                 text          CHECK (notes IS NULL OR length(notes) <= 1000),
    structure_type        text          NOT NULL DEFAULT 'EBOM' CHECK (structure_type IN ('EBOM')),
    created_at            timestamptz   NOT NULL,
    created_by            text          NOT NULL,
    -- Defence in depth for the EA rules enforced by the service: whole-number quantity, designator count = quantity.
    CONSTRAINT bom_usage_ea_whole CHECK (unit <> 'EA' OR quantity = trunc(quantity)),
    CONSTRAINT bom_usage_ea_designators CHECK (unit <> 'EA' OR cardinality(reference_designators) = 0 OR cardinality(reference_designators) = quantity),
    UNIQUE (parent_iteration_id, find_number)
);
-- Explosion reads children by parent iteration; where-used reads parents by child master.
CREATE INDEX bom_usage_parent_idx ON plm.bom_usage (parent_iteration_id) INCLUDE (child_master_id, find_number, quantity, unit);
CREATE INDEX bom_usage_child_idx ON plm.bom_usage (child_master_id) INCLUDE (parent_iteration_id, find_number, quantity, unit);
-- LatestReleased resolution looks for a master's highest Released revision.
CREATE INDEX part_revision_master_state_idx ON plm.part_revision (master_id, lifecycle_state);

-- The private working copy of a check-out (ADR-0003): rows disappear with the check-out row (undo) and are copied
-- into the next iteration at check-in. The find-number uniqueness is deferrable so renumbering can swap numbers in one statement.
CREATE TABLE plm.bom_working_usage (
    id                    uuid          PRIMARY KEY,
    checkout_revision_id  uuid          NOT NULL REFERENCES plm.part_checkout (revision_id) ON DELETE CASCADE,
    child_master_id       uuid          NOT NULL REFERENCES plm.part_master (id),
    quantity              numeric(18,6) NOT NULL CHECK (quantity > 0),
    unit                  text          NOT NULL,
    find_number           int           NOT NULL CHECK (find_number > 0),
    reference_designators text[]        NOT NULL DEFAULT '{}',
    notes                 text          CHECK (notes IS NULL OR length(notes) <= 1000),
    structure_type        text          NOT NULL DEFAULT 'EBOM' CHECK (structure_type IN ('EBOM')),
    created_at            timestamptz   NOT NULL,
    created_by            text          NOT NULL,
    CONSTRAINT bom_working_ea_whole CHECK (unit <> 'EA' OR quantity = trunc(quantity)),
    CONSTRAINT bom_working_ea_designators CHECK (unit <> 'EA' OR cardinality(reference_designators) = 0 OR cardinality(reference_designators) = quantity),
    CONSTRAINT bom_working_find_unique UNIQUE (checkout_revision_id, find_number) DEFERRABLE INITIALLY IMMEDIATE
);
CREATE INDEX bom_working_child_idx ON plm.bom_working_usage (child_master_id);

-- ---------------------------------------------------------------- guards
-- Rows of an iteration are immutable; a new iteration gets a fresh copy at check-in.
CREATE FUNCTION plm.reject_bom_usage_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'BOM usage rows of an iteration are immutable' USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER bom_usage_immutable BEFORE UPDATE OR DELETE ON plm.bom_usage
    FOR EACH ROW EXECUTE FUNCTION plm.reject_bom_usage_change();

-- Only Assembly parts own children; a part never contains itself; nothing is added to a locked revision.
CREATE FUNCTION plm.guard_bom_owner(p_master uuid, p_state text, p_child uuid) RETURNS void LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_type text;
BEGIN
    IF plm.is_locked_state(p_master, p_state) THEN
        RAISE EXCEPTION 'revision is in locked state %: its structure cannot change', p_state USING ERRCODE = 'check_violation';
    END IF;
    SELECT part_type INTO v_type FROM plm.part_master WHERE id = p_master;
    IF v_type IS DISTINCT FROM 'Assembly' THEN
        RAISE EXCEPTION 'only Assembly parts can own BOM lines (part type is %)', v_type USING ERRCODE = 'check_violation';
    END IF;
    IF p_child = p_master THEN
        RAISE EXCEPTION 'a part cannot contain itself' USING ERRCODE = 'check_violation';
    END IF;
END;
$$;

CREATE FUNCTION plm.guard_bom_usage_insert() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_master uuid;
    v_state  text;
BEGIN
    SELECT r.master_id, r.lifecycle_state INTO v_master, v_state
    FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id WHERE i.id = NEW.parent_iteration_id;
    PERFORM plm.guard_bom_owner(v_master, v_state, NEW.child_master_id);
    RETURN NEW;
END;
$$;
CREATE TRIGGER bom_usage_insert_guard BEFORE INSERT ON plm.bom_usage
    FOR EACH ROW EXECUTE FUNCTION plm.guard_bom_usage_insert();

CREATE FUNCTION plm.guard_bom_working_write() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_master uuid;
    v_state  text;
BEGIN
    SELECT master_id, lifecycle_state INTO v_master, v_state FROM plm.part_revision WHERE id = NEW.checkout_revision_id;
    PERFORM plm.guard_bom_owner(v_master, v_state, NEW.child_master_id);
    RETURN NEW;
END;
$$;
CREATE TRIGGER bom_working_write_guard BEFORE INSERT OR UPDATE ON plm.bom_working_usage
    FOR EACH ROW EXECUTE FUNCTION plm.guard_bom_working_write();

GRANT SELECT, INSERT ON plm.bom_usage TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON plm.bom_working_usage TO keel_app;
