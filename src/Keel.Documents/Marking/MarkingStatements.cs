using Keel.Core;

namespace Keel.Documents.Marking;

/// <summary>What is being delivered, to whom and when: the context stamped on every marked output (A10).</summary>
public sealed record MarkingContext(
    Markings Markings,
    string DocumentNumber,
    string DocumentName,
    string IterationLabel,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    string RetrievedBy,
    DateTimeOffset RetrievedAt)
{
    public string Banner => Markings.BannerText();

    /// <summary>Traceability line: which object, which file, who retrieved it and when (UTC).</summary>
    public string TraceLine =>
        $"Keel PLM {DocumentNumber} {IterationLabel} · {FileName} · SHA-256 {Sha256[..16]}… · retrieved {RetrievedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC by {RetrievedBy}";
}

/// <summary>
/// Standard notice texts. Distribution statements follow DoDI 5230.24 (statements A–F; X is cancelled); the export
/// warning is the DoDD 5230.25 notice required on export-controlled technical documents.
/// </summary>
public static class MarkingStatements
{
    public const string ExportControlWarning =
        "WARNING - This document contains technical data whose export is restricted by the Arms Export Control Act (Title 22, U.S.C., Sec 2751, et seq.) " +
        "or the Export Administration Act of 1979, as amended, Title 50, U.S.C., App. 2401 et seq. Violations of these export laws are subject to severe " +
        "criminal penalties. Disseminate in accordance with provisions of DoD Directive 5230.25.";

    public const string DestructionNotice =
        "DESTRUCTION NOTICE - Destroy by any method that will prevent disclosure of contents or reconstruction of the document.";

    /// <summary>
    /// The full distribution statement. ASSUMPTION(A22): the date of determination is not yet captured with the markings,
    /// so it is omitted from the statement until the markings model carries it (Phase 2).
    /// </summary>
    public static string DistributionStatementText(Markings m)
    {
        var reason = m.DistributionReason ?? "";
        var office = m.ControllingOffice ?? "";
        return m.DistributionStatement switch
        {
            DistributionStatement.A => "DISTRIBUTION STATEMENT A. Approved for public release: distribution is unlimited.",
            DistributionStatement.B => $"DISTRIBUTION STATEMENT B. Distribution authorized to U.S. Government agencies only; {reason}. " +
                                       $"Other requests for this document shall be referred to {office}.",
            DistributionStatement.C => $"DISTRIBUTION STATEMENT C. Distribution authorized to U.S. Government agencies and their contractors; {reason}. " +
                                       $"Other requests for this document shall be referred to {office}.",
            DistributionStatement.D => $"DISTRIBUTION STATEMENT D. Distribution authorized to the Department of Defense and U.S. DoD contractors only; {reason}. " +
                                       $"Other requests for this document shall be referred to {office}.",
            DistributionStatement.E => $"DISTRIBUTION STATEMENT E. Distribution authorized to DoD Components only; {reason}. " +
                                       $"Other requests for this document shall be referred to {office}.",
            DistributionStatement.F => $"DISTRIBUTION STATEMENT F. Further dissemination only as directed by {office} or higher DoD authority.",
            _ => throw new ArgumentOutOfRangeException(nameof(m), m.DistributionStatement, "Unknown distribution statement."),
        };
    }

    /// <summary>CUI designation indicator (32 CFR 2002.20): who controls it, the categories and the dissemination control.</summary>
    public static string? CuiDesignation(Markings m) => m.IsCui
        ? $"CUI designation - Controlled by: {m.OwningOrg}. CUI categories: {string.Join(", ", m.CuiCategories)}. " +
          $"Limited dissemination control: DISTRIBUTION {m.DistributionStatement}." +
          (m.ControllingOffice is null ? "" : $" POC: {m.ControllingOffice}.")
        : null;

    /// <summary>Export classification line, e.g. "Export classification: ITAR, USML Category XI(d)."</summary>
    public static string? ExportClassification(Markings m) => m.ExportJurisdiction switch
    {
        ExportJurisdiction.Itar => $"Export classification: ITAR, USML Category {m.UsmlCategory}." + Authorizations(m),
        ExportJurisdiction.Ear => $"Export classification: EAR, ECCN {m.Eccn}." + Authorizations(m),
        _ => null,
    };

    /// <summary>The notices stamped in the footer of every page and printed on cover sheets, in order.</summary>
    public static IReadOnlyList<string> Notices(Markings m)
    {
        var list = new List<string>();
        if (CuiDesignation(m) is { } cui) list.Add(cui);
        if (ExportClassification(m) is { } export) list.Add(export);
        list.Add(DistributionStatementText(m));
        if (m.IsExportControlled) list.Add(ExportControlWarning);
        if (m.DistributionStatement != DistributionStatement.A || m.IsCui) list.Add(DestructionNotice);
        return list;
    }

    private static string Authorizations(Markings m) =>
        m.ExportAuthorizations.IsEmpty ? "" : $" Export authorizations: {string.Join(", ", m.ExportAuthorizations)}.";
}
