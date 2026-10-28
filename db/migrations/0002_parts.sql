-- Increment 1.2: parts with master / revision / iteration (ADR-0002), configurable lifecycles,
-- admin-defined attributes, check-out/check-in and release locking.
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE SCHEMA plm;

-- ---------------------------------------------------------------- lifecycle definitions (data, not code)
CREATE TABLE plm.lifecycle (
    id          text PRIMARY KEY,
    name        text NOT NULL
);

CREATE TABLE plm.lifecycle_state (
    lifecycle_id text    NOT NULL REFERENCES plm.lifecycle (id),
    state        text    NOT NULL,
    label        text    NOT NULL,
    sort_order   int     NOT NULL,
    is_initial   boolean NOT NULL DEFAULT false,
    -- Locked states reject check-out, check-in and content edits (release locking).
    is_locked    boolean NOT NULL DEFAULT false,
    PRIMARY KEY (lifecycle_id, state)
);
CREATE UNIQUE INDEX lifecycle_one_initial ON plm.lifecycle_state (lifecycle_id) WHERE is_initial;

-- How a transition may be taken: 'manual' (any user allowed to modify the object),
-- 'workflow' (only by a review workflow, increment 1.5), 'change' (only by a change notice, increment 1.6).
CREATE TABLE plm.lifecycle_transition (
    lifecycle_id text NOT NULL,
    from_state   text NOT NULL,
    to_state     text NOT NULL,
    via          text NOT NULL CHECK (via IN ('manual', 'workflow', 'change')),
    PRIMARY KEY (lifecycle_id, from_state, to_state),
    FOREIGN KEY (lifecycle_id, from_state) REFERENCES plm.lifecycle_state (lifecycle_id, state),
    FOREIGN KEY (lifecycle_id, to_state) REFERENCES plm.lifecycle_state (lifecycle_id, state)
);

INSERT INTO plm.lifecycle VALUES ('part-default', 'Standard part lifecycle');
INSERT INTO plm.lifecycle_state VALUES
    ('part-default', 'InWork',      'In Work',      10, true,  false),
    ('part-default', 'UnderReview', 'Under Review', 20, false, true),
    ('part-default', 'Released',    'Released',     30, false, true),
    ('part-default', 'Obsolete',    'Obsolete',     40, false, true);
INSERT INTO plm.lifecycle_transition VALUES
    ('part-default', 'InWork',      'UnderReview', 'manual'),
    ('part-default', 'UnderReview', 'InWork',      'manual'),
    ('part-default', 'UnderReview', 'Released',    'workflow'),
    ('part-default', 'Released',    'Obsolete',    'change');

-- ---------------------------------------------------------------- admin-defined attributes
CREATE TABLE plm.attribute_definition (
    object_type  text    NOT NULL,
    name         text    NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label        text    NOT NULL,
    data_type    text    NOT NULL CHECK (data_type IN ('string', 'number', 'boolean', 'enum')),
    unit         text,
    enum_values  text[],
    required     boolean NOT NULL DEFAULT false,
    max_length   int,
    sort_order   int     NOT NULL DEFAULT 0,
    PRIMARY KEY (object_type, name),
    CHECK ((data_type = 'enum') = (enum_values IS NOT NULL))
);

INSERT INTO plm.attribute_definition (object_type, name, label, data_type, unit, enum_values, required, max_length, sort_order) VALUES
    ('part', 'material',      'Material',         'string',  NULL, NULL, false, 120, 10),
    ('part', 'finish',        'Finish',           'string',  NULL, NULL, false, 120, 20),
    ('part', 'mass',          'Mass',             'number',  'kg', NULL, false, NULL, 30),
    ('part', 'cage_code',     'CAGE code',        'string',  NULL, NULL, false, 5,   40),
    ('part', 'critical_item', 'Critical item',    'boolean', NULL, NULL, false, NULL, 50),
    ('part', 'criticality',   'Criticality',      'enum',    NULL, ARRAY['Standard', 'Major', 'Critical safety'], false, NULL, 60);

-- ---------------------------------------------------------------- parts
CREATE SEQUENCE plm.part_number_seq START 1;

