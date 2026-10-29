namespace Keel.Web.Services.Visuals;

public sealed record TreemapItem(string Id, double Value);

public sealed record TreemapTile(string Id, double X, double Y, double Width, double Height, double Value)
{
    public double Area => Width * Height;
}

/// <summary>Items kept for drawing, plus what was folded into the "others" tile (null when nothing was folded). <see cref="OthersCount"/> counts items, not values.</summary>
public sealed record LimitedItems(IReadOnlyList<TreemapItem> Items, TreemapItem? Others, int OthersCount);

/// <summary>
/// Squarified treemap (Bruls, Huizing and van Wijk): tiles keep the aspect ratio close to 1 and their areas are proportional to the values, covering the
/// rectangle exactly (floating-point rounding only). Items with a value that is not positive are dropped. Pure and deterministic: items are ordered by value
/// (largest first) and then by id.
/// </summary>
public static class SquarifiedTreemap
{
    public const string OthersId = "~others";

    public static IReadOnlyList<TreemapTile> Layout(IReadOnlyList<TreemapItem> items, double x, double y, double width, double height)
    {
        var sorted = items.Where(i => i.Value > 0 && double.IsFinite(i.Value)).OrderByDescending(i => i.Value).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        var tiles = new List<TreemapTile>(sorted.Count);
        if (sorted.Count == 0 || width <= 0 || height <= 0) return tiles;

        var total = sorted.Sum(i => i.Value);
        var scale = width * height / total; // value -> area
        var rect = (X: x, Y: y, W: width, H: height);
        var row = new List<TreemapItem>();
        var i0 = 0;
        while (i0 < sorted.Count)
        {
            var side = Math.Min(rect.W, rect.H);
            var next = sorted[i0];
            if (row.Count == 0 || Worst(row, next, side, scale) <= Worst(row, null, side, scale))
            {
                row.Add(next);
                i0++;
                continue;
            }
            rect = Place(row, rect, scale, tiles);
            row.Clear();
        }
        if (row.Count > 0) Place(row, rect, scale, tiles);
        return tiles;
    }

    /// <summary>Keeps the <paramref name="maxTiles"/> - 1 largest items and folds the rest into one "others" item (value = their sum), so a layout never exceeds the budget.</summary>
    public static LimitedItems Limit(IReadOnlyList<TreemapItem> items, int maxTiles)
    {
        var positive = items.Where(i => i.Value > 0 && double.IsFinite(i.Value)).OrderByDescending(i => i.Value).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        if (maxTiles < 2) throw new ArgumentOutOfRangeException(nameof(maxTiles), "At least two tiles are needed to fold the rest into one.");
        if (positive.Count <= maxTiles) return new LimitedItems(positive, null, 0);
        var kept = positive.Take(maxTiles - 1).ToList();
        var rest = positive.Skip(maxTiles - 1).ToList();
        return new LimitedItems(kept, new TreemapItem(OthersId, rest.Sum(i => i.Value)), rest.Count);
    }

    private static double Worst(List<TreemapItem> row, TreemapItem? extra, double side, double scale)
    {
        double sum = 0, min = double.MaxValue, max = 0;
        foreach (var i in row) { sum += i.Value; min = Math.Min(min, i.Value); max = Math.Max(max, i.Value); }
        if (extra is not null) { sum += extra.Value; min = Math.Min(min, extra.Value); max = Math.Max(max, extra.Value); }
        var area = sum * scale;
        var s2 = side * side;
        return Math.Max(s2 * max * scale / (area * area), area * area / (s2 * min * scale));
    }

    private static (double X, double Y, double W, double H) Place(List<TreemapItem> row, (double X, double Y, double W, double H) rect, double scale, List<TreemapTile> tiles)
    {
        var area = row.Sum(i => i.Value) * scale;
        if (rect.W >= rect.H)
        {
            // Lay the row as a column on the left edge.
            var w = rect.W > 0 ? area / rect.H : 0;
            var y = rect.Y;
            for (var k = 0; k < row.Count; k++)
            {
                var h = k == row.Count - 1 ? rect.Y + rect.H - y : row[k].Value * scale / w;
                tiles.Add(new TreemapTile(row[k].Id, rect.X, y, w, h, row[k].Value));
                y += h;
            }
            return (rect.X + w, rect.Y, rect.W - w, rect.H);
        }
        else
        {
            var h = area / rect.W;
            var x = rect.X;
            for (var k = 0; k < row.Count; k++)
            {
                var w = k == row.Count - 1 ? rect.X + rect.W - x : row[k].Value * scale / h;
                tiles.Add(new TreemapTile(row[k].Id, x, rect.Y, w, h, row[k].Value));
                x += w;
            }
            return (rect.X, rect.Y + h, rect.W, rect.H - h);
        }
    }
}
