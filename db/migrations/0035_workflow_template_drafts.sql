-- Increment 9.6: workflow template drafts (ADR-0056, docs/increments/9.6-workflow-admin-mobile.md). Forward-only, runs as keel_migrator.
--   * wf.template_draft: at most one saved work-in-progress definition per template id, so an administrator can build a workflow over several sessions and
--     publish it when it is ready. A draft is NOT a template version: it is never used to start a review, it may be invalid (the engine's rules run when it
--     is validated and published), and publishing the template deletes its draft in the same transaction. Published versions stay immutable (0006).
-- Marking model: none. A template definition names roles, groups and steps, never objects, so it carries no markings.
CREATE TABLE wf.template_draft (
    id           text        PRIMARY KEY CHECK (id ~ '^[a-z][a-z0-9-]{0,62}$'),
    -- The published version the draft was started from; 0 for a template that does not exist yet.
    base_version int         NOT NULL CHECK (base_version >= 0),
    definition   text        NOT NULL CHECK (length(definition) BETWEEN 2 AND 1000000),
    updated_at   timestamptz NOT NULL,
    updated_by   text        NOT NULL CHECK (length(updated_by) BETWEEN 1 AND 200)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON wf.template_draft TO keel_app;
