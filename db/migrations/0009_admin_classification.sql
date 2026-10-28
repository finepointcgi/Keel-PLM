-- Increment 2.2: administrator-defined configuration (attributes, versioned lifecycles, type lists, numbering, workflow pins,
-- controlled vocabularies) and hierarchical classification with inherited attribute sets (ADR-0022).
-- Everything here is data an administrator changes through the UI or API; the migration seeds the values that used to be code,
-- so behaviour is unchanged until an administrator changes something. Forward-only, runs as keel_migrator.
CREATE SCHEMA IF NOT EXISTS cfg;

-- ---------------------------------------------------------------- attribute definitions: validations, help, grouping, retirement
-- A definition is edited in place; the audit log keeps every before/after pair and `revision` counts the edits.
-- Retired definitions stay in the table so values stored on old iterations remain readable.
ALTER TABLE plm.attribute_definition
    ADD COLUMN help_text   text,
    ADD COLUMN grp         text,
    ADD COLUMN pattern     text,
    ADD COLUMN min_value   numeric,
    ADD COLUMN max_value   numeric,
    ADD COLUMN retired     boolean     NOT NULL DEFAULT false,
    ADD COLUMN revision    int         NOT NULL DEFAULT 1,
    ADD COLUMN updated_at  timestamptz,
    ADD COLUMN updated_by  text,
    ADD CONSTRAINT attribute_range_order CHECK (min_value IS NULL OR max_value IS NULL OR min_value <= max_value),
    ADD CONSTRAINT attribute_validations_fit_type CHECK (
        (pattern IS NULL OR data_type = 'string') AND ((min_value IS NULL AND max_value IS NULL) OR data_type = 'number'));

-- ---------------------------------------------------------------- lifecycles: versions (the seeded lifecycles become version 1)
-- A lifecycle family has numbered versions. Version 1 keeps the family name as its id (so every existing row, foreign key and
-- trigger keeps working); version n > 1 has the id '<family>~v<n>'. New objects use the family's current version; an object keeps the
-- version it was created with until an administrator runs the audited "migrate objects" operation.
CREATE TABLE plm.lifecycle_family (
    family          text    PRIMARY KEY CHECK (family ~ '^[a-z][a-z0-9-]{0,40}$'),
    object_type     text    NOT NULL,
    name            text    NOT NULL,
    current_version int     NOT NULL CHECK (current_version >= 1),
    -- System families are seeded and referenced by name from code; they cannot be created or removed by an administrator.
    is_system       boolean NOT NULL DEFAULT false,
    -- Change-management lifecycles (PR, ECR, ECN, CT) are bound to process rules in code and triggers (ADR-0019); view only.
    editable        boolean NOT NULL DEFAULT true
);
INSERT INTO plm.lifecycle_family (family, object_type, name, current_version, is_system, editable) VALUES
    ('part-default',     'part',     'Standard part lifecycle',     1, true, true),
    ('document-default', 'document', 'Standard document lifecycle', 1, true, true),
    ('pr-default',       'pr',       'Problem report lifecycle',    1, true, false),
    ('ecr-default',      'ecr',      'Change request lifecycle',    1, true, false),
    ('ecn-default',      'ecn',      'Change notice lifecycle',     1, true, false),
    ('ct-default',       'ct',       'Change task lifecycle',       1, true, false),
    -- Baseline lifecycle (migration 0008): its transitions are enforced by cfg.baseline triggers, so it is view-only like the change lifecycles.
    ('baseline-default', 'baseline', 'Baseline lifecycle',          1, true, false);

ALTER TABLE plm.lifecycle
    ADD COLUMN family      text,
    ADD COLUMN version     int         NOT NULL DEFAULT 1 CHECK (version >= 1),
    ADD COLUMN status      text        NOT NULL DEFAULT 'published' CHECK (status IN ('draft', 'published')),
    ADD COLUMN created_at  timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN created_by  text        NOT NULL DEFAULT 'system',
    ADD COLUMN change_note text;
UPDATE plm.lifecycle SET family = id;
ALTER TABLE plm.lifecycle
    ALTER COLUMN family SET NOT NULL,
    ADD CONSTRAINT lifecycle_family_fk FOREIGN KEY (family) REFERENCES plm.lifecycle_family (family),
    ADD CONSTRAINT lifecycle_family_version_uq UNIQUE (family, version),
    ADD CONSTRAINT lifecycle_id_matches_version CHECK (id = CASE WHEN version = 1 THEN family ELSE family || '~v' || version END);

