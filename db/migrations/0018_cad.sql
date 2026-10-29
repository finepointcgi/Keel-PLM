-- CAD integration (ADR-0035, docs/increments/8.0-cad.md). Forward-only, runs as keel_migrator.
--
-- Privacy model: CAD files are ordinary documents (plm.document_*), so their bytes, markings, scanning, lifecycle and check-out are the
-- document module's. This schema holds only
--   * the CAD role of a document and the sets of documents a connector checked out together,
--   * the dependency graph between document iterations (what a drawing or an assembly was built against),
--   * model-of / drawing-of associations between documents and parts,
--   * CAD structure proposals (a proposed EBOM, reviewed before anything reaches the BOM). A proposal names parts and is therefore marked
--     data: it carries the marking columns of the access contract (ADR-0004) and is read through AccessPolicy.BuildReadFilter.
-- Nothing in this schema is ever copied into the audit log: audit entries carry numbers and counts only.
CREATE SCHEMA IF NOT EXISTS cad;
GRANT USAGE ON SCHEMA cad TO keel_app;

-- ---------------------------------------------------------------- CAD role of a document
CREATE TABLE cad.cad_document (
    document_master_id uuid        PRIMARY KEY REFERENCES plm.document_master (id),
    role               text        NOT NULL CHECK (role IN ('model', 'drawing', 'derived-pdf', 'step')),
    cad_system         text        CHECK (cad_system IS NULL OR length(cad_system) BETWEEN 1 AND 60),
    registered_at      timestamptz NOT NULL,
    registered_by      text        NOT NULL
);

-- ---------------------------------------------------------------- checked-out sets
-- The exclusive lock is the document check-out of every member (plm.document_checkout). This table remembers which documents were taken
-- together so a connector can check them in as one unit.
CREATE TABLE cad.checkout_set (
    id            uuid        PRIMARY KEY,
    user_sub      text        NOT NULL,
    user_name     text        NOT NULL,
    top_master_id uuid        NOT NULL REFERENCES plm.document_master (id),
    top_number    text        NOT NULL,
    created_at    timestamptz NOT NULL,
    closed_at     timestamptz,
    closed_reason text        CHECK (closed_reason IS NULL OR closed_reason IN ('checked-in', 'cancelled')),
    CHECK ((closed_at IS NULL) = (closed_reason IS NULL))
);
CREATE INDEX checkout_set_open_idx ON cad.checkout_set (user_sub) WHERE closed_at IS NULL;

CREATE TABLE cad.checkout_member (
    set_id             uuid    NOT NULL REFERENCES cad.checkout_set (id),
    document_master_id uuid    NOT NULL REFERENCES plm.document_master (id),
    document_number    text    NOT NULL,
    role               text    NOT NULL CHECK (role IN ('model', 'drawing', 'derived-pdf', 'step')),
    is_new             boolean NOT NULL DEFAULT false,
    base_revision      text    NOT NULL,
    base_iteration     int     NOT NULL,
    -- Set when the member was checked in or released; the set closes when every member is done.
    done_at            timestamptz,
    PRIMARY KEY (set_id, document_master_id)
);
CREATE INDEX checkout_member_doc_idx ON cad.checkout_member (document_master_id);

-- ---------------------------------------------------------------- dependency graph
-- One row per (parent iteration, referenced document): "iteration D-1.3 was built against D-2 at revision A, iteration 4".
-- The seen revision and iteration are what stale-drawing detection compares with the referenced document's latest iteration.
-- Rows are written by the atomic set check-in, in the same transaction as the iterations, and never change.
CREATE TABLE cad.dependency (
    id                  uuid        PRIMARY KEY,
    parent_iteration_id uuid        NOT NULL REFERENCES plm.document_iteration (id),
    child_master_id     uuid        NOT NULL REFERENCES plm.document_master (id),
    seen_revision       text        NOT NULL,
    seen_iteration      int         NOT NULL CHECK (seen_iteration >= 1),
    created_at          timestamptz NOT NULL,
    UNIQUE (parent_iteration_id, child_master_id)
);
CREATE INDEX dependency_child_idx ON cad.dependency (child_master_id);

CREATE FUNCTION cad.guard_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows are immutable (% refused)', TG_TABLE_NAME, TG_OP USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER dependency_immutable BEFORE UPDATE OR DELETE ON cad.dependency FOR EACH ROW EXECUTE FUNCTION cad.guard_immutable();
CREATE TRIGGER dependency_no_truncate BEFORE TRUNCATE ON cad.dependency FOR EACH STATEMENT EXECUTE FUNCTION cad.guard_immutable();

-- ---------------------------------------------------------------- associativity: documents and parts
CREATE TABLE cad.association (
    id                 uuid        PRIMARY KEY,
    kind               text        NOT NULL CHECK (kind IN ('model-of', 'drawing-of')),
    document_master_id uuid        NOT NULL REFERENCES plm.document_master (id),
    part_master_id     uuid        NOT NULL REFERENCES plm.part_master (id),
    created_at         timestamptz NOT NULL,
    created_by         text        NOT NULL,
    UNIQUE (kind, document_master_id, part_master_id)
);
CREATE INDEX association_part_idx ON cad.association (part_master_id);

-- ---------------------------------------------------------------- structure proposals
CREATE SEQUENCE cad.proposal_number_seq START 1;

