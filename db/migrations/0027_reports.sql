-- Increment 8.9: output and bulk work (ADR-0044, docs/increments/8.9-output-bulk.md). Forward-only, runs as keel_migrator.
--   * generated artifacts (release package PDFs): immutable rows pointing at a content-addressed vault object;
--   * saved report definitions (a dataset from a fixed catalog, columns, filters, sort, grouping) that run through the services' own read-filtered queries;
--   * Excel/CSV BOM import proposals (reviewable dry run, then applied through the part and BOM services);
--   * mass change operations and their per-object items, with undo data.
-- Marking model: nothing here carries marking columns. An artifact lists the objects whose data it contains and is downloadable only while the caller may read every one;
-- proposals and mass changes belong to their creator, and the objects they name are re-authorized whenever they are shown.
CREATE SCHEMA rpt;
GRANT USAGE ON SCHEMA rpt TO keel_app;

CREATE SEQUENCE rpt.artifact_number_seq;
CREATE SEQUENCE rpt.import_number_seq;
CREATE SEQUENCE rpt.mass_number_seq;
GRANT USAGE ON SEQUENCE rpt.artifact_number_seq, rpt.import_number_seq, rpt.mass_number_seq TO keel_app;

-- ================================================================ generated artifacts
CREATE TABLE rpt.artifact (
    id             uuid        PRIMARY KEY,
    number         text        NOT NULL UNIQUE,
    kind           text        NOT NULL CHECK (kind IN ('release_package')),
    subject_kind   text        NOT NULL CHECK (subject_kind IN ('part', 'ecn', 'baseline')),
    subject_key    text        NOT NULL CHECK (length(subject_key) BETWEEN 1 AND 64),
    title          text        NOT NULL CHECK (length(title) BETWEEN 1 AND 300),
    sha256         text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    size_bytes     bigint      NOT NULL CHECK (size_bytes > 0),
    banner         text        NOT NULL,
    objects        jsonb       NOT NULL CHECK (jsonb_typeof(objects) = 'array' AND jsonb_array_length(objects) BETWEEN 1 AND 20000),
    created_at     timestamptz NOT NULL,
    created_by     text        NOT NULL,
    created_by_sub text        NOT NULL,
    correlation_id text
);
CREATE INDEX artifact_subject_idx ON rpt.artifact (subject_kind, subject_key, created_at DESC);

CREATE FUNCTION rpt.artifact_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Generated artifacts are immutable (rpt.artifact).' USING ERRCODE = 'integrity_constraint_violation';
END $$;
CREATE TRIGGER artifact_immutable BEFORE UPDATE OR DELETE ON rpt.artifact FOR EACH ROW EXECUTE FUNCTION rpt.artifact_immutable();

-- ================================================================ report definitions
CREATE TABLE rpt.definition (
    id         uuid        PRIMARY KEY,
    name       text        NOT NULL CHECK (length(name) BETWEEN 1 AND 120),
    dataset    text        NOT NULL CHECK (dataset ~ '^[a-z][a-z_]{0,39}$'),
    spec       jsonb       NOT NULL CHECK (jsonb_typeof(spec) = 'object' AND length(spec::text) <= 20000),
    owner_sub  text        NOT NULL,
    owner_name text        NOT NULL,
    visible_to_all boolean     NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL
);
CREATE UNIQUE INDEX definition_owner_name_idx ON rpt.definition (owner_sub, lower(name));

-- ================================================================ BOM import proposals
CREATE TABLE rpt.bom_import (
    id             uuid        PRIMARY KEY,
    number         text        NOT NULL UNIQUE,
    parent_number  text        NOT NULL,
    file_name      text        NOT NULL,
    status         text        NOT NULL CHECK (status IN ('draft', 'applied', 'discarded')),
    rows           jsonb       NOT NULL,
    errors         jsonb       NOT NULL,
    summary        jsonb       NOT NULL,
    result         jsonb,
    created_at     timestamptz NOT NULL,
    created_by     text        NOT NULL,
    created_by_sub text        NOT NULL,
    decided_at     timestamptz
);
CREATE INDEX bom_import_owner_idx ON rpt.bom_import (created_by_sub, created_at DESC);

-- ================================================================ mass change
CREATE TABLE rpt.mass_change (
    id             uuid        PRIMARY KEY,
    number         text        NOT NULL UNIQUE,
    correlation_id text        NOT NULL,
    operation      text        NOT NULL CHECK (operation IN ('set_attribute', 'change_markings', 'baseline_proposal', 'submit_review', 'watch', 'favorite')),
    object_kind    text        NOT NULL,
    parameters     jsonb       NOT NULL,
    status         text        NOT NULL CHECK (status IN ('previewed', 'applied', 'undone')),
    created_at     timestamptz NOT NULL,
    created_by     text        NOT NULL,
    created_by_sub text        NOT NULL,
    applied_at     timestamptz,
    undone_at      timestamptz
);
CREATE INDEX mass_change_owner_idx ON rpt.mass_change (created_by_sub, created_at DESC);

CREATE TABLE rpt.mass_change_item (
    change_id  uuid   NOT NULL REFERENCES rpt.mass_change (id),
    seq        int    NOT NULL,
    object_key text   NOT NULL,
    verdict    text   NOT NULL CHECK (verdict IN ('will_change', 'refused')),
    reason     text,
    result     text   NOT NULL DEFAULT 'pending' CHECK (result IN ('pending', 'applied', 'failed', 'skipped', 'undone', 'undo_failed')),
    detail     text,
    undo       jsonb,
    PRIMARY KEY (change_id, seq),
    UNIQUE (change_id, object_key)
);

-- Proposals of parts for a future baseline (a saved list; the baseline itself is taken over a structure by the baseline service).
CREATE TABLE rpt.baseline_proposal (
    name           text        NOT NULL CHECK (length(name) BETWEEN 1 AND 120),
    owner_sub      text        NOT NULL,
    part_number    text        NOT NULL,
    added_at       timestamptz NOT NULL,
    PRIMARY KEY (owner_sub, name, part_number)
);

GRANT SELECT, INSERT ON rpt.artifact TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON rpt.definition TO keel_app;
GRANT SELECT, INSERT, UPDATE ON rpt.bom_import, rpt.mass_change TO keel_app;
GRANT SELECT, INSERT, UPDATE ON rpt.mass_change_item TO keel_app;
GRANT SELECT, INSERT, DELETE ON rpt.baseline_proposal TO keel_app;