-- Published versions never change (states, transitions, the version row itself). The migrator is exempt so that a later forward-only
-- migration can still evolve seeded data deliberately; the application role cannot.
CREATE FUNCTION plm.guard_lifecycle_row() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF current_user = 'keel_migrator' THEN RETURN COALESCE(NEW, OLD); END IF;
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'lifecycles are never deleted' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.status = 'published' THEN
            RAISE EXCEPTION 'published lifecycle versions are immutable; create a new version' USING ERRCODE = 'insufficient_privilege';
        END IF;
        IF NEW.id <> OLD.id OR NEW.family <> OLD.family OR NEW.version <> OLD.version THEN
            RAISE EXCEPTION 'lifecycle identity is immutable' USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.status = 'published' AND NOT EXISTS (SELECT 1 FROM plm.lifecycle_state WHERE lifecycle_id = NEW.id AND is_initial) THEN
            RAISE EXCEPTION 'a lifecycle needs an initial state' USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    IF TG_OP = 'INSERT' AND NEW.status <> 'draft' THEN
        RAISE EXCEPTION 'a lifecycle version is created as a draft and published after its states exist' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER lifecycle_guard BEFORE INSERT OR UPDATE OR DELETE ON plm.lifecycle FOR EACH ROW EXECUTE FUNCTION plm.guard_lifecycle_row();

CREATE FUNCTION plm.guard_lifecycle_child() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_status text;
BEGIN
    IF current_user = 'keel_migrator' THEN RETURN COALESCE(NEW, OLD); END IF;
    SELECT status INTO v_status FROM plm.lifecycle WHERE id = COALESCE(NEW.lifecycle_id, OLD.lifecycle_id);
    IF v_status IS DISTINCT FROM 'draft' THEN
        RAISE EXCEPTION 'published lifecycle versions are immutable; create a new version' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN COALESCE(NEW, OLD);
END;
$$;
CREATE TRIGGER lifecycle_state_guard BEFORE INSERT OR UPDATE OR DELETE ON plm.lifecycle_state FOR EACH ROW EXECUTE FUNCTION plm.guard_lifecycle_child();
CREATE TRIGGER lifecycle_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON plm.lifecycle_transition FOR EACH ROW EXECUTE FUNCTION plm.guard_lifecycle_child();

-- The family's current version must be a published one.
CREATE FUNCTION plm.guard_lifecycle_family() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW.family <> OLD.family OR NEW.object_type <> OLD.object_type OR NEW.is_system <> OLD.is_system OR NEW.editable <> OLD.editable) THEN
        RAISE EXCEPTION 'lifecycle family identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.current_version < OLD.current_version THEN
        RAISE EXCEPTION 'the current lifecycle version only moves forward' USING ERRCODE = 'check_violation';
    END IF;
    IF TG_OP = 'UPDATE' AND NOT EXISTS (SELECT 1 FROM plm.lifecycle WHERE family = NEW.family AND version = NEW.current_version AND status = 'published') THEN
        RAISE EXCEPTION 'version % of lifecycle % is not published', NEW.current_version, NEW.family USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER lifecycle_family_guard BEFORE UPDATE ON plm.lifecycle_family FOR EACH ROW EXECUTE FUNCTION plm.guard_lifecycle_family();

-- Change objects keep their per-type lifecycle family (versions of those families are not editable in this increment).
ALTER TABLE chg.change DROP CONSTRAINT change_lifecycle_matches_type;
ALTER TABLE chg.change ADD CONSTRAINT change_lifecycle_matches_type CHECK (split_part(lifecycle_id, '~v', 1) = lower(change_type) || '-default');

