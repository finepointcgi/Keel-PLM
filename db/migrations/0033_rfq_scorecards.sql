-- Increment 9.6: RFQs beyond the portal and supplier quality scorecards (ADR-0054, docs/increments/9.6-rfq-scorecards.md). Forward-only, runs as keel_migrator.
--   * request lines (src.request_part) gain a need-by date;
--   * a request can carry released documents (src.request_document): a frozen pin of one released iteration. Whether a supplier receives a document is decided at
--     read time by the outbound release check against its clearance profile (the document is delivered whole or refused, never redacted); no marking columns here,
--     the document's own markings are joined from plm.document_master;
--   * a quote gets a validity date (src.response.valid_until); tiers, notes and exceptions are part of the immutable quote JSON of a response line;
--   * awards (src.award): one active award per request line; each award is tied to the approved vendor list entry (AVL) that records the sourcing link;
--   * published supplier scorecards (src.scorecard_publication): a frozen copy of what the buyer chose to share with the supplier in the portal.
-- Marked data: nothing here carries marking columns. Requests, suppliers, parts and documents keep theirs and every read joins them.

ALTER TABLE src.request_part ADD COLUMN need_by date;
ALTER TABLE src.response ADD COLUMN valid_until date;

-- ================================================================ released documents of a request
CREATE TABLE src.request_document (
    request_id           uuid        NOT NULL REFERENCES src.request (id),
    document_master_id   uuid        NOT NULL REFERENCES plm.document_master (id),
    document_revision_id uuid        NOT NULL,
    iteration_id         uuid        NOT NULL REFERENCES plm.document_iteration (id),
    added_at             timestamptz NOT NULL,
    added_by             text        NOT NULL,
    PRIMARY KEY (request_id, document_master_id),
    FOREIGN KEY (document_revision_id, document_master_id) REFERENCES plm.document_revision (id, master_id)
);
CREATE INDEX request_document_document_idx ON src.request_document (document_master_id);

-- Only the released iteration of a document may be attached, only to a draft request, and the iteration must belong to the pinned revision.
CREATE FUNCTION src.guard_request_document() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (SELECT state FROM src.request WHERE id = NEW.request_id) <> 'Draft' THEN
        RAISE EXCEPTION 'documents are attached when a request is created' USING ERRCODE = 'check_violation';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM plm.document_iteration i JOIN plm.document_revision r ON r.id = i.revision_id
                   WHERE i.id = NEW.iteration_id AND r.id = NEW.document_revision_id AND r.lifecycle_state = 'Released') THEN
        RAISE EXCEPTION 'only a released document version can be attached to a request' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER request_document_guard BEFORE INSERT ON src.request_document FOR EACH ROW EXECUTE FUNCTION src.guard_request_document();

-- ================================================================ awards
CREATE TABLE src.award (
    id              uuid          PRIMARY KEY,
    request_id      uuid          NOT NULL,
    part_master_id  uuid          NOT NULL,
    supplier_id     uuid          NOT NULL REFERENCES src.supplier (id),
    response_id     uuid          NOT NULL REFERENCES src.response (id),
    quantity        numeric(14,4) NOT NULL CHECK (quantity > 0),
    unit_price      numeric(18,6) NOT NULL CHECK (unit_price >= 0),
    currency        text          NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    lead_time_days  int           CHECK (lead_time_days IS NULL OR lead_time_days BETWEEN 0 AND 3650),
    -- The sourcing link: the approved vendor list entry that records the supplier for the part. Created as a draft by the award, or an open entry that already existed.
    entry_id        uuid          NOT NULL REFERENCES src.source_entry (id),
    entry_created   boolean       NOT NULL,
    note            text          CHECK (note IS NULL OR length(note) <= 500),
    awarded_at      timestamptz   NOT NULL,
    awarded_by      text          NOT NULL,
    withdrawn_at    timestamptz,
    withdrawn_by    text,
    withdraw_reason text          CHECK (withdraw_reason IS NULL OR length(withdraw_reason) BETWEEN 1 AND 1000),
    FOREIGN KEY (request_id, part_master_id) REFERENCES src.request_part (request_id, part_master_id),
    CONSTRAINT award_withdrawal_shape CHECK ((withdrawn_at IS NULL) = (withdrawn_by IS NULL) AND (withdrawn_at IS NULL) = (withdraw_reason IS NULL))
);
CREATE UNIQUE INDEX award_one_per_line ON src.award (request_id, part_master_id) WHERE withdrawn_at IS NULL;
CREATE INDEX award_supplier_idx ON src.award (supplier_id);
CREATE INDEX award_entry_idx ON src.award (entry_id);

