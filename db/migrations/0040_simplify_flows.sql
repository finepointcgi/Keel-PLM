-- Simplified flows (ADR 0061): per-user convenience data only. No object data, no markings, no content.
--  * ws.preference.simple_view: the "Simple view" of the navigation (hides the advanced groups; every page stays reachable by search and the palette).
--  * ws.last_choice: the user's last pick for a form field (part type, unit, source, document type, link type) so the quick forms start where they stopped.
-- Forward-only. Rows are owned by one user (user_sub); the CHECKs bound the size of a row and the set of keys.
ALTER TABLE ws.preference ADD COLUMN simple_view boolean NOT NULL DEFAULT false;

CREATE TABLE ws.last_choice (
    user_sub   text        NOT NULL CHECK (length(user_sub) BETWEEN 1 AND 200),
    key        text        NOT NULL CHECK (key IN ('part-type', 'part-unit', 'part-source', 'document-type', 'link-type')),
    value      text        NOT NULL CHECK (length(value) BETWEEN 1 AND 100),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (user_sub, key)
);

GRANT SELECT, INSERT, UPDATE ON ws.last_choice TO keel_app;
