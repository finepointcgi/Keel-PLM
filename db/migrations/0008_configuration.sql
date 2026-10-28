-- Increment 2.1: configuration management (ADR-0021). Baselines (immutable snapshots of an exploded structure) and effectivity
-- (serial / lot / date applicability of BOM lines and of part revisions). Forward-only; nothing here is ever deleted.
CREATE SCHEMA cfg;
GRANT USAGE ON SCHEMA cfg TO keel_app;

-- ================================================================ effectivity
-- An end-item context ("ORCA-POD") gives serial numbers and lots a meaning: serial 100 of what? Contexts are data. A context can be
-- restricted to a program so its name is not shown to readers outside it (the name of a program is itself a marking).
CREATE TABLE cfg.effectivity_context (
    id          text        PRIMARY KEY CHECK (id ~ '^[A-Z][A-Z0-9-]{1,39}$'),
    name        text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    description text        NOT NULL DEFAULT '' CHECK (length(description) <= 2000),
    program     text,
    created_at  timestamptz NOT NULL,
    created_by  text        NOT NULL
);

-- Named blocks map to integers inside a context ("Block 2" is lot 2), so lot ranges stay plain integer ranges.
CREATE TABLE cfg.effectivity_block (
    context_id text NOT NULL REFERENCES cfg.effectivity_context (id),
    name       text NOT NULL CHECK (name ~ '^[A-Za-z0-9][A-Za-z0-9 ._-]{0,39}$'),
    number     int  NOT NULL CHECK (number >= 1),
    PRIMARY KEY (context_id, name),
    UNIQUE (context_id, number)
);

CREATE FUNCTION cfg.reject_modification() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'configuration records are never modified or deleted (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER effectivity_context_immutable BEFORE UPDATE OR DELETE ON cfg.effectivity_context FOR EACH ROW EXECUTE FUNCTION cfg.reject_modification();
CREATE TRIGGER effectivity_block_immutable BEFORE UPDATE OR DELETE ON cfg.effectivity_block FOR EACH ROW EXECUTE FUNCTION cfg.reject_modification();

-- The shape of a stored range. serial and lot need a context and a positive integer range; date has no context and stores UTC day numbers
-- (days since 1970-01-01). At least one end is given (a range open on both ends is "no effectivity" and is stored as NULLs instead).
CREATE FUNCTION cfg.effectivity_ok(p_kind text, p_context text, p_from bigint, p_to bigint) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE
        WHEN p_kind IS NULL THEN p_context IS NULL AND p_from IS NULL AND p_to IS NULL
        WHEN p_kind IN ('serial', 'lot') THEN p_context IS NOT NULL AND (p_from IS NOT NULL OR p_to IS NOT NULL)
             AND COALESCE(p_from, 1) >= 1 AND COALESCE(p_to, 1) >= 1 AND (p_from IS NULL OR p_to IS NULL OR p_from <= p_to)
        WHEN p_kind = 'date' THEN p_context IS NULL AND (p_from IS NOT NULL OR p_to IS NOT NULL) AND (p_from IS NULL OR p_to IS NULL OR p_from <= p_to)
        ELSE false
    END;
$$;

-- Two lines with the same find number are alternatives; they must be told apart by effectivity. They conflict when either has no effectivity,
-- when they use different dimensions or contexts (ambiguous), or when their ranges share a value.
CREATE FUNCTION cfg.ranges_conflict(k1 text, c1 text, f1 bigint, t1 bigint, k2 text, c2 text, f2 bigint, t2 bigint) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$
    SELECT k1 IS NULL OR k2 IS NULL OR k1 <> k2 OR c1 IS DISTINCT FROM c2
        OR (COALESCE(f1, -9223372036854775807) <= COALESCE(t2, 9223372036854775807) AND COALESCE(f2, -9223372036854775807) <= COALESCE(t1, 9223372036854775807));
$$;

ALTER TABLE plm.bom_usage
    ADD COLUMN eff_kind    text,
    ADD COLUMN eff_context text REFERENCES cfg.effectivity_context (id),
    ADD COLUMN eff_from    bigint,
    ADD COLUMN eff_to      bigint,
    -- Ordinal among the alternatives that share a find number in one parent iteration (0 for the first); makes line paths unique.
    ADD COLUMN alt         int NOT NULL DEFAULT 0 CHECK (alt >= 0),
    ADD CONSTRAINT bom_usage_effectivity_shape CHECK (cfg.effectivity_ok(eff_kind, eff_context, eff_from, eff_to));
