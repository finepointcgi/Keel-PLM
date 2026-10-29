-- Increment 8.4: configuration and variants (ADR-0039, docs/increments/8.4-variants.md). Forward-only, runs as keel_migrator.
--   * option families and values per product, option rules (requires / excludes / mutually exclusive) as data;
--   * named variants (a chosen value per option family) with a small lifecycle (Draft, Released, Retired);
--   * option usage conditions on BOM lines (EBOM working and stored rows) as a parsed tree plus canonical text, and the database function that evaluates the tree.
-- Marking model: an option model belongs to the product (a part master) and is readable and changeable exactly as that part is. Nothing here carries
-- marking columns of its own, so a change of the product's markings applies to its options, rules and variants at once.
CREATE SCHEMA var;
GRANT USAGE ON SCHEMA var TO keel_app;

-- ================================================================ the model of one product
CREATE TABLE var.model (
    product_master_id uuid        PRIMARY KEY REFERENCES plm.part_master (id),
    version           int         NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_at        timestamptz NOT NULL,
    created_by        text        NOT NULL,
    updated_at        timestamptz NOT NULL,
    updated_by        text        NOT NULL
);

CREATE TABLE var.option_family (
    id                uuid        PRIMARY KEY,
    product_master_id uuid        NOT NULL REFERENCES var.model (product_master_id),
    key               text        NOT NULL CHECK (key ~ '^[A-Za-z0-9][A-Za-z0-9_.-]{0,39}$' AND lower(key) NOT IN ('and', 'or', 'not', 'in')),
    name              text        NOT NULL CHECK (length(name) BETWEEN 1 AND 120),
    description       text        CHECK (description IS NULL OR length(description) <= 500),
    -- A required family must have a value in every valid configuration; an optional one may be left unset.
    required          boolean     NOT NULL DEFAULT true,
    sort_order        int         NOT NULL DEFAULT 0,
    retired           boolean     NOT NULL DEFAULT false,
    created_at        timestamptz NOT NULL,
    created_by        text        NOT NULL,
    UNIQUE (product_master_id, key),
    UNIQUE (id, product_master_id)
);

CREATE TABLE var.option_value (
    id          uuid        PRIMARY KEY,
    family_id   uuid        NOT NULL REFERENCES var.option_family (id),
    key         text        NOT NULL CHECK (key ~ '^[A-Za-z0-9][A-Za-z0-9_.-]{0,39}$' AND lower(key) NOT IN ('and', 'or', 'not', 'in')),
    name        text        NOT NULL CHECK (length(name) BETWEEN 1 AND 120),
    description text        CHECK (description IS NULL OR length(description) <= 500),
    sort_order  int         NOT NULL DEFAULT 0,
    retired     boolean     NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL,
    created_by  text        NOT NULL,
    UNIQUE (family_id, key),
    UNIQUE (family_id, id)
);

-- Rules as data. requires: the subject option needs ALL listed options. excludes: the subject option cannot be combined with ANY listed option.
-- mutex: at most one of the listed options (two or more) may be chosen. Options are named by key so a rule survives a rename.
CREATE TABLE var.option_rule (
    id                uuid        PRIMARY KEY,
    product_master_id uuid        NOT NULL REFERENCES var.model (product_master_id),
    kind              text        NOT NULL CHECK (kind IN ('requires', 'excludes', 'mutex')),
    subject_family    text,
    subject_value     text,
    members           jsonb       NOT NULL CHECK (jsonb_typeof(members) = 'array' AND jsonb_array_length(members) BETWEEN 1 AND 40),
    reason            text        CHECK (reason IS NULL OR length(reason) <= 500),
    created_at        timestamptz NOT NULL,
    created_by        text        NOT NULL,
    CONSTRAINT option_rule_shape CHECK ((kind = 'mutex') = (subject_family IS NULL) AND (subject_family IS NULL) = (subject_value IS NULL)
                                        AND (kind <> 'mutex' OR jsonb_array_length(members) >= 2))
);
CREATE INDEX option_rule_product_idx ON var.option_rule (product_master_id);

-- ================================================================ named variants
CREATE TABLE var.variant (
    id                uuid        PRIMARY KEY,
    product_master_id uuid        NOT NULL REFERENCES var.model (product_master_id),
    name              text        NOT NULL CHECK (length(name) BETWEEN 1 AND 80),
    description       text        CHECK (description IS NULL OR length(description) <= 500),
    state             text        NOT NULL DEFAULT 'Draft' CHECK (state IN ('Draft', 'Released', 'Retired')),
    created_at        timestamptz NOT NULL,
    created_by        text        NOT NULL,
    released_at       timestamptz,
    released_by       text,
    CONSTRAINT variant_released_shape CHECK ((state = 'Draft') OR released_at IS NOT NULL)
);
CREATE UNIQUE INDEX variant_name_idx ON var.variant (product_master_id, lower(name));

