namespace Keel.Viewing;

/// <summary>Configuration section <c>Keel:Viewer</c> (ADR-0027, VWR-9). Limits protect the server (whole file in memory) and the browser (GPU, tab memory).</summary>
public sealed class ViewerOptions
{
    public const string Section = "Keel:Viewer";
    public const long MiB = 1024 * 1024;

    /// <summary>3D models above this size show "too large to preview" (default 100 MiB).</summary>
    public long MaxModelBytes { get; set; } = 100 * MiB;
    /// <summary>PDFs above this size show "too large to preview" (default 50 MiB). Also bounded by <c>Keel:Vault:MaxPdfStampBytes</c>, the marking limit.</summary>
    public long MaxPdfBytes { get; set; } = 50 * MiB;
    public long MaxImageBytes { get; set; } = 50 * MiB;
    /// <summary>Triangle budget of the 3D viewer. Meshes above it are refused with a message instead of freezing the tab.</summary>
    public int MaxTriangles { get; set; } = 5_000_000;
    /// <summary>At most this many files are read into memory for viewing at once; others wait up to <see cref="ReadWaitSeconds"/>, then get 503.</summary>
    public int MaxConcurrentReads { get; set; } = 8;
    public int ReadWaitSeconds { get; set; } = 15;
    /// <summary>
    /// STEP and IGES are converted by the OpenCascade importer, an LGPL-2.1 component (VWR-12, docs/third-party/viewer.md). A site whose legal review declines it sets
    /// this to false: STEP and IGES then show "no preview" with the file facts and the download action, and the importer files are never requested.
    /// </summary>
    public bool EnableCadImport { get; set; } = true;

    public long LimitFor(string viewerType) => viewerType switch
    {
        Keel.Core.Viewing.ViewerTypes.Model3D => MaxModelBytes,
        Keel.Core.Viewing.ViewerTypes.Pdf => MaxPdfBytes,
        Keel.Core.Viewing.ViewerTypes.Image => MaxImageBytes,
        _ => 0,
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MaxModelBytes is < 1 or > 2048 * MiB) errors.Add("Keel:Viewer:MaxModelBytes must be between 1 byte and 2 GiB.");
        if (MaxPdfBytes is < 1 or > 2048 * MiB) errors.Add("Keel:Viewer:MaxPdfBytes must be between 1 byte and 2 GiB.");
        if (MaxImageBytes is < 1 or > 2048 * MiB) errors.Add("Keel:Viewer:MaxImageBytes must be between 1 byte and 2 GiB.");
        if (MaxTriangles is < 1000 or > 100_000_000) errors.Add("Keel:Viewer:MaxTriangles must be between 1,000 and 100,000,000.");
        if (MaxConcurrentReads is < 1 or > 256) errors.Add("Keel:Viewer:MaxConcurrentReads must be between 1 and 256.");
        if (ReadWaitSeconds is < 1 or > 300) errors.Add("Keel:Viewer:ReadWaitSeconds must be between 1 and 300.");
        return errors;
    }
}