ALTER TABLE plm.bom_usage DROP CONSTRAINT bom_usage_parent_iteration_id_find_number_key;
ALTER TABLE plm.bom_usage ADD CONSTRAINT bom_usage_find_alt_key UNIQUE (parent_iteration_id, find_number, alt);

ALTER TABLE plm.bom_working_usage
    ADD COLUMN eff_kind    text,
    ADD COLUMN eff_context text REFERENCES cfg.effectivity_context (id),
    ADD COLUMN eff_from    bigint,
    ADD COLUMN eff_to      bigint,
    ADD CONSTRAINT bom_working_effectivity_shape CHECK (cfg.effectivity_ok(eff_kind, eff_context, eff_from, eff_to));
ALTER TABLE plm.bom_working_usage DROP CONSTRAINT bom_working_find_unique;
CREATE INDEX bom_working_find_idx ON plm.bom_working_usage (checkout_revision_id, find_number);

-- Immutable rows: assign the alternate ordinal and refuse overlapping alternatives of one find number. A volatile trigger sees the rows the
-- same INSERT ... SELECT wrote before it, so a copy at check-in is validated line by line.
CREATE FUNCTION cfg.guard_usage_alternatives() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_alt int;
BEGIN
    SELECT count(*) INTO v_alt FROM plm.bom_usage WHERE parent_iteration_id = NEW.parent_iteration_id AND find_number = NEW.find_number;
    IF v_alt > 0 AND EXISTS (
        SELECT 1 FROM plm.bom_usage u WHERE u.parent_iteration_id = NEW.parent_iteration_id AND u.find_number = NEW.find_number
          AND cfg.ranges_conflict(u.eff_kind, u.eff_context, u.eff_from, u.eff_to, NEW.eff_kind, NEW.eff_context, NEW.eff_from, NEW.eff_to)) THEN
        RAISE EXCEPTION 'find number % already has a line whose effectivity overlaps this one', NEW.find_number USING ERRCODE = 'unique_violation';
    END IF;
    NEW.alt := v_alt;
    RETURN NEW;
END;
$$;
CREATE TRIGGER bom_usage_alternatives BEFORE INSERT ON plm.bom_usage FOR EACH ROW EXECUTE FUNCTION cfg.guard_usage_alternatives();

-- Working copy: checked at the end of the statement (a renumber swaps find numbers inside one UPDATE).
CREATE FUNCTION cfg.guard_working_alternatives() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM plm.bom_working_usage u WHERE u.checkout_revision_id = NEW.checkout_revision_id AND u.find_number = NEW.find_number AND u.id <> NEW.id
          AND cfg.ranges_conflict(u.eff_kind, u.eff_context, u.eff_from, u.eff_to, NEW.eff_kind, NEW.eff_context, NEW.eff_from, NEW.eff_to)) THEN
        RAISE EXCEPTION 'find number % already has a line whose effectivity overlaps this one', NEW.find_number USING ERRCODE = 'unique_violation';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER bom_working_alternatives AFTER INSERT OR UPDATE ON plm.bom_working_usage
    DEFERRABLE INITIALLY IMMEDIATE FOR EACH ROW EXECUTE FUNCTION cfg.guard_working_alternatives();

-- Revision effectivity: which units a Released revision serves. A revision without a row serves everything not claimed by another revision.
CREATE TABLE cfg.revision_effectivity (
    revision_id   uuid        PRIMARY KEY REFERENCES plm.part_revision (id),
    master_id     uuid        NOT NULL REFERENCES plm.part_master (id),
    eff_kind      text        NOT NULL,
    eff_context   text        REFERENCES cfg.effectivity_context (id),
    eff_from      bigint,
    eff_to        bigint,
    source_change text,
    set_at        timestamptz NOT NULL,
    set_by        text        NOT NULL,
    CONSTRAINT revision_effectivity_shape CHECK (cfg.effectivity_ok(eff_kind, eff_context, eff_from, eff_to))
);
CREATE INDEX revision_effectivity_master_idx ON cfg.revision_effectivity (master_id);

