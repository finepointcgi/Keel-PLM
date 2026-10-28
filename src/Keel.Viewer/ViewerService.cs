using Keel.Core;
using Keel.Core.Viewing;

namespace Keel.Viewing;

public enum ViewerStatus
{
    /// <summary>The viewer can show the file.</summary>
    Ready,
    /// <summary>Above the configured preview limit for its type: "too large to preview, download instead".</summary>
    TooLarge,
    /// <summary>No viewer for this content: facts and the download action only.</summary>
    NoPreview,
    /// <summary>A registered <see cref="IFileViewGate"/> refused (for example a quarantined file).</summary>
    Blocked,
}

/// <summary>What the viewer page and its metadata route show about a file the caller may view.</summary>
public sealed record ViewerDescription(
    ViewableFile File,
    Detection Detection,
    string ViewerType,
    ViewerStatus Status,
    string? Message,
    long LimitBytes,
    int MaxTriangles);

public enum ViewerContentStatus
{
    Ok,
    NotFound,
    Forbidden,
    /// <summary>Too large for the viewer.</summary>
    TooLarge,
    /// <summary>No viewer for the content (or the content is not what the name says).</summary>
    NoPreview,
    /// <summary>A gate refused.</summary>
    Blocked,
    /// <summary>The file cannot be shown with its markings (for example a PDF that cannot be stamped).</summary>
    Unprocessable,
    /// <summary>All read slots are in use; retry shortly.</summary>
    Busy,
}

/// <summary>Bytes ready to send inline, with the media type the SERVER declares (from the detected format, never the stored media type).</summary>
public sealed record ViewerContent(ViewableFile File, byte[] Bytes, FileFormat Format, bool Marked);

public sealed record ViewerContentResult(ViewerContentStatus Status, string? Message, ViewerContent? Content, IReadOnlyList<string>? Reasons = null)
{
    public bool IsOk => Status == ViewerContentStatus.Ok;
}

