-- Gap wave B: platform hardening for production (ADR-0032, docs/increments/7.5-platform.md). Forward-only, runs as keel_migrator.
--   1. idle session lock state in the database (shared by all replicas, survives restarts)
--   2. controlled retention of delivered outbox rows
--   3. e-mail notification channel: per-user opt-in and address, and the send queue
-- Nothing here changes existing rows or existing behaviour: the outbox stays immutable except for the one audited retention path below.

-- ---------------------------------------------------------------- 1. idle session lock registry (NIST 3.1.10, ADR-0026)
-- One row per UI session. The key is the SHA-256 (hex) of the session id claim, so the table never holds a usable session id. Every transition is a
-- conditional UPDATE issued by the application, so several nodes can tick the same sessions and each lock happens once.
CREATE TABLE identity.session_lock (
    session_key     text        PRIMARY KEY CHECK (session_key ~ '^[0-9a-f]{64}$'),
    -- The identity that owns the session, as the audit log records actors (so any node can write the audit event of a transition).
    actor           jsonb       NOT NULL,
    phase           text        NOT NULL CHECK (phase IN ('active', 'locked', 'terminated')),
    lock_reason     text        CHECK (lock_reason IS NULL OR length(lock_reason) <= 40),
    last_activity   timestamptz NOT NULL,
    locked_at       timestamptz,
    absolute_expiry timestamptz NOT NULL,
    created_at      timestamptz NOT NULL,
    updated_at      timestamptz NOT NULL,
    CHECK ((phase = 'active') = (locked_at IS NULL))
);
CREATE INDEX session_lock_phase_idx ON identity.session_lock (phase, last_activity);
CREATE INDEX session_lock_expiry_idx ON identity.session_lock (absolute_expiry);
-- A session record is operational state, not evidence (the audit log is the evidence): expired rows are deleted by the application.
GRANT SELECT, INSERT, UPDATE, DELETE ON identity.session_lock TO keel_app;

-- ---------------------------------------------------------------- 2. outbox retention
-- Until now the outbox and its delivery rows were immutable and kept forever. Delivered rows can be removed after a configured time, and only through
-- integration.purge_delivered_outbox (SECURITY DEFINER, owned by the migrator; keel_app has no DELETE privilege on either table). The function and
-- two guard triggers enforce, in the database and whatever the application does:
--   * a delivery row is deleted only when its status is 'delivered' (pending, failed, withheld and dead rows are never deleted);
--   * an outbox row is deleted only when no delivery row references it (the foreign key), which means every delivery of the event was delivered;
--   * the event has passed the notification fan-out cursor, so no watcher misses it;
--   * both deletions happen only inside the function (a transaction-local flag it sets).
-- The application runs the function in the same transaction as the audit event and the retention_run record, so a purge is never unaudited.
CREATE TABLE integration.retention_run (
    id               bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ran_at           timestamptz NOT NULL,
    cutoff           timestamptz NOT NULL,
    dry_run          boolean     NOT NULL,
    include_unrouted boolean     NOT NULL,
    events_deleted   int         NOT NULL CHECK (events_deleted >= 0),
    deliveries_deleted int       NOT NULL CHECK (deliveries_deleted >= 0),
    oldest_event_at  timestamptz,
    newest_event_at  timestamptz,
    node             text        NOT NULL CHECK (length(node) <= 200)
);
CREATE FUNCTION integration.reject_run_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'integration.retention_run is immutable' USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER retention_run_immutable BEFORE UPDATE OR DELETE ON integration.retention_run
    FOR EACH ROW EXECUTE FUNCTION integration.reject_run_change();
CREATE TRIGGER retention_run_no_truncate BEFORE TRUNCATE ON integration.retention_run
    FOR EACH STATEMENT EXECUTE FUNCTION integration.reject_run_change();
GRANT SELECT, INSERT ON integration.retention_run TO keel_app;