CREATE TABLE cfg.revision_effectivity_history (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    revision_id   uuid        NOT NULL,
    master_id     uuid        NOT NULL,
    eff_kind      text        NOT NULL,
    eff_context   text,
    eff_from      bigint,
    eff_to        bigint,
    source_change text,
    set_at        timestamptz NOT NULL,
    set_by        text        NOT NULL
);
CREATE INDEX revision_effectivity_history_idx ON cfg.revision_effectivity_history (revision_id, id);
CREATE TRIGGER revision_effectivity_history_immutable BEFORE UPDATE OR DELETE ON cfg.revision_effectivity_history FOR EACH ROW EXECUTE FUNCTION cfg.reject_modification();
CREATE TRIGGER revision_effectivity_history_no_truncate BEFORE TRUNCATE ON cfg.revision_effectivity_history FOR EACH STATEMENT EXECUTE FUNCTION cfg.reject_modification();
CREATE FUNCTION cfg.record_revision_effectivity() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO cfg.revision_effectivity_history (revision_id, master_id, eff_kind, eff_context, eff_from, eff_to, source_change, set_at, set_by)
    VALUES (NEW.revision_id, NEW.master_id, NEW.eff_kind, NEW.eff_context, NEW.eff_from, NEW.eff_to, NEW.source_change, NEW.set_at, NEW.set_by);
    RETURN NULL;
END;
$$;
CREATE TRIGGER revision_effectivity_record AFTER INSERT OR UPDATE ON cfg.revision_effectivity FOR EACH ROW EXECUTE FUNCTION cfg.record_revision_effectivity();
CREATE TRIGGER revision_effectivity_no_delete BEFORE DELETE ON cfg.revision_effectivity FOR EACH ROW EXECUTE FUNCTION cfg.reject_modification();

-- Effectivity a change notice's affected object carries ("revision B is effective from serial 101"). Always open-ended upward: serials
-- above the range would otherwise have no revision. Editable while the notice is a draft only.
CREATE TABLE cfg.change_effectivity (
    affected_object_id uuid        PRIMARY KEY REFERENCES chg.affected_object (id),
    eff_kind           text        NOT NULL,
    eff_context        text        REFERENCES cfg.effectivity_context (id),
    eff_from           bigint      NOT NULL,
    eff_to             bigint,
    created_at         timestamptz NOT NULL,
    created_by         text        NOT NULL,
    CONSTRAINT change_effectivity_shape CHECK (cfg.effectivity_ok(eff_kind, eff_context, eff_from, eff_to)),
    CONSTRAINT change_effectivity_open_ended CHECK (eff_to IS NULL)
);
CREATE FUNCTION cfg.guard_change_effectivity() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_id    uuid := CASE WHEN TG_OP = 'DELETE' THEN OLD.affected_object_id ELSE NEW.affected_object_id END;
    v_state text;
BEGIN
    SELECT c.lifecycle_state INTO v_state FROM chg.affected_object ao JOIN chg.change c ON c.id = ao.change_id WHERE ao.id = v_id;
    IF v_state IS DISTINCT FROM 'Draft' THEN
        RAISE EXCEPTION 'the effectivity of an affected object can only change while the change notice is a draft' USING ERRCODE = 'check_violation';
    END IF;
    RETURN COALESCE(NEW, OLD);
END;
$$;
CREATE TRIGGER change_effectivity_guard BEFORE INSERT OR UPDATE OR DELETE ON cfg.change_effectivity FOR EACH ROW EXECUTE FUNCTION cfg.guard_change_effectivity();

-- ================================================================ baselines
INSERT INTO plm.lifecycle VALUES ('baseline-default', 'Baseline lifecycle');
INSERT INTO plm.lifecycle_state (lifecycle_id, state, label, sort_order, is_initial, is_locked, is_final) VALUES
    ('baseline-default', 'Draft',       'Draft',        10, true,  false, false),
    ('baseline-default', 'UnderReview', 'Under review', 20, false, true,  false),
    ('baseline-default', 'Established', 'Established',  30, false, true,  false),
    ('baseline-default', 'Superseded',  'Superseded',   40, false, true,  true),
    ('baseline-default', 'Archived',    'Archived',     50, false, true,  true);
INSERT INTO plm.lifecycle_transition (lifecycle_id, from_state, to_state, via) VALUES
    ('baseline-default', 'Draft',       'UnderReview', 'workflow'),
    ('baseline-default', 'UnderReview', 'Established', 'workflow'),
    ('baseline-default', 'UnderReview', 'Draft',       'workflow'),
    ('baseline-default', 'Draft',       'Established', 'manual'),
    ('baseline-default', 'Draft',       'Archived',    'manual'),
    ('baseline-default', 'Established', 'Superseded',  'manual'),
    ('baseline-default', 'Established', 'Archived',    'manual'),
    ('baseline-default', 'Superseded',  'Archived',    'manual');

