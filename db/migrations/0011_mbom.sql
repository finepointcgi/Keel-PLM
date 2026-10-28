-- Increment 2.3: EBOM / MBOM and reconciliation (ADR-0024). The manufacturing BOM is its own versioned object per (part master, plant):
-- header, revisions, iterations, lines and EBOM equivalence links, plus immutable reconciliation runs. Forward-only; nothing here is deleted.
-- A18: routings and operations stay in ERP/MES; a line only carries a free-text operation reference. A19: line effectivity reuses cfg.*.
CREATE SCHEMA mbom;
GRANT USAGE ON SCHEMA mbom TO keel_app;

-- ================================================================ data lists
-- Plants are administrator data like the reference lists, but each carries a release policy, so they have their own table.
CREATE TABLE mbom.plant (
    code                    text        PRIMARY KEY CHECK (code ~ '^[A-Z][A-Z0-9-]{1,19}$'),
    name                    text        NOT NULL CHECK (length(name) BETWEEN 1 AND 120),
    is_active               boolean     NOT NULL DEFAULT true,
    -- Release policy: may an MBOM of this plant be released while its reconciliation still has warnings? Errors always block.
    release_allows_warnings boolean     NOT NULL DEFAULT true,
    sort_order              int         NOT NULL DEFAULT 0,
    updated_at              timestamptz NOT NULL DEFAULT now(),
    updated_by              text        NOT NULL DEFAULT 'system'
);
INSERT INTO mbom.plant (code, name, release_allows_warnings, sort_order) VALUES
    ('PLANT-A', 'Main plant (synthetic)', true, 10),
    ('PLANT-B', 'Second plant (synthetic)', true, 20);

-- Supply types of an MBOM line. Make, Buy and Consume are referred to by code (reconciliation looks for Make sub-assemblies); they cannot be retired.
CREATE TABLE mbom.supply_type (
    code       text        PRIMARY KEY CHECK (code ~ '^[A-Za-z][A-Za-z0-9-]{0,19}$'),
    label      text        NOT NULL CHECK (length(label) BETWEEN 1 AND 60),
    sort_order int         NOT NULL DEFAULT 0,
    is_active  boolean     NOT NULL DEFAULT true,
    is_system  boolean     NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by text        NOT NULL DEFAULT 'system'
);
INSERT INTO mbom.supply_type (code, label, sort_order, is_system) VALUES
    ('Make', 'Make (manufactured in house)', 10, true),
    ('Buy', 'Buy (purchased)', 20, true),
    ('Consume', 'Consume (consumable or process material)', 30, true);

-- MBOM-only items (adhesives, packaging, tooling kits) are ordinary parts of a seeded type.
INSERT INTO cfg.ref_item (list, code, label, sort_order) VALUES ('part-type', 'Consumable', 'Consumable (MBOM-only items)', 50)
ON CONFLICT (list, code) DO NOTHING;

INSERT INTO cfg.numbering (object_type, prefix, width, sequence_name, prefix_editable) VALUES ('mbom', 'MB-', 6, 'mbom.mbom_number_seq', true);
CREATE SEQUENCE mbom.mbom_number_seq START 1;
GRANT USAGE ON SEQUENCE mbom.mbom_number_seq TO keel_app;

-- ================================================================ lifecycle (data, like every versioned type)
-- View-only in the administration UI: its transitions are enforced here and by the review gateway (like the baseline lifecycle).
INSERT INTO plm.lifecycle_family (family, object_type, name, current_version, is_system, editable) VALUES
    ('mbom-default', 'mbom', 'Manufacturing BOM lifecycle', 1, true, false);
INSERT INTO plm.lifecycle (id, name, family, version, status) VALUES ('mbom-default', 'Manufacturing BOM lifecycle', 'mbom-default', 1, 'published');
INSERT INTO plm.lifecycle_state (lifecycle_id, state, label, sort_order, is_initial, is_locked, is_final) VALUES
    ('mbom-default', 'InWork',      'In Work',      10, true,  false, false),
    ('mbom-default', 'UnderReview', 'Under Review', 20, false, true,  false),
    ('mbom-default', 'Released',    'Released',     30, false, true,  false),
    ('mbom-default', 'Obsolete',    'Obsolete',     40, false, true,  true);
