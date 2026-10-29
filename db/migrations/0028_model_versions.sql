-- Increment 9.1: model versioning for CAD and mesh files (ADR-0046, docs/increments/9.1-model-versions.md). Forward-only, runs as keel_migrator.
--   * mdl.iteration_metrics: what the server measured in the model file of a document iteration (triangles, bounding box, area, volume, fingerprint,
--     STEP product count). One immutable row per iteration. Rows are written only by the documents module, from the stored bytes, after they were
--     verified against their SHA-256; no API accepts these numbers from a client.
-- Marking model: nothing here carries marking columns. A row belongs to a document iteration and is read only through the document's own read filter
-- (the service authorizes Read on the document, answers 404 otherwise, and lists only iterations of that one document).
CREATE SCHEMA mdl;
GRANT USAGE ON SCHEMA mdl TO keel_app;

CREATE TABLE mdl.iteration_metrics (
    iteration_id   uuid        PRIMARY KEY REFERENCES plm.document_iteration (id),
    file_id        uuid        NOT NULL REFERENCES plm.document_file (id),
    -- The bytes that were measured (the primary file of the iteration). Not a vault key of its own: document_file.sha256 keeps the object alive.
    file_sha256    text        NOT NULL CHECK (file_sha256 ~ '^[0-9a-f]{64}$'),
    format         text        NOT NULL CHECK (length(format) BETWEEN 1 AND 16),
    status         text        NOT NULL CHECK (status IN ('analysed', 'facts_only', 'failed')),
    reason         text        CHECK (reason IS NULL OR length(reason) <= 500),
    analyser       int         NOT NULL CHECK (analyser >= 1),
    unit           text        NOT NULL CHECK (unit IN ('mm', 'units')),
    up_axis        text        NOT NULL CHECK (up_axis IN ('y', 'z')),
    triangles      bigint      CHECK (triangles >= 0),
    degenerate     bigint      CHECK (degenerate >= 0),
    vertices       bigint      CHECK (vertices >= 0),
    min_x          double precision,
    min_y          double precision,
    min_z          double precision,
    max_x          double precision,
    max_y          double precision,
    max_z          double precision,
    surface_area   double precision CHECK (surface_area IS NULL OR surface_area >= 0),
    signed_volume  double precision,
    watertight     boolean,
    geometry_fp    text        CHECK (geometry_fp IS NULL OR geometry_fp ~ '^[0-9a-f]{64}$'),
    step_products  int         CHECK (step_products IS NULL OR step_products >= 0),
    analysed_at    timestamptz NOT NULL,
    -- A row that claims numbers must carry all of them; a row that does not (facts only, failed) must carry none, so a number is never half-claimed.
    CONSTRAINT metrics_shape CHECK (
        (status = 'analysed' AND triangles IS NOT NULL AND vertices IS NOT NULL AND degenerate IS NOT NULL AND surface_area IS NOT NULL AND watertight IS NOT NULL
            AND geometry_fp IS NOT NULL AND min_x IS NOT NULL AND min_y IS NOT NULL AND min_z IS NOT NULL AND max_x IS NOT NULL AND max_y IS NOT NULL AND max_z IS NOT NULL
            AND (watertight OR signed_volume IS NULL) AND (NOT watertight OR signed_volume IS NOT NULL))
        OR (status <> 'analysed' AND triangles IS NULL AND vertices IS NULL AND degenerate IS NULL AND surface_area IS NULL AND signed_volume IS NULL AND watertight IS NULL
            AND geometry_fp IS NULL AND min_x IS NULL AND min_y IS NULL AND min_z IS NULL AND max_x IS NULL AND max_y IS NULL AND max_z IS NULL)
    )
);
CREATE INDEX iteration_metrics_fp_idx ON mdl.iteration_metrics (geometry_fp) WHERE geometry_fp IS NOT NULL;
CREATE INDEX iteration_metrics_sha_idx ON mdl.iteration_metrics (file_sha256, analyser);

-- Metrics are measurements of immutable bytes: once written they never change and are never deleted.
CREATE TRIGGER iteration_metrics_immutable BEFORE UPDATE OR DELETE ON mdl.iteration_metrics
    FOR EACH ROW EXECUTE FUNCTION plm.reject_immutable_change();

-- Only a file that belongs to the iteration may be measured for it.
CREATE FUNCTION mdl.guard_metrics_file() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM plm.document_file f WHERE f.id = NEW.file_id AND f.iteration_id = NEW.iteration_id AND f.sha256 = NEW.file_sha256) THEN
        RAISE EXCEPTION 'the measured file must be a file of the iteration, with the stated SHA-256' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER iteration_metrics_file BEFORE INSERT ON mdl.iteration_metrics
    FOR EACH ROW EXECUTE FUNCTION mdl.guard_metrics_file();

GRANT SELECT, INSERT ON mdl.iteration_metrics TO keel_app;
