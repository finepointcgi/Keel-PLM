using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Keel.Documents.Compare;

public enum DiffOp { Equal, Insert, Delete }

/// <summary>
/// A run of text with one operation. <see cref="Change"/> is the index of the change the run belongs to (a deletion and the insertion that replaces it share one),
/// or null for unchanged text. Text keeps the whitespace and line breaks in front of each word, so concatenating the segments of one side restores that side.
/// </summary>
public sealed record DiffSegment(DiffOp Op, string Text, int? Change);

/// <summary>One changed range: a deletion, an insertion, or both (a replacement). Previews are cut at <see cref="WordDiff.PreviewLength"/> characters; the digest covers all of it.</summary>
public sealed record DiffChange(
    int Index, string Kind, int DeletedWords, int InsertedWords, string DeletedPreview, string InsertedPreview, int LeftOffset, int RightOffset, string Digest);

/// <summary>Summary counts. Words are runs of letters and digits; punctuation changes count as changes but not as words.</summary>
public sealed record DiffCounts(int InsertedWords, int DeletedWords, int UnchangedWords, int Changes, int LeftWords, int RightWords, double Similarity);

public sealed record TextDiff(IReadOnlyList<DiffSegment> Segments, IReadOnlyList<DiffChange> Changes, DiffCounts Counts, bool Coarse)
{
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// A word-level diff of two texts (increment 9.5, ADR-0053). Two passes keep it fast on long documents: a Myers diff over whole lines finds the stretches that differ,
/// then a Myers diff over words (and punctuation marks) aligns the inside of each stretch. Whitespace differences alone are not changes (a reflowed PDF or a re-wrapped
/// paragraph is not an edit), but the whitespace of the right-hand text is kept for display. The result is deterministic: the same two texts always give the same list of
/// changes, which is what lets a review comment point at "change 7" (<see cref="DiffVersion"/> changes if the algorithm ever does).
/// </summary>
/// <remarks>
/// Limits (ASSUMPTION A70): the Myers search gives up beyond <see cref="MaxEditDistance"/> edits in one pass; a stretch that exceeds it is shown as one replacement of the
/// whole stretch and <see cref="TextDiff.Coarse"/> says so, so a number of changes is never claimed that was not computed.
/// </remarks>
public static class WordDiff
{
    public const int DiffVersion = 1;
    public const int MaxEditDistance = 4000;
    public const int PreviewLength = 400;
    /// <summary>More tokens than this on one side are refused before any work is done.</summary>
    public const int MaxTokensPerSide = 600_000;

    internal readonly record struct Token(string Gap, string Text)
    {
        public string Full => Gap + Text;
        public bool IsWord
        {
            get
            {
                foreach (var c in Text) if (char.IsLetterOrDigit(c)) return true;
                return false;
            }
        }
    }

    private sealed class TooLargeException : Exception;

    /// <summary>Diffs <paramref name="left"/> (the base) against <paramref name="right"/> (the changed text). Returns null when either side has more than <see cref="MaxTokensPerSide"/> tokens.</summary>
    public static TextDiff? Compare(string left, string right)
    {
        List<Token> a, b;
        try
        {
            a = Tokenize(left);
            b = Tokenize(right);
        }
        catch (TooLargeException)
        {
            return null;
        }
        var ops = new List<(DiffOp Op, Token Token)>(Math.Max(a.Count, b.Count));
        var coarse = Diff(a, b, ops);
        return Assemble(ops, a, b, coarse);
    }

    // ---------------------------------------------------------------- tokens

