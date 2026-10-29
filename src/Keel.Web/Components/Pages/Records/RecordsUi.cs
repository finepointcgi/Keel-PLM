using Keel.Core;
using Keel.Records;
using Keel.Web.Services;

namespace Keel.Web.Components.Pages.Records;

/// <summary>Wording, classes and links for the records management pages (ADR-0041).</summary>
public static class RecordsUi
{
    public static string ScheduleHref(int version) => $"records/schedules/{version}";
    public static string HoldHref(string number) => $"records/holds/{Uri.EscapeDataString(number)}";
    public static string DispositionHref(string number) => $"records/disposition/{Uri.EscapeDataString(number)}";
    public static string CertificateHref(string number) => $"records/certificates/{Uri.EscapeDataString(number)}";
    public static string ArchiveHref(string number) => $"records/archives/{Uri.EscapeDataString(number)}";
    public static string Size(long bytes) => SourcingSize(bytes);

    private static string SourcingSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes / 1024d / 1024:0.#} MiB";

    public static string StatusClass(string status) => status switch
    {
        ScheduleStatuses.Published or DispositionStatuses.Approved or DispositionStatuses.Executed => "state-approved",
        ScheduleStatuses.InReview => "state-underreview",
        ScheduleStatuses.Draft => "state-inwork",
        _ => "state-obsolete",
    };

    public static string WormText(string mode) => mode switch
    {
        "s3_compliance" => "Storage-enforced retention (S3 Object Lock, compliance mode)",
        "s3_governance" => "Storage-enforced retention (S3 Object Lock, governance mode)",
        _ => "Application-enforced immutability (no storage lock)",
    };

    public static string SkipText(string? code) => code switch
    {
        SkipCodes.Held => "Under legal hold",
        SkipCodes.NotEligible => "No longer due",
        SkipCodes.Referenced => "Still used by another record",
        SkipCodes.Gone => "No longer exists",
        SkipCodes.AlreadyDisposed => "Already disposed of",
        _ => "Not disposed of",
    };

    public static string ResultText(string? result, string? code) => result switch
    {
        "destroyed" => "Destroyed",
        "transferred" => "Moved to the archive",
        "retained" => "Kept for another period",
        "skipped" => SkipText(code),
        _ => "Not yet carried out",
    };

    public static string ContentState(string state) => state switch
    {
        "destroyed" => "Confirmed removed from the vault",
        "retained_shared" => "Kept: another record still uses it",
        _ => "Waiting for the vault sweep",
    };

    public static string Message<T>(Outcome<T> o) => o.Kind switch
    {
        OutcomeKind.Invalid => string.Join(" ", o.Errors),
        OutcomeKind.Unavailable => string.Join(" ", o.Errors),
        _ => UiText.Message(o),
    };

    /// <summary>Parses "kind number" lines (for example "document D-000001") typed into a text area.</summary>
    public static (IReadOnlyList<ObjectRef> Items, IReadOnlyList<string> Errors) ParseObjects(string text)
    {
        var items = new List<ObjectRef>();
        var errors = new List<string>();
        foreach (var raw in text.Split(['\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 1 && parts[0].StartsWith("D-", StringComparison.OrdinalIgnoreCase)) items.Add(new ObjectRef(RecordKinds.Document, parts[0].ToUpperInvariant()));
            else if (parts.Length == 1 && parts[0].StartsWith("P-", StringComparison.OrdinalIgnoreCase)) items.Add(new ObjectRef(RecordKinds.Part, parts[0].ToUpperInvariant()));
            else if (parts.Length == 2 && RecordKinds.IsKnown(parts[0].ToLowerInvariant())) items.Add(new ObjectRef(parts[0].ToLowerInvariant(), parts[1]));
            else errors.Add($"'{raw}' is not an object: write a number such as D-000001 or P-000001, or the kind and number.");
        }
        return (items, errors);
    }
}
