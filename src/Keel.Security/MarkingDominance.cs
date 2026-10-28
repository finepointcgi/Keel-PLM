using System.Collections.Immutable;
using Keel.Core;

namespace Keel.Security;

/// <summary>
/// Marking dominance for change management (ADR-0019). A change object lists the objects it affects, so anyone who can read the change
/// learns that those objects exist and what they are called. The change's markings must therefore be at least as restrictive as the
/// markings of every object it lists; otherwise a reader of the change who cannot read a listed part would learn about the part.
/// <para>
/// <see cref="Covers"/> is the pure predicate, <see cref="MinimalCover"/> computes the smallest markings that cover a set of objects.
/// The property the integration tests prove against <see cref="AccessPolicy"/> for every subject: if the change covers the object,
/// everyone who can read the change can read the object.
/// </para>
/// </summary>
public static class MarkingDominance
{
    // Reason codes for what is not covered; stable identifiers shown in messages and written to the audit log.
    public const string CuiNotCovered = "dominance.cui";
    public const string JurisdictionNotCovered = "dominance.jurisdiction";
    public const string ProgramNotCovered = "dominance.program";
    public const string DistributionNotCovered = "dominance.distribution";
    public const string AuthorizationsNotCovered = "dominance.authorizations";

    /// <summary>None and EAR99 = 0 (no nationality gate), EAR with another ECCN = 1, ITAR = 2.</summary>
    public static int JurisdictionRank(Markings m) => m.ExportJurisdiction switch
    {
        ExportJurisdiction.Itar => 2,
        ExportJurisdiction.Ear when !string.Equals(m.Eccn, Markings.Ear99, StringComparison.Ordinal) => 1,
        _ => 0,
    };

    /// <summary>
    /// What <paramref name="change"/> fails to cover of <paramref name="obj"/>; empty when it covers it.
    /// ASSUMPTION (CHG-4): distribution statements order A &lt; B &lt; C &lt; D &lt; E &lt; F by restrictiveness (F is the most restrictive, as in
    /// <see cref="Markings.AggregateBanner"/>), and a change in a program may only list objects of that program or of no program.
    /// Export authorizations must be a subset of the object's, so a non-U.S. reader admitted to the change by an authorization is also
    /// admitted to the object by it.
    /// </summary>
    public static IReadOnlyList<string> Gaps(Markings change, Markings obj)
    {
        var gaps = new List<string>();
        if (!obj.CuiCategories.IsSubsetOf(change.CuiCategories)) gaps.Add(CuiNotCovered);
        if (JurisdictionRank(change) < JurisdictionRank(obj)) gaps.Add(JurisdictionNotCovered);
        if (obj.Program is not null && !string.Equals(change.Program, obj.Program, StringComparison.Ordinal)) gaps.Add(ProgramNotCovered);
        if (change.DistributionStatement < obj.DistributionStatement) gaps.Add(DistributionNotCovered);
        if (obj.IsExportControlled && !change.ExportAuthorizations.IsSubsetOf(obj.ExportAuthorizations)) gaps.Add(AuthorizationsNotCovered);
        return gaps;
    }

    public static bool Covers(Markings change, Markings obj) => Gaps(change, obj).Count == 0;

    public static bool CoversAll(Markings change, IEnumerable<Markings> objects) => objects.All(o => Covers(change, o));

    /// <summary>
    /// The minimal markings that cover <paramref name="change"/> and every object: the union of CUI categories, the highest jurisdiction
    /// (taking its ECCN or USML category from the object that forced it), the object's program, the most restrictive distribution statement,
    /// and the intersection of export authorizations. Returns <paramref name="change"/> itself when nothing needs raising. Fails when two
    /// different programs are involved (one change cannot belong to two programs) or the result would not be valid markings.
    /// </summary>
    public static Result<Markings> MinimalCover(Markings change, IEnumerable<Markings> objects)
    {
        var current = change;
        foreach (var obj in objects)
        {
            if (Covers(current, obj)) continue;
            if (obj.Program is not null && current.Program is not null && !string.Equals(current.Program, obj.Program, StringComparison.Ordinal))
                return Result<Markings>.Fail([$"The change belongs to program {current.Program} but lists an object of program {obj.Program}. Create a separate change for each program."]);

            var jurisdiction = current.ExportJurisdiction;
            var eccn = current.Eccn;
            var usml = current.UsmlCategory;
            if (JurisdictionRank(obj) > JurisdictionRank(current))
            {
                jurisdiction = obj.ExportJurisdiction;
                eccn = obj.Eccn;
                usml = obj.UsmlCategory;
            }
            var distribution = current.DistributionStatement;
            var office = current.ControllingOffice;
            var reason = current.DistributionReason;
            if (obj.DistributionStatement > distribution)
            {
                distribution = obj.DistributionStatement;
                office = obj.ControllingOffice;
                reason = obj.DistributionReason;
            }
            ImmutableSortedSet<string> authorizations = obj.IsExportControlled
                ? [.. current.ExportAuthorizations.Intersect(obj.ExportAuthorizations)]
                : current.ExportAuthorizations;
            if (jurisdiction == ExportJurisdiction.None) authorizations = [];

            var created = Markings.Create(jurisdiction, distribution, current.OwningOrg, eccn, usml,
                current.CuiCategories.Union(obj.CuiCategories), office, reason, obj.Program ?? current.Program, authorizations);
            if (!created.IsSuccess) return created;
            current = created.Value;
        }
        return Result<Markings>.Ok(current);
    }

    /// <summary>Human-readable explanation of <see cref="Gaps"/>, free of anything about the object beyond its own markings.</summary>
    public static string Describe(IEnumerable<string> gaps) => string.Join(" ", gaps.Select(g => g switch
    {
        CuiNotCovered => "The change lacks CUI categories that the object carries.",
        JurisdictionNotCovered => "The object's export jurisdiction is more restrictive than the change's.",
        ProgramNotCovered => "The object belongs to a program the change does not.",
        DistributionNotCovered => "The object's distribution statement is more restrictive than the change's.",
        AuthorizationsNotCovered => "The change lists export authorizations that the object does not.",
        _ => g,
    }));
}
