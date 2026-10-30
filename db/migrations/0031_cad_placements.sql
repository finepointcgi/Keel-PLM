-- Increment 9.5: assembly placements and bounding boxes of a CAD structure (ADR-0052, docs/increments/9.5-3d-viewer.md). Forward-only, runs as keel_migrator.
--   * cad.bom_proposal_occurrence: one row per usage occurrence in the structure file (one NEXT_ASSEMBLY_USAGE_OCCURRENCE in STEP) with the 4x4 child-to-parent
--     matrix the file carries (row-major, 16 numbers; NULL when the file has no usable placement for it).
--   * cad.bom_proposal_part.bbox: the part's own axis-aligned box, six numbers min x y z, max x y z, in the file's length unit (NULL when not derivable).
--   * cad.bom_proposal.length_unit / length_unit_mm: the file's length unit and its size in millimetres (NULL when the file did not say).
-- Marking model: no marking columns of their own. The rows belong to a proposal and are read only through the proposal's marking columns, exactly like
-- bom_proposal_part and bom_proposal_line (the service evaluates Read on the proposal's markings and answers 404 otherwise). The numbers are written once, when the
-- proposal is created from the bytes the caller sent, and are frozen with the rest of the proposal by the existing cad.guard_proposal trigger.
ALTER TABLE cad.bom_proposal
    ADD COLUMN length_unit    text CHECK (length_unit IS NULL OR length(length_unit) BETWEEN 1 AND 8),
    ADD COLUMN length_unit_mm double precision CHECK (length_unit_mm IS NULL OR (length_unit_mm > 0 AND length_unit_mm < 'Infinity'));

ALTER TABLE cad.bom_proposal_part
    ADD COLUMN bbox double precision[] CHECK (bbox IS NULL OR (cardinality(bbox) = 6 AND array_ndims(bbox) = 1 AND bbox[1] <= bbox[4] AND bbox[2] <= bbox[5] AND bbox[3] <= bbox[6]));

CREATE TABLE cad.bom_proposal_occurrence (
    proposal_id   uuid   NOT NULL REFERENCES cad.bom_proposal (id),
    seq           int    NOT NULL CHECK (seq >= 1),
    parent_ref    text   NOT NULL,
    child_ref     text   NOT NULL,
    occurrence_id text   NOT NULL CHECK (length(occurrence_id) <= 100),
    name          text   CHECK (name IS NULL OR length(name) <= 200),
    transform     double precision[] CHECK (transform IS NULL OR (cardinality(transform) = 16 AND array_ndims(transform) = 1)),
    PRIMARY KEY (proposal_id, seq),
    FOREIGN KEY (proposal_id, parent_ref) REFERENCES cad.bom_proposal_part (proposal_id, ref),
    FOREIGN KEY (proposal_id, child_ref)  REFERENCES cad.bom_proposal_part (proposal_id, ref)
);
CREATE INDEX bom_proposal_occurrence_parent_idx ON cad.bom_proposal_occurrence (proposal_id, parent_ref);

CREATE TRIGGER bom_proposal_occurrence_guard BEFORE INSERT OR UPDATE OR DELETE ON cad.bom_proposal_occurrence FOR EACH ROW EXECUTE FUNCTION cad.guard_proposal();

GRANT SELECT, INSERT ON cad.bom_proposal_occurrence TO keel_app;
