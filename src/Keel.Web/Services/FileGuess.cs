using Microsoft.AspNetCore.Components.Forms;

namespace Keel.Web.Services;

/// <summary>One file the person chose for a new part or a part's new documents: the document it will become (name and type), editable before anything is created.</summary>
public sealed class FileRow(IBrowserFile file, string name, string type)
{
    public IBrowserFile File { get; } = file;
    public string Name { get; set; } = name;
    public string Type { get; set; } = type;
}

/// <summary>
/// First guesses for a file chosen in the browser: a document name from the file name and a document type from the extension (and a word in the name). These are only
/// starting values the person sees and can change; a type that is not in the administrator's list is never offered.
/// </summary>
public static class FileGuess
{
    private static readonly HashSet<string> Models = new(StringComparer.OrdinalIgnoreCase)
    { ".stl", ".obj", ".3mf", ".step", ".stp", ".iges", ".igs", ".glb", ".gltf", ".sldprt", ".sldasm", ".ipt", ".iam", ".prt", ".asm", ".catpart", ".catproduct" };

    /// <summary>The file name without its extension, with underscores read as spaces: <c>bracket_v2.stl</c> becomes <c>bracket v2</c>.</summary>
    public static string Name(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName.Trim());
        stem = stem.Replace('_', ' ').Trim();
        return stem.Length == 0 ? fileName.Trim() : stem;
    }

    /// <summary>Whether the file is a CAD or mesh model (the part page then offers its Viewer).</summary>
    public static bool IsModel(string fileName) => Models.Contains(Path.GetExtension(fileName));

    /// <summary>The document type to start with, chosen among <paramref name="available"/> (type codes); falls back to <paramref name="fallback"/>, then to the first available.</summary>
    public static string Type(string fileName, IReadOnlyList<string> available, string fallback)
    {
        string Pick(string wanted) => available.FirstOrDefault(a => string.Equals(a, wanted, StringComparison.OrdinalIgnoreCase)) ?? "";
        var lower = fileName.ToLowerInvariant();
        var wanted =
            Path.GetExtension(lower) is ".stl" or ".obj" or ".3mf" or ".step" or ".stp" or ".iges" or ".igs" or ".glb" or ".gltf" or ".sldprt" or ".sldasm" or ".ipt" or ".iam" or ".prt" or ".asm" or ".catpart" or ".catproduct" ? "CAD"
            : lower.Contains("spec", StringComparison.Ordinal) ? "Specification"
            : lower.Contains("procedure", StringComparison.Ordinal) || lower.Contains("instruction", StringComparison.Ordinal) ? "Procedure"
            : lower.Contains("report", StringComparison.Ordinal) ? "Report"
            : lower.Contains("drawing", StringComparison.Ordinal) || Path.GetExtension(lower) is ".dwg" or ".dxf" ? "Drawing"
            : "";
        var picked = wanted.Length > 0 ? Pick(wanted) : "";
        if (picked.Length > 0) return picked;
        var back = Pick(fallback);
        return back.Length > 0 ? back : available.Count > 0 ? available[0] : fallback;
    }
}
