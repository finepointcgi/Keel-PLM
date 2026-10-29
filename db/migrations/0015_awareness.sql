-- UX-4: notifications, watch lists and comments (ADR-0030). Forward-only, runs as keel_migrator.
--
-- Privacy model in one paragraph: a notification row holds POINTERS ONLY (kind, object type and number, the actor's subject, an optional reference id
-- such as a comment or task id). It never holds a title, name, description or markings: the text is rendered when the row is read, after the
-- object has been read again under the viewer's current access. A watch row is a pointer too. A comment BODY is marked data (it inherits the
-- markings of its object dynamically: the table has no marking columns, access is the access to the object, checked by the service on every read),
-- so comment bodies live here and never in the audit log.
CREATE SCHEMA IF NOT EXISTS ntf;
GRANT USAGE ON SCHEMA ntf TO keel_app;

-- ---------------------------------------------------------------- notifications
CREATE TABLE ntf.notification (
    id            uuid        PRIMARY KEY,
    recipient_sub text        NOT NULL CHECK (length(recipient_sub) BETWEEN 1 AND 200),
    kind          text        NOT NULL CHECK (kind ~ '^[a-z][a-z_]{1,48}$'),
    object_type   text        NOT NULL CHECK (object_type IN ('part', 'document', 'change', 'baseline', 'mbom')),
    object_number text        NOT NULL CHECK (length(object_number) BETWEEN 1 AND 64),
    -- A comment id or a workflow task id: lets the link land on the comment or the task. An opaque id, never marked data.
    ref_id        uuid,
    -- The outbox event (integration.outbox.id) a fan-out row was made from; makes the fan-out idempotent per recipient and kind.
    event_id      uuid,
    actor_sub     text        CHECK (actor_sub IS NULL OR length(actor_sub) BETWEEN 1 AND 200),
    -- Repeated 'object changed' notifications within the dedupe window collapse into one row that counts them.
    occurrences   int         NOT NULL DEFAULT 1 CHECK (occurrences >= 1),
    created_at    timestamptz NOT NULL,
    seen_at       timestamptz,
    read_at       timestamptz
);
CREATE INDEX notification_user_time   ON ntf.notification (recipient_sub, created_at DESC, id);
CREATE INDEX notification_user_unread ON ntf.notification (recipient_sub) WHERE read_at IS NULL;
CREATE UNIQUE INDEX notification_event_once ON ntf.notification (recipient_sub, kind, event_id) WHERE event_id IS NOT NULL;

-- ---------------------------------------------------------------- watch lists
CREATE TABLE ntf.watch (
    -- An opaque row id, so the watch list can offer "stop watching" for an object that is no longer visible without naming it.
    id            uuid        NOT NULL UNIQUE,
    user_sub      text        NOT NULL CHECK (length(user_sub) BETWEEN 1 AND 200),
    object_type   text        NOT NULL CHECK (object_type IN ('part', 'document', 'change', 'baseline', 'mbom')),
    object_number text        NOT NULL CHECK (length(object_number) BETWEEN 1 AND 64),
    -- all: every change, comment and decision. decisions: releases, obsolescence, approvals and baselines only.
    level         text        NOT NULL DEFAULT 'all' CHECK (level IN ('all', 'decisions')),
    created_at    timestamptz NOT NULL,
    PRIMARY KEY (user_sub, object_type, object_number)
);
CREATE INDEX watch_object ON ntf.watch (object_type, object_number);

-- ---------------------------------------------------------------- preferences
CREATE TABLE ntf.preference (
    user_sub   text        PRIMARY KEY CHECK (length(user_sub) BETWEEN 1 AND 200),
    auto_watch boolean     NOT NULL DEFAULT true,
    updated_at timestamptz NOT NULL
);

-- One row per kind the user changed from its default. Kinds that cannot be turned off (mentions, tasks) are enforced by the application.
CREATE TABLE ntf.kind_preference (
    user_sub   text    NOT NULL CHECK (length(user_sub) BETWEEN 1 AND 200),
    kind       text    NOT NULL CHECK (kind ~ '^[a-z][a-z_]{1,48}$'),
    in_app     boolean NOT NULL DEFAULT true,
    -- Tell me about my own actions of this kind too (off by default: nobody needs to be told what they just did).
    notify_own boolean NOT NULL DEFAULT false,
    PRIMARY KEY (user_sub, kind)
);

