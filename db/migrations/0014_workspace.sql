-- UX-1: personal workspace data (ADR-0028). Everything here belongs to exactly one signed-in user (user_sub is the stable IdP subject)
-- and is low-value personal data: display preferences, table layouts, and pointers (object type + number) to objects the user viewed,
-- starred or searched for. No row holds an object name, markings or content; the pointers are re-authorized on every render.
-- Forward-only. Caps are enforced by the application in the same transaction as the write; the CHECKs bound the size of one row.
CREATE SCHEMA IF NOT EXISTS ws;
GRANT USAGE ON SCHEMA ws TO keel_app;

CREATE TABLE ws.preference (
    user_sub      text        PRIMARY KEY CHECK (length(user_sub) BETWEEN 1 AND 200),
    theme         text        NOT NULL DEFAULT 'system' CHECK (theme IN ('system', 'light', 'dark', 'high-contrast')),
    density       text        NOT NULL DEFAULT 'comfortable' CHECK (density IN ('comfortable', 'compact')),
    nav_collapsed boolean     NOT NULL DEFAULT false,
    updated_at    timestamptz NOT NULL
);

CREATE TABLE ws.table_preference (
    user_sub        text        NOT NULL CHECK (length(user_sub) BETWEEN 1 AND 200),
    table_id        text        NOT NULL CHECK (table_id ~ '^[a-z][a-z0-9.-]{0,63}$'),
    hidden_columns  text[]      NOT NULL DEFAULT '{}' CHECK (cardinality(hidden_columns) <= 40),
    density         text        CHECK (density IN ('comfortable', 'compact')),
    sort_column     text        CHECK (sort_column IS NULL OR length(sort_column) <= 64),
    sort_descending boolean     NOT NULL DEFAULT false,
    updated_at      timestamptz NOT NULL,
    PRIMARY KEY (user_sub, table_id)
);

CREATE TABLE ws.recent_item (
    user_sub      text        NOT NULL CHECK (length(user_sub) BETWEEN 1 AND 200),
    object_type   text        NOT NULL CHECK (object_type IN ('part', 'document', 'change', 'baseline', 'mbom')),
    object_number text        NOT NULL CHECK (length(object_number) BETWEEN 1 AND 64),
    last_viewed   timestamptz NOT NULL,
    PRIMARY KEY (user_sub, object_type, object_number)
);
CREATE INDEX recent_item_user_time ON ws.recent_item (user_sub, last_viewed DESC);

CREATE TABLE ws.favorite (
    user_sub      text        NOT NULL CHECK (length(user_sub) BETWEEN 1 AND 200),
    object_type   text        NOT NULL CHECK (object_type IN ('part', 'document', 'change', 'baseline', 'mbom')),
    object_number text        NOT NULL CHECK (length(object_number) BETWEEN 1 AND 64),
    created_at    timestamptz NOT NULL,
    PRIMARY KEY (user_sub, object_type, object_number)
);
CREATE INDEX favorite_user_time ON ws.favorite (user_sub, created_at DESC);

-- A saved search can contain sensitive terms: it is private to its owner, never shared, and the audit log sees only a hash of the text.
CREATE TABLE ws.saved_search (
    id         uuid        PRIMARY KEY,
    user_sub   text        NOT NULL CHECK (length(user_sub) BETWEEN 1 AND 200),
    name       text        NOT NULL CHECK (length(name) BETWEEN 1 AND 80),
    scope      text        NOT NULL CHECK (scope IN ('all', 'part', 'document', 'change', 'baseline', 'mbom')),
    query      text        NOT NULL CHECK (length(query) BETWEEN 1 AND 200),
    filters    jsonb       NOT NULL DEFAULT '{}'::jsonb CHECK (pg_column_size(filters) <= 2000),
    created_at timestamptz NOT NULL,
    UNIQUE (user_sub, name)
);
CREATE INDEX saved_search_user ON ws.saved_search (user_sub, created_at DESC);

GRANT SELECT, INSERT, UPDATE ON ws.preference TO keel_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ws.table_preference, ws.recent_item, ws.favorite, ws.saved_search TO keel_app;
