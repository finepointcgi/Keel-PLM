using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Keel.Core;
using Keel.Security;

namespace Keel.Requirements;

/// <summary>One requirement as the edit engine works on it: identity, place in the hierarchy, content and markings.</summary>
internal sealed record WorkReq(
    Guid Id, string Key, Guid? ParentId, int Ordinal, string Title, string Text, string Type, string Priority, string Method, string Rationale, string Status, Markings Markings, bool IsNew = false)
{
    public string Sha => RequirementRules.ContentSha(Key, Title, Text, Type, Priority, Method, Rationale, Status);
}

/// <summary>Pure rules of the requirements module: hashes, validation, outline numbering, verification state, coverage arithmetic. No database, no clock.</summary>
public static class RequirementRules
{
    public const int MaxRequirements = 5000;
    public const int MaxDepth = 10;
    public const int MaxTitle = 200, MaxText = 8000, MaxRationale = 4000, MaxNotes = 2000;

    public static string FormatKey(int n) => "R-" + n.ToString("D4", CultureInfo.InvariantCulture);

    public static bool IsKey(string? key) => key is { Length: >= 6 and <= 12 } && key.StartsWith("R-", StringComparison.Ordinal) && key.AsSpan(2).ContainsAnyExceptInRange('0', '9') is false;

    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    /// <summary>SHA-256 over the content fields of a requirement (not its place in the hierarchy and not its markings): what a link or a verification was reviewed against.</summary>
    public static string ContentSha(string key, string title, string text, string type, string priority, string method, string rationale, string status) =>
        Hex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', "req-content/1", key, title, text, type, priority, method, rationale, status))));

    /// <summary>SHA-256 over every requirement of an iteration (key, parent, order, content hash, markings): what a review signs.</summary>
    internal static string IterationSha(IEnumerable<(string Key, string? ParentKey, int Ordinal, string Sha, string Banner)> rows) =>
        Hex(SHA256.HashData(Encoding.UTF8.GetBytes("req-iteration/1\n" + string.Join('\n', rows.OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => string.Join('|', r.Key, r.ParentKey ?? "", r.Ordinal.ToString(CultureInfo.InvariantCulture), r.Sha, r.Banner))))));

    public static string ReviewHash(string iterationSha) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes("req-review/1\n" + iterationSha)));

    public static IReadOnlyList<string> Validate(string? title, string? text, string? type, string? priority, string? method, string? rationale, string? status)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitle) errors.Add($"The title is required (at most {MaxTitle} characters).");
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > MaxText) errors.Add($"The requirement text is required (at most {MaxText} characters).");
        if (type is null || !ReqTypes.All.Contains(type)) errors.Add($"The type must be one of: {string.Join(", ", ReqTypes.All)}.");
        if (priority is null || !ReqPriorities.All.Contains(priority)) errors.Add($"The priority must be one of: {string.Join(", ", ReqPriorities.All)}.");
        if (method is null || !VerificationMethods.All.Contains(method)) errors.Add($"The verification method must be one of: {string.Join(", ", VerificationMethods.All)}.");
        if ((rationale?.Length ?? 0) > MaxRationale) errors.Add($"The rationale must be at most {MaxRationale} characters.");
        if (status is null || !ReqStatuses.All.Contains(status)) errors.Add($"The status must be one of: {string.Join(", ", ReqStatuses.All)}.");
        return errors;
    }

    /// <summary>
    /// Depth-first outline order of a hierarchy: siblings by ordinal, then key. A row whose parent is not in the set is treated as a root. Returns the outline
    /// number ("1", "1.2", "1.2.1") and depth (0 for a root) of every row, in display order.
    /// </summary>
    public static IReadOnlyList<(Guid Id, string Outline, int Depth)> Outline(IReadOnlyList<(Guid Id, Guid? Parent, int Ordinal, string Tie)> rows)
    {
        var ids = rows.Select(r => r.Id).ToHashSet();
        var children = rows.GroupBy(r => r.Parent is { } p && ids.Contains(p) ? p : Guid.Empty)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Ordinal).ThenBy(r => r.Tie, StringComparer.Ordinal).ToList());
        var result = new List<(Guid, string, int)>(rows.Count);
        var seen = new HashSet<Guid>();
        void Walk(Guid parent, string prefix, int depth)
        {
            if (!children.TryGetValue(parent, out var list)) return;
            var n = 0;
            foreach (var r in list)
            {
                if (!seen.Add(r.Id)) continue;
                n++;
                var label = prefix.Length == 0 ? n.ToString(CultureInfo.InvariantCulture) : prefix + "." + n.ToString(CultureInfo.InvariantCulture);
                result.Add((r.Id, label, depth));
                Walk(r.Id, label, depth + 1);
            }
        }
        Walk(Guid.Empty, "", 0);
        return result;
    }

    /// <summary>The verification state of a requirement's current content from its records (newest first): the newest record for this exact content decides; records of older content are stale.</summary>
    public static string VerificationState(string currentSha, IReadOnlyList<(string Sha, string Result)> recordsNewestFirst)
    {
        if (recordsNewestFirst.Count == 0) return VerificationStates.None;
        foreach (var r in recordsNewestFirst)
            if (r.Sha == currentSha)
                return r.Result switch { VerificationResults.Pass => VerificationStates.Verified, VerificationResults.Waived => VerificationStates.Waived, _ => VerificationStates.Failed };
        return VerificationStates.Stale;
    }

    public static CoverageSummary Summarize(IReadOnlyList<CoverageRow> rows)
    {
        var scope = rows.Where(r => r.InScope).ToList();
        return new CoverageSummary(
            rows.Count, scope.Count, scope.Count(r => !r.Unallocated), scope.Count(r => r.Unallocated),
            scope.Count(r => r.VerificationState == VerificationStates.Verified), scope.Count(r => r.VerificationState == VerificationStates.Waived),
            scope.Count(r => r.VerificationState == VerificationStates.Failed), scope.Count(r => r.VerificationState == VerificationStates.Stale),
            scope.Count(r => r.VerificationState == VerificationStates.None), scope.Count(r => r.PlannedVerifications > 0),
            rows.Sum(r => r.SuspectLinks), scope.Count(r => r.Unallocated || r.Unverified));
    }

    /// <summary>A requirement's markings must be at least as restrictive as its specification's (the specification's reader must be able to read what it lists, never the reverse).</summary>
    public static IReadOnlyList<string> DominanceGaps(Markings requirement, Markings spec) => MarkingDominance.Gaps(requirement, spec);

    /// <summary>The number of levels of the deepest chain of parents above a requirement (0 for a root).</summary>
    internal static int DepthOf(Guid id, IReadOnlyDictionary<Guid, WorkReq> all)
    {
        var depth = 0;
        var seen = new HashSet<Guid> { id };
        var cur = all[id].ParentId;
        while (cur is { } p && all.TryGetValue(p, out var parent))
        {
            if (!seen.Add(p)) return int.MaxValue;
            depth++;
            cur = parent.ParentId;
        }
        return depth;
    }
}