-- ---------------------------------------------------------------- outbox fan-out cursor
-- The fan-out service reads integration.outbox in commit order, like the pull API: (created_in_tx, seq) after this position, and only rows whose
-- transaction is older than every in-flight transaction (so an event that commits late is never skipped). The row is advanced in the same
-- transaction as the notifications it wrote. It starts at the current end of the outbox: history before this migration is not replayed.
CREATE TABLE ntf.fanout_cursor (
    id         int         PRIMARY KEY CHECK (id = 1),
    last_xid   text        NOT NULL CHECK (last_xid ~ '^[0-9]+$'),
    last_seq   bigint      NOT NULL CHECK (last_seq >= 0),
    updated_at timestamptz NOT NULL
);
INSERT INTO ntf.fanout_cursor (id, last_xid, last_seq, updated_at)
SELECT 1, COALESCE((SELECT created_in_tx::text FROM integration.outbox ORDER BY created_in_tx DESC, seq DESC LIMIT 1), '0'),
          COALESCE((SELECT seq FROM integration.outbox ORDER BY created_in_tx DESC, seq DESC LIMIT 1), 0), now();

-- ---------------------------------------------------------------- comments
CREATE TABLE ntf.comment (
    id             uuid        PRIMARY KEY,
    -- The audit object type is derived from the number for changes (pr, ecr, ecn); here the UI kind is stored.
    object_type    text        NOT NULL CHECK (object_type IN ('part', 'document', 'change', 'baseline', 'mbom')),
    object_number  text        NOT NULL CHECK (length(object_number) BETWEEN 1 AND 64),
    author_sub     text        NOT NULL CHECK (length(author_sub) BETWEEN 1 AND 200),
    author_name    text        NOT NULL CHECK (length(author_name) BETWEEN 1 AND 200),
    body           text        NOT NULL CHECK (length(body) BETWEEN 1 AND 4000),
    created_at     timestamptz NOT NULL,
    parent_id      uuid        REFERENCES ntf.comment (id),
    deleted_at     timestamptz,
    deleted_by     text,
    deleted_by_name text,
    deleted_reason text        CHECK (deleted_reason IS NULL OR length(deleted_reason) BETWEEN 1 AND 500),
    CHECK ((deleted_at IS NULL) = (deleted_by IS NULL) AND (deleted_at IS NULL) = (deleted_reason IS NULL))
);
CREATE INDEX comment_object ON ntf.comment (object_type, object_number, created_at, id);
CREATE INDEX comment_author_time ON ntf.comment (author_sub, created_at DESC);
CREATE INDEX comment_parent ON ntf.comment (parent_id) WHERE parent_id IS NOT NULL;

-- A comment is immutable: no UPDATE of anything but the one-time deletion marker, and no DELETE at all. A "deleted" comment keeps its body
-- (the UI hides it; an auditor can still read it, like the audit log retains it). Replies go one level deep, to a comment of the same object.
CREATE FUNCTION ntf.guard_comment() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.parent_id IS NOT NULL THEN
            IF NOT EXISTS (SELECT 1 FROM ntf.comment p WHERE p.id = NEW.parent_id AND p.parent_id IS NULL
                           AND p.object_type = NEW.object_type AND p.object_number = NEW.object_number) THEN
                RAISE EXCEPTION 'a reply must answer a top-level comment of the same object' USING ERRCODE = 'check_violation';
            END IF;
        END IF;
        IF NEW.deleted_at IS NOT NULL THEN
            RAISE EXCEPTION 'a comment cannot be created deleted' USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    ELSIF TG_OP = 'UPDATE' THEN
        IF OLD.deleted_at IS NOT NULL
           OR NEW.id <> OLD.id OR NEW.object_type <> OLD.object_type OR NEW.object_number <> OLD.object_number
           OR NEW.author_sub <> OLD.author_sub OR NEW.author_name <> OLD.author_name OR NEW.body <> OLD.body
           OR NEW.created_at <> OLD.created_at OR NEW.parent_id IS DISTINCT FROM OLD.parent_id
           OR NEW.deleted_at IS NULL THEN
            RAISE EXCEPTION 'comments are immutable (only a one-time deletion marker may be set)' USING ERRCODE = 'insufficient_privilege';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'comments are never deleted (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER comment_guard BEFORE INSERT OR UPDATE OR DELETE ON ntf.comment FOR EACH ROW EXECUTE FUNCTION ntf.guard_comment();
CREATE TRIGGER comment_no_truncate BEFORE TRUNCATE ON ntf.comment FOR EACH STATEMENT EXECUTE FUNCTION ntf.guard_comment();

-- ---------------------------------------------------------------- grants (least privilege)
GRANT SELECT, INSERT, UPDATE, DELETE ON ntf.notification TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ntf.watch TO keel_app;
GRANT SELECT, INSERT, UPDATE ON ntf.preference TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ntf.kind_preference TO keel_app;
GRANT SELECT, UPDATE ON ntf.fanout_cursor TO keel_app;
GRANT SELECT, INSERT, UPDATE ON ntf.comment TO keel_app;

-- The audit table already has an (object_type, object_id, seq) index (0001, event_object_idx): the activity feed reads through it, so this
-- migration adds no index to the append-only audit table.
