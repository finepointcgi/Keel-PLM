using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Keel.Core.Variants;

/// <summary>
/// The usage condition of a BOM line (ADR-0039): a boolean expression over option choices such as <c>Engine = A AND (Payload = X OR Payload = Y)</c>.
/// It is parsed once into this tree. The tree and its canonical text are what is stored; nothing is ever evaluated as text, in code or in SQL (the database
/// function <c>var.eval_cond</c> walks the stored JSON form). Operands of AND and OR are sorted and de-duplicated, so two spellings of one condition have one
/// canonical text.
/// </summary>
public abstract record OptionExpr
{
    /// <summary>True when the expression holds for the selection (family key to value key). A family that is not selected never equals any value.</summary>
    public abstract bool Evaluate(IReadOnlyDictionary<string, string> selection);

    internal abstract int Depth { get; }
    internal abstract int NodeCount { get; }

    public sealed record Eq(string Family, string Value) : OptionExpr
    {
        public override bool Evaluate(IReadOnlyDictionary<string, string> selection) => selection.TryGetValue(Family, out var v) && string.Equals(v, Value, StringComparison.Ordinal);
        internal override int Depth => 1;
        internal override int NodeCount => 1;
    }

    public sealed record Not(OptionExpr Operand) : OptionExpr
    {
        public override bool Evaluate(IReadOnlyDictionary<string, string> selection) => !Operand.Evaluate(selection);
        internal override int Depth => 1 + Operand.Depth;
        internal override int NodeCount => 1 + Operand.NodeCount;
    }

    public sealed record And(ImmutableArray<OptionExpr> Operands) : OptionExpr
    {
        public override bool Evaluate(IReadOnlyDictionary<string, string> selection) => Operands.All(o => o.Evaluate(selection));
        internal override int Depth => 1 + Operands.Max(o => o.Depth);
        internal override int NodeCount => 1 + Operands.Sum(o => o.NodeCount);
    }

    public sealed record Or(ImmutableArray<OptionExpr> Operands) : OptionExpr
    {
        public override bool Evaluate(IReadOnlyDictionary<string, string> selection) => Operands.Any(o => o.Evaluate(selection));
        internal override int Depth => 1 + Operands.Max(o => o.Depth);
        internal override int NodeCount => 1 + Operands.Sum(o => o.NodeCount);
    }

    /// <summary>Every (family, value) pair the expression mentions.</summary>
    public IReadOnlySet<(string Family, string Value)> References()
    {
        var set = new HashSet<(string, string)>();
        Collect(this, set);
        return set;
    }

    private static void Collect(OptionExpr e, HashSet<(string, string)> into)
    {
        switch (e)
        {
            case Eq q: into.Add((q.Family, q.Value)); break;
            case Not n: Collect(n.Operand, into); break;
            case And a: foreach (var o in a.Operands) Collect(o, into); break;
            case Or o2: foreach (var o in o2.Operands) Collect(o, into); break;
        }
    }

    /// <summary>The canonical text: operators in capitals, single-family alternatives written as IN, operands in a fixed order, only the parentheses that are needed.</summary>
    public string ToCanonical() => OptionExpressions.Print(this);

    /// <summary>The stored tree form read by <c>var.eval_cond</c>.</summary>
    public JsonNode ToJson() => this switch
    {
        Eq q => new JsonObject { ["op"] = "eq", ["family"] = q.Family, ["value"] = q.Value },
        Not n => new JsonObject { ["op"] = "not", ["arg"] = n.Operand.ToJson() },
        And a => new JsonObject { ["op"] = "and", ["args"] = new JsonArray([.. a.Operands.Select(o => o.ToJson())]) },
        Or o => new JsonObject { ["op"] = "or", ["args"] = new JsonArray([.. o.Operands.Select(x => x.ToJson())]) },
        _ => throw new InvalidOperationException("Unknown expression node."),
    };

