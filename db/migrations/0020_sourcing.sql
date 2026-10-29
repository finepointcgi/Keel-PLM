-- Increment 8.2: suppliers and sourcing (ADR-0037, docs/increments/8.2-sourcing.md). Forward-only, runs as keel_migrator.
--   * the supplier master with certifications, contacts and opaque quality references;
--   * manufacturer parts (MPN) and the approved manufacturer / vendor list entries of a part (AML / AVL), with approval through the workflow engine;
--   * alternate and replacement relations between parts;
--   * the lightweight supplier portal: portal users scoped to one supplier, requests for information or quotation, immutable responses and their files;
--   * workflow templates for supplier approval and list entry approval;
--   * object type lists of the awareness tables now accept 'supplier'.
-- Marked data: suppliers and requests carry the marking columns of ADR-0004. MPNs, list entries and relations carry none: they are readable only
-- through a readable part (the part's markings, joined at query time), so a change of the part's markings applies to them at once.
CREATE SCHEMA src;
GRANT USAGE ON SCHEMA src TO keel_app;

CREATE SEQUENCE src.supplier_seq START 1;
CREATE SEQUENCE src.entry_seq START 1;
CREATE SEQUENCE src.request_seq START 1;
GRANT USAGE ON SEQUENCE src.supplier_seq, src.entry_seq, src.request_seq TO keel_app;

-- ================================================================ suppliers
CREATE TABLE src.supplier (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE CHECK (length(number) BETWEEN 2 AND 40),
    name                   text        NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    cage_code              text        CHECK (cage_code IS NULL OR cage_code ~ '^[0-9A-Z]{5}$'),
    duns_id                text        CHECK (duns_id IS NULL OR duns_id ~ '^[0-9]{9}$'),
    country                text        NOT NULL CHECK (country ~ '^[A-Z]{3}$'),
    roles                  text[]      NOT NULL CHECK (cardinality(roles) >= 1 AND roles <@ ARRAY['vendor', 'manufacturer']),
    status                 text        NOT NULL CHECK (status IN ('prospective', 'approved', 'conditional', 'disapproved', 'obsolete')),
    -- Approval runs through the workflow engine: an author proposes a status, the review (in_review) leaves the status as it is, and a completed review sets the proposed one.
    proposed_status        text        CHECK (proposed_status IS NULL OR proposed_status IN ('approved', 'conditional')),
    in_review              boolean     NOT NULL DEFAULT false,
    status_reason          text        CHECK (status_reason IS NULL OR length(status_reason) <= 1000),
    -- Export control: a foreign supplier (foreign ownership, control or foreign-person staff with access) is never selected for ITAR-marked parts.
    is_foreign             boolean     NOT NULL,
    -- Name of an integration clearance profile (migration 0012): what this supplier may receive through the portal. Null = nothing.
    clearance_profile      text        REFERENCES integration.clearance_profile (name),
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
    revision               int         NOT NULL DEFAULT 1,
    created_at             timestamptz NOT NULL,
    created_by             text        NOT NULL,
    updated_at             timestamptz,
    updated_by             text,
    CONSTRAINT supplier_foreign_country CHECK (country = 'USA' OR is_foreign),
    CONSTRAINT supplier_review_shape CHECK (NOT in_review OR proposed_status IS NOT NULL)
);
CREATE UNIQUE INDEX supplier_cage_key ON src.supplier (cage_code) WHERE cage_code IS NOT NULL;
CREATE INDEX supplier_name_idx ON src.supplier (lower(name));

CREATE TABLE src.supplier_certification (
    id                 uuid        PRIMARY KEY,
    supplier_id        uuid        NOT NULL REFERENCES src.supplier (id),
    cert_type          text        NOT NULL CHECK (cert_type ~ '^[A-Z0-9][A-Z0-9 ._-]{1,29}$'),
    certificate_number text        NOT NULL CHECK (length(certificate_number) BETWEEN 1 AND 80),
    issuer             text        NOT NULL CHECK (length(issuer) BETWEEN 1 AND 120),
    scope              text        CHECK (scope IS NULL OR length(scope) <= 500),
    issued_on          date        NOT NULL,
    expires_on         date        NOT NULL,
    revoked            boolean     NOT NULL DEFAULT false,
    created_at         timestamptz NOT NULL,
    created_by         text        NOT NULL,
    CHECK (expires_on >= issued_on)
);
CREATE INDEX certification_supplier_idx ON src.supplier_certification (supplier_id);
CREATE INDEX certification_expiry_idx ON src.supplier_certification (expires_on) WHERE NOT revoked;

-- One row per (certification, stage) once its notice was raised: the expiry job is idempotent.
CREATE TABLE src.certification_notice (
    certification_id uuid        NOT NULL REFERENCES src.supplier_certification (id),
    stage            text        NOT NULL CHECK (stage IN ('d60', 'd30', 'd7', 'expired')),
    noticed_at       timestamptz NOT NULL,
    PRIMARY KEY (certification_id, stage)
);

-- Contacts hold personal data and are never written to audit details, notifications or logs (only counts and ids).
CREATE TABLE src.supplier_contact (
    id          uuid        PRIMARY KEY,
    supplier_id uuid        NOT NULL REFERENCES src.supplier (id),
    name        text        NOT NULL CHECK (length(name) BETWEEN 1 AND 120),
    email       text        CHECK (email IS NULL OR (length(email) <= 200 AND email ~ '^[^@\s]+@[^@\s]+$')),
    phone       text        CHECK (phone IS NULL OR length(phone) <= 40),
    title       text        CHECK (title IS NULL OR length(title) <= 120),
    is_primary  boolean     NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL,
    created_by  text        NOT NULL
);
CREATE INDEX contact_supplier_idx ON src.supplier_contact (supplier_id);
CREATE UNIQUE INDEX contact_one_primary ON src.supplier_contact (supplier_id) WHERE is_primary;

-- Quality records live in the Quality module (built in parallel): only an opaque kind and id are kept here, never a title or text.
CREATE TABLE src.supplier_quality_ref (
    id          uuid        PRIMARY KEY,
    supplier_id uuid        NOT NULL REFERENCES src.supplier (id),
    ref_kind    text        NOT NULL CHECK (ref_kind ~ '^[a-z][a-z-]{1,19}$'),
    ref_id      text        NOT NULL CHECK (length(ref_id) BETWEEN 1 AND 100),
    added_at    timestamptz NOT NULL,
    added_by    text        NOT NULL,
    UNIQUE (supplier_id, ref_kind, ref_id)
);

-- A portal user is an IdP identity with the plm-supplier role, linked by an administrator to exactly one supplier. The link is the ONLY thing
-- that scopes the user: no request parameter, claim or header names a supplier.
CREATE TABLE src.portal_user (
    user_sub     text        PRIMARY KEY CHECK (length(user_sub) BETWEEN 1 AND 200),
    supplier_id  uuid        NOT NULL REFERENCES src.supplier (id),
    username     text        NOT NULL,
    display_name text        NOT NULL,
    active       boolean     NOT NULL DEFAULT true,
    created_at   timestamptz NOT NULL,
    created_by   text        NOT NULL
);
CREATE INDEX portal_user_supplier_idx ON src.portal_user (supplier_id);

-- ================================================================ manufacturer parts and list entries
CREATE TABLE src.mpn (
    id              uuid        PRIMARY KEY,
    manufacturer_id uuid        NOT NULL REFERENCES src.supplier (id),
    mpn             text        NOT NULL CHECK (length(mpn) BETWEEN 1 AND 60),
    description     text        CHECK (description IS NULL OR length(description) <= 200),
    created_at      timestamptz NOT NULL,
    created_by      text        NOT NULL
);
CREATE UNIQUE INDEX mpn_key ON src.mpn (manufacturer_id, upper(mpn));

-- One row per list entry. kind aml: part x manufacturer part (the manufacturer is the supplier); kind avl: part x vendor, optionally for one MPN.
-- approval_state: Draft -> UnderReview -> Active -> Withdrawn. An Active entry is immutable except its end date and its withdrawal.
CREATE TABLE src.source_entry (
    id                uuid          PRIMARY KEY,
    number            text          NOT NULL UNIQUE CHECK (length(number) BETWEEN 2 AND 40),
    kind              text          NOT NULL CHECK (kind IN ('aml', 'avl')),
    part_master_id    uuid          NOT NULL REFERENCES plm.part_master (id),
    revision_scope    text          CHECK (revision_scope IS NULL OR revision_scope ~ '^[A-Z]{1,3}$'),
    supplier_id       uuid          NOT NULL REFERENCES src.supplier (id),
    mpn_id            uuid          REFERENCES src.mpn (id),
    status            text          NOT NULL CHECK (status IN ('preferred', 'approved', 'alternate', 'disapproved')),
    approval_state    text          NOT NULL CHECK (approval_state IN ('Draft', 'UnderReview', 'Active', 'Withdrawn')),
    effective_from    date,
    effective_to      date,
    vendor_part_number text         CHECK (vendor_part_number IS NULL OR length(vendor_part_number) BETWEEN 1 AND 60),
    lead_time_days    int           CHECK (lead_time_days IS NULL OR lead_time_days BETWEEN 0 AND 3650),
    moq               numeric(14,4) CHECK (moq IS NULL OR moq > 0),
    currency          text          CHECK (currency IS NULL OR currency ~ '^[A-Z]{3}$'),
    -- Plain data, validated by the service: [{ "quantity": 100, "unitPrice": 1.25 }, ...] in ascending quantity.
    price_breaks      jsonb         NOT NULL DEFAULT '[]' CHECK (jsonb_typeof(price_breaks) = 'array' AND jsonb_array_length(price_breaks) <= 20),
    note              text          CHECK (note IS NULL OR length(note) <= 500),
    reason            text          CHECK (reason IS NULL OR length(reason) <= 1000),
    revision          int           NOT NULL DEFAULT 1,
    created_at        timestamptz   NOT NULL,
    created_by        text          NOT NULL,
    updated_at        timestamptz,
    updated_by        text,
    CONSTRAINT entry_dates CHECK (effective_to IS NULL OR effective_from IS NULL OR effective_to >= effective_from),
    CONSTRAINT entry_aml_has_mpn CHECK (kind <> 'aml' OR mpn_id IS NOT NULL)
);
CREATE INDEX entry_part_idx ON src.source_entry (part_master_id);
CREATE INDEX entry_supplier_idx ON src.source_entry (supplier_id);
CREATE INDEX entry_mpn_idx ON src.source_entry (mpn_id) WHERE mpn_id IS NOT NULL;
CREATE UNIQUE INDEX entry_open_key ON src.source_entry
    (part_master_id, coalesce(revision_scope, ''), kind, supplier_id, coalesce(mpn_id, '00000000-0000-0000-0000-000000000000'::uuid))
    WHERE approval_state IN ('Draft', 'UnderReview', 'Active');

-- Defense in depth for the export-control rule (the service checks first and says why): a foreign supplier is never tied to an ITAR part.
CREATE FUNCTION src.guard_foreign_itar() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE foreign_supplier boolean; jurisdiction text;
BEGIN
    SELECT is_foreign INTO foreign_supplier FROM src.supplier WHERE id = NEW.supplier_id;
    SELECT export_jurisdiction INTO jurisdiction FROM plm.part_master WHERE id = NEW.part_master_id;
    IF foreign_supplier AND jurisdiction = 'Itar' AND NEW.approval_state <> 'Withdrawn' THEN
        RAISE EXCEPTION 'a foreign supplier cannot be selected for an ITAR-marked part' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER entry_foreign_itar BEFORE INSERT OR UPDATE ON src.source_entry FOR EACH ROW EXECUTE FUNCTION src.guard_foreign_itar();

CREATE TABLE src.part_relation (
    id                   uuid        PRIMARY KEY,
    part_master_id       uuid        NOT NULL REFERENCES plm.part_master (id),
    related_part_id      uuid        NOT NULL REFERENCES plm.part_master (id),
    relation             text        NOT NULL CHECK (relation IN ('alternate', 'replaces')),
    note                 text        CHECK (note IS NULL OR length(note) <= 500),
    created_at           timestamptz NOT NULL,
    created_by           text        NOT NULL,
    CHECK (part_master_id <> related_part_id),
    UNIQUE (part_master_id, related_part_id, relation)
);
CREATE INDEX relation_related_idx ON src.part_relation (related_part_id);

-- ================================================================ requests (RFQ-lite) and responses
CREATE TABLE src.request (
    id                     uuid        PRIMARY KEY,
    number                 text        NOT NULL UNIQUE CHECK (length(number) BETWEEN 2 AND 40),
    kind                   text        NOT NULL CHECK (kind IN ('rfi', 'rfq')),
    title                  text        NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    description            text        NOT NULL DEFAULT '' CHECK (length(description) <= 4000),
    state                  text        NOT NULL CHECK (state IN ('Draft', 'Issued', 'Closed', 'Cancelled')),
    due_on                 date,
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
    issued_at              timestamptz,
    closed_at              timestamptz
);

CREATE TABLE src.request_part (
    request_id     uuid          NOT NULL REFERENCES src.request (id),
    part_master_id uuid          NOT NULL REFERENCES plm.part_master (id),
    quantity       numeric(14,4) NOT NULL CHECK (quantity > 0),
    unit           text          NOT NULL DEFAULT 'EA' CHECK (length(unit) BETWEEN 1 AND 10),
    note           text          CHECK (note IS NULL OR length(note) <= 500),
    PRIMARY KEY (request_id, part_master_id)
);

CREATE TABLE src.request_recipient (
    request_id      uuid        NOT NULL REFERENCES src.request (id),
    supplier_id     uuid        NOT NULL REFERENCES src.supplier (id),
    state           text        NOT NULL CHECK (state IN ('Sent', 'Responded', 'Declined')),
    first_viewed_at timestamptz,
    added_at        timestamptz NOT NULL,
    PRIMARY KEY (request_id, supplier_id)
);
CREATE INDEX recipient_supplier_idx ON src.request_recipient (supplier_id);

CREATE FUNCTION src.guard_recipient_itar() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (SELECT is_foreign FROM src.supplier WHERE id = NEW.supplier_id)
       AND EXISTS (SELECT 1 FROM src.request_part rp JOIN plm.part_master pm ON pm.id = rp.part_master_id WHERE rp.request_id = NEW.request_id AND pm.export_jurisdiction = 'Itar') THEN
        RAISE EXCEPTION 'a foreign supplier cannot receive a request for an ITAR-marked part' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER recipient_foreign_itar BEFORE INSERT ON src.request_recipient FOR EACH ROW EXECUTE FUNCTION src.guard_recipient_itar();

CREATE FUNCTION src.guard_request_part_itar() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (SELECT export_jurisdiction FROM plm.part_master WHERE id = NEW.part_master_id) = 'Itar'
       AND EXISTS (SELECT 1 FROM src.request_recipient rr JOIN src.supplier s ON s.id = rr.supplier_id WHERE rr.request_id = NEW.request_id AND s.is_foreign) THEN
        RAISE EXCEPTION 'an ITAR-marked part cannot be added to a request that a foreign supplier receives' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER request_part_foreign_itar BEFORE INSERT ON src.request_part FOR EACH ROW EXECUTE FUNCTION src.guard_request_part_itar();

-- A response is immutable: a correction is a new response with the next sequence number; the latest one counts.
CREATE TABLE src.response (
    id                 uuid        PRIMARY KEY,
    request_id         uuid        NOT NULL REFERENCES src.request (id),
    supplier_id        uuid        NOT NULL REFERENCES src.supplier (id),
    seq                int         NOT NULL CHECK (seq >= 1),
    declined           boolean     NOT NULL DEFAULT false,
    message            text        NOT NULL DEFAULT '' CHECK (length(message) <= 4000),
    -- [{ "part": "P-000001", "unitPrice": 1.2, "currency": "USD", "leadTimeDays": 30, "moq": 100 }]
    quote              jsonb       NOT NULL DEFAULT '[]' CHECK (jsonb_typeof(quote) = 'array' AND jsonb_array_length(quote) <= 500),
    submitted_at       timestamptz NOT NULL,
    submitted_by_sub   text        NOT NULL,
    submitted_by_name  text        NOT NULL,
    UNIQUE (request_id, supplier_id, seq)
);
CREATE INDEX response_supplier_idx ON src.response (supplier_id);

CREATE TABLE src.response_file (
    id          uuid        PRIMARY KEY,
    response_id uuid        NOT NULL REFERENCES src.response (id),
    kind        text        NOT NULL CHECK (kind IN ('response', 'certificate')),
    file_name   text        NOT NULL CHECK (length(file_name) BETWEEN 1 AND 200),
    sha256      text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    size_bytes  bigint      NOT NULL CHECK (size_bytes >= 0),
    uploaded_at timestamptz NOT NULL
);
CREATE INDEX response_file_response_idx ON src.response_file (response_id);

CREATE FUNCTION src.guard_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows are immutable', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER response_immutable BEFORE UPDATE OR DELETE ON src.response FOR EACH ROW EXECUTE FUNCTION src.guard_immutable();
CREATE TRIGGER response_file_immutable BEFORE UPDATE OR DELETE ON src.response_file FOR EACH ROW EXECUTE FUNCTION src.guard_immutable();
CREATE TRIGGER response_no_truncate BEFORE TRUNCATE ON src.response FOR EACH STATEMENT EXECUTE FUNCTION src.guard_immutable();
CREATE TRIGGER response_file_no_truncate BEFORE TRUNCATE ON src.response_file FOR EACH STATEMENT EXECUTE FUNCTION src.guard_immutable();

-- ================================================================ workflow templates (ordinary data, replaceable by administrators)
INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('supplier-approval', 1, 'Supplier approval',
        'Quality review by one reviewer, then parallel approval where every chosen approver must approve. The signatures cover the supplier record and its certifications.',
        'supplier', $tpl$
{
  "id": "supplier-approval",
  "name": "Supplier approval",
  "description": "Quality review by one reviewer, then parallel approval where every chosen approver must approve. The signatures cover the supplier record and its certifications.",
  "objectType": "supplier",
  "slots": [
    { "name": "reviewer",  "label": "Quality reviewer", "role": "plm-viewer",   "min": 1, "max": 1 },
    { "name": "approvers", "label": "Approvers",        "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "Supplier approval", "steps": ["quality-review", "approval", "release"] },
    { "id": "quality-review", "type": "Task", "name": "Quality review", "kind": "review",
      "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 5 },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" },
      "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "release",  "type": "End",      "name": "Approve", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('source-entry-approval', 1, 'Approved list entry approval',
        'Review by one reviewer, then parallel approval where every chosen approver must approve. The signatures cover the part, the supplier or manufacturer part, status, dates and vendor data of the entry.',
        'source_entry', $tpl$
{
  "id": "source-entry-approval",
  "name": "Approved list entry approval",
  "description": "Review by one reviewer, then parallel approval where every chosen approver must approve. The signatures cover the part, the supplier or manufacturer part, status, dates and vendor data of the entry.",
  "objectType": "source_entry",
  "slots": [
    { "name": "reviewer",  "label": "Reviewer",  "role": "plm-viewer",   "min": 1, "max": 1 },
    { "name": "approvers", "label": "Approvers", "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "List entry approval", "steps": ["review", "approval", "release"] },
    { "id": "review",   "type": "Task",     "name": "Review", "kind": "review",
      "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 5 },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" },
      "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "release",  "type": "End",      "name": "Approve", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');

-- ================================================================ awareness tables accept the new object type
-- The object type lists of the workspace and notification tables are CHECK constraints created by migrations 0014 and 0015 (and extended by other
-- modules). They are found by their definition and replaced by a pattern, so this migration does not depend on the order of other additions.
DO $$
DECLARE c record;
BEGIN
    FOR c IN
        SELECT n.nspname AS sch, t.relname AS tbl, k.conname AS con
        FROM pg_constraint k JOIN pg_class t ON t.oid = k.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE k.contype = 'c' AND n.nspname IN ('ws', 'ntf') AND pg_get_constraintdef(k.oid) LIKE '%object_type%' AND pg_get_constraintdef(k.oid) LIKE '%mbom%'
    LOOP
        EXECUTE format('ALTER TABLE %I.%I DROP CONSTRAINT %I', c.sch, c.tbl, c.con);
        EXECUTE format('ALTER TABLE %I.%I ADD CONSTRAINT %I CHECK (object_type ~ ''^[a-z][a-z_]{1,30}$'')', c.sch, c.tbl, c.tbl || '_object_type_known');
    END LOOP;
END;
$$;

-- ================================================================ privileges (least privilege)
GRANT SELECT, INSERT, UPDATE ON src.supplier, src.mpn, src.source_entry, src.request, src.request_recipient TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON src.supplier_certification, src.supplier_contact, src.supplier_quality_ref, src.portal_user, src.part_relation, src.request_part TO keel_app;
GRANT SELECT, INSERT ON src.certification_notice, src.response, src.response_file TO keel_app;