-- ---------------------------------------------------------------- reference lists (types, sources, units, priorities, vocabularies)
-- One table for every administrator-maintained list. Items are retired, never deleted, so old objects keep valid values.
CREATE TABLE cfg.ref_item (
    list             text        NOT NULL CHECK (list IN ('part-type', 'part-source', 'unit', 'document-type', 'change-priority', 'change-class', 'vocab.office', 'vocab.program')),
    code             text        NOT NULL CHECK (length(code) BETWEEN 1 AND 60 AND code = btrim(code)),
    label            text        NOT NULL CHECK (length(label) BETWEEN 1 AND 120),
    sort_order       int         NOT NULL DEFAULT 0,
    is_active        boolean     NOT NULL DEFAULT true,
    -- Part and document types choose the lifecycle family their new objects start in (null = the default family).
    lifecycle_family text        REFERENCES plm.lifecycle_family (family),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       text        NOT NULL DEFAULT 'system',
    PRIMARY KEY (list, code),
    CHECK (lifecycle_family IS NULL OR list IN ('part-type', 'document-type'))
);
INSERT INTO cfg.ref_item (list, code, label, sort_order) VALUES
    ('part-type', 'Assembly',    'Assembly',     10), ('part-type', 'Component', 'Component', 20),
    ('part-type', 'Standard',    'Standard',     30), ('part-type', 'RawMaterial', 'Raw material', 40),
    ('part-source', 'Make', 'Make', 10), ('part-source', 'Buy', 'Buy', 20),
    ('document-type', 'Specification', 'Specification', 10), ('document-type', 'Drawing', 'Drawing', 20),
    ('document-type', 'CAD', 'CAD', 30), ('document-type', 'Report', 'Report', 40),
    ('document-type', 'Procedure', 'Procedure', 50), ('document-type', 'Other', 'Other', 60),
    ('change-priority', 'Low', 'Low', 10), ('change-priority', 'Medium', 'Medium', 20),
    ('change-priority', 'High', 'High', 30), ('change-priority', 'Critical', 'Critical', 40),
    -- Class codes carry process rules in code and triggers (A8); only their labels are editable.
    ('change-class', 'I', 'Class I', 10), ('change-class', 'II', 'Class II', 20),
    ('unit', 'EA', 'Each', 10), ('unit', 'SET', 'Set', 20), ('unit', 'mm', 'Millimetre', 30), ('unit', 'cm', 'Centimetre', 40),
    ('unit', 'm', 'Metre', 50), ('unit', 'in', 'Inch', 60), ('unit', 'ft', 'Foot', 70), ('unit', 'g', 'Gram', 80),
    ('unit', 'kg', 'Kilogram', 90), ('unit', 'lb', 'Pound', 100), ('unit', 'mL', 'Millilitre', 110), ('unit', 'L', 'Litre', 120),
    ('unit', 'gal', 'Gallon', 130), ('unit', 'm2', 'Square metre', 140), ('unit', 'm3', 'Cubic metre', 150),
    -- Pickers only (ASSUMPTION ADM-9): programs remain security attributes released by the IdP (A2); this list helps users choose a value.
    ('vocab.office', 'ACME', 'ACME Engineering (synthetic)', 10),
    ('vocab.program', 'ORCA', 'ORCA (synthetic)', 10);

-- The old closed CHECK lists are replaced by the data above.
DO $$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT c.conrelid::regclass AS tbl, c.conname FROM pg_constraint c
        WHERE c.contype = 'c' AND (
            (c.conrelid = 'plm.part_master'::regclass AND (pg_get_constraintdef(c.oid) LIKE '%part_type%' OR pg_get_constraintdef(c.oid) LIKE '%source%'))
            OR (c.conrelid = 'plm.document_master'::regclass AND pg_get_constraintdef(c.oid) LIKE '%document_type%')
            OR (c.conrelid = 'chg.change'::regclass AND pg_get_constraintdef(c.oid) LIKE '%priority%'))
    LOOP
        EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', r.tbl, r.conname);
    END LOOP;
END;
$$;

-- Safety net: a new object must use an active list item (existing rows stay valid after the item is retired).
CREATE FUNCTION cfg.assert_ref_item() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_value text := to_jsonb(NEW) ->> TG_ARGV[1];
BEGIN
    IF TG_OP = 'UPDATE' AND v_value IS NOT DISTINCT FROM (to_jsonb(OLD) ->> TG_ARGV[1]) THEN RETURN NEW; END IF;
    IF NOT EXISTS (SELECT 1 FROM cfg.ref_item WHERE list = TG_ARGV[0] AND code = v_value AND is_active) THEN
        RAISE EXCEPTION '% is not an active % (configure it under Administration > Types and lists)', v_value, TG_ARGV[0] USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER part_master_part_type_ref BEFORE INSERT OR UPDATE OF part_type ON plm.part_master FOR EACH ROW EXECUTE FUNCTION cfg.assert_ref_item('part-type', 'part_type');