-- Baseline types are data. requires_signoff: a baseline of this type becomes Established only through the review workflow with e-signatures.
CREATE TABLE cfg.baseline_type (
    type             text    PRIMARY KEY,
    label            text    NOT NULL,
    requires_signoff boolean NOT NULL,
    sort_order       int     NOT NULL
);
INSERT INTO cfg.baseline_type VALUES
    ('Design',  'Design baseline',  false, 10),
    ('Release', 'Release baseline', true,  20),
    ('Product', 'Product baseline', true,  30);

CREATE SEQUENCE cfg.baseline_seq START 1;

CREATE TABLE cfg.baseline (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE CHECK (number ~ '^BL-[0-9]{6,}$'),
    name                   text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    purpose                text        NOT NULL DEFAULT '' CHECK (length(purpose) <= 4000),
    -- Program milestone the baseline belongs to (PDR, CDR, FCA, PCA ...): free text, shown and searchable.
    milestone              text        CHECK (milestone IS NULL OR length(milestone) BETWEEN 1 AND 40),
    baseline_type          text        NOT NULL REFERENCES cfg.baseline_type (type),
    signoff                boolean     NOT NULL,
    root_master_id         uuid        NOT NULL REFERENCES plm.part_master (id),
    root_revision_id       uuid        NOT NULL REFERENCES plm.part_revision (id),
    root_iteration_id      uuid        NOT NULL REFERENCES plm.part_iteration (id),
    spec                   text        NOT NULL CHECK (spec IN ('Latest', 'LatestReleased', 'Effectivity')),
    released_only          boolean     NOT NULL,
    include_documents      boolean     NOT NULL,
    spec_context           text,
    spec_serial            bigint,
    spec_lot               bigint,
    spec_day               bigint,
    item_count             int         NOT NULL CHECK (item_count >= 1),
    document_count         int         NOT NULL DEFAULT 0 CHECK (document_count >= 0),
    -- SHA-256 over the canonical item list, computed at creation; the review's signatures cover it and verification recomputes it.
    content_sha256         text        NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    lifecycle_id           text        NOT NULL DEFAULT 'baseline-default' CHECK (lifecycle_id = 'baseline-default'),
    lifecycle_state        text        NOT NULL,
    establish_reason       text,
    established_at         timestamptz,
    established_by         text,
    superseded_by          uuid        REFERENCES cfg.baseline (id),
    state_reason           text,
    -- Marking columns: the contract used by AccessPolicy.BuildReadFilter (ADR-0004). They dominate every item and pinned document.
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
    state_changed_at       timestamptz NOT NULL,
    state_changed_by       text        NOT NULL,
    FOREIGN KEY (lifecycle_id, lifecycle_state) REFERENCES plm.lifecycle_state (lifecycle_id, state),
    CONSTRAINT baseline_established_has_reason CHECK (
        lifecycle_state IN ('Draft', 'UnderReview', 'Archived') OR (establish_reason IS NOT NULL AND length(establish_reason) > 0 AND established_at IS NOT NULL)),
    CONSTRAINT baseline_superseded_has_successor CHECK (superseded_by IS NULL OR lifecycle_state IN ('Superseded', 'Archived'))
);
CREATE INDEX baseline_root_idx ON cfg.baseline (root_master_id, created_at DESC);
CREATE INDEX baseline_state_idx ON cfg.baseline (lifecycle_state, created_at DESC);
CREATE INDEX baseline_program_idx ON cfg.baseline (program);
CREATE INDEX baseline_number_trgm ON cfg.baseline USING gin (number gin_trgm_ops);
CREATE INDEX baseline_name_trgm ON cfg.baseline USING gin (name gin_trgm_ops);

