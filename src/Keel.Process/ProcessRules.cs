using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Keel.Core;

namespace Keel.Process;

/// <summary>Pure rules of process planning (ADR-0042): serialization and digest of plan content, validation, coverage of MBOM lines, staleness, calibration state.</summary>
public static partial class ProcessRules
{
    public const int MaxRoutings = 10, MaxOperations = 200, MaxStepsPerOperation = 100, MaxAttachmentsPerStep = 10, MaxToolsPerOperation = 20, MaxComponentsPerOperation = 500;
    public const decimal MaxMinutes = 1_000_000m;
    public static readonly string[] CalibrationWarnDays = ["30", "14", "7"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string Serialize(PlanContent content) => JsonSerializer.Serialize(Normalize(content), Json);

    public static PlanContent Deserialize(string json) => JsonSerializer.Deserialize<PlanContent>(json, Json) ?? PlanContent.Empty;

    /// <summary>Orders routings (primary first, then by code) and operations by number, so equal content always has one text and one digest.</summary>
    public static PlanContent Normalize(PlanContent c) => new([.. c.Routings
        .Select(r => r with
        {
            Operations = [.. r.Operations.OrderBy(o => o.Number).Select(o => o with { Components = [.. o.Components.OrderBy(x => x.Find).ThenBy(x => x.Alt)] })],
        })
        .OrderByDescending(r => r.IsPrimary).ThenBy(r => r.Code, StringComparer.Ordinal)]);

    public static string Hash(PlanContent c) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(c))));

    /// <summary>The digest a review binds: the content digest and the MBOM revision (and its digest) the content was written against.</summary>
    public static string ReviewHash(string contentSha, string mbomRevision, string mbomSha) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"plan-review/1\n{contentSha}\n{mbomRevision}\n{mbomSha}")));

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,19}$")]
    private static partial Regex CodePattern();

    public static bool ValidRoutingCode(string? code) => code is not null && CodePattern().IsMatch(code);

    // ---------------------------------------------------------------- structure

    /// <summary>Structural errors of a content document. <paramref name="activeWorkCenters"/> are the active work center codes of the plan's plant.</summary>
    public static IReadOnlyList<string> Validate(PlanContent content, IReadOnlySet<string> activeWorkCenters)
    {
        var errors = new List<string>();
        if (content.Routings.Count is < 1 or > MaxRoutings) errors.Add($"A plan has 1 to {MaxRoutings} routings.");
        if (content.Routings.Count(r => r.IsPrimary) != 1) errors.Add("Exactly one routing is the primary routing.");
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<Guid>();
        foreach (var r in content.Routings)
        {
            if (!ValidRoutingCode(r.Code)) errors.Add($"Routing code '{r.Code}' must be 1 to 20 capital letters, digits, '-' or '_'.");
            else if (!codes.Add(r.Code)) errors.Add($"Routing code {r.Code} is used twice.");
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 120) errors.Add($"Routing {r.Code}: the name is required (at most 120 characters).");
            if (r.Condition is { Length: > 500 }) errors.Add($"Routing {r.Code}: the condition is at most 500 characters.");
            if (r.Note is { Length: > 1000 }) errors.Add($"Routing {r.Code}: the note is at most 1000 characters.");
            if (r.Operations.Count > MaxOperations) errors.Add($"Routing {r.Code} has more than {MaxOperations} operations.");
            var numbers = new HashSet<int>();
            foreach (var o in r.Operations)
            {
                var where = $"Routing {r.Code}, operation {o.Number}";
                if (o.Number is < 1 or > 9999) errors.Add($"{where}: the operation number is 1 to 9999.");
                else if (!numbers.Add(o.Number)) errors.Add($"{where}: the number is used twice in the routing.");
                if (!ids.Add(o.Id)) errors.Add($"{where}: duplicate operation identifier.");
                if (string.IsNullOrWhiteSpace(o.Name) || o.Name.Length > 200) errors.Add($"{where}: the description is required (at most 200 characters).");
                if (o.WorkCenter is not null && !activeWorkCenters.Contains(o.WorkCenter)) errors.Add($"{where}: work center {o.WorkCenter} is not an active work center of the plant.");
                if (o.SetupMinutes is < 0 or > MaxMinutes || o.RunMinutes is < 0 or > MaxMinutes) errors.Add($"{where}: times are 0 to {MaxMinutes:0} minutes.");
                if (o.Condition is { Length: > 500 }) errors.Add($"{where}: the condition is at most 500 characters.");
                if (o.InspectionText is { Length: > 1000 }) errors.Add($"{where}: the inspection text is at most 1000 characters.");
                if (o.Inspection && string.IsNullOrWhiteSpace(o.InspectionText)) errors.Add($"{where}: an inspection point needs a text saying what to inspect.");
                if (o.Components.Count > MaxComponentsPerOperation) errors.Add($"{where}: too many assigned components.");
                if (o.Components.Any(c => c.Quantity is <= 0)) errors.Add($"{where}: an assigned quantity must be greater than zero.");
                if (o.Components.GroupBy(c => (c.Find, c.Alt)).Any(g => g.Count() > 1)) errors.Add($"{where}: a component line is assigned twice.");
                if (o.Tools.Count > MaxToolsPerOperation || o.Tools.Distinct().Count() != o.Tools.Count) errors.Add($"{where}: at most {MaxToolsPerOperation} different tools.");
                if (o.Steps.Count > MaxStepsPerOperation) errors.Add($"{where}: at most {MaxStepsPerOperation} steps.");
                foreach (var s in o.Steps)
                {
                    if (!StepKinds.All.Contains(s.Kind)) errors.Add($"{where}: unknown step kind '{s.Kind}'.");
                    if (string.IsNullOrWhiteSpace(s.Text) || s.Text.Length > 2000) errors.Add($"{where}: each step needs text (at most 2000 characters).");
                    if (!ids.Add(s.Id)) errors.Add($"{where}: duplicate step identifier.");
                    if (s.Attachments.Count > MaxAttachmentsPerStep) errors.Add($"{where}: at most {MaxAttachmentsPerStep} attachments per step.");
                    if (s.Tools.Count > MaxToolsPerOperation || s.Tools.Distinct().Count() != s.Tools.Count) errors.Add($"{where}: a step names at most {MaxToolsPerOperation} different tools.");
                    foreach (var a in s.Attachments)
                    {
                        if (string.IsNullOrWhiteSpace(a.Document) || a.Document.Length > 40 || a.FileId == Guid.Empty) errors.Add($"{where}: an attachment names a document and a file.");
                        if (string.IsNullOrWhiteSpace(a.Caption) || a.Caption.Length > 200) errors.Add($"{where}: each attachment needs a caption (the text alternative; at most 200 characters).");
                    }
                }
            }
        }
        return errors;
    }

    // ---------------------------------------------------------------- coverage of the bound MBOM

    /// <summary>
    /// Compares assignments with the lines of the bound MBOM revision, per routing: an assignment naming a line the revision does not have is an error, assigning more than a line's
    /// quantity is an error, a consumable line (not phantom) nobody consumes is a warning. Lines are matched by find number and alternate.
    /// </summary>
    public static IReadOnlyList<PlanFinding> Coverage(PlanContent content, IReadOnlyList<MbomLineRef> lines)
    {
        var findings = new List<PlanFinding>();
        var byKey = lines.ToDictionary(l => (l.Find, l.Alt));
        foreach (var r in content.Routings)
        {
            var assigned = new Dictionary<(int, int), decimal>();
            foreach (var o in r.Operations)
                foreach (var c in o.Components)
                {
                    if (!byKey.TryGetValue((c.Find, c.Alt), out var line))
                    {
                        findings.Add(new PlanFinding(Severities.Error, "assignment.orphan", $"Operation {o.Number} consumes line {c.Find}{(c.Alt > 0 ? $" alternate {c.Alt}" : "")}, which the bound MBOM revision does not have.", r.Code, o.Number));
                        continue;
                    }
                    if (!line.Redacted && line.ChildMasterId is { } child && child != c.PartMasterId)
                        findings.Add(new PlanFinding(Severities.Warning, "assignment.part_changed", $"Operation {o.Number}: line {c.Find} now holds a different component than when it was assigned.", r.Code, o.Number));
                    assigned[(c.Find, c.Alt)] = assigned.GetValueOrDefault((c.Find, c.Alt)) + (c.Quantity ?? line.Quantity);
                }
            foreach (var line in lines.Where(l => !l.Phantom).OrderBy(l => l.Find).ThenBy(l => l.Alt))
            {
                var total = assigned.GetValueOrDefault((line.Find, line.Alt));
                if (total == 0) findings.Add(new PlanFinding(Severities.Warning, "line.unassigned", $"Line {line.Find}{(line.Alt > 0 ? $" alternate {line.Alt}" : "")} is not consumed at any operation of routing {r.Code}.", r.Code));
                else if (total > line.Quantity + 0.00001m)
                    findings.Add(new PlanFinding(Severities.Error, "line.over", $"Line {line.Find}{(line.Alt > 0 ? $" alternate {line.Alt}" : "")}: {Fmt(total)} assigned in routing {r.Code}, but the line needs {Fmt(line.Quantity)}.", r.Code));
            }
        }
        return findings;
    }

    public static string Fmt(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- staleness and calibration

    public static Staleness StalenessOf(string boundRevision, string boundSha, MbomRevisionRef? latestReleased) =>
        latestReleased is null ? Staleness.NoReleasedMbom
        : latestReleased.Revision == boundRevision && latestReleased.ContentSha256 == boundSha ? Staleness.Current : Staleness.Stale;

    /// <summary>The notice stage for a calibration due on <paramref name="due"/> (the last valid day): overdue the day after, then 7, 14 and 30 days ahead; null when later.</summary>
    public static string? NoticeStage(DateOnly due, DateOnly today)
    {
        var days = due.DayNumber - today.DayNumber;
        return days < 0 ? "overdue" : days <= 7 ? "d7" : days <= 14 ? "d14" : days <= 30 ? "d30" : null;
    }

    public static string CalibrationState(bool required, DateOnly? due, DateOnly today)
    {
        if (!required) return "not_required";
        if (due is null) return "never";
        var days = due.Value.DayNumber - today.DayNumber;
        return days < 0 ? "overdue" : days <= 30 ? "due_soon" : "valid";
    }

    public static string CalibrationLabel(string state) => state switch
    {
        "not_required" => "Not required", "never" => "Never calibrated", "overdue" => "Overdue", "due_soon" => "Due soon", "valid" => "In calibration", _ => state,
    };

    /// <summary>The next free operation number: the highest plus ten, rounded to a multiple of ten.</summary>
    public static int NextOperationNumber(IEnumerable<int> existing)
    {
        var max = existing.DefaultIfEmpty(0).Max();
        return Math.Min(9999, ((max / 10) + 1) * 10);
    }

    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