/// <summary>
/// The one place that decides whether a file is shown (ADR-0027): authorization and audit are the owner's (<see cref="IViewableFileSource"/>), the viewer adds
/// content detection, size limits, the <see cref="IFileViewGate"/> checks of other modules, and a bound on files held in memory at once. Every refusal after
/// authorization is audited as a denial; a delivery is audited before the first byte is written.
/// </summary>
public sealed class ViewerService(IViewableFileSource source, ViewerOptions options, IEnumerable<IFileViewGate> gates) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.MaxConcurrentReads, options.MaxConcurrentReads);

    public ViewerOptions Options => options;

    /// <summary>Describes a file for the page: authorization, then a head-only content check, the size limit and the gates. No audit on success (see <see cref="IViewableFileSource.DescribeAsync"/>).</summary>
    public async Task<Outcome<ViewerDescription>> DescribeAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct = default)
    {
        var described = await source.DescribeAsync(subject, number, fileId, correlationId, ct);
        return await ToDescriptionAsync(described, ct);
    }

    public async Task<Outcome<ViewerDescription>> DescribePrimaryAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct = default) =>
        await ToDescriptionAsync(await source.FindPrimaryAsync(subject, number, revision, correlationId, ct), ct);

    private async Task<Outcome<ViewerDescription>> ToDescriptionAsync(Outcome<ViewableFile> described, CancellationToken ct)
    {
        if (!described.IsOk) return new Outcome<ViewerDescription>(described.Kind, null, described.Errors);
        var file = described.Value!;
        var head = await source.ReadHeadAsync(file, FileSniffer.HeadBytes, ct);
        var detection = FileSniffer.Detect(file.FileName, head, file.SizeBytes);
        var (status, message) = await StatusAsync(file, detection, ct);
        return Outcome<ViewerDescription>.Ok(new ViewerDescription(file, detection, detection.Format.Viewer, status, message, options.LimitFor(detection.Format.Viewer), options.MaxTriangles));
    }

    /// <summary>Reads, verifies, detects, checks, audits and returns the bytes to send. Throws <see cref="StoredFileIntegrityException"/> (already audited) when the stored file is damaged.</summary>
    public async Task<ViewerContentResult> OpenAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct = default)
    {
        var described = await source.DescribeAsync(subject, number, fileId, correlationId, ct);
        if (!described.IsOk) return Failed(described);
        var file = described.Value!;

        // Cheap refusals first, before the file is read into memory: size by the name's format and the head-only content check.
        var head = await source.ReadHeadAsync(file, FileSniffer.HeadBytes, ct);
        var early = FileSniffer.Detect(file.FileName, head, file.SizeBytes);
        var (preStatus, preMessage) = await StatusAsync(file, early, ct);
        if (preStatus != ViewerStatus.Ready) return await RefuseAsync(subject, file, early, preStatus, preMessage!, correlationId, ct);

        if (!await _slots.WaitAsync(TimeSpan.FromSeconds(options.ReadWaitSeconds), ct))
            return new ViewerContentResult(ViewerContentStatus.Busy, "The viewer is busy with other files. Try again in a moment.", null);
        try
        {
            var read = await source.ReadAsync(subject, number, fileId, correlationId, ct);
            if (!read.IsOk)
            {
                if (read.Kind == OutcomeKind.Invalid)
                {
                    var message = string.Join(" ", read.Errors);
                    await source.RecordAsync(subject, file, new ViewRecord(early.Format.Viewer, early.Format.Id, "refused", ViewOutcome.Refused, message), correlationId, ct);
                    return new ViewerContentResult(ViewerContentStatus.Unprocessable, message, null);
                }
                return Failed(read);
            }
            var content = read.Value!;

            // Detection on the verified (and, for PDFs, stamped) bytes decides what is sent; a file that changed type between the head check and now,
            // or whose content does not match its name, is refused.
            var detected = FileSniffer.Detect(file.FileName, content.Bytes, content.Bytes.Length);
            var (status, msg) = await StatusAsync(file, detected, ct);
            if (status != ViewerStatus.Ready) return await RefuseAsync(subject, file, detected, status, msg!, correlationId, ct);

            await source.RecordAsync(subject, file, new ViewRecord(detected.Format.Viewer, detected.Format.Id, content.Marked ? "pdf-stamped" : "overlay", ViewOutcome.Delivered,
                detected.Warning), correlationId, ct);
            return new ViewerContentResult(ViewerContentStatus.Ok, null, new ViewerContent(file, content.Bytes, detected.Format, content.Marked));
        }
        finally
        {
            _slots.Release();
        }
    }

    // The single place a view can be refused (ADR-0027, VWR-8): content support, size limit, gates.
    private async Task<(ViewerStatus Status, string? Message)> StatusAsync(ViewableFile file, Detection detection, CancellationToken ct)
    {
        if (!detection.Recognised)
            return (ViewerStatus.NoPreview, detection.Warning ?? "There is no preview for this type of file. Its details are shown below.");
        if (!options.EnableCadImport && detection.Format.Id is "step" or "iges")
            return (ViewerStatus.NoPreview, "Previews of STEP and IGES files are switched off at this site. Download the file to open it in a CAD system.");
        var limit = options.LimitFor(detection.Format.Viewer);
        var actual = file.SizeBytes;
        if (actual > limit)
            return (ViewerStatus.TooLarge, $"This file is too large to preview ({Size(actual)}; the limit for {detection.Format.Label} files is {Size(limit)}). Download it instead.");
        foreach (var gate in gates)
        {
            var decision = await gate.CheckAsync(file, ct);
            if (!decision.Allowed) return (ViewerStatus.Blocked, decision.Reason ?? "This file cannot be viewed right now.");
        }
        return (ViewerStatus.Ready, null);
    }

    private async Task<ViewerContentResult> RefuseAsync(Subject subject, ViewableFile file, Detection detection, ViewerStatus status, string message, string? correlationId, CancellationToken ct)
    {
        var reason = status switch { ViewerStatus.TooLarge => "too_large", ViewerStatus.Blocked => "blocked", _ => "no_preview" };
        await source.RecordAsync(subject, file, new ViewRecord(detection.Format.Viewer, detection.Format.Id, "refused", ViewOutcome.Refused, reason + ": " + message), correlationId, ct);
        var mapped = status switch { ViewerStatus.TooLarge => ViewerContentStatus.TooLarge, ViewerStatus.Blocked => ViewerContentStatus.Blocked, _ => ViewerContentStatus.NoPreview };
        return new ViewerContentResult(mapped, message, null);
    }

    private static ViewerContentResult Failed<T>(Outcome<T> outcome) => new(
        outcome.Kind == OutcomeKind.Forbidden ? ViewerContentStatus.Forbidden : ViewerContentStatus.NotFound, null, null, outcome.Errors);

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KiB",
        < 1024L * 1024 * 1024 => (bytes / 1024d / 1024).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MiB",
        _ => (bytes / 1024d / 1024 / 1024).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " GiB",
    };

    public void Dispose() => _slots.Dispose();
}
