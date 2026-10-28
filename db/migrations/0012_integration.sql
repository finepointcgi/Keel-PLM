-- Phase 4 (ADR-0025): transactional outbox, delivery to ERP sinks, clearance profiles for the outbound release check,
-- and the Technical Data Package (TDP) export record. Forward-only, runs as keel_migrator.
CREATE SCHEMA integration;

-- ---------------------------------------------------------------- clearance profiles
-- What a receiving system (or a TDP recipient) is cleared for. The outbound release check (Keel.Security.OutboundRelease) evaluates an
-- object's markings against exactly these columns; nothing else decides whether data leaves the system.
CREATE TABLE integration.clearance_profile (
    id                    uuid        PRIMARY KEY,
    name                  text        NOT NULL UNIQUE CHECK (name ~ '^[a-z][a-z0-9-]{1,40}$'),
    label                 text        NOT NULL CHECK (length(label) BETWEEN 1 AND 120),
    description           text        NOT NULL DEFAULT '' CHECK (length(description) <= 1000),
    recipient_category    text        NOT NULL CHECK (recipient_category IN ('Public', 'Usg', 'Dod', 'UsgContractor', 'DodContractor', 'Foreign')),
    us_persons_only       boolean     NOT NULL,
    allowed_jurisdictions text[]      NOT NULL CHECK (allowed_jurisdictions <@ ARRAY['None', 'Ear', 'Itar']),
    -- Empty = every ECCN / USML category of an allowed jurisdiction; otherwise exact codes or prefixes ending in '*' (for example 9A*).
    allowed_eccns         text[]      NOT NULL DEFAULT '{}',
    allowed_usml          text[]      NOT NULL DEFAULT '{}',
    allowed_cui           text[]      NOT NULL DEFAULT '{}',
    allowed_programs      text[]      NOT NULL DEFAULT '{}',
    -- Statement F is never released automatically, so it cannot be listed.
    allowed_statements    text[]      NOT NULL CHECK (allowed_statements <@ ARRAY['A', 'B', 'C', 'D', 'E']),
    -- Export authorizations (license or agreement ids) the recipient holds, for example TAA-0042.
    export_authorizations text[]      NOT NULL DEFAULT '{}',
    active                boolean     NOT NULL DEFAULT true,
    revision              int         NOT NULL DEFAULT 1,
    created_at            timestamptz NOT NULL,
    created_by            text        NOT NULL,
    updated_at            timestamptz,
    updated_by            text
);

-- Synthetic example profiles an administrator adjusts for the site (programs are site data and start empty).
INSERT INTO integration.clearance_profile
    (id, name, label, description, recipient_category, us_persons_only, allowed_jurisdictions, allowed_cui, allowed_statements, created_at, created_by) VALUES
    (gen_random_uuid(), 'public-release', 'Public release', 'Approved for public release only (statement A, no export-controlled data).', 'Public', false,
        ARRAY['None'], '{}', ARRAY['A'], now(), 'system'),
    (gen_random_uuid(), 'usg-agency', 'U.S. Government agency', 'A U.S. Government agency operated by U.S. persons.', 'Usg', true,
        ARRAY['None', 'Ear', 'Itar'], ARRAY['EXPT', 'CTI'], ARRAY['A', 'B', 'C'], now(), 'system'),
    (gen_random_uuid(), 'dod-component', 'DoD component', 'A DoD component operated by U.S. persons.', 'Dod', true,
        ARRAY['None', 'Ear', 'Itar'], ARRAY['EXPT', 'CTI'], ARRAY['A', 'B', 'C', 'D', 'E'], now(), 'system'),
    (gen_random_uuid(), 'dod-contractor', 'DoD contractor', 'A DoD contractor facility operated by U.S. persons.', 'DodContractor', true,
        ARRAY['None', 'Ear', 'Itar'], ARRAY['EXPT', 'CTI'], ARRAY['A', 'C', 'D'], now(), 'system');
