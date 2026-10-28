using System.Collections.Frozen;

namespace Keel.Core.Viewing;

/// <summary>Which in-browser viewer shows a format (ADR-0027). <see cref="None"/> means "no preview, facts and download only".</summary>
public static class ViewerTypes
{
    public const string Pdf = "pdf";
    public const string Model3D = "model3d";
    public const string Image = "image";
    public const string None = "none";
}

/// <summary>One format the viewer knows: a stable id, the viewer that shows it, a label for badges and the media type the server declares.</summary>
public sealed record FileFormat(string Id, string Viewer, string Label, string MediaType, string Badge);

/// <summary>
/// The formats of increment 6.0 and their file extensions. This table is used by the UI (badges, whether a "View" link is useful) and by the
/// viewer's content detection, which never trusts the extension or the stored media type alone (see <c>FileSniffer</c> in Keel.Viewer).
/// </summary>
public static class FileFormats
{
    public static readonly FileFormat Pdf = new("pdf", ViewerTypes.Pdf, "PDF", "application/pdf", "PDF");
    public static readonly FileFormat Png = new("png", ViewerTypes.Image, "PNG image", "image/png", "IMG");
    public static readonly FileFormat Jpeg = new("jpeg", ViewerTypes.Image, "JPEG image", "image/jpeg", "IMG");
    public static readonly FileFormat Gif = new("gif", ViewerTypes.Image, "GIF image", "image/gif", "IMG");
    public static readonly FileFormat WebP = new("webp", ViewerTypes.Image, "WebP image", "image/webp", "IMG");
    public static readonly FileFormat Svg = new("svg", ViewerTypes.Image, "SVG image", "image/svg+xml", "SVG");
    public static readonly FileFormat Stl = new("stl", ViewerTypes.Model3D, "STL model", "model/stl", "3D");
    public static readonly FileFormat Gltf = new("gltf", ViewerTypes.Model3D, "glTF model", "model/gltf+json", "3D");
    public static readonly FileFormat Glb = new("glb", ViewerTypes.Model3D, "GLB model", "model/gltf-binary", "3D");
    public static readonly FileFormat Obj = new("obj", ViewerTypes.Model3D, "OBJ model", "model/obj", "3D");
    public static readonly FileFormat Step = new("step", ViewerTypes.Model3D, "STEP model", "model/step", "3D");
    public static readonly FileFormat Iges = new("iges", ViewerTypes.Model3D, "IGES model", "model/iges", "3D");
    public static readonly FileFormat Unknown = new("unknown", ViewerTypes.None, "File", "application/octet-stream", "FILE");

    public static IReadOnlyList<FileFormat> All { get; } = [Pdf, Png, Jpeg, Gif, WebP, Svg, Stl, Gltf, Glb, Obj, Step, Iges];

    private static readonly FrozenDictionary<string, FileFormat> ByExt = new Dictionary<string, FileFormat>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = Pdf, [".png"] = Png, [".jpg"] = Jpeg, [".jpeg"] = Jpeg, [".jpe"] = Jpeg, [".gif"] = Gif, [".webp"] = WebP, [".svg"] = Svg,
        [".stl"] = Stl, [".gltf"] = Gltf, [".glb"] = Glb, [".obj"] = Obj,
        [".step"] = Step, [".stp"] = Step, [".p21"] = Step, [".iges"] = Iges, [".igs"] = Iges,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The format a file name claims (by extension only); <see cref="Unknown"/> when it has none the viewer knows.</summary>
    public static FileFormat FromFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return Unknown;
        var dot = fileName.LastIndexOf('.');
        return dot >= 0 && ByExt.TryGetValue(fileName[dot..], out var f) ? f : Unknown;
    }

    public static FileFormat? FromId(string? id) => All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
}
