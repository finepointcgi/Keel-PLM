using System.Collections.Immutable;
using System.Data.Common;

namespace Keel.Core;

/// <summary>
/// The kinds of object that notifications, watch lists, comments and activity feeds can point at (UX-4, ADR-0030). A pointer is a kind plus a business
/// number; everything else about the object is read again, under the viewer's own access, whenever it is shown.
/// </summary>
public static class AwarenessKinds
{
    public const string Part = "part";
    public const string Document = "document";
    public const string Change = "change";
    public const string Baseline = "baseline";
    public const string Mbom = "mbom";
    /// <summary>Quality records (8.1): nonconformance reports, corrective and preventive actions, deviations and waivers.</summary>
    public const string Ncr = "ncr";
    public const string Capa = "capa";
    public const string Deviation = "deviation";
    public const string Supplier = "supplier";
    public const string ReqSpec = "reqspec";
    /// <summary>8.7 manufacturing process data: a process plan (routing, operations, work instructions) and a tool, fixture or gauge.</summary>
    public const string ProcessPlan = "process_plan";
    public const string Tool = "tool";

    public static readonly ImmutableArray<string> All = [Part, Document, Change, Baseline, Mbom, Supplier, Ncr, Capa, Deviation, ReqSpec, ProcessPlan, Tool];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);

    /// <summary>The route of an object's page (relative to the application base).</summary>
    public static string Path(string kind, string number) => kind switch
    {
        Part => "parts/" + Uri.EscapeDataString(number),
        Document => "documents/" + Uri.EscapeDataString(number),
        Change => "changes/" + Uri.EscapeDataString(number),
        Baseline => "baselines/" + Uri.EscapeDataString(number),
        Mbom => "mboms/" + Uri.EscapeDataString(number),
        Ncr => "quality/ncrs/" + Uri.EscapeDataString(number),
        Capa => "quality/capas/" + Uri.EscapeDataString(number),
        Deviation => "quality/deviations/" + Uri.EscapeDataString(number),
        Supplier => "suppliers/" + Uri.EscapeDataString(number),
        ReqSpec => "requirements/" + Uri.EscapeDataString(number),
        ProcessPlan => "process/plans/" + Uri.EscapeDataString(number),
        Tool => "process/tools/" + Uri.EscapeDataString(number),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown object kind."),
    };

    public static string Label(string kind) => kind switch
    {
        Part => "Part",
        Document => "Document",
        Change => "Change",
        Baseline => "Baseline",
        Mbom => "Manufacturing BOM",
        Ncr => "NCR",
        Capa => "CAPA",
        Deviation => "Deviation",
        Supplier => "Supplier",
        ReqSpec => "Requirement specification",
        ProcessPlan => "Process plan",
        Tool => "Tool",
        _ => kind,
    };

    /// <summary>
    /// The object type the audit log uses for an object (<c>part</c>, <c>document</c>, <c>baseline</c>, <c>mbom</c>; for changes <c>pr</c>, <c>ecr</c> or <c>ecn</c>, taken from the
    /// number's prefix). Null when a change number has no known prefix.
    /// </summary>
    public static string? AuditObjectType(string kind, string number)
    {
        if (kind != Change) return kind;
        var dash = number.IndexOf('-', StringComparison.Ordinal);
        var prefix = dash > 0 ? number[..dash].ToLowerInvariant() : null;
        return prefix is "pr" or "ecr" or "ecn" ? prefix : null;
    }

    /// <summary>The awareness kind of an audit or workflow object type, or null for types that are not objects a user can open (sessions, configuration, ...).</summary>
    public static string? FromAuditObjectType(string? auditType) => auditType switch
    {
        "part" => Part,
        "document" => Document,
        "pr" or "ecr" or "ecn" => Change,
        "baseline" => Baseline,
        "mbom" => Mbom,
        "ncr" => Ncr,
        "capa" => Capa,
        "deviation" => Deviation,
        "supplier" => Supplier,
        "reqspec" => ReqSpec,
        "process_plan" => ProcessPlan,
        "tool" => Tool,
        _ => null,
    };

    /// <summary>The tab of an object's page that shows its review (documents show it on the overview).</summary>
    public static string ReviewTab(string kind) => kind == Document ? "overview" : "review";
}

/// <summary>An object as awareness features need to see it: what to call it and the markings its readers are checked against.</summary>
public sealed record ObjectInfo(string Kind, string Number, string Name, string StateLabel, Markings Markings);

/// <summary>
/// Implemented once per object kind by the module that owns it (parts, documents, changes, baselines, MBOMs), as a KEY LOOKUP: it returns the object's name,
/// state and markings, or null when there is no such object. It makes no access decision. The notification, watch, comment and activity services evaluate
/// <c>AccessPolicy</c> on the returned markings for the viewer (or, for fan-out, the recipient's directory snapshot), so a denied object and a missing object
/// look the same to the caller (ADR-0004).
/// </summary>
public interface IObjectAccess
{
    /// <summary>One of <see cref="AwarenessKinds"/>.</summary>
    string Kind { get; }