    /// <summary>Reads the stored tree form back. Returns null for anything that is not a well-formed, in-limits tree (never throws on bad data).</summary>
    public static OptionExpr? FromJson(JsonNode? node)
    {
        OptionExpr? e;
        try { e = Read(node, 0); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
        return e is null || e.Depth > OptionExpressions.MaxDepth + 2 || e.NodeCount > OptionExpressions.MaxNodes ? null : OptionExpressions.Normalize(e);
    }

    private static OptionExpr? Read(JsonNode? node, int depth)
    {
        if (node is not JsonObject o || depth > OptionExpressions.MaxDepth + 2) return null;
        switch (o["op"]?.GetValue<string>())
        {
            case "eq":
                var f = o["family"]?.GetValue<string>();
                var v = o["value"]?.GetValue<string>();
                return f is not null && v is not null && OptionExpressions.IsKey(f) && OptionExpressions.IsKey(v) ? new Eq(f, v) : null;
            case "not":
                return Read(o["arg"], depth + 1) is { } inner ? new Not(inner) : null;
            case "and" or "or":
                if (o["args"] is not JsonArray arr || arr.Count == 0) return null;
                var list = new List<OptionExpr>();
                foreach (var a in arr)
                {
                    if (Read(a, depth + 1) is not { } x) return null;
                    list.Add(x);
                }
                return o["op"]!.GetValue<string>() == "and" ? new And([.. list]) : new Or([.. list]);
            default: return null;
        }
    }
}

public sealed record OptionParseError(int Position, string Message);

public sealed record OptionParseResult(OptionExpr? Expression, IReadOnlyList<OptionParseError> Errors)
{
    public bool IsOk => Expression is not null;
    public string CanonicalText => Expression!.ToCanonical();
}

/// <summary>Parser, normalizer, printer and plain-language description of <see cref="OptionExpr"/>.</summary>
public static class OptionExpressions
{
    public const int MaxTextLength = 500;
    public const int MaxDepth = 8;
    public const int MaxNodes = 64;

    private static readonly string[] Reserved = ["and", "or", "not", "in"];

    /// <summary>A family or value key: a letter or digit first, then letters, digits, underscore, dot or hyphen; at most 40 characters; not a keyword.</summary>
    public static bool IsKey(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 40 || !char.IsAsciiLetterOrDigit(s[0])) return false;
        foreach (var c in s) if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')) return false;
        return !Reserved.Contains(s, StringComparer.OrdinalIgnoreCase);
    }

    public static string KeyRule => "Keys start with a letter or digit and use letters, digits, '_', '.' and '-' (at most 40 characters). The words and, or, not and in are reserved.";

    // ---------------------------------------------------------------- parsing

    private enum T { Word, LParen, RParen, Comma, Eq, Ne, End }

    private readonly record struct Token(T Kind, string Text, int Pos);