CREATE TRIGGER part_master_source_ref BEFORE INSERT OR UPDATE OF source ON plm.part_master FOR EACH ROW EXECUTE FUNCTION cfg.assert_ref_item('part-source', 'source');
CREATE TRIGGER part_master_unit_ref BEFORE INSERT OR UPDATE OF default_unit ON plm.part_master FOR EACH ROW EXECUTE FUNCTION cfg.assert_ref_item('unit', 'default_unit');
CREATE TRIGGER document_master_type_ref BEFORE INSERT OR UPDATE OF document_type ON plm.document_master FOR EACH ROW EXECUTE FUNCTION cfg.assert_ref_item('document-type', 'document_type');
CREATE TRIGGER change_priority_ref BEFORE INSERT OR UPDATE OF priority ON chg.change FOR EACH ROW EXECUTE FUNCTION cfg.assert_ref_item('change-priority', 'priority');

-- ---------------------------------------------------------------- numbering (prefix and width per object type; the sequence never goes back)
CREATE TABLE cfg.numbering (
    object_type     text        PRIMARY KEY,
    prefix          text        NOT NULL CHECK (prefix ~ '^[A-Z0-9][A-Z0-9._-]{0,15}$'),
    width           int         NOT NULL CHECK (width BETWEEN 4 AND 12),
    sequence_name   text        NOT NULL,
    -- Change prefixes are fixed by the number format checks of the chg tables (ADR-0019).
    prefix_editable boolean     NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      text        NOT NULL DEFAULT 'system'
);
INSERT INTO cfg.numbering (object_type, prefix, width, sequence_name, prefix_editable) VALUES
    ('part',     'P-',   6, 'plm.part_number_seq',     true),
    ('document', 'D-',   6, 'plm.document_number_seq', true),
    ('pr',       'PR-',  6, 'chg.pr_seq',              false),
    ('ecr',      'ECR-', 6, 'chg.ecr_seq',             false),
    ('ecn',      'ECN-', 6, 'chg.ecn_seq',             false),
    ('ct',       'CT-',  6, 'chg.ct_seq',              false);

-- ---------------------------------------------------------------- workflow template pins (which template an object type's review uses)
CREATE TABLE wf.template_pin (
    object_type text        PRIMARY KEY,
    template_id text        NOT NULL,
    pinned_at   timestamptz NOT NULL,
    pinned_by   text        NOT NULL
);

-- ---------------------------------------------------------------- classification (ADR-0022)
CREATE TABLE cfg.class_node (
    id          uuid        PRIMARY KEY,
    code        text        NOT NULL CHECK (code ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,39}$'),
    name        text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    description text        NOT NULL DEFAULT '' CHECK (length(description) <= 2000),
    parent_id   uuid        REFERENCES cfg.class_node (id),
    -- Materialized path of codes ('/MECH/FASTENER/'): subtree queries are a prefix match and cycles are visible at a glance.
    path        text        NOT NULL,
    depth       int         NOT NULL CHECK (depth BETWEEN 1 AND 12),
    sort_order  int         NOT NULL DEFAULT 0,
    status      text        NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'retired')),
    created_at  timestamptz NOT NULL,
    created_by  text        NOT NULL,
    updated_at  timestamptz NOT NULL,
    updated_by  text        NOT NULL,
    CONSTRAINT class_path_ends_with_code CHECK (path LIKE '%/' || code || '/'),
    CONSTRAINT class_root_has_no_parent CHECK ((depth = 1) = (parent_id IS NULL))
);
CREATE UNIQUE INDEX class_node_code_uq ON cfg.class_node (lower(code));
CREATE INDEX class_node_path_idx ON cfg.class_node (path text_pattern_ops);
CREATE INDEX class_node_parent_idx ON cfg.class_node (parent_id, sort_order);

CREATE TABLE cfg.class_attribute (
    node_id     uuid        NOT NULL REFERENCES cfg.class_node (id),
    name        text        NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label       text        NOT NULL,
    data_type   text        NOT NULL CHECK (data_type IN ('string', 'number', 'boolean', 'enum')),
    unit        text,
    enum_values text[],
    required    boolean     NOT NULL DEFAULT false,
    max_length  int,
    sort_order  int         NOT NULL DEFAULT 0,
    help_text   text,
    grp         text,
    pattern     text,
    min_value   numeric,
    max_value   numeric,
    retired     boolean     NOT NULL DEFAULT false,
    revision    int         NOT NULL DEFAULT 1,
    updated_at  timestamptz,
    updated_by  text,
    PRIMARY KEY (node_id, name),
    CHECK ((data_type = 'enum') = (enum_values IS NOT NULL)),
    CHECK (min_value IS NULL OR max_value IS NULL OR min_value <= max_value),
    CHECK ((pattern IS NULL OR data_type = 'string') AND ((min_value IS NULL AND max_value IS NULL) OR data_type = 'number'))
);

