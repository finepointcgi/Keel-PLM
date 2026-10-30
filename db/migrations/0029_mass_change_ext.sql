-- Increment 9.5: mass change beyond parts (ADR-0050, docs/increments/9.5-mass-change-ext.md). Forward-only, runs as keel_migrator.
--   * rpt.mass_change: the operation list grows (documents, assembly structure, requirements); a mass change may be attached to a change order (change_number),
--     and records where its selection came from (typed list, where-used of a component, uploaded CSV/Excel file).
--   * rpt.mass_change_item: per-object parameters, used when an uploaded file gives each row its own value (a part-specific attribute value, a replacement component).
-- Marking model: unchanged from 0027. Nothing here carries marking columns; a mass change belongs to its creator and the objects it names are re-authorized whenever shown.
-- The file name and the component number of a selection are stored as typed by the user; neither is marked data (a part number is an identifier, A4).
ALTER TABLE rpt.mass_change DROP CONSTRAINT mass_change_operation_check;
ALTER TABLE rpt.mass_change ADD CONSTRAINT mass_change_operation_check CHECK (operation IN (
    'set_attribute', 'change_markings', 'baseline_proposal', 'submit_review', 'watch', 'favorite',
    'set_description', 'add_component', 'replace_component', 'remove_component', 'set_requirement_field'));
ALTER TABLE rpt.mass_change ADD CONSTRAINT mass_change_kind_check CHECK (object_kind IN ('part', 'document', 'assembly', 'requirement'));

ALTER TABLE rpt.mass_change
    ADD COLUMN change_number  text CHECK (change_number IS NULL OR length(change_number) BETWEEN 1 AND 64),
    ADD COLUMN source         text NOT NULL DEFAULT 'typed' CHECK (source IN ('typed', 'where_used', 'upload')),
    ADD COLUMN source_detail  text CHECK (source_detail IS NULL OR length(source_detail) <= 200);

ALTER TABLE rpt.mass_change_item
    ADD COLUMN item_parameters jsonb CHECK (item_parameters IS NULL OR jsonb_typeof(item_parameters) = 'object');
