using PdfSharp.Fonts;

namespace Keel.Documents.Marking;

/// <summary>
/// Resolves every font request to the DejaVu Sans faces embedded in this assembly. PDFsharp 6 has no system font
/// access off Windows, and an air-gapped Linux host may have no fonts at all, so marking never depends on them.
/// </summary>
public sealed class EmbeddedFontResolver : IFontResolver
{
    public const string FamilyName = "Keel Sans";
    private const string Regular = "Keel.Documents.Fonts.DejaVuSans.ttf";
    private const string Bold = "Keel.Documents.Fonts.DejaVuSans-Bold.ttf";
    private static readonly Lock Gate = new();

    /// <summary>Installs the resolver once per process. Safe to call from any thread, any number of times.</summary>
    public static void EnsureInstalled()
    {
        lock (Gate)
        {
            if (GlobalFontSettings.FontResolver is EmbeddedFontResolver) return;
            GlobalFontSettings.FontResolver = new EmbeddedFontResolver();
        }
    }

    // Italic is not used for markings; italic requests get the upright face rather than a simulated slant.
    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) => new(bold ? Bold : Regular);

    public byte[] GetFont(string faceName)
    {
        using var s = typeof(EmbeddedFontResolver).Assembly.GetManifestResourceStream(faceName)
            ?? throw new InvalidOperationException($"Embedded font {faceName} is missing from the assembly.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