-- The class of a part is iteration-owned data (ADR-0002, ADR-0022): frozen at check-in, copied forward on revise.
CREATE TABLE cfg.part_classification (
    iteration_id uuid  PRIMARY KEY REFERENCES plm.part_iteration (id),
    class_id     uuid  NOT NULL REFERENCES cfg.class_node (id),
    class_code   text  NOT NULL,
    attributes   jsonb NOT NULL DEFAULT '{}'::jsonb
);
CREATE INDEX part_classification_class_idx ON cfg.part_classification (class_id);

-- The check-out's private working copy; removed with the check-out.
CREATE TABLE cfg.part_checkout_classification (
    revision_id uuid  PRIMARY KEY REFERENCES plm.part_checkout (revision_id) ON DELETE CASCADE,
    class_id    uuid  NOT NULL REFERENCES cfg.class_node (id),
    attributes  jsonb NOT NULL DEFAULT '{}'::jsonb
);

-- One typed row per classified attribute value of a frozen iteration: the facet search ranges over (attr, num) and equals over (attr, txt).
CREATE TABLE cfg.part_class_value (
    iteration_id uuid    NOT NULL REFERENCES plm.part_iteration (id),
    attr         text    NOT NULL,
    txt          text,
    num          numeric,
    flag         boolean,
    PRIMARY KEY (iteration_id, attr),
    CHECK (((txt IS NOT NULL)::int + (num IS NOT NULL)::int + (flag IS NOT NULL)::int) = 1)
);
CREATE INDEX part_class_value_num_idx ON cfg.part_class_value (attr, num) INCLUDE (iteration_id) WHERE num IS NOT NULL;
CREATE INDEX part_class_value_txt_idx ON cfg.part_class_value (attr, txt) INCLUDE (iteration_id) WHERE txt IS NOT NULL;
CREATE INDEX part_class_value_flag_idx ON cfg.part_class_value (attr, flag) INCLUDE (iteration_id) WHERE flag IS NOT NULL;

CREATE FUNCTION cfg.reject_classification_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'classified iteration data is immutable (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER part_classification_immutable BEFORE UPDATE OR DELETE ON cfg.part_classification FOR EACH ROW EXECUTE FUNCTION cfg.reject_classification_change();
CREATE TRIGGER part_class_value_immutable BEFORE UPDATE OR DELETE ON cfg.part_class_value FOR EACH ROW EXECUTE FUNCTION cfg.reject_classification_change();

-- A node is never deleted (classified history points at it); retire it. Identity columns never change.
CREATE FUNCTION cfg.guard_class_node() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'classification nodes are never deleted; retire them' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NEW.id <> OLD.id OR NEW.code <> OLD.code OR NEW.created_at <> OLD.created_at THEN
        RAISE EXCEPTION 'classification node identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER class_node_guard BEFORE UPDATE OR DELETE ON cfg.class_node FOR EACH ROW EXECUTE FUNCTION cfg.guard_class_node();

-- ---------------------------------------------------------------- lifecycle usage and object migration (aggregates only)
-- The admin UI must tell an administrator how many objects sit in each state before a lifecycle changes. These functions return counts
-- and never marked columns; they run with the caller's privileges (SECURITY INVOKER), so they are not a way around the read filter for rows.
CREATE FUNCTION cfg.lifecycle_usage(p_object_type text)
RETURNS TABLE (lifecycle_id text, state text, revisions bigint, checked_out bigint) LANGUAGE plpgsql STABLE AS $$
BEGIN
    IF p_object_type = 'part' THEN
        RETURN QUERY SELECT m.lifecycle_id, r.lifecycle_state, count(*), count(c.revision_id)
            FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id LEFT JOIN plm.part_checkout c ON c.revision_id = r.id
            GROUP BY m.lifecycle_id, r.lifecycle_state;
    ELSIF p_object_type = 'document' THEN
        RETURN QUERY SELECT m.lifecycle_id, r.lifecycle_state, count(*), count(c.revision_id)
            FROM plm.document_master m JOIN plm.document_revision r ON r.master_id = m.id LEFT JOIN plm.document_checkout c ON c.revision_id = r.id
            GROUP BY m.lifecycle_id, r.lifecycle_state;
    ELSE
        RAISE EXCEPTION 'lifecycle usage is not available for object type %', p_object_type;
    END IF;
END;
$$;

