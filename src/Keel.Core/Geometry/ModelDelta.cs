using System.Globalization;

namespace Keel.Core.Geometry;

/// <summary>One line of the numeric comparison. <see cref="Change"/> and <see cref="Percent"/> are B minus A and that change relative to A; null when a side has no number.</summary>
public sealed record DeltaRow(string Key, string Label, string Unit, double? A, double? B, double? Change, double? Percent, string? Note = null);

/// <summary>The numeric comparison of two model versions and its plain-language summary (ADR-0046).</summary>
public sealed record ModelDelta(IReadOnlyList<DeltaRow> Rows, string Summary, bool SameGeometry, bool Comparable);

public static class ModelDeltas
{
    /// <summary>A change smaller than this share of the larger value (0.05 %) is not worth a sentence.</summary>
    private const double RelativeNoise = 0.0005;
    private const double AbsoluteNoise = 1e-4;

    public static ModelDelta Compare(ModelMetrics a, ModelMetrics b, bool sameBytes)
    {
        if (!a.IsAnalysed || !b.IsAnalysed)
        {
            var why = !a.IsAnalysed && !b.IsAnalysed ? "Neither version is measured by Keel"
                : !a.IsAnalysed ? "The first version is not measured by Keel" : "The second version is not measured by Keel";
            var notes = a.Status == "failed" || b.Status == "failed" ? " (at least one file could not be read)" : "";
            return new ModelDelta([], $"{why}{notes}, so no numbers can be compared. Compare the files themselves.", false, false);
        }

        var unit = a.Unit == b.Unit && a.Unit == "mm" ? "mm" : "units";
        var rows = new List<DeltaRow>
        {
            Row("sizeX", "Size X", unit, a.SizeX, b.SizeX),
            Row("sizeY", "Size Y", unit, a.SizeY, b.SizeY),
            Row("sizeZ", "Size Z", unit, a.SizeZ, b.SizeZ),
            Row("volume", "Volume", unit + "³", a.Volume, b.Volume, a.Watertight == true && b.Watertight == true ? null : "Only closed (watertight) meshes have a volume."),
            Row("area", "Surface area", unit + "²", a.SurfaceArea, b.SurfaceArea),
            Row("triangles", "Triangles", "", a.Triangles, b.Triangles),
            Row("vertices", "Vertices", "", a.Vertices, b.Vertices),
        };
        var sameGeometry = a.GeometryFingerprint is not null && a.GeometryFingerprint == b.GeometryFingerprint;
        return new ModelDelta(rows, Summarize(a, b, unit, sameGeometry, sameBytes), sameGeometry, true);
    }

    private static DeltaRow Row(string key, string label, string unit, double? a, double? b, string? note = null)
    {
        double? change = a is { } x && b is { } y ? y - x : null;
        double? percent = change is { } c && a is { } x2 && Math.Abs(x2) > 1e-12 ? c / Math.Abs(x2) * 100 : null;
        return new DeltaRow(key, label, unit, a, b, change, percent, note);
    }

    private static string Summarize(ModelMetrics a, ModelMetrics b, string unit, bool sameGeometry, bool sameBytes)
    {
        if (sameBytes) return "The two versions are the same file.";
        if (sameGeometry) return "Same geometry: the shape is identical; only the file differs (for example another export).";

        var phrases = new List<string>();
        var heightFirst = b.Up == "y";
        double?[] sizeA = [a.SizeX, a.SizeY, a.SizeZ], sizeB = [b.SizeX, b.SizeY, b.SizeZ];
        // Words per axis: X is the width; the up axis is the height; the remaining one is the depth.
        string[] more = heightFirst ? ["wider", "taller", "deeper"] : ["wider", "deeper", "taller"];
        string[] less = heightFirst ? ["narrower", "shorter", "shallower"] : ["narrower", "shallower", "shorter"];
        for (var i = 0; i < 3; i++)
        {
            if (sizeA[i] is not { } sa || sizeB[i] is not { } sb) continue;
            var d = sb - sa;
            if (!Significant(d, sa, sb)) continue;
            phrases.Add($"{(d > 0 ? more[i] : less[i])} by {Length(Math.Abs(d))} {unit}");
        }
        if (a.Volume is { } va && b.Volume is { } vb && a.Watertight == true && b.Watertight == true && va > 0)
        {
            var pct = (vb - va) / va * 100;
            if (Math.Abs(pct) >= 0.05) phrases.Add($"volume {(pct > 0 ? "up" : "down")} {Math.Abs(pct).ToString("0.0", CultureInfo.InvariantCulture)} %");
        }
        else if (a.Watertight != b.Watertight)
        {
            phrases.Add(b.Watertight == true ? "mesh is now closed (volume available)" : "mesh is no longer closed (no volume)");
        }
        if (a.SurfaceArea is { } aa && b.SurfaceArea is { } ab && aa > 0)
        {
            var pct = (ab - aa) / aa * 100;
            if (Math.Abs(pct) >= 0.05) phrases.Add($"surface area {(pct > 0 ? "up" : "down")} {Math.Abs(pct).ToString("0.0", CultureInfo.InvariantCulture)} %");
        }
        if (a.Triangles != b.Triangles && a.Triangles is { } ta && b.Triangles is { } tb)
        {
            var diff = Math.Abs(tb - ta);
            phrases.Add($"{diff.ToString("N0", CultureInfo.InvariantCulture)} {(tb > ta ? "more" : "fewer")} triangles");
        }
        if (phrases.Count == 0)
            return "No measurable difference in size, volume or surface area; the mesh itself differs slightly.";
        var text = string.Join(", ", phrases);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static bool Significant(double d, double a, double b) =>
        Math.Abs(d) >= AbsoluteNoise && Math.Abs(d) >= RelativeNoise * Math.Max(Math.Abs(a), Math.Abs(b));

    /// <summary>1 decimal from 0.1 up, 3 decimals below ("2.0", "0.050").</summary>
    internal static string Length(double v) => v >= 0.1 ? v.ToString("0.0", CultureInfo.InvariantCulture) : v.ToString("0.000", CultureInfo.InvariantCulture);
}