    Task<ObjectInfo?> FindAsync(string number, CancellationToken ct);
}

/// <summary>
/// One notification to record. The row stores pointers only: the kind code, the object pointer, the actor's subject and an optional reference id. The text is
/// rendered at read time, after a read check. <see cref="Markings"/> are the object's markings at the moment of the event: the sink checks every recipient
/// against them (directory snapshot) before a row is written.
/// </summary>
/// <param name="Kind">A notification kind code (<c>Keel.Notifications.NotificationKinds</c>).</param>
/// <param name="ObjectType">One of <see cref="AwarenessKinds"/>.</param>
/// <param name="ObjectNumber">The object's business number.</param>
/// <param name="Markings">The object's markings now.</param>
/// <param name="ActorSub">Who did it; they are not notified about their own action unless they chose to be.</param>
/// <param name="RecipientSub">The one recipient, or null to notify the watchers of the object (a watcher below the event's level is skipped).</param>
/// <param name="RefId">A comment id or workflow task id the link should land on.</param>
/// <param name="EventId">The outbox event this notification was made from (idempotency of the fan-out).</param>
public sealed record NotificationRequest(
    string Kind, string ObjectType, string ObjectNumber, Markings Markings, string? ActorSub, string? RecipientSub = null, Guid? RefId = null, Guid? EventId = null);

/// <summary>
/// Implemented by <c>Keel.Notifications</c>. Modules call it inside the transaction of the business change they report, exactly like
/// <see cref="IDomainEventSink"/>, so a notification exists only when the change committed. A module with no sink registered skips notifications.
/// </summary>
public interface INotificationSink
{
    /// <summary>Records the notification for its recipient (or for the watchers), after checking that each recipient may read the object. Never throws for a recipient who may not.</summary>
    Task AppendAsync(DbConnection conn, DbTransaction tx, NotificationRequest request, CancellationToken ct);

    /// <summary>Starts watching an object for the user when the user's "watch what I create, check out, sign or comment on" preference is on (the default).</summary>
    Task AutoWatchAsync(DbConnection conn, DbTransaction tx, string userSub, string objectType, string objectNumber, CancellationToken ct);
}

/// <summary>
/// The stable codes of the notification kinds, shared by the modules that raise notifications and by <c>Keel.Notifications</c>, which owns what each kind means
/// (label, sentence, preferences). A notification row stores only the code.
/// </summary>
public static class NotificationKindCodes
{
    public const string TaskAssigned = "task_assigned";
    public const string ReviewRequested = "review_requested";
    public const string ChangeTaskAssigned = "change_task_assigned";
    public const string ReviewApproved = "review_approved";
    public const string ReviewRejected = "review_rejected";
    public const string ChangeDecisionApproved = "change_decision_approved";
    public const string ChangeDecisionRejected = "change_decision_rejected";
    public const string WatchedChanged = "watched_changed";
    public const string WatchedState = "watched_state";
    public const string WatchedReleased = "watched_released";
    public const string WatchedObsoleted = "watched_obsoleted";
    public const string WatchedApproved = "watched_approved";
    public const string WatchedEstablished = "watched_established";
    public const string CommentAdded = "comment_added";
    public const string CommentMention = "comment_mention";
    /// <summary>Quality (8.1): a corrective or containment action was assigned to the person.</summary>
    public const string QualityActionAssigned = "quality_action_assigned";
    /// <summary>Quality (8.1): an action the person owns is past its due date.</summary>
    public const string QualityActionOverdue = "quality_action_overdue";
    /// <summary>Quality (8.1): a deviation or waiver ends soon.</summary>
    public const string DeviationExpiring = "deviation_expiring";
    /// <summary>Quality (8.1): a deviation or waiver ended.</summary>
    public const string DeviationExpired = "deviation_expired";
    /// <summary>8.2 sourcing: a supplier certification is about to expire / has expired, and a supplier's standing changed.</summary>
    public const string SupplierCertExpiring = "supplier_cert_expiring";
    public const string SupplierCertExpired = "supplier_cert_expired";
    public const string SupplierStatusChanged = "supplier_status_changed";
    /// <summary>8.5 compliance: a part's declaration became non-compliant (list update or edit), is about to expire / has expired, or a supplier provided a declaration. Pointer is the part.</summary>
    public const string ComplianceNonCompliant = "compliance_noncompliant";
    public const string ComplianceDeclarationExpiring = "compliance_declaration_expiring";
    public const string ComplianceDeclarationExpired = "compliance_declaration_expired";
    public const string ComplianceSupplierDeclaration = "compliance_supplier_declaration";
    /// <summary>8.7 process data: a tool, fixture or gauge is due for calibration soon / is overdue. Pointer is the tool.</summary>
    public const string ToolCalibrationDue = "tool_calibration_due";
    public const string ToolCalibrationOverdue = "tool_calibration_overdue";
}