CREATE FUNCTION cfg.lifecycle_active_reviews(p_object_type text, p_lifecycle_id text) RETURNS bigint LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_count bigint;
BEGIN
    IF p_object_type = 'part' THEN
        SELECT count(*) INTO v_count FROM wf.instance i JOIN plm.part_master m ON m.id = i.object_id
        WHERE i.object_type = 'part' AND i.status = 'Active' AND m.lifecycle_id = p_lifecycle_id;
    ELSIF p_object_type = 'document' THEN
        SELECT count(*) INTO v_count FROM wf.instance i JOIN plm.document_master m ON m.id = i.object_id
        WHERE i.object_type = 'document' AND i.status = 'Active' AND m.lifecycle_id = p_lifecycle_id;
    ELSE
        v_count := 0;
    END IF;
    RETURN v_count;
END;
$$;

-- Moves every object of one lifecycle version to another, mapping states (p_map_from[i] becomes p_map_to[i]). The caller validates the
-- mapping; the function refuses an unmapped state so a partial mapping can never strand an object. Returns the number of revisions moved.
CREATE FUNCTION cfg.migrate_lifecycle_objects(p_object_type text, p_from text, p_to text, p_map_from text[], p_map_to text[])
RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE
    v_moved bigint;
BEGIN
    IF cardinality(p_map_from) <> cardinality(p_map_to) THEN RAISE EXCEPTION 'state mapping is malformed'; END IF;
    IF p_object_type = 'part' THEN
        IF EXISTS (SELECT 1 FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id
                   WHERE m.lifecycle_id = p_from AND r.lifecycle_state <> ALL (p_map_from)) THEN
            RAISE EXCEPTION 'a state in use is not mapped';
        END IF;
        UPDATE plm.part_revision r SET lifecycle_state = map.to_state
            FROM plm.part_master m, unnest(p_map_from, p_map_to) AS map(from_state, to_state)
            WHERE m.id = r.master_id AND m.lifecycle_id = p_from AND r.lifecycle_state = map.from_state AND map.to_state <> map.from_state;
        GET DIAGNOSTICS v_moved = ROW_COUNT;
        UPDATE plm.part_master SET lifecycle_id = p_to WHERE lifecycle_id = p_from;
    ELSIF p_object_type = 'document' THEN
        IF EXISTS (SELECT 1 FROM plm.document_master m JOIN plm.document_revision r ON r.master_id = m.id
                   WHERE m.lifecycle_id = p_from AND r.lifecycle_state <> ALL (p_map_from)) THEN
            RAISE EXCEPTION 'a state in use is not mapped';
        END IF;
        UPDATE plm.document_revision r SET lifecycle_state = map.to_state
            FROM plm.document_master m, unnest(p_map_from, p_map_to) AS map(from_state, to_state)
            WHERE m.id = r.master_id AND m.lifecycle_id = p_from AND r.lifecycle_state = map.from_state AND map.to_state <> map.from_state;
        GET DIAGNOSTICS v_moved = ROW_COUNT;
        UPDATE plm.document_master SET lifecycle_id = p_to WHERE lifecycle_id = p_from;
    ELSE
        RAISE EXCEPTION 'objects of type % cannot be migrated', p_object_type;
    END IF;
    RETURN v_moved;
END;
$$;

-- ---------------------------------------------------------------- privileges (least privilege for keel_app)
GRANT USAGE ON SCHEMA cfg TO keel_app;
GRANT SELECT, INSERT, UPDATE ON cfg.ref_item, cfg.numbering, cfg.class_node, cfg.class_attribute TO keel_app;
GRANT SELECT, INSERT ON cfg.part_classification, cfg.part_class_value TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON cfg.part_checkout_classification TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON wf.template_pin TO keel_app;
GRANT INSERT, UPDATE ON wf.reason_code, chg.reason_code TO keel_app;
GRANT SELECT ON plm.lifecycle_family TO keel_app;
GRANT INSERT, UPDATE ON plm.attribute_definition, plm.lifecycle, plm.lifecycle_state, plm.lifecycle_transition, plm.lifecycle_family TO keel_app;
-- Next-number preview reads the sequence; "advance" sets it (forward only, checked by the service).
GRANT SELECT, UPDATE ON SEQUENCE plm.part_number_seq, plm.document_number_seq, chg.pr_seq, chg.ecr_seq, chg.ecn_seq, chg.ct_seq TO keel_app;
GRANT EXECUTE ON FUNCTION cfg.lifecycle_usage(text), cfg.lifecycle_active_reviews(text, text), cfg.migrate_lifecycle_objects(text, text, text, text[], text[]) TO keel_app;