INSERT INTO integration.clearance_profile
    (id, name, label, description, recipient_category, us_persons_only, allowed_jurisdictions, allowed_cui, allowed_statements, export_authorizations, created_at, created_by) VALUES
    (gen_random_uuid(), 'foreign-government-taa-0042', 'Foreign government (TAA-0042)', 'A foreign government recipient covered by the technical assistance agreement TAA-0042.', 'Foreign', false,
        ARRAY['None', 'Ear', 'Itar'], ARRAY['EXPT'], ARRAY['A', 'B', 'C', 'D', 'E'], ARRAY['TAA-0042'], now(), 'system');

-- ---------------------------------------------------------------- sinks
-- Configuration holds no secrets: secrets and key material are referenced by name and read from the secrets directory / environment.
CREATE TABLE integration.sink (
    id             uuid        PRIMARY KEY,
    name           text        NOT NULL UNIQUE CHECK (name ~ '^[a-z][a-z0-9-]{1,40}$'),
    label          text        NOT NULL CHECK (length(label) BETWEEN 1 AND 120),
    kind           text        NOT NULL CHECK (kind IN ('webhook', 'filedrop', 'broker')),
    enabled        boolean     NOT NULL DEFAULT false,
    profile_id     uuid        NOT NULL REFERENCES integration.clearance_profile (id),
    config         jsonb       NOT NULL,
    -- Empty = every event type except the optional full-structure event.
    event_types    text[]      NOT NULL DEFAULT '{}',
    full_structure boolean     NOT NULL DEFAULT false,
    revision       int         NOT NULL DEFAULT 1,
    created_at     timestamptz NOT NULL,
    created_by     text        NOT NULL,
    updated_at     timestamptz,
    updated_by     text
);

-- ---------------------------------------------------------------- outbox
-- Written in the same transaction as the business change. Immutable. The marking columns are the contract of AccessPolicy.BuildReadFilter,
-- so clearance can be evaluated without a join; an event carries the markings that cover everything in its payload.
CREATE TABLE integration.outbox (
    id                     uuid        PRIMARY KEY,   -- the event id consumers deduplicate on (UUID v7)
    seq                    bigint      GENERATED ALWAYS AS IDENTITY UNIQUE,
    event_type             text        NOT NULL CHECK (event_type ~ '^[a-z][a-z0-9_.]{1,60}$'),
    event_version          int         NOT NULL CHECK (event_version >= 1),
    aggregate_type         text        NOT NULL,
    aggregate_id           uuid,
    aggregate_number       text        NOT NULL,
    aggregate_revision     text,
    occurred_at            timestamptz NOT NULL,
    payload                jsonb       NOT NULL,
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
    correlation_id         text,
    actor                  text        NOT NULL,
    -- The transaction that wrote the event. Readers only see events whose transaction is older than every in-flight transaction
    -- (created_in_tx < pg_snapshot_xmin(pg_current_snapshot())), so a cursor can never skip an event that commits late.
    created_in_tx          xid8        NOT NULL DEFAULT pg_current_xact_id()
);
CREATE INDEX outbox_visibility_idx ON integration.outbox (created_in_tx, seq);
CREATE INDEX outbox_aggregate_idx ON integration.outbox (aggregate_type, aggregate_number, seq);
CREATE INDEX outbox_type_idx ON integration.outbox (event_type, seq);

CREATE FUNCTION integration.reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'integration.% is immutable', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER outbox_immutable BEFORE UPDATE OR DELETE ON integration.outbox
    FOR EACH ROW EXECUTE FUNCTION integration.reject_change();
CREATE TRIGGER outbox_no_truncate BEFORE TRUNCATE ON integration.outbox
    FOR EACH STATEMENT EXECUTE FUNCTION integration.reject_change();