CREATE FUNCTION src.guard_award() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NOT EXISTS (SELECT 1 FROM src.response WHERE id = NEW.response_id AND request_id = NEW.request_id AND supplier_id = NEW.supplier_id AND NOT declined) THEN
            RAISE EXCEPTION 'an award must rest on a quote of the same supplier for the same request' USING ERRCODE = 'check_violation';
        END IF;
        IF (SELECT is_foreign FROM src.supplier WHERE id = NEW.supplier_id)
           AND (SELECT export_jurisdiction FROM plm.part_master WHERE id = NEW.part_master_id) = 'Itar' THEN
            RAISE EXCEPTION 'a foreign supplier cannot be awarded an ITAR-marked part' USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END IF;
    -- An award is immutable except for its withdrawal, which happens once.
    IF OLD.withdrawn_at IS NOT NULL OR NEW.withdrawn_at IS NULL
       OR (NEW.id, NEW.request_id, NEW.part_master_id, NEW.supplier_id, NEW.response_id, NEW.quantity, NEW.unit_price, NEW.currency, NEW.lead_time_days, NEW.entry_id, NEW.entry_created, NEW.awarded_at, NEW.awarded_by)
          IS DISTINCT FROM (OLD.id, OLD.request_id, OLD.part_master_id, OLD.supplier_id, OLD.response_id, OLD.quantity, OLD.unit_price, OLD.currency, OLD.lead_time_days, OLD.entry_id, OLD.entry_created, OLD.awarded_at, OLD.awarded_by) THEN
        RAISE EXCEPTION 'an award can only be withdrawn' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER award_guard BEFORE INSERT OR UPDATE ON src.award FOR EACH ROW EXECUTE FUNCTION src.guard_award();
CREATE TRIGGER award_no_delete BEFORE DELETE ON src.award FOR EACH ROW EXECUTE FUNCTION src.guard_immutable();
CREATE TRIGGER award_no_truncate BEFORE TRUNCATE ON src.award FOR EACH STATEMENT EXECUTE FUNCTION src.guard_immutable();

-- ================================================================ published scorecards
-- What the buyer chose to show a supplier: a frozen payload (score, components, trend) that carries counts and rates only, never a record title or text.
CREATE TABLE src.scorecard_publication (
    id              uuid        PRIMARY KEY,
    supplier_id     uuid        NOT NULL REFERENCES src.supplier (id),
    as_of           date        NOT NULL,
    formula_version int         NOT NULL CHECK (formula_version >= 1),
    payload         jsonb       NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    published_at    timestamptz NOT NULL,
    published_by    text        NOT NULL,
    withdrawn_at    timestamptz,
    withdrawn_by    text,
    CONSTRAINT scorecard_withdrawal_shape CHECK ((withdrawn_at IS NULL) = (withdrawn_by IS NULL))
);
CREATE INDEX scorecard_publication_supplier_idx ON src.scorecard_publication (supplier_id, published_at DESC);

CREATE FUNCTION src.guard_scorecard_publication() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.withdrawn_at IS NOT NULL OR NEW.withdrawn_at IS NULL
       OR (NEW.id, NEW.supplier_id, NEW.as_of, NEW.formula_version, NEW.payload, NEW.published_at, NEW.published_by)
          IS DISTINCT FROM (OLD.id, OLD.supplier_id, OLD.as_of, OLD.formula_version, OLD.payload, OLD.published_at, OLD.published_by) THEN
        RAISE EXCEPTION 'a published scorecard can only be withdrawn' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER scorecard_publication_guard BEFORE UPDATE ON src.scorecard_publication FOR EACH ROW EXECUTE FUNCTION src.guard_scorecard_publication();
CREATE TRIGGER scorecard_publication_no_delete BEFORE DELETE ON src.scorecard_publication FOR EACH ROW EXECUTE FUNCTION src.guard_immutable();
CREATE TRIGGER scorecard_publication_no_truncate BEFORE TRUNCATE ON src.scorecard_publication FOR EACH STATEMENT EXECUTE FUNCTION src.guard_immutable();

-- ================================================================ privileges (least privilege)
GRANT SELECT, INSERT ON src.request_document TO keel_app;
GRANT SELECT, INSERT, UPDATE ON src.award, src.scorecard_publication TO keel_app;