CREATE TABLE plm.part_master (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE,
    name                   text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    part_type              text        NOT NULL CHECK (part_type IN ('Assembly', 'Component', 'Standard', 'RawMaterial')),
    default_unit           text        NOT NULL,
    source                 text        NOT NULL CHECK (source IN ('Make', 'Buy')),
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
CREATE INDEX part_master_search_idx ON plm.part_master USING gin (search);
CREATE INDEX part_master_number_trgm ON plm.part_master USING gin (number gin_trgm_ops);
CREATE INDEX part_master_name_trgm ON plm.part_master USING gin (name gin_trgm_ops);
CREATE INDEX part_master_program_idx ON plm.part_master (program);

CREATE TABLE plm.part_revision (
    id               uuid        PRIMARY KEY,
    master_id        uuid        NOT NULL REFERENCES plm.part_master (id),
    revision         text        NOT NULL,
    lifecycle_state  text        NOT NULL,
    is_latest        boolean     NOT NULL,
    latest_iteration int         NOT NULL CHECK (latest_iteration >= 1),
    created_at       timestamptz NOT NULL,
    created_by       text        NOT NULL,
    state_changed_at timestamptz NOT NULL,
    state_changed_by text        NOT NULL,
    UNIQUE (master_id, revision)
);
CREATE UNIQUE INDEX part_revision_one_latest ON plm.part_revision (master_id) WHERE is_latest;
CREATE INDEX part_revision_state_idx ON plm.part_revision (lifecycle_state) WHERE is_latest;

-- Iterations are immutable once written (no UPDATE/DELETE, enforced below).
CREATE TABLE plm.part_iteration (
    id           uuid        PRIMARY KEY,
    revision_id  uuid        NOT NULL REFERENCES plm.part_revision (id),
    iteration    int         NOT NULL CHECK (iteration >= 1),
    description  text        NOT NULL DEFAULT '',
    attributes   jsonb       NOT NULL DEFAULT '{}'::jsonb,
    comment      text,
    created_at   timestamptz NOT NULL,
    created_by   text        NOT NULL,
    UNIQUE (revision_id, iteration)
);

-- At most one check-out per revision (primary key). Holds the private working copy until check-in.
CREATE TABLE plm.part_checkout (
    revision_id    uuid        PRIMARY KEY REFERENCES plm.part_revision (id),
    user_id        text        NOT NULL,
    user_name      text,
    checked_out_at timestamptz NOT NULL,
    description    text        NOT NULL,
    attributes     jsonb       NOT NULL
);

-- ---------------------------------------------------------------- release locking (defence in depth, ADR-0002)
CREATE FUNCTION plm.is_locked_state(p_master uuid, p_state text) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT s.is_locked FROM plm.part_master m
    JOIN plm.lifecycle_state s ON s.lifecycle_id = m.lifecycle_id AND s.state = p_state
    WHERE m.id = p_master;
$$;

CREATE FUNCTION plm.reject_iteration_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'part iterations are immutable' USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER part_iteration_immutable BEFORE UPDATE OR DELETE ON plm.part_iteration
    FOR EACH ROW EXECUTE FUNCTION plm.reject_iteration_change();

CREATE FUNCTION plm.guard_locked_revision() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_master uuid;
    v_state  text;
BEGIN
    SELECT master_id, lifecycle_state INTO v_master, v_state FROM plm.part_revision WHERE id = NEW.revision_id;
    IF plm.is_locked_state(v_master, v_state) THEN
        RAISE EXCEPTION 'revision is in locked state % (%)', v_state, TG_TABLE_NAME USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER part_iteration_locked BEFORE INSERT ON plm.part_iteration
    FOR EACH ROW EXECUTE FUNCTION plm.guard_locked_revision();
CREATE TRIGGER part_checkout_locked BEFORE INSERT OR UPDATE ON plm.part_checkout
    FOR EACH ROW EXECUTE FUNCTION plm.guard_locked_revision();

-- A revision in a locked state may only change lifecycle state (transition) or lose is_latest (superseded by a
-- new revision). Content pointers are frozen.
CREATE FUNCTION plm.guard_revision_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF plm.is_locked_state(OLD.master_id, OLD.lifecycle_state)
       AND (NEW.latest_iteration <> OLD.latest_iteration OR NEW.revision <> OLD.revision OR NEW.master_id <> OLD.master_id) THEN
        RAISE EXCEPTION 'revision % is locked in state %', OLD.revision, OLD.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER part_revision_locked BEFORE UPDATE ON plm.part_revision
    FOR EACH ROW EXECUTE FUNCTION plm.guard_revision_update();
-- Parts are never deleted; they are obsoleted through change control.
CREATE FUNCTION plm.reject_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows cannot be deleted; obsolete them through change control', TG_TABLE_NAME
        USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER part_revision_no_delete BEFORE DELETE ON plm.part_revision
    FOR EACH ROW EXECUTE FUNCTION plm.reject_delete();
CREATE TRIGGER part_master_no_delete BEFORE DELETE ON plm.part_master
    FOR EACH ROW EXECUTE FUNCTION plm.reject_delete();

-- Identity of a master never changes.
CREATE FUNCTION plm.guard_master_identity() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id <> OLD.id OR NEW.number <> OLD.number OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
        RAISE EXCEPTION 'part identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER part_master_identity BEFORE UPDATE ON plm.part_master
    FOR EACH ROW EXECUTE FUNCTION plm.guard_master_identity();

GRANT USAGE ON SCHEMA plm TO keel_app;
GRANT SELECT ON plm.lifecycle, plm.lifecycle_state, plm.lifecycle_transition, plm.attribute_definition TO keel_app;
GRANT SELECT, INSERT, UPDATE ON plm.part_master, plm.part_revision TO keel_app;
GRANT SELECT, INSERT ON plm.part_iteration TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON plm.part_checkout TO keel_app;
GRANT USAGE ON SEQUENCE plm.part_number_seq TO keel_app;
