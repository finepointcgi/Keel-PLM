-- Gap wave C: document review and release through the workflow engine (ADR-0033, docs/increments/7.6-doc-review-licenses.md). Forward-only, runs as keel_migrator.
--   1. In Work <-> Under Review of every version of the document lifecycle family become `workflow` transitions, exactly as migration 0006 did for parts:
--      a document enters and leaves review only through the workflow engine (submit, cancel, reject), and Released is reached only by an approved review.
--      Administrators keep the audited "set state" override. The migrator is exempt from the published-lifecycle guard (migration 0009), so the
--      seeded and any administrator-created versions of the family are corrected in place; nothing else about a lifecycle changes.
--   2. The seeded workflow template for documents (ordinary data: administrators replace it by uploading a new version, or pin another one).
-- No table, column or privilege changes: the engine's tables already carry the object type as data.

UPDATE plm.lifecycle_transition SET via = 'workflow'
WHERE lifecycle_id IN (SELECT id FROM plm.lifecycle WHERE family = 'document-default')
  AND ((from_state = 'InWork' AND to_state = 'UnderReview') OR (from_state = 'UnderReview' AND to_state = 'InWork'));

INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by)
VALUES ('document-release-review', 1, 'Document release review',
        'Technical review by one reviewer, then parallel approval where every chosen approver must approve, then release. The signatures cover the description and every file of the version by SHA-256.',
        'document', $tpl$
{
  "id": "document-release-review",
  "name": "Document release review",
  "description": "Technical review by one reviewer, then parallel approval where every chosen approver must approve, then release. The signatures cover the description and every file of the version by SHA-256.",
  "objectType": "document",
  "slots": [
    { "name": "reviewer",  "label": "Technical reviewer", "role": "plm-viewer",   "min": 1, "max": 1 },
    { "name": "approvers", "label": "Approvers",          "role": "plm-approver", "min": 1, "max": 10 }
  ],
  "start": "flow",
  "nodes": [
    { "id": "flow",     "type": "Serial",   "name": "Document release review", "steps": ["tech-review", "approval", "release"] },
    { "id": "tech-review", "type": "Task",  "name": "Technical review", "kind": "review",
      "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 5 },
    { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" },
      "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
    { "id": "release",  "type": "End",      "name": "Release", "outcome": "Release" }
  ]
}
$tpl$::jsonb, true, now(), 'system');
