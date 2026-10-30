using Keel.Cad.Structure;
using Keel.Core;
using Keel.Core.Geometry;
using Keel.Core.Viewing;

namespace Keel.Cad;

public enum AssemblyViewStatus
{
    /// <summary>The model can be shown (see <see cref="AssemblyViewResult.Kind"/>).</summary>
    Ok,
    /// <summary>No such document or file for this caller (also the answer when the caller may not read or download it): nothing is said about it.</summary>
    NotFound,
    TooLarge,
    /// <summary>The file is not a model, or the model type has no viewer in the tab (the full viewer or a download is the way).</summary>
    NoPreview,
    /// <summary>A view gate refused (for example the file is quarantined).</summary>
    Blocked,
    /// <summary>The STEP file could not be read (damaged, several top-level products, over a limit). The message is safe to show.</summary>
    Unprocessable,
    Busy,
}

public static class AssemblyViewKinds
{
    /// <summary>A STEP file read for its structure: placed instances with boxes.</summary>
    public const string Assembly = "assembly";
    /// <summary>A mesh format (STL, OBJ, 3MF): the browser fetches the triangles from the access-checked mesh route.</summary>
    public const string Mesh = "mesh";
    /// <summary>A model type the tab cannot draw (glTF, IGES): only the facts and the link to the full viewer.</summary>
    public const string Other = "other";
}

/// <summary>What the Viewer tab shows for one model file. Built only after Read and Download were evaluated on the document's markings by the file source.</summary>
public sealed record AssemblyViewResult(AssemblyViewStatus Status, string? Message, ViewableFile? File = null, string? Kind = null, string? Format = null, StructureModel? Model = null, AssemblyLayout? Layout = null)
{
    public bool IsOk => Status == AssemblyViewStatus.Ok;
}

/// <summary>
/// The Viewer tab of a document or part page (ADR-0052): reads the model file of a document the caller may view, through the same file source the file viewer uses
/// (Read AND Download on the markings, SHA-256 verified read, <c>document.view</c> audit written before anything is returned, view gates such as quarantine), and for
/// STEP derives the placed structure with Keel's own reader. A caller who may not view the document gets NotFound and learns nothing, not even a part name. The
/// service never reads a file it was not authorized for and never returns bytes: only numbers and names derived from the file.
/// </summary>
public sealed class AssemblyViewService(IViewableFileSource source, IEnumerable<IFileViewGate> gates) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly IFileViewGate[] _gates = [.. gates];

    /// <summary>Opens the primary file of the latest (or the given) revision of a document, or the given file of it.</summary>
    public async Task<AssemblyViewResult> OpenAsync(Subject subject, string number, Guid? fileId, string? revision, string? correlationId, CancellationToken ct = default)
    {
        var described = fileId is { } f
            ? await source.DescribeAsync(subject, number, f, correlationId, ct)
            : await source.FindPrimaryAsync(subject, number, revision, correlationId, ct);
        if (!described.IsOk) return new AssemblyViewResult(AssemblyViewStatus.NotFound, null);
        var file = described.Value!;

        var format = ModelFormats.FromFileName(file.FileName);
        if (format is null) return await RefuseAsync(subject, file, "unknown", AssemblyViewStatus.NoPreview, "no_preview", "This file is not a 3D model.", correlationId, ct);
        if (file.SizeBytes > StructureLimits.MaxBytes && format == ModelFormats.Step)
            return await RefuseAsync(subject, file, format, AssemblyViewStatus.TooLarge, "too_large", $"This STEP file is too large to read here ({file.SizeBytes / (1024 * 1024)} MiB; the limit is {StructureLimits.MaxBytes / (1024 * 1024)} MiB).", correlationId, ct);
        foreach (var gate in _gates)
        {
            var decision = await gate.CheckAsync(file, ct);
            if (!decision.Allowed) return await RefuseAsync(subject, file, format, AssemblyViewStatus.Blocked, "blocked", decision.Reason ?? "This file cannot be viewed right now.", correlationId, ct);
        }
        if (format != ModelFormats.Step)
            return new AssemblyViewResult(AssemblyViewStatus.Ok, null, file, ModelFormats.IsMesh(format) ? AssemblyViewKinds.Mesh : AssemblyViewKinds.Other, format);

        if (!await _slots.WaitAsync(TimeSpan.FromSeconds(10), ct))
            return new AssemblyViewResult(AssemblyViewStatus.Busy, "The viewer is busy with other files. Try again in a moment.", file);
        try
        {
            var read = await source.ReadAsync(subject, file.DocumentNumber, file.FileId, correlationId, ct);
            if (!read.IsOk) return new AssemblyViewResult(AssemblyViewStatus.NotFound, null);
            StructureModel model;
            try
            {
                var bytes = read.Value!.Bytes;
                model = await Task.Run(() => StructureReader.Read(StructureFormats.Step, bytes, new StructureReadOptions()), ct);
            }
            catch (StructureFormatException e)
            {
                return await RefuseAsync(subject, file, format, AssemblyViewStatus.Unprocessable, "unreadable", e.Message, correlationId, ct);
            }
            var layout = AssemblyLayouts.Expand(model);
            await source.RecordAsync(subject, file, new ViewRecord(ViewerTypes.Model3D, format, "assembly", ViewOutcome.Delivered, "placements"), correlationId, ct);
            return new AssemblyViewResult(AssemblyViewStatus.Ok, null, file, AssemblyViewKinds.Assembly, format, model, layout);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<AssemblyViewResult> RefuseAsync(Subject subject, ViewableFile file, string format, AssemblyViewStatus status, string reason, string message, string? correlationId, CancellationToken ct)
    {
        await source.RecordAsync(subject, file, new ViewRecord(ViewerTypes.Model3D, format, "refused", ViewOutcome.Refused, reason + ": " + message), correlationId, ct);
        return new AssemblyViewResult(status, message, file);
    }

    public void Dispose() => _slots.Dispose();
}
