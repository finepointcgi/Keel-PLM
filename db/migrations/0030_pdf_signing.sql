-- Increment 9.5: digitally signed release packages (ADR-0051, docs/increments/9.5-pdf-signing.md). Forward-only, runs as keel_migrator.
--   * signing identities: the operator's certificate (public, with its chain) plus a REFERENCE to where the private key lives (file, PKCS#12, certificate store, OpenSSL provider/HSM).
--     No private key and no password is ever stored in the database;
--   * a singleton policy: when signing is required, packages are refused while no identity is active;
--   * one immutable row per signed artifact: who signed with which certificate, when, and the digest that was signed.
-- Marking model: nothing here carries marking columns. A signature covers the exact bytes of the PDF, so the aggregate banner and every distribution notice on every page are signed
-- with it; signing adds an invisible signature field and changes no page content.

CREATE TABLE rpt.signing_identity (
    id              uuid        PRIMARY KEY,
    name            text        NOT NULL CHECK (length(name) BETWEEN 1 AND 120),
    key_source      text        NOT NULL CHECK (key_source IN ('pem_file', 'pfx_file', 'x509_store', 'openssl_provider')),
    certificate_pem text        NOT NULL CHECK (length(certificate_pem) BETWEEN 100 AND 100000),
    key_reference   text        NOT NULL CHECK (length(key_reference) BETWEEN 1 AND 1000),
    password_reference text     CHECK (password_reference IS NULL OR password_reference ~ '^(env|file):.{1,500}$'),
    reason          text        NOT NULL CHECK (length(reason) BETWEEN 1 AND 200),
    location        text        NOT NULL CHECK (length(location) BETWEEN 1 AND 200),
    thumbprint      text        NOT NULL CHECK (thumbprint ~ '^[0-9a-f]{64}$'),
    subject_dn      text        NOT NULL,
    not_before      timestamptz NOT NULL,
    not_after       timestamptz NOT NULL,
    algorithm       text        NOT NULL,
    is_development  boolean     NOT NULL DEFAULT false,
    is_active       boolean     NOT NULL DEFAULT false,
    created_at      timestamptz NOT NULL,
    created_by      text        NOT NULL,
    created_by_sub  text        NOT NULL,
    retired_at      timestamptz,
    retired_by      text
);
CREATE UNIQUE INDEX signing_identity_name_idx ON rpt.signing_identity (lower(name));
-- At most one active identity.
CREATE UNIQUE INDEX signing_identity_one_active_idx ON rpt.signing_identity ((true)) WHERE is_active;

-- Identities are configuration history: never deleted, and only the active/retired state changes.
CREATE FUNCTION rpt.signing_identity_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Signing identities are never deleted (rpt.signing_identity).' USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF (NEW.id, NEW.name, NEW.key_source, NEW.certificate_pem, NEW.key_reference, NEW.password_reference, NEW.reason, NEW.location, NEW.thumbprint, NEW.subject_dn,
        NEW.not_before, NEW.not_after, NEW.algorithm, NEW.is_development, NEW.created_at, NEW.created_by, NEW.created_by_sub)
       IS DISTINCT FROM
       (OLD.id, OLD.name, OLD.key_source, OLD.certificate_pem, OLD.key_reference, OLD.password_reference, OLD.reason, OLD.location, OLD.thumbprint, OLD.subject_dn,
        OLD.not_before, OLD.not_after, OLD.algorithm, OLD.is_development, OLD.created_at, OLD.created_by, OLD.created_by_sub) THEN
        RAISE EXCEPTION 'A signing identity can only be activated or retired (rpt.signing_identity).' USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF OLD.retired_at IS NOT NULL AND (NEW.is_active OR NEW.retired_at IS DISTINCT FROM OLD.retired_at) THEN
        RAISE EXCEPTION 'A retired signing identity stays retired (rpt.signing_identity).' USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER signing_identity_guard BEFORE UPDATE OR DELETE ON rpt.signing_identity FOR EACH ROW EXECUTE FUNCTION rpt.signing_identity_guard();

CREATE TABLE rpt.signing_policy (
    id         smallint    PRIMARY KEY CHECK (id = 1),
    required   boolean     NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL,
    updated_by text        NOT NULL
);
INSERT INTO rpt.signing_policy (id, required, updated_at, updated_by) VALUES (1, false, now(), 'migration');

CREATE TABLE rpt.artifact_signature (
    artifact_id    uuid        PRIMARY KEY REFERENCES rpt.artifact (id),
    identity_id    uuid        NOT NULL REFERENCES rpt.signing_identity (id),
    thumbprint     text        NOT NULL CHECK (thumbprint ~ '^[0-9a-f]{64}$'),
    subject_dn     text        NOT NULL,
    algorithm      text        NOT NULL,
    signed_at      timestamptz NOT NULL,
    reason         text        NOT NULL,
    location       text        NOT NULL,
    signed_digest  text        NOT NULL CHECK (signed_digest ~ '^[0-9a-f]{64}$')
);
CREATE INDEX artifact_signature_identity_idx ON rpt.artifact_signature (identity_id);
CREATE TRIGGER artifact_signature_immutable BEFORE UPDATE OR DELETE ON rpt.artifact_signature FOR EACH ROW EXECUTE FUNCTION rpt.artifact_immutable();

GRANT SELECT, INSERT ON rpt.signing_identity TO keel_app;
GRANT UPDATE (is_active, retired_at, retired_by) ON rpt.signing_identity TO keel_app;
GRANT SELECT, UPDATE ON rpt.signing_policy TO keel_app;
GRANT SELECT, INSERT ON rpt.artifact_signature TO keel_app;
