using System.Globalization;

namespace Keel.Core.Geometry;

/// <summary>The model formats Keel versions specially (ADR-0046). Mesh formats are measured; the others get file facts only.</summary>
public static class ModelFormats
{
    public const string Stl = "stl", Obj = "obj", ThreeMf = "3mf", Step = "step", Gltf = "gltf", Glb = "glb", Iges = "iges";

    public static string? FromFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var dot = fileName.LastIndexOf('.');
        if (dot < 0) return null;
        return fileName[dot..].ToLowerInvariant() switch
        {
            ".stl" => Stl, ".obj" => Obj, ".3mf" => ThreeMf,
            ".step" or ".stp" or ".p21" => Step, ".gltf" => Gltf, ".glb" => Glb, ".iges" or ".igs" => Iges,
            _ => null,
        };
    }

    public static bool IsModel(string? fileName) => FromFileName(fileName) is not null;
    public static bool IsMesh(string? format) => format is Stl or Obj or ThreeMf;

    public static string Label(string format) => format switch
    {
        Stl => "STL", Obj => "OBJ", ThreeMf => "3MF", Step => "STEP", Gltf => "glTF", Glb => "GLB", Iges => "IGES", _ => "File",
    };
}

/// <summary>
/// Counts the products in a STEP exchange file. Implemented by the CAD module (its in-house STEP structure reader); the documents module only sees
/// this port, so a site without CAD integration simply gets "not analysed".
/// </summary>
public interface IStepProductCounter
{
    /// <summary>The number of products, or null when the file cannot be read as STEP structure. Never throws for hostile input.</summary>
    int? CountProducts(ReadOnlyMemory<byte> content);
}

public static class ModelAnalysis
{
    /// <summary>Why a non-mesh format is not measured. Shown verbatim next to the file facts.</summary>
    public const string NotAnalysedReason = "Keel does not measure the geometry of this format; only file facts are shown.";

    /// <summary>
    /// Analyses one model file. Mesh formats (STL, OBJ, 3MF) are read and measured; everything else yields <c>facts_only</c>. Bytes are only needed for the
    /// mesh formats and for the STEP product count, so callers may pass an empty memory for the other formats.
    /// </summary>
    public static ModelMetrics Analyze(string fileName, ReadOnlyMemory<byte> content, long sizeBytes, MeshLimits limits, IStepProductCounter? step = null)
    {
        var format = ModelFormats.FromFileName(fileName) ?? "other";
        if (!ModelFormats.IsMesh(format))
        {
            int? products = null;
            if (format == ModelFormats.Step && step is not null && content.Length > 0) products = step.CountProducts(content);
            return new ModelMetrics
            {
                Status = "facts_only", Format = format, StepProducts = products,
                Reason = format == ModelFormats.Step && products is null ? "The STEP product count could not be read; " + NotAnalysedReason : NotAnalysedReason,
            };
        }
        if (sizeBytes > limits.MaxBytes)
            return Failed(format, $"The file is too large to analyse ({sizeBytes / (1024 * 1024)} MiB; the limit is {limits.MaxBytes / (1024 * 1024)} MiB).");
        try
        {
            var span = content.Span;
            var mesh = format switch
            {
                ModelFormats.Stl => StlReader.Read(span, limits),
                ModelFormats.Obj => ObjReader.Read(span, limits),
                _ => ThreeMfReader.Read(content, limits),
            };
            return MeshAnalyzer.Analyze(mesh);
        }
        catch (MeshFormatException e)
        {
            return Failed(format, e.Message);
        }
    }

    private static ModelMetrics Failed(string format, string reason) => new() { Status = "failed", Format = format, Reason = reason };

    /// <summary>The mesh behind a file for the visual compare, or the plain reason it cannot be shown.</summary>
    public static MeshData ReadMesh(string fileName, ReadOnlyMemory<byte> content, MeshLimits limits)
    {
        var format = ModelFormats.FromFileName(fileName);
        if (!ModelFormats.IsMesh(format)) throw new MeshFormatException("The visual comparison supports STL, OBJ and 3MF models.");
        if (content.Length > limits.MaxBytes) throw new MeshFormatException("The file is too large to compare visually.");
        return format switch
        {
            ModelFormats.Stl => StlReader.Read(content.Span, limits),
            ModelFormats.Obj => ObjReader.Read(content.Span, limits),
            _ => ThreeMfReader.Read(content, limits),
        };
    }

}
