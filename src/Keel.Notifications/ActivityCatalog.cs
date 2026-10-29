using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Keel.Notifications;

public enum FieldKind { Text, State, ObjectRef }

/// <summary>A detail of an audit event that is safe to show under "Show details": the stored key, its label and how to present the value.</summary>
public sealed record ActivityField(string Key, string Label, FieldKind Kind = FieldKind.Text);

/// <summary>
/// One audit action and how the activity feed says it. <see cref="Sentence"/> may use <c>{actor}</c> and <c>{key}</c> for stored detail keys; when a key that the
/// sentence needs is missing, <see cref="Fallback"/> is used instead (it needs no keys). The feed never prints an action code.
/// </summary>
public sealed partial record ActivityRule(string Action, string Category, string Sentence, string? Fallback, IReadOnlyList<ActivityField> Fields)
{
    public IEnumerable<string> RequiredKeys => PlaceholderPattern().Matches(Sentence).Select(m => m.Groups[1].Value).Where(k => k != "actor");

    [GeneratedRegex(@"\{([A-Za-z]+)\}")]
    private static partial Regex PlaceholderPattern();
}

/// <summary>
/// The data table behind the activity feed (ADR-0030): which audit actions are events a person wants to read, how each is phrased, which of its stored details are
/// safe to show, which actions are views (shown to auditors on request only) and which are deliberately hidden. A test fails when an audit action of the code
/// base is in none of the three lists, so a new action cannot slip into the feed with a raw code, or into the log of views unnoticed.
/// </summary>
public static partial class ActivityCatalog
{
    private static ActivityField F(string key, string label, FieldKind kind = FieldKind.Text) => new(key, label, kind);

    private static ActivityRule R(string action, string category, string sentence, string? fallback = null, params ActivityField[] fields) => new(action, category, sentence, fallback, fields);

    private static readonly ActivityField Reason = F("reason", "Reason");
    private static readonly ActivityField Revision = F("revision", "Version");
    private static readonly ActivityField Comment = F("comment", "Comment");
    private static readonly ActivityField From = F("from", "From", FieldKind.State);
    private static readonly ActivityField To = F("to", "To", FieldKind.State);
    private static readonly ActivityField Before = F("before", "Before");
    private static readonly ActivityField After = F("after", "After");