-- ---------------------------------------------------------------- deliveries (one row per event and sink)
CREATE TABLE integration.delivery (
    id               uuid        PRIMARY KEY,
    event_id         uuid        NOT NULL REFERENCES integration.outbox (id),
    sink_id          uuid        NOT NULL REFERENCES integration.sink (id),
    status           text        NOT NULL CHECK (status IN ('pending', 'delivered', 'failed', 'withheld', 'dead')),
    attempts         int         NOT NULL DEFAULT 0,
    next_attempt_at  timestamptz NOT NULL,
    last_attempt_at  timestamptz,
    last_error       text,
    delivered_at     timestamptz,
    withheld_reason  text[],
    -- Lease of the dispatcher instance working on the row; a crashed dispatcher's lease simply expires.
    claimed_by       text,
    claimed_until    timestamptz,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL,
    UNIQUE (event_id, sink_id),
    CHECK ((status = 'withheld') = (withheld_reason IS NOT NULL))
);
CREATE INDEX delivery_due_idx ON integration.delivery (next_attempt_at) WHERE status IN ('pending', 'failed');
CREATE INDEX delivery_sink_status_idx ON integration.delivery (sink_id, status);

-- ---------------------------------------------------------------- TDP element categories (admin-editable mapping with seeded defaults)
CREATE TABLE integration.tdp_category (
    document_type text NOT NULL,
    link_type     text NOT NULL DEFAULT '*',
    category      text NOT NULL CHECK (category IN ('DRAWINGS', 'MODELS', 'ASSOCIATED_LISTS', 'SPECIFICATIONS', 'STANDARDS',
                                                    'PERFORMANCE_REQUIREMENTS', 'QUALITY_ASSURANCE', 'PACKAGING', 'OTHER')),
    updated_at    timestamptz NOT NULL,
    updated_by    text        NOT NULL,
    PRIMARY KEY (document_type, link_type)
);
INSERT INTO integration.tdp_category (document_type, link_type, category, updated_at, updated_by) VALUES
    ('Drawing', '*', 'DRAWINGS', now(), 'system'),
    ('CAD', '*', 'MODELS', now(), 'system'),
    ('Specification', '*', 'SPECIFICATIONS', now(), 'system'),
    ('Procedure', '*', 'OTHER', now(), 'system'),
    ('Report', '*', 'OTHER', now(), 'system'),
    ('Other', '*', 'OTHER', now(), 'system');

-- ---------------------------------------------------------------- TDP export record (immutable; the manifest, never the package bytes)
CREATE TABLE integration.tdp_export (
    id                     uuid        PRIMARY KEY,
    created_at             timestamptz NOT NULL,
    exporter_id            text        NOT NULL,
    exporter_name          text        NOT NULL,
    object_type            text        NOT NULL CHECK (object_type IN ('part', 'baseline')),
    object_number          text        NOT NULL,
    object_revision        text,
    recipient_profile_id   uuid        NOT NULL REFERENCES integration.clearance_profile (id),
    recipient_profile_name text        NOT NULL,
    recipient_category     text        NOT NULL,
    -- The exact text that was signed (not jsonb, which would reorder keys): the record can be verified without the package.
    manifest               text        NOT NULL,
    manifest_sha256        text        NOT NULL CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
    signature              text        NOT NULL,
    signature_key_id       text        NOT NULL,
    element_counts         jsonb       NOT NULL,
    correlation_id         text,
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL
);
CREATE INDEX tdp_export_object_idx ON integration.tdp_export (object_type, object_number, created_at DESC);
CREATE TRIGGER tdp_export_immutable BEFORE UPDATE OR DELETE ON integration.tdp_export
    FOR EACH ROW EXECUTE FUNCTION integration.reject_change();
CREATE TRIGGER tdp_export_no_truncate BEFORE TRUNCATE ON integration.tdp_export
    FOR EACH STATEMENT EXECUTE FUNCTION integration.reject_change();

-- ---------------------------------------------------------------- grants (least privilege)
GRANT USAGE ON SCHEMA integration TO keel_app;
GRANT SELECT, INSERT, UPDATE ON integration.clearance_profile, integration.sink TO keel_app;
GRANT SELECT, INSERT ON integration.outbox TO keel_app;
GRANT SELECT, INSERT, UPDATE ON integration.delivery TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON integration.tdp_category TO keel_app;
GRANT SELECT, INSERT ON integration.tdp_export TO keel_app;