CREATE TABLE cfg.baseline_item (
    baseline_id           uuid          NOT NULL REFERENCES cfg.baseline (id),
    seq                   int           NOT NULL CHECK (seq >= 0),
    -- seq of the parent item (null for the root, seq 0). Items are stored depth-first, so a parent always precedes its children.
    parent_seq            int,
    level                 int           NOT NULL CHECK (level >= 0),
    path                  text          NOT NULL,
    find_number           int,
    quantity              numeric(18,6) NOT NULL,
    unit                  text          NOT NULL,
    ext_quantity          numeric       NOT NULL,
    master_id             uuid          NOT NULL REFERENCES plm.part_master (id),
    revision_id           uuid          NOT NULL REFERENCES plm.part_revision (id),
    iteration_id          uuid          NOT NULL REFERENCES plm.part_iteration (id),
    reference_designators text[]        NOT NULL DEFAULT '{}',
    notes                 text,
    eff_kind              text,
    eff_context           text,
    eff_from              bigint,
    eff_to                bigint,
    PRIMARY KEY (baseline_id, seq),
    CONSTRAINT baseline_item_parent CHECK ((seq = 0) = (parent_seq IS NULL) AND (parent_seq IS NULL OR parent_seq < seq))
);
CREATE INDEX baseline_item_master_idx ON cfg.baseline_item (master_id);
CREATE INDEX baseline_item_iteration_idx ON cfg.baseline_item (iteration_id);

-- Documents pinned at creation: one row per (item, link) with the exact document revision.
CREATE TABLE cfg.baseline_document (
    baseline_id          uuid NOT NULL REFERENCES cfg.baseline (id),
    seq                  int  NOT NULL CHECK (seq >= 1),
    item_seq             int  NOT NULL,
    link_type            text NOT NULL CHECK (link_type IN ('describes', 'references')),
    document_master_id   uuid NOT NULL REFERENCES plm.document_master (id),
    document_revision_id uuid NOT NULL REFERENCES plm.document_revision (id),
    PRIMARY KEY (baseline_id, seq),
    FOREIGN KEY (baseline_id, item_seq) REFERENCES cfg.baseline_item (baseline_id, seq)
);
CREATE INDEX baseline_document_master_idx ON cfg.baseline_document (document_master_id);

-- Items and pinned documents are never changed or deleted; they are written only while the baseline is a draft (its creating transaction).
CREATE FUNCTION cfg.guard_baseline_content() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_state text;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'baseline content is immutable (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
    END IF;
    SELECT lifecycle_state INTO v_state FROM cfg.baseline WHERE id = NEW.baseline_id;
    IF v_state IS DISTINCT FROM 'Draft' THEN
        RAISE EXCEPTION 'items can only be added to a draft baseline (state %)', v_state USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER baseline_item_guard BEFORE INSERT OR UPDATE OR DELETE ON cfg.baseline_item FOR EACH ROW EXECUTE FUNCTION cfg.guard_baseline_content();
CREATE TRIGGER baseline_document_guard BEFORE INSERT OR UPDATE OR DELETE ON cfg.baseline_document FOR EACH ROW EXECUTE FUNCTION cfg.guard_baseline_content();
CREATE TRIGGER baseline_item_no_truncate BEFORE TRUNCATE ON cfg.baseline_item FOR EACH STATEMENT EXECUTE FUNCTION cfg.reject_modification();
CREATE TRIGGER baseline_document_no_truncate BEFORE TRUNCATE ON cfg.baseline_document FOR EACH STATEMENT EXECUTE FUNCTION cfg.reject_modification();