    public static readonly IReadOnlyList<ActivityRule> Rules =
    [
        // parts
        R("part.create", "created", "{actor} created the part", null, Revision, F("markings", "Markings")),
        R("part.checkout", "checkout", "{actor} checked out the part", null, Revision),
        R("part.undo_checkout", "checkout", "{actor} undid the check-out", null, F("checkedOutBy", "Checked out by")),
        R("part.edit", "edit", "{actor} edited the working copy"),
        R("part.checkin", "version", "{actor} checked in version {revision}", "{actor} checked in a new version", Revision, Comment),
        R("part.transition", "state", "{actor} moved the part from {from} to {to}", "{actor} changed the state of the part", Revision, From, To),
        R("part.set_state", "state", "{actor} set the state from {from} to {to} (administrator override)", "{actor} set the state (administrator override)", Revision, From, To, Reason),
        R("part.markings_change", "markings", "{actor} changed the security markings", null, Before, After, Reason),
        R("part.classify", "edit", "{actor} changed the classification of the part", null, F("before", "Before"), F("after", "After")),
        R("part.link_document", "link", "{actor} linked a document", null, F("document", "Document", FieldKind.ObjectRef), F("linkType", "Link type"), F("documentRevision", "Document version")),
        R("part.unlink_document", "link", "{actor} removed a document link", null, F("document", "Document", FieldKind.ObjectRef), F("linkType", "Link type")),
        // bill of materials of a part
        R("bom.add_usage", "structure", "{actor} added a line to the bill of materials", null, F("child", "Child part", FieldKind.ObjectRef), F("find", "Find number")),
        R("bom.update_usage", "structure", "{actor} changed a line of the bill of materials", null, F("find", "Find number")),
        R("bom.remove_usage", "structure", "{actor} removed a line from the bill of materials"),
        R("bom.renumber", "structure", "{actor} renumbered the lines of the bill of materials", null, F("lines", "Lines")),
        R("bom.copy_structure", "structure", "{actor} copied a structure into the bill of materials"),
        // options, variants and usage conditions (8.4): details are option keys, variant names and counts
        R("bom.set_condition", "structure", "{actor} changed the option condition of a line", null, F("find", "Find number"), Before, After),
        R("options.model_create", "created", "{actor} created the option model of the product"),
        R("options.family_add", "edit", "{actor} added the option {family}", "{actor} added an option", F("family", "Option"), F("required", "Required")),
        R("options.family_update", "edit", "{actor} changed the option {family}", "{actor} changed an option", F("family", "Option"), F("retired", "Retired"), F("required", "Required")),
        R("options.family_delete", "edit", "{actor} removed the option {family}", "{actor} removed an option", F("family", "Option")),
        R("options.value_add", "edit", "{actor} added the value {value} to {family}", "{actor} added an option value", F("family", "Option"), F("value", "Value")),
        R("options.value_update", "edit", "{actor} changed the value {value} of {family}", "{actor} changed an option value", F("family", "Option"), F("value", "Value"), F("retired", "Retired")),
        R("options.value_delete", "edit", "{actor} removed the value {value} from {family}", "{actor} removed an option value", F("family", "Option"), F("value", "Value")),
        R("options.rule_add", "edit", "{actor} added an option rule", null, F("kind", "Kind"), F("subject", "Option"), F("members", "Related options")),
        R("options.rule_delete", "edit", "{actor} removed an option rule"),
        R("variant.create", "created", "{actor} defined the variant {variant}", "{actor} defined a variant", F("variant", "Variant")),
        R("variant.update", "edit", "{actor} changed the variant {variant}", "{actor} changed a variant", F("variant", "Variant")),
        R("variant.release", "state", "{actor} released the variant {variant}", "{actor} released a variant", F("variant", "Variant")),
        R("variant.retire", "state", "{actor} retired the variant {variant}", "{actor} retired a variant", F("variant", "Variant")),
        R("variant.delete", "edit", "{actor} deleted the draft variant {variant}", "{actor} deleted a draft variant", F("variant", "Variant")),
        // manufacturing process data (8.7): details are numbers, codes and counts only; step text, names and notes are never written to the audit log
        R("process.plan_create", "created", "{actor} created the process plan", null, F("mbom", "Manufacturing BOM", FieldKind.ObjectRef), F("mbomRevision", "MBOM revision"), F("markings", "Markings")),
        R("process.plan_checkout", "checkout", "{actor} checked out the process plan", null, Revision),
        R("process.plan_undo_checkout", "checkout", "{actor} discarded the working copy of the process plan", null, F("discarded", "Discarded version")),
        R("process.plan_edit", "edit", "{actor} edited the working copy of the process plan", null, Revision, F("routings", "Routings"), F("operations", "Operations"), F("steps", "Steps")),
        R("process.plan_checkin", "version", "{actor} checked in version {revision} of the process plan", "{actor} checked in a new version of the process plan", Revision, Comment),
        R("process.plan_revise", "version", "{actor} started revision {to} of the process plan", "{actor} started a new revision of the process plan", F("from", "From"), F("to", "To"), F("mbomRevision", "MBOM revision")),
        R("process.plan_rebind", "edit", "{actor} bound the process plan to MBOM revision {to}", "{actor} bound the process plan to another MBOM revision", F("from", "From"), F("to", "To")),
        R("process.plan_submit", "review", "{actor} submitted the process plan for review", null, Revision, F("mbomRevision", "MBOM revision")),
        R("process.plan_release", "state", "The review released the process plan", null, Revision, F("mbomRevision", "MBOM revision")),
        R("process.plan_review_cancel", "review", "The review of the process plan ended without release", null, Revision),
        R("tool.create", "created", "{actor} created the tool", null, F("kind", "Kind"), F("plant", "Plant"), F("calibration", "Calibration"), F("markings", "Markings")),
        R("tool.update", "edit", "{actor} edited the tool", null, F("calibration", "Calibration")),
        R("tool.calibrate", "edit", "{actor} recorded a calibration, next due {nextDue}", "{actor} recorded a calibration", F("calibratedOn", "Calibrated on"), F("nextDue", "Next due")),
        R("tool.retire", "state", "{actor} retired the tool", null, Reason),
        R("tool.restore", "state", "{actor} restored the tool", null, Reason),
        R("tool.calibration_notice", "state", "The calibration needs attention: it is due soon or overdue", null, F("stage", "Stage"), F("due", "Due")),
        // documents
        R("document.create", "created", "{actor} created the document", null, Revision, F("file", "File"), F("markings", "Markings")),
        R("document.checkout", "checkout", "{actor} checked out the document", null, Revision),
        R("document.undo_checkout", "checkout", "{actor} undid the check-out", null, F("checkedOutBy", "Checked out by")),
        R("document.edit", "edit", "{actor} edited the working copy"),
        R("document.file_add", "file", "{actor} added a file", null, F("file", "File"), F("role", "Role")),
        R("document.file_remove", "file", "{actor} removed a file", null, F("file", "File"), F("role", "Role")),
        R("document.checkin", "version", "{actor} checked in version {revision}", "{actor} checked in a new version", Revision, Comment),
        R("document.version_upload", "version", "{actor} uploaded version {revision}", "{actor} uploaded a new version", Revision, Comment, F("previous", "Previous version")),
        R("document.version_restore", "version", "{actor} restored version {restoredFrom} as {revision}", "{actor} restored an earlier version", Revision, F("restoredFrom", "Restored from"), Comment),
        R("document.revise", "version", "{actor} revised the document to {revision}", "{actor} revised the document", Revision, F("from", "From version"), Reason),
        R("document.transition", "state", "{actor} moved the document from {from} to {to}", "{actor} changed the state of the document", Revision, From, To),
        R("document.set_state", "state", "{actor} set the state from {from} to {to} (administrator override)", "{actor} set the state (administrator override)", Revision, From, To, Reason),
        R("document.markings_change", "markings", "{actor} changed the security markings", null, Before, After, Reason),
        R("document.quarantine", "state", "{actor} quarantined a file", null, F("file", "File"), Reason),
        R("document.quarantine_release", "state", "{actor} released a file from quarantine", null, F("file", "File"), Reason),
        // changes
        R("change.create", "created", "{actor} created the change", null, F("type", "Type"), F("class", "Class"), F("markings", "Markings")),
        R("change.update", "edit", "{actor} edited the change", null, F("title", "Title"), F("priority", "Priority")),
        R("change.classify", "edit", "{actor} classified the change", null, F("from", "From"), F("to", "To")),
        R("change.add_affected", "link", "{actor} added an object to the change", null, F("object", "Object", FieldKind.ObjectRef), Revision, F("action", "Action")),
        R("change.remove_affected", "link", "{actor} removed an object from the change", null, F("object", "Object", FieldKind.ObjectRef)),
        R("change.set_effectivity", "edit", "{actor} set the effectivity of an object", null, F("object", "Object", FieldKind.ObjectRef), F("effectivity", "Effectivity")),
        R("change.markings_change", "markings", "{actor} changed the security markings", null, Before, After, Reason),
        R("change.markings_raise", "markings", "{actor} raised the markings of the change to cover its objects"),
        R("change.submit", "review", "{actor} submitted the change for review"),
        R("change.transition", "state", "{actor} moved the change from {from} to {to}", "{actor} changed the state of the change", From, To, Reason),
        R("change.approve", "review", "The change request was approved"),
        R("change.reject", "review", "The change request was rejected"),
        R("change.release", "review", "{actor} released the change notice", null, F("released", "Released"), F("obsoleted", "Obsoleted")),
        R("change.review_cancel", "review", "The review of the change was cancelled"),
        R("change.task_create", "change", "{actor} created change task {task}", "{actor} created a change task", F("task", "Task"), F("title", "Title"), F("assignee", "Assigned to")),
        R("change.task_start", "change", "{actor} started change task {task}", "{actor} started a change task", F("task", "Task")),
        R("change.task_complete", "change", "{actor} completed change task {task}", "{actor} completed a change task", F("task", "Task"), F("note", "Note")),
        R("change.task_cancel", "change", "{actor} cancelled change task {task}", "{actor} cancelled a change task", F("task", "Task"), F("note", "Reason")),
        // baselines
        R("baseline.create", "created", "{actor} created the baseline"),
        R("baseline.update", "edit", "{actor} edited the baseline", null, F("name", "Name"), F("milestone", "Milestone")),
        R("baseline.submit", "review", "{actor} submitted the baseline for review"),
        R("baseline.establish", "baseline", "{actor} established the baseline", null, F("root", "Root"), F("items", "Items"), F("documents", "Documents"), F("banner", "Markings")),
        R("baseline.review_cancel", "review", "The review of the baseline was cancelled"),
        R("baseline.supersede", "baseline", "{actor} superseded the baseline", null, Reason),
        R("baseline.archive", "baseline", "{actor} archived the baseline", null, Reason),
        // requirement specifications (8.3): details are keys, numbers, codes and counts, never requirement text
        R("requirement.create", "created", "{actor} created the requirement specification"),
        R("requirement.edit", "structure", "{actor} changed the requirements", null, F("created", "Added"), F("updated", "Changed"), F("moved", "Moved"), F("suspectLinks", "Links now suspect")),
        R("requirement.import", "structure", "{actor} imported requirements from a file", null, F("format", "Format"), F("created", "Added"), F("updated", "Changed")),
        R("requirement.revise", "version", "{actor} started revision {to}", "{actor} started a new revision", F("from", "From revision"), F("to", "New revision"), Reason),
        R("requirement.submit", "review", "{actor} submitted the specification for review"),
        R("requirement.review_cancel", "review", "The review of the specification was cancelled"),
        R("requirement.release", "state", "{actor} released the specification"),
        R("requirement.transition", "state", "{actor} moved the specification from {from} to {to}", "{actor} changed the state of the specification", From, To, Reason),
        R("requirement.markings_change", "markings", "{actor} changed the security markings", null, Before, After, Reason),
        R("requirement.link_add", "link", "{actor} linked requirement {key}", "{actor} added a traceability link", F("key", "Requirement"), F("linkType", "Relation"), F("target", "Linked to")),
        R("requirement.link_remove", "link", "{actor} removed a link of requirement {key}", "{actor} removed a traceability link", F("key", "Requirement")),
        R("requirement.link_review", "link", "{actor} reviewed a suspect link of requirement {key}", "{actor} reviewed a suspect link", F("key", "Requirement"), F("linkType", "Relation")),
        R("requirement.link_suspect", "link", "A link of requirement {key} became suspect", "A traceability link became suspect", F("key", "Requirement"), F("linkType", "Relation"), F("reason", "Why")),
        R("requirement.verify", "review", "{actor} recorded a verification of requirement {key}", "{actor} recorded a verification", F("key", "Requirement"), F("result", "Result"), F("formal", "Formal")),
        // manufacturing BOMs
        R("mbom.create", "created", "{actor} created the manufacturing BOM"),
        R("mbom.checkout", "checkout", "{actor} checked out the manufacturing BOM"),
        R("mbom.undo_checkout", "checkout", "{actor} undid the check-out"),
        R("mbom.checkin", "version", "{actor} checked in a new version of the manufacturing BOM", null, Revision, Comment),
        R("mbom.revise", "version", "{actor} started a new revision of the manufacturing BOM"),
        R("mbom.submit", "review", "{actor} submitted the manufacturing BOM for review"),
        R("mbom.review_cancel", "review", "The review of the manufacturing BOM was cancelled"),
        R("mbom.release", "state", "{actor} released the manufacturing BOM"),
        R("mbom.transition", "state", "{actor} moved the manufacturing BOM from {from} to {to}", "{actor} changed the state of the manufacturing BOM", From, To),
        R("mbom.add_line", "structure", "{actor} added a line"),
        R("mbom.update_line", "structure", "{actor} changed a line"),
        R("mbom.remove_line", "structure", "{actor} removed a line"),
        R("mbom.set_links", "structure", "{actor} changed the links of a line"),
        R("mbom.set_derivation", "structure", "{actor} changed how the manufacturing BOM is derived"),
        R("mbom.generate", "structure", "{actor} generated the manufacturing BOM from the engineering BOM"),
        R("mbom.regenerate_apply", "structure", "{actor} applied a regeneration from the engineering BOM"),
        R("mbom.reconcile", "structure", "{actor} reconciled the manufacturing BOM with the engineering BOM"),
        R("mbom.markings_change", "markings", "{actor} changed the security markings", null, Before, After, Reason),
        // nonconformance report
        R("ncr.create", "created", "{actor} created the nonconformance report", null, F("markings", "Markings"), F("part", "Part", FieldKind.ObjectRef), F("revision", "Version"), F("severity", "Severity"), F("quantity", "Quantity")),
        R("ncr.update", "edit", "{actor} edited the nonconformance report", null),
        R("ncr.markings_change", "markings", "{actor} changed the security markings", null, Before, After, Reason),
        R("ncr.submit", "review", "{actor} submitted the nonconformance report for review", null),
        R("ncr.review_cancel", "review", "The review of the nonconformance report was cancelled", null),
        R("ncr.review_reject", "review", "The review of the nonconformance report was rejected", null),
        R("ncr.link_add", "link", "{actor} added a link", null, F("target", "Linked record", FieldKind.ObjectRef)),
        R("ncr.link_remove", "link", "{actor} removed a link", null, F("target", "Linked record", FieldKind.ObjectRef)),
        R("ncr.action_create", "change", "{actor} added an action item", null, F("action", "Action"), F("type", "Type"), F("owner", "Owner"), F("due", "Due")),
        R("ncr.action_start", "change", "{actor} started an action item", null, F("action", "Action"), F("type", "Type"), F("owner", "Owner"), F("due", "Due")),
        R("ncr.action_complete", "change", "{actor} completed an action item", null, F("action", "Action"), F("type", "Type"), F("owner", "Owner"), F("due", "Due")),
        R("ncr.action_cancel", "change", "{actor} cancelled an action item", null, F("action", "Action"), F("type", "Type"), F("owner", "Owner"), F("due", "Due")),
        R("ncr.action_overdue", "change", "An action item became overdue", null, F("action", "Action")),
        R("ncr.propose_disposition", "edit", "{actor} proposed a disposition", null, F("disposition", "Disposition")),
        R("ncr.disposition_approved", "review", "The review board approved the disposition", null),
        R("ncr.close", "state", "{actor} closed the report", null, From, To),
        R("ncr.cancel", "state", "{actor} cancelled the report", null, From, To),
        // corrective action
        R("capa.create", "created", "{actor} created the corrective action", null, F("markings", "Markings"), F("type", "Type"), F("source", "Source"), F("owner", "Owner")),
        R("capa.update", "edit", "{actor} edited the corrective action", null),
        R("capa.markings_change", "markings", "{actor} changed the security markings", null, Before, After, Reason),
        R("capa.submit", "review", "{actor} submitted the corrective action for review", null),
        R("capa.review_cancel", "review", "The review of the corrective action was cancelled", null),
        R("capa.review_reject", "review", "The review of the corrective action was rejected", null),
        R("capa.link_add", "link", "{actor} added a link", null, F("target", "Linked record", FieldKind.ObjectRef)),
        R("capa.link_remove", "link", "{actor} removed a link", null, F("target", "Linked record", FieldKind.ObjectRef)),
        R("capa.action_create", "change", "{actor} added an action item", null, F("action", "Action"), F("type", "Type"), F("owner", "Owner"), F("due", "Due")),
        R("capa.action_start", "change", "{actor} started an action item", null, F("action", "Action"), F("type", "Type"), F("owner", "Owner"), F("due", "Due")),
        R("capa.action_complete", "change", "{actor} completed an action item", null, F("action", "Action"), F("type", "Type"), F("owner", "Owner"), F("due", "Due")),
        R("capa.action_cancel", "change", "{actor} cancelled an action item", null, F("action", "Action"), F("type", "Type"), F("owner", "Owner"), F("due", "Due")),
        R("capa.action_overdue", "change", "An action item became overdue", null, F("action", "Action")),
        R("capa.analysis_save", "edit", "{actor} saved the root cause analysis", null, F("whys", "Whys"), F("causes", "Causes")),
        R("capa.verification_plan", "edit", "{actor} saved the verification plan", null, F("due", "Verification due")),
        R("capa.verification_record", "edit", "{actor} recorded the verification result", null, F("effective", "Effective")),
        R("capa.start", "state", "{actor} started the work", null, From, To),
        R("capa.close", "review", "The verification was signed and the corrective action closed", null),
        R("capa.cancel", "state", "{actor} cancelled the corrective action", null, From, To),
        // deviation or waiver
        R("deviation.create", "created", "{actor} created the deviation or waiver", null, F("markings", "Markings"), F("kind", "Kind"), F("from", "From"), F("to", "To"), F("customerApproval", "Customer approval")),
        R("deviation.update", "edit", "{actor} edited the deviation or waiver", null),
        R("deviation.markings_change", "markings", "{actor} changed the security markings", null, Before, After, Reason),
        R("deviation.submit", "review", "{actor} submitted the deviation or waiver for review", null),
        R("deviation.review_cancel", "review", "The review of the deviation or waiver was cancelled", null),
        R("deviation.review_reject", "review", "The review of the deviation or waiver was rejected", null),
        R("deviation.link_add", "link", "{actor} added a link", null, F("target", "Linked record", FieldKind.ObjectRef)),
        R("deviation.link_remove", "link", "{actor} removed a link", null, F("target", "Linked record", FieldKind.ObjectRef)),
        R("deviation.scope_add", "link", "{actor} added a part to the scope", null, F("part", "Part", FieldKind.ObjectRef), F("serials", "Serials"), F("lots", "Lots")),
        R("deviation.scope_remove", "link", "{actor} removed a part from the scope", null, F("part", "Part", FieldKind.ObjectRef)),
        R("deviation.approve", "review", "The deviation or waiver was approved", null, F("to", "To", FieldKind.State)),
        R("deviation.customer_approval", "review", "{actor} recorded the customer approval", null, F("approvedOn", "Approved on")),
        R("deviation.customer_reject", "state", "{actor} recorded that the customer declined", null, From, To, Reason),
        R("deviation.withdraw", "state", "{actor} withdrew the request", null, From, To),
        R("deviation.revoke", "state", "{actor} revoked the approval", null, From, To),
        R("deviation.expire", "state", "The approved window ended and the record expired", null, From, To),
        R("deviation.expiry_warning", "state", "The approved window ends soon", null, F("days", "Days ahead")),
        // reviews (workflow), for every reviewable object
        R("workflow.start", "review", "{actor} submitted for review", null, Revision, F("iteration", "Iteration"), F("participants", "Participants")),
        R("workflow.task_assign", "review", "{actor} asked {assignee} for {name}", "{actor} assigned a review task", F("name", "Task"), F("assignee", "Assigned to")),
        R("workflow.sign", "review", "{actor} signed and {decision}", "{actor} signed", F("meaning", "Meaning"), F("decision", "Decision"), Revision, F("iteration", "Iteration")),
        R("workflow.complete", "review", "The review finished: {outcome}", "The review finished", F("outcome", "Outcome"), Revision, F("iteration", "Iteration")),
        R("workflow.cancel", "review", "{actor} cancelled the review", null, Reason),
        R("workflow.reassign", "review", "{actor} reassigned a review task from {from} to {to}", "{actor} reassigned a review task", F("from", "From"), F("to", "To"), Reason),
        // suppliers and sourcing (8.2): details are numbers, codes and counts only; names and contacts are never written to the audit log
        R("supplier.create", "created", "{actor} created the supplier", null, F("roles", "Roles"), F("markings", "Markings")),
        R("supplier.update", "edit", "{actor} edited the supplier", null, F("clearanceProfile", "Clearance profile")),
        R("supplier.markings_change", "markings", "{actor} changed the security markings", null, Before, After, Reason),
        R("supplier.propose", "state", "{actor} proposed the status {proposed} for approval", "{actor} proposed a status for approval", From, F("proposed", "Proposed status", FieldKind.State)),
        R("supplier.submit", "review", "{actor} submitted the supplier for approval", null, F("proposed", "Proposed status", FieldKind.State)),
        R("supplier.approve", "state", "The review approved the supplier as {to}", "The review approved the supplier", From, To),
        R("supplier.review_cancel", "review", "The approval review ended without approval"),
        R("supplier.status", "state", "{actor} changed the status from {from} to {to}", "{actor} changed the status", From, To, Reason),
        R("supplier.cert_add", "edit", "{actor} added a {certType} certification", "{actor} added a certification", F("certType", "Certification"), F("expires", "Expires")),
        R("supplier.cert_revoke", "edit", "{actor} revoked a {certType} certification", "{actor} revoked a certification", F("certType", "Certification"), Reason),
        R("supplier.cert_expiry_notice", "state", "A {certType} certification needs attention: it expires soon or has expired", "A certification needs attention: it expires soon or has expired", F("certType", "Certification"), F("expires", "Expires")),
        R("supplier.contact_add", "edit", "{actor} added a contact"),
        R("supplier.contact_remove", "edit", "{actor} removed a contact"),
        R("supplier.quality_ref_add", "link", "{actor} linked a quality record", null, F("refKind", "Kind"), F("refId", "Record")),
        R("supplier.quality_ref_remove", "link", "{actor} removed a quality record link"),
        R("supplier.portal_link", "edit", "{actor} gave a user access to the supplier portal"),
        R("supplier.portal_unlink", "edit", "{actor} removed a user's access to the supplier portal"),
        R("sourcing.entry_create", "link", "{actor} added the list entry {entry}", "{actor} added a list entry", F("entry", "Entry"), F("list", "List"), F("supplier", "Supplier"), F("status", "Status")),
        R("sourcing.entry_update", "edit", "{actor} edited the draft list entry {entry}", "{actor} edited a draft list entry", F("entry", "Entry"), F("status", "Status")),
        R("sourcing.entry_submit", "review", "{actor} submitted the list entry {entry} for approval", "{actor} submitted a list entry for approval", F("entry", "Entry"), F("list", "List")),
        R("sourcing.entry_approve", "state", "The review approved the list entry {entry}", "The review approved a list entry", F("entry", "Entry"), F("list", "List"), F("status", "Status")),
        R("sourcing.entry_review_cancel", "review", "The approval review of the list entry {entry} ended without approval", "The approval review of a list entry ended without approval", F("entry", "Entry")),
        R("sourcing.entry_withdraw", "state", "{actor} withdrew the list entry {entry}", "{actor} withdrew a list entry", F("entry", "Entry"), Reason),
        R("sourcing.entry_end", "state", "{actor} ended the list entry {entry}", "{actor} ended a list entry", F("entry", "Entry"), F("endsOn", "Ends on"), Reason),
        R("sourcing.relation_add", "link", "{actor} related the part to {related}", "{actor} related another part", F("related", "Part", FieldKind.ObjectRef), F("relation", "Relation")),
        R("sourcing.relation_remove", "link", "{actor} removed a related part"),
        // product compliance (8.5): events of a part's declarations; details are declaration numbers, codes and counts only, never names, notes or reasons
        R("compliance.declaration_create", "created", "{actor} added the compliance declaration {declaration}", "{actor} added a compliance declaration", F("declaration", "Declaration"), F("source", "Source"), F("status", "Status", FieldKind.State)),
        R("compliance.declaration_update", "edit", "{actor} changed the compliance declaration {declaration}", "{actor} changed a compliance declaration", F("declaration", "Declaration"), F("status", "Status", FieldKind.State)),
        R("compliance.declaration_verify", "state", "{actor} verified the compliance declaration {declaration}", "{actor} verified a compliance declaration", F("declaration", "Declaration"), F("status", "Status", FieldKind.State)),
        R("compliance.declaration_withdraw", "state", "{actor} withdrew the compliance declaration {declaration}", "{actor} withdrew a compliance declaration", F("declaration", "Declaration"), From),
        R("compliance.document_link", "link", "{actor} linked the document {document} to the compliance declaration {declaration}", "{actor} linked a document to a compliance declaration", F("declaration", "Declaration"), F("document", "Document", FieldKind.ObjectRef)),
        R("compliance.document_unlink", "link", "{actor} removed the link to the document {document} from the compliance declaration {declaration}", "{actor} removed a document link from a compliance declaration", F("declaration", "Declaration"), F("document", "Document", FieldKind.ObjectRef)),
        R("compliance.status_change", "state", "The status of the compliance declaration {declaration} changed from {from} to {to}", "The status of a compliance declaration changed", F("declaration", "Declaration"), From, To, F("cause", "Cause")),
        R("compliance.expiry_notice", "state", "The compliance declaration {declaration} needs attention: it expires soon or has expired", "A compliance declaration needs attention: it expires soon or has expired", F("declaration", "Declaration"), F("stage", "Stage"), F("expires", "Expires")),
        // comments
        R("comment.create", "comment", "{actor} commented"),
        R("comment.delete", "comment", "{actor} removed a comment", null, F("by", "Removed as")),
    ];