    public static OptionParseResult Parse(string? text)
    {
        text ??= "";
        if (string.IsNullOrWhiteSpace(text)) return Fail(0, "The condition is empty.");
        if (text.Length > MaxTextLength) return Fail(MaxTextLength, $"The condition is longer than {MaxTextLength} characters.");
        var tokens = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            switch (c)
            {
                case '(': tokens.Add(new(T.LParen, "(", i++)); continue;
                case ')': tokens.Add(new(T.RParen, ")", i++)); continue;
                case ',': tokens.Add(new(T.Comma, ",", i++)); continue;
                case '=': tokens.Add(new(T.Eq, "=", i)); i += i + 1 < text.Length && text[i + 1] == '=' ? 2 : 1; continue;
                case '!' when i + 1 < text.Length && text[i + 1] == '=': tokens.Add(new(T.Ne, "!=", i)); i += 2; continue;
                case '!': tokens.Add(new(T.Word, "not", i++)); continue;
                case '&' when i + 1 < text.Length && text[i + 1] == '&': tokens.Add(new(T.Word, "and", i)); i += 2; continue;
                case '|' when i + 1 < text.Length && text[i + 1] == '|': tokens.Add(new(T.Word, "or", i)); i += 2; continue;
            }
            if (char.IsAsciiLetterOrDigit(c))
            {
                var start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.' or '-')) i++;
                tokens.Add(new(T.Word, text[start..i], start));
                continue;
            }
            return Fail(i, $"The character '{c}' is not allowed in a condition.");
        }
        tokens.Add(new(T.End, "", text.Length));
        var p = new Parser(tokens);
        var expr = p.ParseOr();
        if (p.Error is not null) return new(null, [p.Error]);
        if (p.Peek.Kind != T.End) return Fail(p.Peek.Pos, p.Peek.Kind == T.RParen ? "A closing parenthesis ')' has no opening one." : $"Unexpected '{p.Peek.Text}'. Join terms with AND or OR.");
        var normalized = Normalize(expr!);
        if (normalized.NodeCount > MaxNodes) return Fail(0, $"The condition has more than {MaxNodes} parts; simplify it.");
        if (normalized.Depth > MaxDepth) return Fail(0, $"The condition is nested more than {MaxDepth} levels deep; simplify it.");
        return new(normalized, []);
    }

    private static OptionParseResult Fail(int pos, string message) => new(null, [new OptionParseError(pos, message)]);

    private sealed class Parser(List<Token> tokens)
    {
        private int _i;
        private int _parens;
        public OptionParseError? Error { get; private set; }
        public Token Peek => tokens[_i];

        private bool IsWord(string w) => Peek.Kind == T.Word && string.Equals(Peek.Text, w, StringComparison.OrdinalIgnoreCase);

        private OptionExpr? Err(Token at, string message)
        {
            Error ??= new OptionParseError(at.Pos, message);
            return null;
        }

        public OptionExpr? ParseOr()
        {
            var first = ParseAnd();
            if (first is null) return null;
            var list = new List<OptionExpr> { first };
            while (IsWord("or"))
            {
                _i++;
                var next = ParseAnd();
                if (next is null) return null;
                list.Add(next);
            }
            return list.Count == 1 ? first : new OptionExpr.Or([.. list]);
        }

        private OptionExpr? ParseAnd()
        {
            var first = ParseUnary();
            if (first is null) return null;
            var list = new List<OptionExpr> { first };
            while (IsWord("and"))
            {
                _i++;
                var next = ParseUnary();
                if (next is null) return null;
                list.Add(next);
            }
            return list.Count == 1 ? first : new OptionExpr.And([.. list]);
        }

        private OptionExpr? ParseUnary()
        {
            if (IsWord("not"))
            {
                _i++;
                var inner = ParseUnary();
                return inner is null ? null : new OptionExpr.Not(inner);
            }
            return ParsePrimary();
        }

        private OptionExpr? ParsePrimary()
        {
            var t = Peek;
            if (t.Kind == T.LParen)
            {
                if (++_parens > 40) return Err(t, "The condition is nested too deeply.");
                _i++;
                var inner = ParseOr();
                if (inner is null) return null;
                if (Peek.Kind != T.RParen) return Err(Peek, "A closing parenthesis ')' is missing.");
                _i++;
                _parens--;
                return inner;
            }
            if (t.Kind != T.Word) return Err(t, t.Kind == T.End ? "The condition ends too early: name an option such as Engine = A." : $"Expected an option name but found '{t.Text}'.");
            if (!IsKey(t.Text)) return Err(t, $"'{t.Text}' is a reserved word or not a valid option key. {KeyRule}");
            _i++;
            var family = t.Text;
            var negate = false;
            if (IsWord("not") && tokens[_i + 1].Kind == T.Word && string.Equals(tokens[_i + 1].Text, "in", StringComparison.OrdinalIgnoreCase)) { negate = true; _i++; }
            if (IsWord("in"))
            {
                _i++;
                if (Peek.Kind != T.LParen) return Err(Peek, "IN needs a list in parentheses, for example Engine IN (A, B).");
                _i++;
                var values = new List<OptionExpr>();
                while (true)
                {
                    var v = Peek;
                    if (v.Kind != T.Word || !IsKey(v.Text)) return Err(v, "Expected an option value in the list.");
                    _i++;
                    values.Add(new OptionExpr.Eq(family, v.Text));
                    if (Peek.Kind == T.Comma) { _i++; continue; }
                    break;
                }
                if (Peek.Kind != T.RParen) return Err(Peek, "A closing parenthesis ')' is missing after the list.");
                _i++;
                OptionExpr any = values.Count == 1 ? values[0] : new OptionExpr.Or([.. values]);
                return negate ? new OptionExpr.Not(any) : any;
            }
            if (negate) return Err(Peek, "NOT must be followed by IN here, for example Engine NOT IN (A, B).");
            var op = Peek;
            if (op.Kind is not (T.Eq or T.Ne)) return Err(op, $"Expected '=' or '!=' or IN after '{family}'.");
            _i++;
            var val = Peek;
            if (val.Kind != T.Word || !IsKey(val.Text)) return Err(val, $"Expected an option value after '{family} {op.Text}'.");
            _i++;
            OptionExpr eq = new OptionExpr.Eq(family, val.Text);
            return op.Kind == T.Ne ? new OptionExpr.Not(eq) : eq;
        }
    }

    // ---------------------------------------------------------------- normal form

    /// <summary>Flattens nested AND/OR, removes double negation, sorts and de-duplicates operands, collapses one-operand groups.</summary>
    public static OptionExpr Normalize(OptionExpr e)
    {
        switch (e)
        {
            case OptionExpr.Eq: return e;
            case OptionExpr.Not n:
                var inner = Normalize(n.Operand);
                return inner is OptionExpr.Not nn ? nn.Operand : new OptionExpr.Not(inner);
            case OptionExpr.And a: return Group(a.Operands, isAnd: true);
            case OptionExpr.Or o: return Group(o.Operands, isAnd: false);
            default: throw new InvalidOperationException("Unknown expression node.");
        }
    }

    private static OptionExpr Group(ImmutableArray<OptionExpr> operands, bool isAnd)
    {
        var flat = new List<OptionExpr>();
        foreach (var raw in operands)
        {
            var x = Normalize(raw);
            if (isAnd && x is OptionExpr.And xa) flat.AddRange(xa.Operands);
            else if (!isAnd && x is OptionExpr.Or xo) flat.AddRange(xo.Operands);
            else flat.Add(x);
        }
        var distinct = flat.Select(x => (Text: Print(x), X: x)).GroupBy(t => t.Text, StringComparer.Ordinal).Select(g => g.First()).OrderBy(t => t.Text, StringComparer.Ordinal).Select(t => t.X).ToList();
        if (distinct.Count == 1) return distinct[0];
        return isAnd ? new OptionExpr.And([.. distinct]) : new OptionExpr.Or([.. distinct]);
    }

    // ---------------------------------------------------------------- printing

    public static string Print(OptionExpr e) => PrintAt(e, 0);

    // Context: 0 = top level, 1 = inside AND, 2 = inside OR, 3 = inside NOT. A group inside an OR or a NOT is always parenthesized, for the reader.
    private static string PrintAt(OptionExpr e, int ctx)
    {
        switch (e)
        {
            case OptionExpr.Eq q: return $"{q.Family} = {q.Value}";
            case OptionExpr.Not { Operand: OptionExpr.Eq q }: return $"{q.Family} != {q.Value}";
            case OptionExpr.Not n when SameFamilyAlternatives(n.Operand) is { } alt: return $"{alt.Family} NOT IN ({string.Join(", ", alt.Values)})";
            case OptionExpr.Not n: return "NOT " + PrintAt(n.Operand, 3);
            case OptionExpr.Or o when SameFamilyAlternatives(o) is { } alt: return $"{alt.Family} IN ({string.Join(", ", alt.Values)})";
            case OptionExpr.Or o:
            {
                var s = string.Join(" OR ", o.Operands.Select(x => PrintAt(x, 2)));
                return ctx >= 1 ? "(" + s + ")" : s;
            }
            case OptionExpr.And a:
            {
                var s = string.Join(" AND ", a.Operands.Select(x => PrintAt(x, 1)));
                return ctx >= 2 ? "(" + s + ")" : s;
            }
            default: throw new InvalidOperationException("Unknown expression node.");
        }
    }

    private static (string Family, List<string> Values)? SameFamilyAlternatives(OptionExpr e)
    {
        if (e is not OptionExpr.Or o || o.Operands.Length < 2) return null;
        string? family = null;
        var values = new List<string>();
        foreach (var x in o.Operands)
        {
            if (x is not OptionExpr.Eq q || (family is not null && q.Family != family)) return null;
            family = q.Family;
            values.Add(q.Value);
        }
        return (family!, values);
    }

    // ---------------------------------------------------------------- plain language

    /// <summary>A sentence for the condition editor. <paramref name="familyName"/> and <paramref name="valueName"/> turn keys into display names; keys are shown when they return null.</summary>
    public static string Describe(OptionExpr e, Func<string, string?>? familyName = null, Func<string, string, string?>? valueName = null)
    {
        string F(string k) => familyName?.Invoke(k) ?? k;
        string V(string f, string k) => valueName?.Invoke(f, k) ?? k;

        string Go(OptionExpr x, int ctx)
        {
            switch (x)
            {
                case OptionExpr.Eq q: return $"{F(q.Family)} is {V(q.Family, q.Value)}";
                case OptionExpr.Not { Operand: OptionExpr.Eq q }: return $"{F(q.Family)} is not {V(q.Family, q.Value)}";
                case OptionExpr.Not n when SameFamilyAlternatives(n.Operand) is { } alt:
                    return $"{F(alt.Family)} is neither {JoinNames([.. alt.Values.Select(v => V(alt.Family, v))], "nor")}";
                case OptionExpr.Not n: return "it is not true that (" + Go(n.Operand, 0) + ")";
                case OptionExpr.Or o when SameFamilyAlternatives(o) is { } alt:
                    return $"{F(alt.Family)} is {JoinNames([.. alt.Values.Select(v => V(alt.Family, v))], "or")}";
                case OptionExpr.Or o:
                {
                    var s = string.Join(" or ", o.Operands.Select(y => Go(y, 2)));
                    return ctx >= 1 ? "(" + s + ")" : s;
                }
                case OptionExpr.And a:
                {
                    var s = string.Join(" and ", a.Operands.Select(y => Go(y, 1)));
                    return ctx >= 2 ? "(" + s + ")" : s;
                }
                default: throw new InvalidOperationException("Unknown expression node.");
            }
        }
        return "Used when " + Go(e, 0) + ".";
    }

    private static string JoinNames(List<string> names, string last) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " " + last + " " + names[^1];

    // ---------------------------------------------------------------- selection helpers

    /// <summary>The JSON object <c>var.eval_cond</c> receives: family key to value key, keys sorted.</summary>
    public static string SelectionJson(IReadOnlyDictionary<string, string> selection) =>
        new JsonObject(selection.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => KeyValuePair.Create<string, JsonNode?>(k.Key, k.Value))).ToJsonString();

    public static string DescribeSelection(IReadOnlyDictionary<string, string> selection) =>
        selection.Count == 0 ? "no options" : string.Join(", ", selection.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} = {k.Value}"));
}

/// <summary>The options chosen for one resolved configuration (a named variant, or an ad-hoc choice): family key to value key.</summary>
public sealed record VariantSelection(string Label, IReadOnlyDictionary<string, string> Options)
{
    public string Json => OptionExpressions.SelectionJson(Options);
}