CREATE FUNCTION cfg.guard_baseline_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    -- What the snapshot is, who made it, how it is marked: fixed at creation, in every state.
    IF NEW.id <> OLD.id OR NEW.number <> OLD.number OR NEW.baseline_type <> OLD.baseline_type OR NEW.signoff <> OLD.signoff
       OR NEW.root_master_id <> OLD.root_master_id OR NEW.root_revision_id <> OLD.root_revision_id OR NEW.root_iteration_id <> OLD.root_iteration_id
       OR NEW.spec <> OLD.spec OR NEW.released_only <> OLD.released_only OR NEW.include_documents <> OLD.include_documents
       OR NEW.spec_context IS DISTINCT FROM OLD.spec_context OR NEW.spec_serial IS DISTINCT FROM OLD.spec_serial
       OR NEW.spec_lot IS DISTINCT FROM OLD.spec_lot OR NEW.spec_day IS DISTINCT FROM OLD.spec_day
       OR NEW.item_count <> OLD.item_count OR NEW.document_count <> OLD.document_count OR NEW.content_sha256 <> OLD.content_sha256
       OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by
       OR NEW.export_jurisdiction <> OLD.export_jurisdiction OR NEW.eccn IS DISTINCT FROM OLD.eccn OR NEW.usml_category IS DISTINCT FROM OLD.usml_category
       OR NEW.cui_categories <> OLD.cui_categories OR NEW.distribution_statement <> OLD.distribution_statement
       OR NEW.controlling_office IS DISTINCT FROM OLD.controlling_office OR NEW.distribution_reason IS DISTINCT FROM OLD.distribution_reason
       OR NEW.program IS DISTINCT FROM OLD.program OR NEW.owning_org <> OLD.owning_org OR NEW.export_authorizations <> OLD.export_authorizations THEN
        RAISE EXCEPTION 'baseline % content and markings are immutable', OLD.number USING ERRCODE = 'check_violation';
    END IF;
    -- Descriptive text can change only while the baseline is a draft.
    IF OLD.lifecycle_state <> 'Draft' AND (NEW.name <> OLD.name OR NEW.purpose <> OLD.purpose OR NEW.milestone IS DISTINCT FROM OLD.milestone) THEN
        RAISE EXCEPTION 'baseline % is %: its description is frozen', OLD.number, OLD.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    -- An established baseline's record of establishment never changes.
    IF OLD.lifecycle_state NOT IN ('Draft', 'UnderReview') AND (
           NEW.establish_reason IS DISTINCT FROM OLD.establish_reason OR NEW.established_at IS DISTINCT FROM OLD.established_at
        OR NEW.established_by IS DISTINCT FROM OLD.established_by) THEN
        RAISE EXCEPTION 'baseline % is %: the record of its establishment cannot change', OLD.number, OLD.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.lifecycle_state <> OLD.lifecycle_state THEN
        IF NOT EXISTS (SELECT 1 FROM plm.lifecycle_transition WHERE lifecycle_id = OLD.lifecycle_id AND from_state = OLD.lifecycle_state AND to_state = NEW.lifecycle_state) THEN
            RAISE EXCEPTION 'baseline lifecycle has no transition from % to %', OLD.lifecycle_state, NEW.lifecycle_state USING ERRCODE = 'check_violation';
        END IF;
        -- A baseline that requires sign-off cannot skip the review.
        IF NEW.signoff AND OLD.lifecycle_state = 'Draft' AND NEW.lifecycle_state = 'Established' THEN
            RAISE EXCEPTION 'baseline % requires sign-off: it is established through the review workflow', OLD.number USING ERRCODE = 'check_violation';
        END IF;
    ELSIF OLD.lifecycle_state NOT IN ('Draft') AND (NEW.superseded_by IS DISTINCT FROM OLD.superseded_by AND OLD.superseded_by IS NOT NULL) THEN
        RAISE EXCEPTION 'baseline % was already superseded', OLD.number USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER baseline_update_guard BEFORE UPDATE ON cfg.baseline FOR EACH ROW EXECUTE FUNCTION cfg.guard_baseline_update();
CREATE TRIGGER baseline_no_delete BEFORE DELETE ON cfg.baseline FOR EACH ROW EXECUTE FUNCTION cfg.reject_modification();

-- ================================================================ review of a baseline (program milestone sign-off)
INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('baseline-establish-review', 1, 'Baseline establishment review',
        'Serial technical review by one reviewer, then parallel approval where every chosen approver must approve (program milestone sign-off), then establishment.',
        'baseline', $tpl$
{
  "id": "baseline-establish-review",
  "name": "Baseline establishment review",
  "description": "Serial technical review by one reviewer, then parallel approval where every chosen approver must approve (program milestone sign-off), then establishment.",
  "objectType": "baseline",
  "slots": [
    { "name": "reviewer",  "label": "Technical reviewer", "role": "plm-viewer",   "min": 1, "max": 1 },
    { "name": "approvers", "label": "Approvers",          "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "Establishment review", "steps": ["tech-review", "approval", "release"] },
    { "id": "tech-review", "type": "Task",  "name": "Technical review", "kind": "review",
      "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 5 },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" },
      "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "release",  "type": "End",      "name": "Establish", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

-- ================================================================ privileges
GRANT SELECT, INSERT ON cfg.effectivity_context, cfg.effectivity_block TO keel_app;
GRANT SELECT, INSERT, UPDATE ON cfg.revision_effectivity TO keel_app;
GRANT SELECT, INSERT ON cfg.revision_effectivity_history TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON cfg.change_effectivity TO keel_app;
GRANT SELECT ON cfg.baseline_type TO keel_app;
GRANT SELECT, INSERT, UPDATE ON cfg.baseline TO keel_app;
GRANT SELECT, INSERT ON cfg.baseline_item, cfg.baseline_document TO keel_app;
GRANT USAGE ON SEQUENCE cfg.baseline_seq TO keel_app;