    private static readonly FrozenDictionary<string, ActivityRule> ByAction = Rules.ToFrozenDictionary(r => r.Action, StringComparer.Ordinal);

    public static ActivityRule? Find(string action) => ByAction.GetValueOrDefault(action);

    /// <summary>Views of an object: never shown in the feed, shown to an auditor who asks for them. The value is how the event reads.</summary>
    public static readonly IReadOnlyDictionary<string, string> Views = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["part.read"] = "{actor} viewed the part",
        ["part.search"] = "{actor} found the part in a search",
        ["part.read_document_links"] = "{actor} viewed the document links",
        ["part.classification_read"] = "{actor} viewed the classification",
        ["part.classification_search"] = "{actor} found the part in a classification search",
        ["part.json"] = "{actor} read the part through the interface",
        ["document.read"] = "{actor} viewed the document",
        ["document.search"] = "{actor} found the document in a search",
        ["document.view"] = "{actor} opened a file in the viewer",
        ["document.download"] = "{actor} downloaded a file",
        ["document.model_history"] = "{actor} viewed the version history",
        ["document.model_compare"] = "{actor} compared two versions",
        ["document.versions_download"] = "{actor} downloaded versions as a ZIP",
        ["document.read_part_links"] = "{actor} viewed the part links",
        ["change.read"] = "{actor} viewed the change",
        ["change.search"] = "{actor} found the change in a search",
        ["change.for_object"] = "{actor} listed the changes of an object",
        ["change.impact"] = "{actor} ran an impact analysis",
        ["change.impact_export"] = "{actor} exported the impact map",
        ["change.task_list"] = "{actor} listed change tasks",
        ["baseline.read"] = "{actor} viewed the baseline",
        ["baseline.search"] = "{actor} found the baseline in a search",
        ["baseline.summary"] = "{actor} viewed the baseline summary",
        ["baseline.compare"] = "{actor} compared baselines",
        ["baseline.compare_current"] = "{actor} compared the baseline with the current structure",
        ["baseline.explode"] = "{actor} exploded the baseline structure",
        ["baseline.export"] = "{actor} exported the baseline",
        ["baseline.verify"] = "{actor} verified the baseline",
        ["baseline.documents"] = "{actor} listed the baseline documents",
        ["mbom.read"] = "{actor} viewed the manufacturing BOM",
        ["mbom.read_released"] = "{actor} viewed the released manufacturing BOM",
        ["mbom.search"] = "{actor} found the manufacturing BOM in a search",
        ["mbom.lines"] = "{actor} viewed the lines",
        ["mbom.explode"] = "{actor} exploded the manufacturing BOM",
        ["mbom.export"] = "{actor} exported the manufacturing BOM",
        ["mbom.compare"] = "{actor} compared manufacturing BOMs",
        ["mbom.summary"] = "{actor} viewed the summary",
        ["mbom.side_by_side"] = "{actor} compared the engineering and manufacturing BOMs",
        ["mbom.ebom_lines"] = "{actor} viewed the engineering BOM lines",
        ["mbom.where_used"] = "{actor} ran a where-used",
        ["mbom.usage"] = "{actor} viewed a usage",
        ["mbom.usage_link"] = "{actor} viewed a usage link",
        ["mbom.working_read"] = "{actor} viewed the working copy",
        ["mbom.working_link"] = "{actor} viewed a working link",
        ["mbom.working_usage"] = "{actor} viewed the working usage",
        ["mbom.impact"] = "{actor} ran an impact analysis",
        ["mbom.regenerate_propose"] = "{actor} previewed a regeneration",
        ["mbom.reconciliation_read"] = "{actor} viewed a reconciliation",
        ["mbom.reconciliation_history"] = "{actor} viewed the reconciliation history",
        ["bom.explode"] = "{actor} exploded the bill of materials",
        ["bom.where_used"] = "{actor} ran a where-used",
        ["bom.compare"] = "{actor} compared bills of materials",
        ["bom.compare_units"] = "{actor} compared bills of materials by unit",
        ["bom.summary"] = "{actor} viewed the bill of materials summary",
        ["bom.export"] = "{actor} exported the bill of materials",
        ["bom.working_read"] = "{actor} viewed the working bill of materials",
        ["variant.view"] = "{actor} viewed the options and variants of the product",
        ["variant.check"] = "{actor} checked a combination of options",
        ["variant.resolve"] = "{actor} resolved the structure for a variant",
        ["bom.compare_variants"] = "{actor} compared two variants of the product",
        ["process.search"] = "{actor} found the process plan in a search",
        ["process.read"] = "{actor} viewed the process plan",
        ["process.working_read"] = "{actor} opened the working copy of the process plan",
        ["process.shopfloor_read"] = "{actor} opened the shop floor view of the process plan",
        ["tool.read"] = "{actor} viewed the tool",
        ["tool.search"] = "{actor} found the tool in a search",
        ["supplier.read"] = "{actor} viewed the supplier",
        ["supplier.search"] = "{actor} found the supplier in a search",
        ["sourcing.part_read"] = "{actor} viewed the sourcing of the part",
        ["sourcing.entry_read"] = "{actor} viewed a list entry",
        ["sourcing.where_sourced"] = "{actor} ran a where-sourced query",
        ["sourcing.coverage"] = "{actor} ran a sourcing coverage report",
        ["ncr.read"] = "{actor} viewed the report",
        ["ncr.search"] = "{actor} found the report in a search",
        ["capa.read"] = "{actor} viewed the corrective action",
        ["capa.search"] = "{actor} found the corrective action in a search",
        ["deviation.read"] = "{actor} viewed the deviation or waiver",
        ["deviation.search"] = "{actor} found the deviation or waiver in a search",
        ["deviation.for_part"] = "{actor} listed the deviations and waivers of the part",
        ["compliance.part_read"] = "{actor} viewed the compliance of the part",
        ["compliance.declaration_read"] = "{actor} viewed a compliance declaration",
        ["compliance.declaration_search"] = "{actor} searched compliance declarations",
        ["compliance.rollup"] = "{actor} ran a compliance roll-up",
        ["compliance.export"] = "{actor} exported a compliance report",
        ["requirement.read"] = "{actor} viewed the requirement specification",
        ["requirement.search"] = "{actor} found the requirement specification in a search",
        ["requirement.list"] = "{actor} viewed the requirements",
        ["requirement.links"] = "{actor} viewed the links of a requirement",
        ["requirement.verifications"] = "{actor} viewed the verification records of a requirement",
        ["requirement.trace"] = "{actor} viewed the traceability",
        ["requirement.coverage"] = "{actor} viewed the coverage",
        ["requirement.export"] = "{actor} exported the requirements",
        ["requirement.import_preview"] = "{actor} previewed an import of requirements",
        ["requirement.object_trace"] = "{actor} viewed the requirements traced to this object",
        ["workflow.read"] = "{actor} viewed a review",
        ["workflow.task_read"] = "{actor} viewed a review task",
        ["workflow.status"] = "{actor} viewed the review status",
        ["workflow.map"] = "{actor} viewed the review progress map",
        ["workflow.verify"] = "{actor} verified a review's signatures",
    };

    /// <summary>
    /// Actions that are deliberately not in the feed: not an event about one object a person can open (administration, sessions, integration, the system's own work),
    /// a refusal or a failure (the feed shows successes only; an auditor sees denials with views), or a technical step inside another event.
    /// </summary>
    public static readonly FrozenSet<string> Hidden = new[]
    {
        "access.denied", "audit.checkpoint", "audit.read", "audit.verify", "session.lock", "session.lock_expired", "session.logout", "session.step_up", "session.terminated_idle", "session.unlock",
        "siem.chain_break", "vault.gc", "vault.gc_report", "vault.scan_coverage", "migration.batch.end", "migration.batch.start", "migration.classification.classes", "migration.object.import",
        "effectivity.context_create", "effectivity.context_list", "effectivity.context_read", "effectivity.part_read", "classification.json", "file.quarantined",
        "document.rescan", "document.read_denied", "document.upload_infected", "document.upload_refused", "part.not_readable", "baseline.established", "baseline.not_established",
        "mbom.plant_save", "mbom.supply_type_save", "tdp.too_many_documents", "tdp.too_many_parts", "bom.published", "bom.structure.published", "mbom.released",
        "workflow.task_skip", "workflow.list", "workflow.tasks_list", "workflow.templates_list", "workflow.template_upload", "workflow.users_search",
        "workspace.read", "workspace.saved_search.create", "workspace.saved_search.delete", "workspace.saved_search.replace", "activity.read", "comment.read", "watch.set",
        "integration.event_skipped", "quality.my_actions", "quality.dashboard", "process.work_center_save",
        // Product compliance (8.5) reference data and list publication: administration, not events of one object a person can open.
        "compliance.substance_create", "compliance.substance_update", "compliance.substance_read", "compliance.substance_search", "compliance.material_create", "compliance.material_update",
        "compliance.material_read", "compliance.list_create", "compliance.list_read", "compliance.list_draft_create", "compliance.list_draft_save", "compliance.list_draft_discard",
        "compliance.list_submit", "compliance.list_publish", "compliance.list_review_cancel",
        "requirement.link_type_save", "requirement.sweep",
        // Refusal reason codes that happen to look like actions (they are written into the details of a denial, never as an event's action).
        "comment.not_author_or_admin", "workflow.cancel_not_starter_or_admin",
    }.Concat(Keel.Core.EventTypes.All).ToFrozenSet(StringComparer.Ordinal); // the event types of the ERP contract are outbox events, not audit actions

    /// <summary>Whole namespaces that belong to administration and operations, not to an object's story.</summary>
    public static readonly IReadOnlyList<string> HiddenPrefixes = ["admin.", "integration.", "migration.", "siem.", "vault.", "audit.", "session.", "tdp.", "workspace.", "records.", "output."];

    public static bool IsHidden(string action) => Hidden.Contains(action) || HiddenPrefixes.Any(p => action.StartsWith(p, StringComparison.Ordinal));

    /// <summary>True for an action the catalog accounts for: an event of the feed, a view, or deliberately hidden.</summary>
    public static bool IsKnown(string action) => ByAction.ContainsKey(action) || Views.ContainsKey(action) || IsHidden(action);

    /// <summary>The actions the feed shows to everyone who can read the object.</summary>
    public static readonly IReadOnlyList<string> EventActions = [.. Rules.Select(r => r.Action)];

    /// <summary>"InWork" becomes "In work", "UnderReview" becomes "Under review".</summary>
    public static string Prettify(string code)
    {
        var spaced = SplitWords().Replace(code, " $1").Trim().Replace('_', ' ');
        return spaced.Length == 0 ? code : char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }

    [GeneratedRegex(@"(?<=[a-z])([A-Z])")]
    private static partial Regex SplitWords();
}
