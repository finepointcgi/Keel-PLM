using System.Text;

namespace Keel.Core;

/// <summary>
/// ASME Y14.35 revision letters (A7): A–Y without I, O, Q, S, X, Z; after Y comes AA, AB, … (bijective base 20).
/// </summary>
public static class RevisionScheme
{
    public const string Alphabet = "ABCDEFGHJKLMNPRTUVWY";
    public const string First = "A";

    public static bool IsValid(string? label) =>
        !string.IsNullOrEmpty(label) && label.Length <= 3 && label.All(c => Alphabet.Contains(c, StringComparison.Ordinal));

    public static string Next(string label)
    {
        if (!IsValid(label)) throw new ArgumentException($"'{label}' is not a valid ASME Y14.35 revision.", nameof(label));
        return FromIndex(ToIndex(label) + 1);
    }

    /// <summary>Ordinal position: A = 1, Y = 20, AA = 21. Used for ordering revisions.</summary>
    public static int ToIndex(string label)
    {
        var n = 0;
        foreach (var c in label) n = n * Alphabet.Length + Alphabet.IndexOf(c, StringComparison.Ordinal) + 1;
        return n;
    }

    public static string FromIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        var sb = new StringBuilder();
        while (index > 0)
        {
            index--;
            sb.Insert(0, Alphabet[index % Alphabet.Length]);
            index /= Alphabet.Length;
        }
        return sb.ToString();
    }
}