-- Updates stay forbidden. Deletes are allowed only inside the retention function.
DROP TRIGGER outbox_immutable ON integration.outbox;
CREATE TRIGGER outbox_no_update BEFORE UPDATE ON integration.outbox
    FOR EACH ROW EXECUTE FUNCTION integration.reject_change();
CREATE FUNCTION integration.guard_retention_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF current_setting('keel.outbox_retention', true) IS DISTINCT FROM 'on' THEN
        RAISE EXCEPTION 'integration.% rows are deleted only by integration.purge_delivered_outbox', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF TG_TABLE_NAME = 'delivery' THEN
        IF OLD.status <> 'delivered' THEN
            RAISE EXCEPTION 'a delivery that is % is never deleted', OLD.status USING ERRCODE = 'insufficient_privilege';
        END IF;
    END IF;
    RETURN OLD;
END;
$$;
CREATE TRIGGER outbox_retention_delete BEFORE DELETE ON integration.outbox
    FOR EACH ROW EXECUTE FUNCTION integration.guard_retention_delete();
CREATE TRIGGER delivery_retention_delete BEFORE DELETE ON integration.delivery
    FOR EACH ROW EXECUTE FUNCTION integration.guard_retention_delete();

-- p_cutoff: only events that occurred before it, and (for routed events) whose last delivery was before it.
-- p_include_unrouted: also events that never had a delivery row (written while no sink was enabled); default false in the application.
-- p_dry_run: count only.
-- Returns one row; all zero when another node is running the purge right now.
CREATE FUNCTION integration.purge_delivered_outbox(p_cutoff timestamptz, p_batch int, p_include_unrouted boolean, p_dry_run boolean)
RETURNS TABLE (events_deleted int, deliveries_deleted int, oldest_event_at timestamptz, newest_event_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_ids uuid[];
    v_deliveries int := 0;
    v_events int := 0;
    v_cursor_xid xid8;
    v_cursor_seq bigint;
BEGIN
    IF p_batch IS NULL OR p_batch < 1 OR p_batch > 100000 THEN
        RAISE EXCEPTION 'batch size must be between 1 and 100000';
    END IF;
    IF p_cutoff IS NULL OR p_cutoff > now() - interval '1 day' THEN
        RAISE EXCEPTION 'the retention cutoff must be at least one day in the past';
    END IF;
    IF NOT pg_try_advisory_xact_lock(hashtextextended('keel:outbox-retention', 0)) THEN
        RETURN QUERY SELECT 0, 0, NULL::timestamptz, NULL::timestamptz;
        RETURN;
    END IF;

    -- The notification fan-out reads the outbox in commit order; never remove an event it has not passed. A site without the fan-out table keeps no such constraint.
    IF to_regclass('ntf.fanout_cursor') IS NOT NULL THEN
        EXECUTE 'SELECT last_xid::xid8, last_seq FROM ntf.fanout_cursor WHERE id = 1' INTO v_cursor_xid, v_cursor_seq;
    END IF;

    SELECT array_agg(c.id), min(c.occurred_at), max(c.occurred_at)
      INTO v_ids, oldest_event_at, newest_event_at
      FROM (
        SELECT o.id, o.occurred_at
          FROM integration.outbox o
         WHERE o.occurred_at < p_cutoff
           AND o.created_in_tx < pg_snapshot_xmin(pg_current_snapshot())
           AND (to_regclass('ntf.fanout_cursor') IS NULL OR (v_cursor_xid IS NOT NULL AND (o.created_in_tx, o.seq) <= (v_cursor_xid, v_cursor_seq)))
           AND CASE
                 WHEN EXISTS (SELECT 1 FROM integration.delivery d WHERE d.event_id = o.id)
                   THEN NOT EXISTS (SELECT 1 FROM integration.delivery d WHERE d.event_id = o.id AND (d.status <> 'delivered' OR d.delivered_at IS NULL OR d.delivered_at >= p_cutoff))
                 ELSE p_include_unrouted
               END
         ORDER BY o.seq
         LIMIT p_batch
      ) c;

    IF v_ids IS NULL THEN
        RETURN QUERY SELECT 0, 0, NULL::timestamptz, NULL::timestamptz;
        RETURN;
    END IF;

    IF p_dry_run THEN
        SELECT count(*)::int INTO v_deliveries FROM integration.delivery d WHERE d.event_id = ANY (v_ids) AND d.status = 'delivered';
        RETURN QUERY SELECT cardinality(v_ids), v_deliveries, oldest_event_at, newest_event_at;
        RETURN;
    END IF;

    PERFORM set_config('keel.outbox_retention', 'on', true);
    DELETE FROM integration.delivery d WHERE d.event_id = ANY (v_ids) AND d.status = 'delivered';
    GET DIAGNOSTICS v_deliveries = ROW_COUNT;
    -- Fails (foreign key) and rolls everything back if any delivery of an event is still there.
    DELETE FROM integration.outbox o WHERE o.id = ANY (v_ids);
    GET DIAGNOSTICS v_events = ROW_COUNT;
    PERFORM set_config('keel.outbox_retention', 'off', true);
    RETURN QUERY SELECT v_events, v_deliveries, oldest_event_at, newest_event_at;
END;
$$;
REVOKE ALL ON FUNCTION integration.purge_delivered_outbox(timestamptz, int, boolean, boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION integration.purge_delivered_outbox(timestamptz, int, boolean, boolean) TO keel_app;

-- ---------------------------------------------------------------- 3. e-mail notification channel (ADR-0032)
-- Opt-in per user: nobody gets mail until they enter an address and switch it on. The address is the user's own entry (the IdP releases no e-mail claim
-- that the directory keeps) and is only ever used as a recipient; mail text carries no names, numbers or markings.
ALTER TABLE ntf.preference
    ADD COLUMN email_enabled boolean NOT NULL DEFAULT false,
    ADD COLUMN email_address text;
ALTER TABLE ntf.preference ADD CONSTRAINT preference_email_format CHECK (
    email_address IS NULL OR (length(email_address) BETWEEN 6 AND 254 AND email_address ~ '^[^[:space:]@<>",;:()\[\]\\]+@[^[:space:]@<>",;:()\[\]\\]+\.[^[:space:]@<>",;:()\[\]\\]+$'));
ALTER TABLE ntf.preference ADD CONSTRAINT preference_email_needs_address CHECK (NOT email_enabled OR email_address IS NOT NULL);

-- Per kind: should this kind also send mail (once the user opted in)? Default yes; kinds that cannot be switched off in the application still can be
-- switched off here: mail is a convenience, the in-app notification is what cannot be missed.
ALTER TABLE ntf.kind_preference ADD COLUMN email boolean NOT NULL DEFAULT true;

-- At most one pending mail per recipient: further notifications while a mail waits add nothing (the mail only says "you have a notification").
CREATE TABLE ntf.email_queue (
    id            uuid        PRIMARY KEY,
    recipient_sub text        NOT NULL CHECK (length(recipient_sub) BETWEEN 1 AND 200),
    status        text        NOT NULL CHECK (status IN ('pending', 'sent', 'failed', 'skipped')),
    attempts      int         NOT NULL DEFAULT 0,
    created_at    timestamptz NOT NULL,
    not_before    timestamptz NOT NULL,
    claimed_until timestamptz,
    sent_at       timestamptz,
    -- A generic failure class and message ("connection refused", "TLS required but not offered"): never an address, never message text.
    last_error    text        CHECK (last_error IS NULL OR length(last_error) <= 300)
);
CREATE UNIQUE INDEX email_queue_one_pending ON ntf.email_queue (recipient_sub) WHERE status = 'pending';
CREATE INDEX email_queue_due ON ntf.email_queue (not_before) WHERE status = 'pending';
CREATE INDEX email_queue_sent ON ntf.email_queue (recipient_sub, sent_at DESC) WHERE status = 'sent';
GRANT SELECT, INSERT, UPDATE, DELETE ON ntf.email_queue TO keel_app;