CREATE TABLE cad.bom_proposal (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE,
    title                  text        NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    source_format          text        NOT NULL CHECK (source_format IN ('json', 'step', 'csv', 'xml')),
    source_system          text        CHECK (source_system IS NULL OR length(source_system) <= 60),
    root_ref               text        NOT NULL,
    status                 text        NOT NULL CHECK (status IN ('draft', 'applied', 'partially-applied', 'discarded')),
    apply_removals         boolean     NOT NULL DEFAULT false,
    apply_result           jsonb,
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
    decided_at             timestamptz,
    decided_by             text
);
CREATE INDEX bom_proposal_created_idx ON cad.bom_proposal (created_at DESC);

CREATE TABLE cad.bom_proposal_part (
    proposal_id        uuid    NOT NULL REFERENCES cad.bom_proposal (id),
    ref                text    NOT NULL CHECK (length(ref) BETWEEN 1 AND 200),
    cad_number         text    CHECK (cad_number IS NULL OR length(cad_number) <= 100),
    name               text    NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    part_type          text    NOT NULL,
    unit               text    NOT NULL,
    source             text    NOT NULL,
    attributes         jsonb   NOT NULL DEFAULT '{}'::jsonb,
    match_on           jsonb   NOT NULL DEFAULT '[]'::jsonb,
    match_kind         text    NOT NULL CHECK (match_kind IN ('number', 'attribute', 'new', 'ambiguous')),
    matched_master_id  uuid    REFERENCES plm.part_master (id),
    candidate_ids      uuid[]  NOT NULL DEFAULT '{}',
    decision           text    NOT NULL CHECK (decision IN ('match', 'create', 'skip', 'undecided')),
    decision_master_id uuid    REFERENCES plm.part_master (id),
    created_master_id  uuid    REFERENCES plm.part_master (id),
    PRIMARY KEY (proposal_id, ref)
);

CREATE TABLE cad.bom_proposal_line (
    id                    uuid          PRIMARY KEY,
    proposal_id           uuid          NOT NULL REFERENCES cad.bom_proposal (id),
    seq                   int           NOT NULL,
    parent_ref            text          NOT NULL,
    child_ref             text          NOT NULL,
    find_number           int           CHECK (find_number IS NULL OR find_number BETWEEN 1 AND 999999),
    quantity              numeric(18,6) NOT NULL CHECK (quantity > 0 AND quantity <= 1000000),
    unit                  text,
    reference_designators text[]        NOT NULL DEFAULT '{}',
    notes                 text,
    FOREIGN KEY (proposal_id, parent_ref) REFERENCES cad.bom_proposal_part (proposal_id, ref),
    FOREIGN KEY (proposal_id, child_ref)  REFERENCES cad.bom_proposal_part (proposal_id, ref)
);
CREATE INDEX bom_proposal_line_idx ON cad.bom_proposal_line (proposal_id, seq);

-- A proposal is reviewed while it is a draft; once applied or discarded it never changes again (the outcome stays as evidence).
CREATE FUNCTION cad.guard_proposal() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_TABLE_NAME = 'bom_proposal' THEN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'proposals are never deleted' USING ERRCODE = 'insufficient_privilege';
        END IF;
        IF TG_OP = 'UPDATE' AND (OLD.status <> 'draft' OR NEW.id <> OLD.id OR NEW.number <> OLD.number OR NEW.created_at <> OLD.created_at
                                  OR NEW.created_by <> OLD.created_by OR NEW.source_format <> OLD.source_format) THEN
            RAISE EXCEPTION 'a proposal that is no longer a draft cannot change' USING ERRCODE = 'insufficient_privilege';
        END IF;
        RETURN NEW;
    END IF;
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'proposal rows are never deleted' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF EXISTS (SELECT 1 FROM cad.bom_proposal p WHERE p.id = NEW.proposal_id AND p.status <> 'draft') THEN
            RAISE EXCEPTION 'a proposal that is no longer a draft cannot change' USING ERRCODE = 'insufficient_privilege';
        END IF;
        RETURN NEW;
    END IF;
    IF EXISTS (SELECT 1 FROM cad.bom_proposal p WHERE p.id = OLD.proposal_id AND p.status <> 'draft') THEN
        RAISE EXCEPTION 'a proposal that is no longer a draft cannot change' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER bom_proposal_guard BEFORE UPDATE OR DELETE ON cad.bom_proposal FOR EACH ROW EXECUTE FUNCTION cad.guard_proposal();
CREATE TRIGGER bom_proposal_part_guard BEFORE INSERT OR UPDATE OR DELETE ON cad.bom_proposal_part FOR EACH ROW EXECUTE FUNCTION cad.guard_proposal();
CREATE TRIGGER bom_proposal_line_guard BEFORE INSERT OR UPDATE OR DELETE ON cad.bom_proposal_line FOR EACH ROW EXECUTE FUNCTION cad.guard_proposal();

-- ---------------------------------------------------------------- grants (least privilege)
GRANT SELECT, INSERT, UPDATE ON cad.cad_document TO keel_app;
GRANT SELECT, INSERT, UPDATE ON cad.checkout_set TO keel_app;
GRANT SELECT, INSERT, UPDATE ON cad.checkout_member TO keel_app;
GRANT SELECT, INSERT ON cad.dependency TO keel_app;
GRANT SELECT, INSERT, DELETE ON cad.association TO keel_app;
GRANT SELECT, INSERT, UPDATE ON cad.bom_proposal TO keel_app;
GRANT SELECT, INSERT, UPDATE ON cad.bom_proposal_part TO keel_app;
GRANT SELECT, INSERT ON cad.bom_proposal_line TO keel_app;
GRANT USAGE ON SEQUENCE cad.proposal_number_seq TO keel_app;