    internal static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var gap = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { gap.Append(c); i++; continue; }
            var start = i;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i += 2;
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                i++;
                while (i < text.Length)
                {
                    var d = text[i];
                    if (char.IsLetterOrDigit(d) || d == '_') { i++; continue; }
                    // 3.14, don't, 1,000, M3-4: a joiner between two letters or digits stays inside the word.
                    if (d is '.' or ',' or '\'' or '’' or '-' && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1])) { i++; continue; }
                    break;
                }
            }
            else i++;
            tokens.Add(new Token(gap.ToString(), text[start..i]));
            gap.Clear();
            if (tokens.Count > MaxTokensPerSide) throw new TooLargeException();
        }
        return tokens;
    }

    private static int LineOf(string gap)
    {
        var n = 0;
        foreach (var c in gap) if (c == '\n') n++;
        return n;
    }

    // ---------------------------------------------------------------- two-pass diff

    /// <summary>Fills <paramref name="ops"/>; returns true when some stretch had to be shown as one replacement.</summary>
    private static bool Diff(List<Token> a, List<Token> b, List<(DiffOp, Token)> ops)
    {
        var linesA = Lines(a);
        var linesB = Lines(b);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var ka = linesA.Select(l => Id(ids, l.Key)).ToArray();
        var kb = linesB.Select(l => Id(ids, l.Key)).ToArray();
        var script = Myers(ka, kb, MaxEditDistance);
        var coarse = false;
        if (script is null)
        {
            // Too many differing lines for the line pass: one stretch, words only.
            coarse |= DiffWords(a, b, ops);
            return coarse;
        }
        List<Token> delTokens = [], insTokens = [];
        void FlushHunk()
        {
            if (delTokens.Count == 0 && insTokens.Count == 0) return;
            coarse |= DiffWords(delTokens, insTokens, ops);
            delTokens = [];
            insTokens = [];
        }
        foreach (var (op, i, j) in script)
        {
            switch (op)
            {
                case DiffOp.Equal:
                    FlushHunk();
                    foreach (var t in linesB[j].Tokens) ops.Add((DiffOp.Equal, t));
                    break;
                case DiffOp.Delete: delTokens.AddRange(linesA[i].Tokens); break;
                default: insTokens.AddRange(linesB[j].Tokens); break;
            }
        }
        FlushHunk();
        return coarse;
    }

    private sealed record Line(string Key, List<Token> Tokens);

    /// <summary>Groups tokens into lines (a token starts a new line when the whitespace in front of it holds a line break). The key ignores whitespace.</summary>
    private static List<Line> Lines(List<Token> tokens)
    {
        var lines = new List<Line>();
        List<Token>? current = null;
        foreach (var t in tokens)
        {
            if (current is null || LineOf(t.Gap) > 0)
            {
                current = [];
                lines.Add(new Line("", current));
            }
            current.Add(t);
        }
        return [.. lines.Select(l => l with { Key = string.Join('\u001f', l.Tokens.Select(t => t.Text)) })];
    }

    private static int Id(Dictionary<string, int> ids, string key)
    {
        if (!ids.TryGetValue(key, out var id)) ids[key] = id = ids.Count;
        return id;
    }

    private static bool DiffWords(List<Token> a, List<Token> b, List<(DiffOp, Token)> ops)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var ka = a.Select(t => Id(ids, t.Text)).ToArray();
        var kb = b.Select(t => Id(ids, t.Text)).ToArray();
        var script = Myers(ka, kb, MaxEditDistance);
        if (script is null)
        {
            foreach (var t in a) ops.Add((DiffOp.Delete, t));
            foreach (var t in b) ops.Add((DiffOp.Insert, t));
            return a.Count > 0 && b.Count > 0;
        }
        foreach (var (op, i, j) in script)
            ops.Add(op switch { DiffOp.Equal => (op, b[j]), DiffOp.Delete => (op, a[i]), _ => (op, b[j]) });
        return false;
    }

    /// <summary>
    /// Myers' O(ND) shortest edit script over integer keys, after trimming the common prefix and suffix. Returns the script in order as (op, index in a, index in b),
    /// or null when more than <paramref name="maxD"/> edits would be needed. Memory is O(D squared), bounded by the cap.
    /// </summary>
    internal static List<(DiffOp Op, int A, int B)>? Myers(int[] a, int[] b, int maxD)
    {
        int n = a.Length, m = b.Length;
        var prefix = 0;
        while (prefix < n && prefix < m && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix && a[n - 1 - suffix] == b[m - 1 - suffix]) suffix++;
        var result = new List<(DiffOp, int, int)>(n + m);
        for (var i = 0; i < prefix; i++) result.Add((DiffOp.Equal, i, i));

        int an = n - prefix - suffix, bn = m - prefix - suffix;
        if (an == 0) for (var j = 0; j < bn; j++) result.Add((DiffOp.Insert, -1, prefix + j));
        else if (bn == 0) for (var i = 0; i < an; i++) result.Add((DiffOp.Delete, prefix + i, -1));
        else
        {
            var middle = MyersCore(a.AsSpan(prefix, an), b.AsSpan(prefix, bn), maxD);
            if (middle is null) return null;
            foreach (var (op, i, j) in middle) result.Add((op, i < 0 ? -1 : i + prefix, j < 0 ? -1 : j + prefix));
        }
        for (var s = suffix; s > 0; s--) result.Add((DiffOp.Equal, n - s, m - s));
        return result;
    }

    private static List<(DiffOp, int, int)>? MyersCore(ReadOnlySpan<int> a, ReadOnlySpan<int> b, int maxD)
    {
        int n = a.Length, m = b.Length;
        var max = Math.Min(n + m, maxD);
        var off = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var found = -1;
        for (var d = 0; d <= max && found < 0; d++)
        {
            var snap = new int[2 * d + 3];
            Array.Copy(v, off - d - 1, snap, 0, snap.Length);
            trace.Add(snap);
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[off + k - 1] < v[off + k + 1])) x = v[off + k + 1];
                else x = v[off + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }
        if (found < 0) return null;

        var edits = new List<(DiffOp, int, int)>(n + m);
        int cx = n, cy = m;
        for (var d = found; d > 0; d--)
        {
            var prev = trace[d];
            var k = cx - cy;
            int prevK;
            if (k == -d || (k != d && prev[k - 1 + d + 1] < prev[k + 1 + d + 1])) prevK = k + 1; else prevK = k - 1;
            var prevX = prev[prevK + d + 1];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY) { edits.Add((DiffOp.Equal, cx - 1, cy - 1)); cx--; cy--; }
            if (cx == prevX) { edits.Add((DiffOp.Insert, -1, cy - 1)); cy--; }
            else { edits.Add((DiffOp.Delete, cx - 1, -1)); cx--; }
        }
        while (cx > 0 && cy > 0) { edits.Add((DiffOp.Equal, cx - 1, cy - 1)); cx--; cy--; }
        edits.Reverse();
        return edits;
    }

    // ---------------------------------------------------------------- segments, changes, counts

    private static TextDiff Assemble(List<(DiffOp Op, Token Token)> ops, List<Token> a, List<Token> b, bool coarse)
    {
        var segments = new List<DiffSegment>();
        var changes = new List<DiffChange>();
        var equal = new StringBuilder();
        int leftPos = 0, rightPos = 0, insertedWords = 0, deletedWords = 0, unchangedWords = 0;

        void FlushEqual()
        {
            if (equal.Length == 0) return;
            segments.Add(new DiffSegment(DiffOp.Equal, equal.ToString(), null));
            equal.Clear();
        }

        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Op == DiffOp.Equal)
            {
                var t = ops[i].Token;
                equal.Append(t.Full);
                leftPos += t.Full.Length;
                rightPos += t.Full.Length;
                if (t.IsWord) unchangedWords++;
                i++;
                continue;
            }
            FlushEqual();
            // One change = the whole run of insertions and deletions between two unchanged words; deletions are shown first.
            var del = new List<Token>();
            var ins = new List<Token>();
            while (i < ops.Count && ops[i].Op != DiffOp.Equal)
            {
                (ops[i].Op == DiffOp.Delete ? del : ins).Add(ops[i].Token);
                i++;
            }
            var index = changes.Count;
            var delText = string.Concat(del.Select(t => t.Full));
            var insText = string.Concat(ins.Select(t => t.Full));
            var dw = del.Count(t => t.IsWord);
            var iw = ins.Count(t => t.IsWord);
            deletedWords += dw;
            insertedWords += iw;
            changes.Add(new DiffChange(index, del.Count > 0 && ins.Count > 0 ? "replace" : del.Count > 0 ? "delete" : "insert", dw, iw,
                Preview(delText), Preview(insText), leftPos, rightPos, ChangeDigest(del, ins)));
            if (del.Count > 0) segments.Add(new DiffSegment(DiffOp.Delete, delText, index));
            if (ins.Count > 0) segments.Add(new DiffSegment(DiffOp.Insert, insText, index));
            leftPos += delText.Length;
            rightPos += insText.Length;
        }
        FlushEqual();

        var leftWords = a.Count(t => t.IsWord);
        var rightWords = b.Count(t => t.IsWord);
        var similarity = leftWords + rightWords == 0 ? 1.0 : 2.0 * unchangedWords / (leftWords + rightWords);
        return new TextDiff(segments, changes, new DiffCounts(insertedWords, deletedWords, unchangedWords, changes.Count, leftWords, rightWords, Math.Round(similarity, 4)), coarse);
    }

    private static string Preview(string text)
    {
        var t = text.Trim();
        return t.Length <= PreviewLength ? t : t[..PreviewLength].TrimEnd() + "…";
    }

    /// <summary>SHA-256 over the deleted and inserted words of a change, whitespace ignored: the identity a review comment is pinned to.</summary>
    internal static string ChangeDigest(List<Token> del, List<Token> ins)
    {
        var text = $"keel-redline-v{DiffVersion.ToString(CultureInfo.InvariantCulture)}\n{string.Join(' ', del.Select(t => t.Text))}\n{string.Join(' ', ins.Select(t => t.Text))}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
