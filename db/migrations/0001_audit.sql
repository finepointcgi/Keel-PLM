-- ADR-0005: tamper-evident audit log.
-- Runs as keel_migrator (owner). The application role keel_app gets INSERT/SELECT only.
CREATE SCHEMA IF NOT EXISTS audit;

CREATE TABLE audit.event (
    seq            bigint      PRIMARY KEY CHECK (seq > 0),
    id             uuid        NOT NULL UNIQUE,
    occurred_at    timestamptz NOT NULL,
    actor          text        NOT NULL,
    action         text        NOT NULL,
    object_type    text,
    object_id      text,
    outcome        text        NOT NULL CHECK (outcome IN ('Success', 'Denied', 'Failure')),
    correlation_id text,
    payload        text        NOT NULL,
    prev_hash      bytea       NOT NULL CHECK (octet_length(prev_hash) = 32),
    hash           bytea       NOT NULL CHECK (octet_length(hash) = 32)
);
CREATE INDEX event_object_idx ON audit.event (object_type, object_id, seq);
CREATE INDEX event_actor_idx  ON audit.event (actor, seq);
CREATE INDEX event_time_idx   ON audit.event (occurred_at);

CREATE TABLE audit.checkpoint (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    seq         bigint      NOT NULL REFERENCES audit.event (seq),
    hash        bytea       NOT NULL CHECK (octet_length(hash) = 32),
    created_at  timestamptz NOT NULL,
    key_id      text        NOT NULL,
    signature   bytea       NOT NULL
);

-- Reject modification for every role, including the owner. A superuser could disable the trigger;
-- signed checkpoints and pgAudit (ops guide) detect that.
CREATE FUNCTION audit.reject_modification() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit records are immutable (% on %)', TG_OP, TG_TABLE_NAME
        USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER event_immutable BEFORE UPDATE OR DELETE ON audit.event
    FOR EACH ROW EXECUTE FUNCTION audit.reject_modification();
CREATE TRIGGER event_no_truncate BEFORE TRUNCATE ON audit.event
    FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_modification();
CREATE TRIGGER checkpoint_immutable BEFORE UPDATE OR DELETE ON audit.checkpoint
    FOR EACH ROW EXECUTE FUNCTION audit.reject_modification();
CREATE TRIGGER checkpoint_no_truncate BEFORE TRUNCATE ON audit.checkpoint
    FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_modification();

GRANT USAGE ON SCHEMA audit TO keel_app;
GRANT SELECT, INSERT ON audit.event, audit.checkpoint TO keel_app;
