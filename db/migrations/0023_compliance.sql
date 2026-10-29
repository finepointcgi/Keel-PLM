-- Increment 8.5: product compliance data (ADR-0040, docs/increments/8.5-compliance.md). Forward-only, runs as keel_migrator.
--   * the substance master (CAS numbers) and materials with their typical composition;
--   * regulatory lists (RoHS, REACH SVHC, conflict minerals, ...) in versions with effective dates, thresholds and exemptions; a version is published only through
--     the workflow engine (signatures) and is immutable afterwards;
--   * compliance declarations of parts (composition, exemptions, expiry, verification, linked documents) with opaque references to manufacturer parts and suppliers;
--   * the stored result of evaluating a declaration against the active list versions, and expiry notices.
-- Marked data: declarations carry no marking columns. They are readable only through a readable part (the part's markings, joined at query time), so a change of the
-- part's markings applies to them at once. Substances, materials and regulatory lists are public reference data (regulations), not marked.
CREATE SCHEMA cmp;
GRANT USAGE ON SCHEMA cmp TO keel_app;

CREATE SEQUENCE cmp.substance_seq START 1;
CREATE SEQUENCE cmp.declaration_seq START 1;
GRANT USAGE ON SEQUENCE cmp.substance_seq, cmp.declaration_seq TO keel_app;

-- ================================================================ substances and materials
CREATE TABLE cmp.substance (
    id         uuid        PRIMARY KEY,
    number     text        NOT NULL UNIQUE CHECK (number ~ '^SUB-[0-9]{6}$'),
    cas_number text        NOT NULL UNIQUE CHECK (cas_number ~ '^[0-9]{2,7}-[0-9]{2}-[0-9]$'),
    name       text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    ec_number  text        CHECK (ec_number IS NULL OR ec_number ~ '^[0-9]{3}-[0-9]{3}-[0-9]$'),
    -- The 3TG flag: which conflict mineral this substance is, if any.
    mineral    text        CHECK (mineral IS NULL OR mineral IN ('tin', 'tantalum', 'tungsten', 'gold')),
    revision   int         NOT NULL DEFAULT 1,
    created_at timestamptz NOT NULL,
    created_by text        NOT NULL,
    updated_at timestamptz,
    updated_by text
);
CREATE INDEX substance_name_idx ON cmp.substance (lower(name));

CREATE TABLE cmp.material (
    id          uuid        PRIMARY KEY,
    code        text        NOT NULL UNIQUE CHECK (code ~ '^[A-Z0-9][A-Z0-9._-]{1,39}$'),
    name        text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    description text        CHECK (description IS NULL OR length(description) <= 1000),
    active      boolean     NOT NULL DEFAULT true,
    revision    int         NOT NULL DEFAULT 1,
    created_at  timestamptz NOT NULL,
    created_by  text        NOT NULL,
    updated_at  timestamptz,
    updated_by  text
);

-- The typical composition of a material: each row is the mass percent of one substance in the material (the rest is unspecified).
CREATE TABLE cmp.material_substance (
    material_id  uuid          NOT NULL REFERENCES cmp.material (id),
    substance_id uuid          NOT NULL REFERENCES cmp.substance (id),
    fraction_pct numeric(12,8) NOT NULL CHECK (fraction_pct > 0 AND fraction_pct <= 100),
    PRIMARY KEY (material_id, substance_id)
);
CREATE INDEX material_substance_substance_idx ON cmp.material_substance (substance_id);

-- ================================================================ regulatory lists
CREATE TABLE cmp.reg_list (
    id          uuid        PRIMARY KEY,
    code        text        NOT NULL UNIQUE CHECK (code ~ '^[a-z][a-z0-9_]{1,30}$'),
    name        text        NOT NULL CHECK (length(name) BETWEEN 1 AND 120),
    -- restricted: a substance above its threshold is not allowed (RoHS); svhc: above the threshold it must be reported (REACH); conflict: presence is reported and
    -- the declaration says whether it is conflict-free (3TG).
    kind        text        NOT NULL CHECK (kind IN ('restricted', 'svhc', 'conflict')),
    description text        CHECK (description IS NULL OR length(description) <= 1000),
    active      boolean     NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL,
    created_by  text        NOT NULL
);

CREATE TABLE cmp.reg_list_version (
    id             uuid        PRIMARY KEY,
    list_id        uuid        NOT NULL REFERENCES cmp.reg_list (id),
    version        int         NOT NULL CHECK (version >= 1),
    status         text        NOT NULL CHECK (status IN ('draft', 'in_review', 'published', 'superseded')),
    effective_from date        NOT NULL,
    source_note    text        CHECK (source_note IS NULL OR length(source_note) <= 500),
    -- The organization of the administrator who prepared the version: the review engine lets only people of that organization start its review (the Modify rule of
    -- ADR-0004 applies to the markings it is given, and the version's markings are those of public data owned by this organization).
    owning_org     text        NOT NULL CHECK (length(owning_org) BETWEEN 1 AND 100),
    revision       int         NOT NULL DEFAULT 1,
    created_at     timestamptz NOT NULL,
    created_by     text        NOT NULL,
    updated_at     timestamptz,
    updated_by     text,
    published_at   timestamptz,
    published_by   text,
    UNIQUE (list_id, version),
    CONSTRAINT version_published_shape CHECK ((status IN ('published', 'superseded')) = (published_at IS NOT NULL))
);
-- One version in preparation per list, and one published (current) version.
CREATE UNIQUE INDEX reg_list_version_one_open ON cmp.reg_list_version (list_id) WHERE status IN ('draft', 'in_review');
CREATE UNIQUE INDEX reg_list_version_one_published ON cmp.reg_list_version (list_id) WHERE status = 'published';

CREATE TABLE cmp.reg_list_entry (
    id            uuid          PRIMARY KEY,
    version_id    uuid          NOT NULL REFERENCES cmp.reg_list_version (id),
    substance_id  uuid          NOT NULL REFERENCES cmp.substance (id),
    -- Mass percent (w/w) of the declared item above which the substance counts; 0 means any amount.
    threshold_pct numeric(12,8) NOT NULL CHECK (threshold_pct >= 0 AND threshold_pct <= 100),
    note          text          CHECK (note IS NULL OR length(note) <= 300),
    UNIQUE (version_id, substance_id)
);

-- An exemption of a list version: a code a declaration can claim for a substance line. substance_id null = the code applies to any substance of the list.
CREATE TABLE cmp.reg_list_exemption (
    id           uuid PRIMARY KEY,
    version_id   uuid NOT NULL REFERENCES cmp.reg_list_version (id),
    code         text NOT NULL CHECK (code ~ '^[A-Za-z0-9][A-Za-z0-9().,/ -]{0,29}$'),
    description  text NOT NULL CHECK (length(description) BETWEEN 1 AND 300),
    substance_id uuid REFERENCES cmp.substance (id),
    expires_on   date,
    UNIQUE (version_id, code)
);

CREATE FUNCTION cmp.guard_version_content() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v uuid; s text;
BEGIN
    v := CASE WHEN TG_OP = 'DELETE' THEN OLD.version_id ELSE NEW.version_id END;
    SELECT status INTO s FROM cmp.reg_list_version WHERE id = v;
    IF s IS DISTINCT FROM 'draft' THEN
        RAISE EXCEPTION 'The entries of a regulatory list version can change only while it is a draft' USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;
CREATE TRIGGER reg_list_entry_draft_only BEFORE INSERT OR UPDATE OR DELETE ON cmp.reg_list_entry FOR EACH ROW EXECUTE FUNCTION cmp.guard_version_content();
CREATE TRIGGER reg_list_exemption_draft_only BEFORE INSERT OR UPDATE OR DELETE ON cmp.reg_list_exemption FOR EACH ROW EXECUTE FUNCTION cmp.guard_version_content();

CREATE FUNCTION cmp.guard_version() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status <> 'draft' THEN RAISE EXCEPTION 'A published regulatory list version cannot be deleted' USING ERRCODE = 'integrity_constraint_violation'; END IF;
        RETURN OLD;
    END IF;
    IF NEW.id <> OLD.id OR NEW.list_id <> OLD.list_id OR NEW.version <> OLD.version THEN
        RAISE EXCEPTION 'The identity of a regulatory list version cannot change' USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF OLD.status = 'published' AND NOT (NEW.status = 'superseded' AND NEW.effective_from = OLD.effective_from AND NEW.published_at = OLD.published_at) THEN
        RAISE EXCEPTION 'A published regulatory list version can only be superseded' USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF OLD.status = 'superseded' THEN
        RAISE EXCEPTION 'A superseded regulatory list version cannot change' USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF OLD.status = 'in_review' AND NEW.status NOT IN ('in_review', 'draft', 'published') THEN
        RAISE EXCEPTION 'A version in review is published or returned to draft' USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF OLD.status = 'in_review' AND NEW.status = 'in_review' AND NEW.effective_from <> OLD.effective_from THEN
        RAISE EXCEPTION 'A version in review is frozen' USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER reg_list_version_guard BEFORE UPDATE OR DELETE ON cmp.reg_list_version FOR EACH ROW EXECUTE FUNCTION cmp.guard_version();

-- ================================================================ declarations
CREATE TABLE cmp.declaration (
    id             uuid          PRIMARY KEY,
    number         text          NOT NULL UNIQUE CHECK (number ~ '^CD-[0-9]{6}$'),
    part_master_id uuid          NOT NULL REFERENCES plm.part_master (id),
    -- A revision letter the declaration is valid for; null covers every revision.
    revision_scope text          CHECK (revision_scope IS NULL OR revision_scope ~ '^[A-Z]{1,3}$'),
    source         text          NOT NULL CHECK (source IN ('internal', 'supplier')),
    -- Opaque references to the sourcing module (built in parallel): a supplier number and a manufacturer part, kept as text, never resolved here.
    supplier_ref   text          CHECK (supplier_ref IS NULL OR supplier_ref ~ '^[A-Za-z0-9][A-Za-z0-9._ -]{0,59}$'),
    mpn_ref        text          CHECK (mpn_ref IS NULL OR mpn_ref ~ '^[A-Za-z0-9][A-Za-z0-9._/ -]{0,79}$'),
    -- The mass of the declared item, to turn a declared mass into a percent.
    item_mass_mg   numeric(18,6) CHECK (item_mass_mg IS NULL OR item_mass_mg > 0),
    declared_on    date          NOT NULL,
    expires_on     date          NOT NULL,
    status         text          NOT NULL CHECK (status IN ('declared', 'verified', 'expired', 'non_compliant', 'withdrawn')),
    verified_at    timestamptz,
    verified_by    text,
    note           text          CHECK (note IS NULL OR length(note) <= 500),
    withdrawn_reason text        CHECK (withdrawn_reason IS NULL OR length(withdrawn_reason) <= 500),
    revision       int           NOT NULL DEFAULT 1,
    created_at     timestamptz   NOT NULL,
    created_by     text          NOT NULL,
    updated_at     timestamptz,
    updated_by     text,
    CHECK (expires_on >= declared_on)
);
CREATE INDEX declaration_part_idx ON cmp.declaration (part_master_id);
CREATE INDEX declaration_expiry_idx ON cmp.declaration (expires_on) WHERE status <> 'withdrawn';
CREATE INDEX declaration_supplier_idx ON cmp.declaration (supplier_ref) WHERE supplier_ref IS NOT NULL;

CREATE TABLE cmp.declaration_line (
    id             uuid          PRIMARY KEY,
    declaration_id uuid          NOT NULL REFERENCES cmp.declaration (id),
    ordinal        int           NOT NULL CHECK (ordinal >= 1),
    substance_id   uuid          REFERENCES cmp.substance (id),
    material_id    uuid          REFERENCES cmp.material (id),
    percent        numeric(14,8) CHECK (percent IS NULL OR (percent >= 0 AND percent <= 100)),
    mass_mg        numeric(18,6) CHECK (mass_mg IS NULL OR mass_mg >= 0),
    -- An exemption code of the list versions the declaration is evaluated against.
    exemption_code text          CHECK (exemption_code IS NULL OR exemption_code ~ '^[A-Za-z0-9][A-Za-z0-9().,/ -]{0,29}$'),
    -- For conflict-mineral substances: true conflict-free, false not, null unknown.
    conflict_free  boolean,
    CONSTRAINT line_one_subject CHECK ((substance_id IS NULL) <> (material_id IS NULL)),
    CONSTRAINT line_has_amount CHECK (percent IS NOT NULL OR mass_mg IS NOT NULL),
    UNIQUE (declaration_id, ordinal)
);
CREATE INDEX declaration_line_substance_idx ON cmp.declaration_line (substance_id) WHERE substance_id IS NOT NULL;

-- A declaration points at documents of the document module by number (the vault holds the files); readability of the document is checked when it is shown.
CREATE TABLE cmp.declaration_document (
    id              uuid        PRIMARY KEY,
    declaration_id  uuid        NOT NULL REFERENCES cmp.declaration (id),
    document_number text        NOT NULL CHECK (length(document_number) BETWEEN 2 AND 40),
    linked_at       timestamptz NOT NULL,
    linked_by       text        NOT NULL,
    UNIQUE (declaration_id, document_number)
);

-- The result of the last evaluation of a declaration against one regulation (the list version that was active then).
CREATE TABLE cmp.evaluation (
    declaration_id uuid        NOT NULL REFERENCES cmp.declaration (id),
    list_code      text        NOT NULL,
    list_version   int         NOT NULL,
    result         text        NOT NULL CHECK (result IN ('compliant', 'exempt', 'non_compliant', 'unverified')),
    findings       int         NOT NULL CHECK (findings >= 0),
    evaluated_at   timestamptz NOT NULL,
    PRIMARY KEY (declaration_id, list_code)
);

-- One row per (declaration, stage) once its notice was raised: the expiry job is idempotent.
CREATE TABLE cmp.expiry_notice (
    declaration_id uuid        NOT NULL REFERENCES cmp.declaration (id),
    stage          text        NOT NULL CHECK (stage IN ('d60', 'd30', 'd7', 'expired')),
    noticed_at     timestamptz NOT NULL,
    PRIMARY KEY (declaration_id, stage)
);

-- ================================================================ workflow template for publishing a list version (ordinary data, replaceable by administrators)
INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('reg-list-publish', 1, 'Regulatory list publication',
        'Review by one reviewer, then parallel approval where every chosen approver must approve. The signatures cover the entries, thresholds, exemptions and effective date of the list version.',
        'reg_list_version', $tpl$
{
  "id": "reg-list-publish",
  "name": "Regulatory list publication",
  "description": "Review by one reviewer, then parallel approval where every chosen approver must approve. The signatures cover the entries, thresholds, exemptions and effective date of the list version.",
  "objectType": "reg_list_version",
  "slots": [
    { "name": "reviewer",  "label": "Compliance reviewer", "role": "plm-viewer",   "min": 1, "max": 1 },
    { "name": "approvers", "label": "Approvers",           "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "List publication", "steps": ["review", "approval", "release"] },
    { "id": "review",   "type": "Task",     "name": "Review", "kind": "review",
      "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 5 },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" },
      "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "release",  "type": "End",      "name": "Publish", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

-- ================================================================ privileges (least privilege)
GRANT SELECT, INSERT, UPDATE ON cmp.substance, cmp.material, cmp.reg_list, cmp.declaration TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON cmp.reg_list_version, cmp.reg_list_entry, cmp.reg_list_exemption, cmp.material_substance, cmp.declaration_line, cmp.declaration_document, cmp.evaluation TO keel_app;
GRANT SELECT, INSERT ON cmp.expiry_notice TO keel_app;

-- ================================================================ seed: synthetic starter lists (published by the system; replace them through new versions)
-- These are small samples to make the module usable out of the box, NOT the legal lists: an administrator loads the current official lists as new versions.
INSERT INTO cmp.substance (id, number, cas_number, name, mineral, created_at, created_by)
SELECT gen_random_uuid(), 'SUB-' || lpad(nextval('cmp.substance_seq')::text, 6, '0'), v.cas, v.name, v.mineral, now(), 'system'
FROM (VALUES
    ('7439-92-1', 'Lead', NULL), ('7439-97-6', 'Mercury', NULL), ('7440-43-9', 'Cadmium', NULL), ('1333-82-0', 'Chromium trioxide (hexavalent chromium)', NULL),
    ('59536-65-1', 'Polybrominated biphenyls (PBB)', NULL), ('1163-19-5', 'Decabromodiphenyl ether (PBDE)', NULL),
    ('117-81-7', 'Bis(2-ethylhexyl) phthalate (DEHP)', NULL), ('85-68-7', 'Benzyl butyl phthalate (BBP)', NULL), ('84-74-2', 'Dibutyl phthalate (DBP)', NULL),
    ('84-69-5', 'Diisobutyl phthalate (DIBP)', NULL), ('25637-99-4', 'Hexabromocyclododecane (HBCDD)', NULL), ('7646-79-9', 'Cobalt dichloride', NULL),
    ('7440-31-5', 'Tin', 'tin'), ('7440-25-7', 'Tantalum', 'tantalum'), ('7440-33-7', 'Tungsten', 'tungsten'), ('7440-57-5', 'Gold', 'gold')
) AS v(cas, name, mineral);

INSERT INTO cmp.reg_list (id, code, name, kind, description, created_at, created_by) VALUES
    (gen_random_uuid(), 'rohs', 'RoHS', 'restricted', 'Restriction of hazardous substances in electrical and electronic equipment (sample list).', now(), 'system'),
    (gen_random_uuid(), 'reach_svhc', 'REACH SVHC', 'svhc', 'REACH substances of very high concern above 0.1 % by mass of an article (sample list).', now(), 'system'),
    (gen_random_uuid(), 'conflict_minerals', 'Conflict minerals (3TG)', 'conflict', 'Tin, tantalum, tungsten and gold: reported with their conflict-free status.', now(), 'system');

INSERT INTO cmp.reg_list_version (id, list_id, version, status, effective_from, source_note, owning_org, created_at, created_by)
SELECT gen_random_uuid(), l.id, 1, 'draft', DATE '2020-01-01', 'Starter list seeded by the system', 'SYSTEM', now(), 'system' FROM cmp.reg_list l;

INSERT INTO cmp.reg_list_entry (id, version_id, substance_id, threshold_pct)
SELECT gen_random_uuid(), ver.id, s.id, e.threshold
FROM (VALUES
    ('rohs', '7439-92-1', 0.1), ('rohs', '7439-97-6', 0.1), ('rohs', '7440-43-9', 0.01), ('rohs', '1333-82-0', 0.1), ('rohs', '59536-65-1', 0.1), ('rohs', '1163-19-5', 0.1),
    ('rohs', '117-81-7', 0.1), ('rohs', '85-68-7', 0.1), ('rohs', '84-74-2', 0.1), ('rohs', '84-69-5', 0.1),
    ('reach_svhc', '117-81-7', 0.1), ('reach_svhc', '85-68-7', 0.1), ('reach_svhc', '84-74-2', 0.1), ('reach_svhc', '84-69-5', 0.1), ('reach_svhc', '7439-92-1', 0.1),
    ('reach_svhc', '25637-99-4', 0.1), ('reach_svhc', '7646-79-9', 0.1),
    ('conflict_minerals', '7440-31-5', 0), ('conflict_minerals', '7440-25-7', 0), ('conflict_minerals', '7440-33-7', 0), ('conflict_minerals', '7440-57-5', 0)
) AS e(list_code, cas, threshold)
JOIN cmp.reg_list l ON l.code = e.list_code
JOIN cmp.reg_list_version ver ON ver.list_id = l.id AND ver.version = 1
JOIN cmp.substance s ON s.cas_number = e.cas;

INSERT INTO cmp.reg_list_exemption (id, version_id, code, description, substance_id, expires_on)
SELECT gen_random_uuid(), ver.id, e.code, e.description, s.id, e.expires
FROM (VALUES
    ('6(a)', 'Lead as an alloying element in steel for machining purposes, up to 0.35 % by weight', '7439-92-1', NULL::date),
    ('6(b)', 'Lead as an alloying element in aluminium, up to 0.4 % by weight', '7439-92-1', NULL),
    ('6(c)', 'Copper alloy containing up to 4 % lead by weight', '7439-92-1', NULL),
    ('7(a)', 'Lead in high melting temperature type solders (lead-based alloys with 85 % by weight or more lead)', '7439-92-1', NULL),
    ('7(c)-I', 'Electrical and electronic components containing lead in a glass or ceramic other than dielectric ceramic in capacitors', '7439-92-1', DATE '2027-07-21')
) AS e(code, description, cas, expires)
JOIN cmp.reg_list l ON l.code = 'rohs'
JOIN cmp.reg_list_version ver ON ver.list_id = l.id AND ver.version = 1
JOIN cmp.substance s ON s.cas_number = e.cas;

UPDATE cmp.reg_list_version SET status = 'published', published_at = now(), published_by = 'system', updated_at = now(), updated_by = 'system' WHERE version = 1;