INSERT INTO plm.lifecycle_transition (lifecycle_id, from_state, to_state, via) VALUES
    ('mbom-default', 'InWork',      'UnderReview', 'workflow'),
    ('mbom-default', 'UnderReview', 'InWork',      'workflow'),
    ('mbom-default', 'UnderReview', 'Released',    'workflow'),
    ('mbom-default', 'Released',    'Obsolete',    'manual'),
    ('mbom-default', 'InWork',      'Obsolete',    'manual');

INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('mbom-release-review', 1, 'MBOM release review',
        'Technical review by one reviewer, then parallel approval where every chosen approver must approve, then release. Needs a fresh reconciliation without errors.',
        'mbom', $tpl$
{
  "id": "mbom-release-review",
  "name": "MBOM release review",
  "description": "Technical review by one reviewer, then parallel approval where every chosen approver must approve, then release. Needs a fresh reconciliation without errors.",
  "objectType": "mbom",
  "slots": [
    { "name": "reviewer",  "label": "Manufacturing reviewer", "role": "plm-viewer",   "min": 1, "max": 1 },
    { "name": "approvers", "label": "Approvers",              "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "MBOM release review", "steps": ["tech-review", "approval", "release"] },
    { "id": "tech-review", "type": "Task",  "name": "Manufacturing review", "kind": "review",
      "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 5 },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" },
      "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "release",  "type": "End",      "name": "Release", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

-- ================================================================ the MBOM object
CREATE TABLE mbom.mbom (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE CHECK (length(number) BETWEEN 2 AND 40),
    part_master_id         uuid        NOT NULL REFERENCES plm.part_master (id),
    plant                  text        NOT NULL REFERENCES mbom.plant (code),
    lifecycle_id           text        NOT NULL REFERENCES plm.lifecycle (id),
    -- Marking columns: the contract used by AccessPolicy.BuildReadFilter (ADR-0004). They dominate the part's markings (MarkingDominance).
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
    UNIQUE (part_master_id, plant)
);
CREATE INDEX mbom_plant_idx ON mbom.mbom (plant);
CREATE INDEX mbom_program_idx ON mbom.mbom (program);
CREATE INDEX mbom_number_trgm ON mbom.mbom USING gin (number gin_trgm_ops);

CREATE TABLE mbom.revision (
    id               uuid        PRIMARY KEY,
    mbom_id          uuid        NOT NULL REFERENCES mbom.mbom (id),
    revision         text        NOT NULL,
    lifecycle_state  text        NOT NULL,
    is_latest        boolean     NOT NULL,
    latest_iteration int         NOT NULL CHECK (latest_iteration >= 1),
    created_at       timestamptz NOT NULL,
    created_by       text        NOT NULL,
    state_changed_at timestamptz NOT NULL,
    state_changed_by text        NOT NULL,
    state_reason     text,
    UNIQUE (mbom_id, revision)
);
CREATE UNIQUE INDEX mbom_revision_one_latest ON mbom.revision (mbom_id) WHERE is_latest;
CREATE INDEX mbom_revision_state_idx ON mbom.revision (mbom_id, lifecycle_state);

-- Iterations are immutable. The content hash covers the lines and links and is computed by the service at creation; verification recomputes it.
CREATE TABLE mbom.iteration (
    id                        uuid        PRIMARY KEY,
    revision_id               uuid        NOT NULL REFERENCES mbom.revision (id),
    iteration                 int         NOT NULL CHECK (iteration >= 1),
    description               text        NOT NULL DEFAULT '' CHECK (length(description) <= 4000),
    comment                   text        CHECK (comment IS NULL OR length(comment) <= 1000),
    -- The EBOM part iteration this iteration was derived from (and how it was chosen); null for a manual MBOM that is not tied to an EBOM.
    derived_part_iteration_id uuid        REFERENCES plm.part_iteration (id),
    derived_spec              text        CHECK (derived_spec IS NULL OR length(derived_spec) <= 200),
    content_sha256            text        NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    line_count                int         NOT NULL CHECK (line_count >= 0),
    link_count                int         NOT NULL CHECK (link_count >= 0),
    created_at                timestamptz NOT NULL,
    created_by                text        NOT NULL,
    UNIQUE (revision_id, iteration),
    CHECK ((derived_part_iteration_id IS NULL) = (derived_spec IS NULL))
);
CREATE INDEX mbom_iteration_derived_idx ON mbom.iteration (derived_part_iteration_id);

-- At most one check-out per revision. Holds the private working copy (lines and links) until check-in.
CREATE TABLE mbom.checkout (
    revision_id               uuid        PRIMARY KEY REFERENCES mbom.revision (id),
    user_id                   text        NOT NULL,
    user_name                 text,
    checked_out_at            timestamptz NOT NULL,
    description               text        NOT NULL,
    derived_part_iteration_id uuid        REFERENCES plm.part_iteration (id),
    derived_spec              text        CHECK (derived_spec IS NULL OR length(derived_spec) <= 200),
    CHECK ((derived_part_iteration_id IS NULL) = (derived_spec IS NULL))
);

-- ================================================================ lines
CREATE TABLE mbom.usage (
    id                    uuid          PRIMARY KEY,
    iteration_id          uuid          NOT NULL REFERENCES mbom.iteration (id),
    child_master_id       uuid          NOT NULL REFERENCES plm.part_master (id),
    -- Net quantity per assembly in the line's unit (units are not converted). Scrap is planned loss on top of it (ADR-0024).
    quantity              numeric(18,6) NOT NULL CHECK (quantity > 0),
    unit                  text          NOT NULL,
    find_number           int           NOT NULL CHECK (find_number > 0),
    alt                   int           NOT NULL DEFAULT 0 CHECK (alt >= 0),
    reference_designators text[]        NOT NULL DEFAULT '{}',
    scrap_factor          numeric(9,6)  CHECK (scrap_factor IS NULL OR (scrap_factor >= 0 AND scrap_factor <= 10)),
    phantom               boolean       NOT NULL DEFAULT false,
    supply_type           text          NOT NULL REFERENCES mbom.supply_type (code),
    operation_ref         text          CHECK (operation_ref IS NULL OR length(operation_ref) BETWEEN 1 AND 60),
    mbom_only             boolean       NOT NULL DEFAULT false,
    notes                 text          CHECK (notes IS NULL OR length(notes) <= 1000),
    eff_kind              text,
    eff_context           text          REFERENCES cfg.effectivity_context (id),
    eff_from              bigint,
    eff_to                bigint,
    created_at            timestamptz   NOT NULL,
    created_by            text          NOT NULL,
    CONSTRAINT mbom_usage_effectivity_shape CHECK (cfg.effectivity_ok(eff_kind, eff_context, eff_from, eff_to)),
    CONSTRAINT mbom_usage_ea_whole CHECK (unit <> 'EA' OR quantity = trunc(quantity)),
    CONSTRAINT mbom_usage_find_alt_key UNIQUE (iteration_id, find_number, alt)
);
CREATE INDEX mbom_usage_iteration_idx ON mbom.usage (iteration_id) INCLUDE (child_master_id, find_number, alt, quantity, unit);
CREATE INDEX mbom_usage_child_idx ON mbom.usage (child_master_id) INCLUDE (iteration_id);

CREATE TABLE mbom.working_usage (
    id                    uuid          PRIMARY KEY,
    checkout_revision_id  uuid          NOT NULL REFERENCES mbom.checkout (revision_id) ON DELETE CASCADE,
    child_master_id       uuid          NOT NULL REFERENCES plm.part_master (id),
    quantity              numeric(18,6) NOT NULL CHECK (quantity > 0),
    unit                  text          NOT NULL,
    find_number           int           NOT NULL CHECK (find_number > 0),
    reference_designators text[]        NOT NULL DEFAULT '{}',
    scrap_factor          numeric(9,6)  CHECK (scrap_factor IS NULL OR (scrap_factor >= 0 AND scrap_factor <= 10)),
    phantom               boolean       NOT NULL DEFAULT false,
    supply_type           text          NOT NULL REFERENCES mbom.supply_type (code),
    operation_ref         text          CHECK (operation_ref IS NULL OR length(operation_ref) BETWEEN 1 AND 60),
    mbom_only             boolean       NOT NULL DEFAULT false,
    notes                 text          CHECK (notes IS NULL OR length(notes) <= 1000),
    eff_kind              text,
    eff_context           text          REFERENCES cfg.effectivity_context (id),
    eff_from              bigint,
    eff_to                bigint,
    created_at            timestamptz   NOT NULL,
    created_by            text          NOT NULL,
    CONSTRAINT mbom_working_effectivity_shape CHECK (cfg.effectivity_ok(eff_kind, eff_context, eff_from, eff_to)),
    CONSTRAINT mbom_working_ea_whole CHECK (unit <> 'EA' OR quantity = trunc(quantity))
);
CREATE INDEX mbom_working_find_idx ON mbom.working_usage (checkout_revision_id, find_number);
CREATE INDEX mbom_working_child_idx ON mbom.working_usage (child_master_id);

-- ================================================================ EBOM equivalence links
-- A link says "this MBOM line implements that EBOM line". 1:1, 1:n (split: several MBOM lines link to one EBOM line) and n:1 (merge: one MBOM
-- line links to several EBOM lines) are all rows of this table. EBOM usage rows are immutable, so a link stays valid as the EBOM moves on;
-- reconciliation reports the drift. matched: same part, same quantity; quantity_adjusted: declared quantity difference (reason);
-- substituted: a different part is built instead (reason). A line with no link is "manufacturing only" and must be flagged mbom_only.
CREATE TABLE mbom.usage_link (
    mbom_usage_id uuid NOT NULL REFERENCES mbom.usage (id),
    ebom_usage_id uuid NOT NULL REFERENCES plm.bom_usage (id),
    status        text NOT NULL CHECK (status IN ('matched', 'quantity_adjusted', 'substituted')),
    reason        text CHECK (reason IS NULL OR length(reason) BETWEEN 1 AND 500),
    PRIMARY KEY (mbom_usage_id, ebom_usage_id),
    CONSTRAINT mbom_link_reason CHECK ((status = 'matched') = (reason IS NULL))
);
CREATE INDEX mbom_usage_link_ebom_idx ON mbom.usage_link (ebom_usage_id);

CREATE TABLE mbom.working_link (
    working_usage_id uuid NOT NULL REFERENCES mbom.working_usage (id) ON DELETE CASCADE,
    ebom_usage_id    uuid NOT NULL REFERENCES plm.bom_usage (id),
    status           text NOT NULL CHECK (status IN ('matched', 'quantity_adjusted', 'substituted')),
    reason           text CHECK (reason IS NULL OR length(reason) BETWEEN 1 AND 500),
    PRIMARY KEY (working_usage_id, ebom_usage_id),
    CONSTRAINT mbom_working_link_reason CHECK ((status = 'matched') = (reason IS NULL))
);
CREATE INDEX mbom_working_link_ebom_idx ON mbom.working_link (ebom_usage_id);

-- ================================================================ reconciliation runs (immutable evidence)
CREATE TABLE mbom.reconciliation_run (
    id                        uuid         PRIMARY KEY,
    mbom_id                   uuid         NOT NULL REFERENCES mbom.mbom (id),
    revision_id               uuid         NOT NULL REFERENCES mbom.revision (id),
    iteration_id              uuid         NOT NULL REFERENCES mbom.iteration (id),
    -- Against which EBOM: the resolved part iteration, how it was chosen (readable text and the structured selection).
    ebom_part_iteration_id    uuid         NOT NULL REFERENCES plm.part_iteration (id),
    spec                      text         NOT NULL CHECK (length(spec) <= 200),
    spec_kind                 text         NOT NULL CHECK (spec_kind IN ('Latest', 'LatestReleased', 'Effectivity', 'Baseline', 'Iteration')),
    spec_baseline             text,
    spec_context              text,
    spec_serial               bigint,
    spec_lot                  bigint,
    spec_day                  bigint,
    spec_iteration            text,
    derived_part_iteration_id uuid         REFERENCES plm.part_iteration (id),
    status                    text         NOT NULL CHECK (status IN ('Reconciled', 'ReconciledWithExceptions', 'NotReconciled', 'Stale')),
    ebom_lines                int          NOT NULL CHECK (ebom_lines >= 0),
    mbom_lines                int          NOT NULL CHECK (mbom_lines >= 0),
    covered_lines             int          NOT NULL CHECK (covered_lines >= 0),
    coverage_pct              numeric(5,2) NOT NULL CHECK (coverage_pct BETWEEN 0 AND 100),
    errors                    int          NOT NULL CHECK (errors >= 0),
    warnings                  int          NOT NULL CHECK (warnings >= 0),
    infos                     int          NOT NULL CHECK (infos >= 0),
    content_sha256            text         NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    duration_ms               int          NOT NULL CHECK (duration_ms >= 0),
    run_at                    timestamptz  NOT NULL,
    run_by                    text         NOT NULL,
    run_by_id                 text         NOT NULL
);
CREATE INDEX mbom_run_iteration_idx ON mbom.reconciliation_run (iteration_id, run_at DESC);
CREATE INDEX mbom_run_mbom_idx ON mbom.reconciliation_run (mbom_id, run_at DESC);

CREATE TABLE mbom.reconciliation_finding (
    run_id          uuid          NOT NULL REFERENCES mbom.reconciliation_run (id),
    seq             int           NOT NULL CHECK (seq >= 1),
    code            text          NOT NULL,
    severity        text          NOT NULL CHECK (severity IN ('error', 'warning', 'info')),
    ebom_usage_id   uuid          REFERENCES plm.bom_usage (id),
    mbom_usage_id   uuid          REFERENCES mbom.usage (id),
    -- The part the finding is about; reading a finding joins the part master through the read filter, so unreadable parts become placeholders.
    child_master_id uuid          REFERENCES plm.part_master (id),
    ebom_find       int,
    mbom_find       int,
    qty_expected    numeric(24,6),
    qty_actual      numeric(24,6),
    unit            text,
    detail          text,
    PRIMARY KEY (run_id, seq)
);
CREATE INDEX mbom_finding_child_idx ON mbom.reconciliation_finding (child_master_id);

-- What a release review signed: the iteration content hash, the reconciliation run and its hash, bound when the review starts.
CREATE TABLE mbom.review_binding (
    id             uuid        PRIMARY KEY,
    revision_id    uuid        NOT NULL REFERENCES mbom.revision (id),
    iteration_id   uuid        NOT NULL REFERENCES mbom.iteration (id),
    run_id         uuid        NOT NULL REFERENCES mbom.reconciliation_run (id),
    review_sha256  text        NOT NULL CHECK (review_sha256 ~ '^[0-9a-f]{64}$'),
    bound_at       timestamptz NOT NULL,
    bound_by       text        NOT NULL
);
CREATE INDEX mbom_binding_revision_idx ON mbom.review_binding (revision_id, bound_at DESC);

-- ================================================================ guards (defence in depth; the service enforces the same rules with messages)
CREATE FUNCTION mbom.reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'MBOM records are immutable (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER mbom_iteration_immutable BEFORE UPDATE OR DELETE ON mbom.iteration FOR EACH ROW EXECUTE FUNCTION mbom.reject_change();
CREATE TRIGGER mbom_usage_immutable BEFORE UPDATE OR DELETE ON mbom.usage FOR EACH ROW EXECUTE FUNCTION mbom.reject_change();
CREATE TRIGGER mbom_usage_link_immutable BEFORE UPDATE OR DELETE ON mbom.usage_link FOR EACH ROW EXECUTE FUNCTION mbom.reject_change();
CREATE TRIGGER mbom_run_immutable BEFORE UPDATE OR DELETE ON mbom.reconciliation_run FOR EACH ROW EXECUTE FUNCTION mbom.reject_change();
CREATE TRIGGER mbom_finding_immutable BEFORE UPDATE OR DELETE ON mbom.reconciliation_finding FOR EACH ROW EXECUTE FUNCTION mbom.reject_change();
CREATE TRIGGER mbom_binding_immutable BEFORE UPDATE OR DELETE ON mbom.review_binding FOR EACH ROW EXECUTE FUNCTION mbom.reject_change();
CREATE TRIGGER mbom_run_no_truncate BEFORE TRUNCATE ON mbom.reconciliation_run FOR EACH STATEMENT EXECUTE FUNCTION mbom.reject_change();
CREATE TRIGGER mbom_finding_no_truncate BEFORE TRUNCATE ON mbom.reconciliation_finding FOR EACH STATEMENT EXECUTE FUNCTION mbom.reject_change();
CREATE TRIGGER mbom_binding_no_truncate BEFORE TRUNCATE ON mbom.review_binding FOR EACH STATEMENT EXECUTE FUNCTION mbom.reject_change();
CREATE TRIGGER mbom_revision_no_delete BEFORE DELETE ON mbom.revision FOR EACH ROW EXECUTE FUNCTION mbom.reject_change();
CREATE TRIGGER mbom_mbom_no_delete BEFORE DELETE ON mbom.mbom FOR EACH ROW EXECUTE FUNCTION mbom.reject_change();

CREATE FUNCTION mbom.is_locked_state(p_mbom uuid, p_state text) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT COALESCE((SELECT s.is_locked FROM mbom.mbom m JOIN plm.lifecycle_state s ON s.lifecycle_id = m.lifecycle_id AND s.state = p_state WHERE m.id = p_mbom), true);
$$;

-- An MBOM belongs to an Assembly part, and its identity never changes.
CREATE FUNCTION mbom.guard_mbom() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_type text;
BEGIN
    IF TG_OP = 'INSERT' THEN
        SELECT part_type INTO v_type FROM plm.part_master WHERE id = NEW.part_master_id;
        IF v_type IS DISTINCT FROM 'Assembly' THEN
            RAISE EXCEPTION 'only Assembly parts have a manufacturing BOM (part type is %)', v_type USING ERRCODE = 'check_violation';
        END IF;
    ELSIF NEW.id <> OLD.id OR NEW.number <> OLD.number OR NEW.part_master_id <> OLD.part_master_id OR NEW.plant <> OLD.plant
       OR NEW.lifecycle_id <> OLD.lifecycle_id OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
        RAISE EXCEPTION 'MBOM identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER mbom_guard BEFORE INSERT OR UPDATE ON mbom.mbom FOR EACH ROW EXECUTE FUNCTION mbom.guard_mbom();

-- A locked revision keeps its content pointers; a release needs a review bound to a reconciliation without errors for its latest iteration.
CREATE FUNCTION mbom.guard_revision_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF mbom.is_locked_state(OLD.mbom_id, OLD.lifecycle_state)
       AND (NEW.latest_iteration <> OLD.latest_iteration OR NEW.revision <> OLD.revision OR NEW.mbom_id <> OLD.mbom_id) THEN
        RAISE EXCEPTION 'MBOM revision % is locked in state %', OLD.revision, OLD.lifecycle_state USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.lifecycle_state <> OLD.lifecycle_state THEN
        IF NOT EXISTS (SELECT 1 FROM mbom.mbom m JOIN plm.lifecycle_transition t ON t.lifecycle_id = m.lifecycle_id
                       WHERE m.id = OLD.mbom_id AND t.from_state = OLD.lifecycle_state AND t.to_state = NEW.lifecycle_state) THEN
            RAISE EXCEPTION 'the MBOM lifecycle has no transition from % to %', OLD.lifecycle_state, NEW.lifecycle_state USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.lifecycle_state = 'Released' AND NOT EXISTS (
            SELECT 1 FROM mbom.review_binding b
            JOIN mbom.reconciliation_run r ON r.id = b.run_id
            JOIN mbom.iteration i ON i.id = b.iteration_id
            WHERE b.revision_id = NEW.id AND i.iteration = NEW.latest_iteration AND r.status IN ('Reconciled', 'ReconciledWithExceptions')) THEN
            RAISE EXCEPTION 'an MBOM is released only after a reconciliation without errors was bound to its review' USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER mbom_revision_guard BEFORE UPDATE ON mbom.revision FOR EACH ROW EXECUTE FUNCTION mbom.guard_revision_update();

CREATE FUNCTION mbom.guard_locked_revision() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_mbom  uuid;
    v_state text;
BEGIN
    SELECT mbom_id, lifecycle_state INTO v_mbom, v_state FROM mbom.revision WHERE id = NEW.revision_id;
    IF mbom.is_locked_state(v_mbom, v_state) THEN
        RAISE EXCEPTION 'MBOM revision is in locked state % (%)', v_state, TG_TABLE_NAME USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER mbom_iteration_locked BEFORE INSERT ON mbom.iteration FOR EACH ROW EXECUTE FUNCTION mbom.guard_locked_revision();
CREATE TRIGGER mbom_checkout_locked BEFORE INSERT OR UPDATE ON mbom.checkout FOR EACH ROW EXECUTE FUNCTION mbom.guard_locked_revision();

-- Lines: nothing is added to a locked revision; an MBOM never contains its own part; alternatives of one find number must not overlap.
CREATE FUNCTION mbom.guard_usage_insert() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_mbom  uuid;
    v_state text;
    v_part  uuid;
    v_alt   int;
BEGIN
    SELECT r.mbom_id, r.lifecycle_state INTO v_mbom, v_state FROM mbom.iteration i JOIN mbom.revision r ON r.id = i.revision_id WHERE i.id = NEW.iteration_id;
    IF mbom.is_locked_state(v_mbom, v_state) THEN
        RAISE EXCEPTION 'MBOM revision is in locked state %: its lines cannot change', v_state USING ERRCODE = 'check_violation';
    END IF;
    SELECT part_master_id INTO v_part FROM mbom.mbom WHERE id = v_mbom;
    IF NEW.child_master_id = v_part THEN
        RAISE EXCEPTION 'an MBOM cannot contain its own part' USING ERRCODE = 'check_violation';
    END IF;
    SELECT count(*) INTO v_alt FROM mbom.usage WHERE iteration_id = NEW.iteration_id AND find_number = NEW.find_number;
    IF v_alt > 0 AND EXISTS (
        SELECT 1 FROM mbom.usage u WHERE u.iteration_id = NEW.iteration_id AND u.find_number = NEW.find_number
          AND cfg.ranges_conflict(u.eff_kind, u.eff_context, u.eff_from, u.eff_to, NEW.eff_kind, NEW.eff_context, NEW.eff_from, NEW.eff_to)) THEN
        RAISE EXCEPTION 'find number % already has a line whose effectivity overlaps this one', NEW.find_number USING ERRCODE = 'unique_violation';
    END IF;
    NEW.alt := v_alt;
    RETURN NEW;
END;
$$;
CREATE TRIGGER mbom_usage_insert_guard BEFORE INSERT ON mbom.usage FOR EACH ROW EXECUTE FUNCTION mbom.guard_usage_insert();

CREATE FUNCTION mbom.guard_working_write() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_mbom  uuid;
    v_state text;
    v_part  uuid;
BEGIN
    SELECT mbom_id, lifecycle_state INTO v_mbom, v_state FROM mbom.revision WHERE id = NEW.checkout_revision_id;
    IF mbom.is_locked_state(v_mbom, v_state) THEN
        RAISE EXCEPTION 'MBOM revision is in locked state %: its lines cannot change', v_state USING ERRCODE = 'check_violation';
    END IF;
    SELECT part_master_id INTO v_part FROM mbom.mbom WHERE id = v_mbom;
    IF NEW.child_master_id = v_part THEN
        RAISE EXCEPTION 'an MBOM cannot contain its own part' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER mbom_working_write_guard BEFORE INSERT OR UPDATE ON mbom.working_usage FOR EACH ROW EXECUTE FUNCTION mbom.guard_working_write();

CREATE FUNCTION mbom.guard_working_alternatives() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM mbom.working_usage u WHERE u.checkout_revision_id = NEW.checkout_revision_id AND u.find_number = NEW.find_number AND u.id <> NEW.id
          AND cfg.ranges_conflict(u.eff_kind, u.eff_context, u.eff_from, u.eff_to, NEW.eff_kind, NEW.eff_context, NEW.eff_from, NEW.eff_to)) THEN
        RAISE EXCEPTION 'find number % already has a line whose effectivity overlaps this one', NEW.find_number USING ERRCODE = 'unique_violation';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER mbom_working_alternatives AFTER INSERT OR UPDATE ON mbom.working_usage
    DEFERRABLE INITIALLY IMMEDIATE FOR EACH ROW EXECUTE FUNCTION mbom.guard_working_alternatives();

-- ================================================================ privileges
GRANT SELECT, INSERT, UPDATE ON mbom.plant, mbom.supply_type TO keel_app;
GRANT SELECT, INSERT, UPDATE ON mbom.mbom, mbom.revision TO keel_app;
GRANT SELECT, INSERT ON mbom.iteration, mbom.usage, mbom.usage_link, mbom.reconciliation_run, mbom.reconciliation_finding, mbom.review_binding TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON mbom.checkout, mbom.working_usage, mbom.working_link TO keel_app;