CREATE TABLE var.variant_selection (
    variant_id uuid NOT NULL REFERENCES var.variant (id),
    family_id  uuid NOT NULL,
    value_id   uuid NOT NULL,
    PRIMARY KEY (variant_id, family_id),
    FOREIGN KEY (family_id, value_id) REFERENCES var.option_value (family_id, id)
);
CREATE INDEX variant_selection_value_idx ON var.variant_selection (value_id);

-- A released or retired variant is a frozen definition: its choices, and the options it uses, cannot change (release lock).
CREATE FUNCTION var.guard_variant_selection() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_state text;
    v_id    uuid := COALESCE(NEW.variant_id, OLD.variant_id);
BEGIN
    SELECT state INTO v_state FROM var.variant WHERE id = v_id;
    IF v_state IS DISTINCT FROM 'Draft' THEN
        RAISE EXCEPTION 'the choices of a released variant cannot change' USING ERRCODE = 'check_violation';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER variant_selection_lock BEFORE INSERT OR UPDATE OR DELETE ON var.variant_selection
    FOR EACH ROW EXECUTE FUNCTION var.guard_variant_selection();

CREATE FUNCTION var.guard_variant_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.state <> 'Draft' AND (NEW.name <> OLD.name OR NEW.product_master_id <> OLD.product_master_id) THEN
        RAISE EXCEPTION 'a released variant cannot be renamed or moved' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.state = 'Retired' AND NEW.state <> 'Retired' THEN
        RAISE EXCEPTION 'a retired variant stays retired' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.state = 'Released' AND NEW.state = 'Draft' THEN
        RAISE EXCEPTION 'a released variant cannot go back to draft' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER variant_update_guard BEFORE UPDATE ON var.variant FOR EACH ROW EXECUTE FUNCTION var.guard_variant_update();

CREATE FUNCTION var.reject_variant_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.state <> 'Draft' THEN
        RAISE EXCEPTION 'a released variant cannot be deleted; retire it' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN OLD;
END;
$$;
CREATE TRIGGER variant_delete_guard BEFORE DELETE ON var.variant FOR EACH ROW EXECUTE FUNCTION var.reject_variant_delete();

-- ================================================================ usage conditions on BOM lines
-- opt_cond is the canonical text and opt_ast the parsed tree of one condition (both null: the line is used in every variant). The tree is the only form ever
-- evaluated. Stored rows of an iteration are already immutable and locked with the revision, so a condition on a released iteration changes only by revising.
ALTER TABLE plm.bom_usage
    ADD COLUMN opt_cond text,
    ADD COLUMN opt_ast  jsonb,
    ADD CONSTRAINT bom_usage_opt_shape CHECK ((opt_cond IS NULL) = (opt_ast IS NULL) AND (opt_cond IS NULL OR length(opt_cond) <= 500));
ALTER TABLE plm.bom_working_usage
    ADD COLUMN opt_cond text,
    ADD COLUMN opt_ast  jsonb,
    ADD CONSTRAINT bom_working_opt_shape CHECK ((opt_cond IS NULL) = (opt_ast IS NULL) AND (opt_cond IS NULL OR length(opt_cond) <= 500));
CREATE INDEX bom_usage_opt_idx ON plm.bom_usage (parent_iteration_id) WHERE opt_ast IS NOT NULL;

-- Evaluates a stored condition tree for a selection {"Family": "Value", ...}. Null tree: used. An option that is not selected never matches a value.
-- IMMUTABLE, no dynamic SQL: the tree is data and is walked, never executed.
CREATE FUNCTION var.eval_cond(ast jsonb, sel jsonb) RETURNS boolean LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    v_op  text;
    v_arg jsonb;
BEGIN
    IF ast IS NULL THEN RETURN true; END IF;
    v_op := ast ->> 'op';
    IF v_op = 'eq' THEN
        RETURN COALESCE(sel ->> (ast ->> 'family') = (ast ->> 'value'), false);
    ELSIF v_op = 'not' THEN
        RETURN NOT var.eval_cond(ast -> 'arg', sel);
    ELSIF v_op = 'and' THEN
        FOR v_arg IN SELECT jsonb_array_elements(ast -> 'args') LOOP
            IF NOT var.eval_cond(v_arg, sel) THEN RETURN false; END IF;
        END LOOP;
        RETURN true;
    ELSIF v_op = 'or' THEN
        FOR v_arg IN SELECT jsonb_array_elements(ast -> 'args') LOOP
            IF var.eval_cond(v_arg, sel) THEN RETURN true; END IF;
        END LOOP;
        RETURN false;
    END IF;
    RAISE EXCEPTION 'unknown condition node' USING ERRCODE = 'data_exception';
END;
$$;
GRANT EXECUTE ON FUNCTION var.eval_cond(jsonb, jsonb) TO keel_app;

GRANT SELECT, INSERT, UPDATE ON var.model, var.variant TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON var.option_family, var.option_value, var.option_rule TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON var.variant_selection TO keel_app;
GRANT DELETE ON var.variant TO keel_app;
