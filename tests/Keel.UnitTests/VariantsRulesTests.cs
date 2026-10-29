using Keel.Core.Variants;
using Keel.Variants;

namespace Keel.UnitTests;

/// <summary>The condition language (parse, canonical text, tree round trip, evaluation, plain language) and the option rule engine (ADR-0039).</summary>
public sealed class VariantsRulesTests
{
    private static OptionExpr Parse(string text)
    {
        var r = OptionExpressions.Parse(text);
        Assert.True(r.IsOk, string.Join("; ", r.Errors.Select(e => e.Message)));
        return r.Expression!;
    }

    private static Dictionary<string, string> Sel(params (string F, string V)[] c) => c.ToDictionary(x => x.F, x => x.V, StringComparer.Ordinal);

    [Theory]
    [InlineData("Engine = A", "Engine = A")]
    [InlineData("engine==A", "engine = A")]
    [InlineData("Engine != A", "Engine != A")]
    [InlineData("NOT Engine = A", "Engine != A")]
    [InlineData("Engine IN (B, A)", "Engine IN (A, B)")]
    [InlineData("Engine = B or Engine = A", "Engine IN (A, B)")]
    [InlineData("Engine NOT IN (B, A)", "Engine NOT IN (A, B)")]
    [InlineData("Payload = X AND Engine = A", "Engine = A AND Payload = X")]
    [InlineData("Engine = A && (Payload = X || Payload = Y)", "Engine = A AND Payload IN (X, Y)")]
    [InlineData("(Engine = A AND Payload = X) OR Engine = B", "(Engine = A AND Payload = X) OR Engine = B")]
    [InlineData("Engine = A AND Engine = A", "Engine = A")]
    [InlineData("NOT NOT Engine = A", "Engine = A")]
    [InlineData("NOT (Engine = A AND Payload = X)", "NOT (Engine = A AND Payload = X)")]
    [InlineData("((Engine = A))", "Engine = A")]
    [InlineData("Engine = A AND (Payload = X AND Color = red)", "Color = red AND Engine = A AND Payload = X")]
    public void Canonical_text_is_one_spelling_per_meaning(string input, string canonical)
    {
        var e = Parse(input);
        Assert.Equal(canonical, e.ToCanonical());
        Assert.Equal(canonical, Parse(canonical).ToCanonical()); // idempotent
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Engine")]
    [InlineData("Engine =")]
    [InlineData("= A")]
    [InlineData("Engine = A AND")]
    [InlineData("Engine = A Payload = X")]
    [InlineData("(Engine = A")]
    [InlineData("Engine = A)")]
    [InlineData("Engine IN A")]
    [InlineData("Engine IN ()")]
    [InlineData("Engine IN (A,)")]
    [InlineData("and = A")]
    [InlineData("Engine = or")]
    [InlineData("Engine = 'A'")]
    [InlineData("Engine = A; DROP TABLE x")]
    [InlineData("Engine = A -- comment")]
    [InlineData("Eng$ine = A")]
    [InlineData("Engine NOT = A")]
    public void Malformed_conditions_are_refused_with_a_position_and_a_message(string input)
    {
        var r = OptionExpressions.Parse(input);
        Assert.False(r.IsOk);
        var error = Assert.Single(r.Errors);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.InRange(error.Position, 0, input.Length + 1);
    }

    [Fact]
    public void Limits_on_length_nodes_and_depth_hold()
    {
        Assert.False(OptionExpressions.Parse(new string('a', 501)).IsOk);
        var many = string.Join(" AND ", Enumerable.Range(0, 70).Select(i => $"F{i} = V"));
        Assert.False(OptionExpressions.Parse(many).IsOk);
        var deep = "(A=1 OR (B=1 AND (C=1 OR (D=1 AND (E=1 OR (F=1 AND (G=1 OR (H=1 AND (I=1 OR (J=1 AND K=1))))))))))";
        Assert.False(OptionExpressions.Parse(deep).IsOk);
        var ok = string.Join(" AND ", Enumerable.Range(0, 20).Select(i => $"F{i} = V"));
        Assert.True(OptionExpressions.Parse(ok).IsOk);
    }

    [Fact]
    public void Evaluation_treats_an_unselected_option_as_never_equal()
    {
        var e = Parse("Engine = A AND (Payload = X OR Payload = Y)");
        Assert.True(e.Evaluate(Sel(("Engine", "A"), ("Payload", "Y"))));
        Assert.False(e.Evaluate(Sel(("Engine", "A"), ("Payload", "Z"))));
        Assert.False(e.Evaluate(Sel(("Engine", "A"))));
        Assert.True(Parse("Engine != A").Evaluate(Sel()));          // not selected is not A
        Assert.False(Parse("Engine = A").Evaluate(Sel()));
        Assert.True(Parse("Engine NOT IN (A, B)").Evaluate(Sel(("Engine", "C"))));
        Assert.False(Parse("Engine = a").Evaluate(Sel(("Engine", "A")))); // values compare exactly
    }

    [Fact]
    public void The_stored_tree_round_trips_and_bad_trees_are_refused_not_thrown()
    {
        foreach (var text in new[] { "Engine = A", "NOT (A = 1 AND B = 2)", "A IN (1, 2) OR (B = 3 AND NOT C = 4)" })
        {
            var e = Parse(text);
            var back = OptionExpr.FromJson(e.ToJson());
            Assert.Equal(e.ToCanonical(), back!.ToCanonical());
        }
        Assert.Null(OptionExpr.FromJson(null));
        Assert.Null(OptionExpr.FromJson(System.Text.Json.Nodes.JsonNode.Parse("""{"op":"eval","family":"a"}""")));
        Assert.Null(OptionExpr.FromJson(System.Text.Json.Nodes.JsonNode.Parse("""{"op":"and","args":[]}""")));
        Assert.Null(OptionExpr.FromJson(System.Text.Json.Nodes.JsonNode.Parse("""{"op":"eq","family":"a b","value":"x"}""")));
        Assert.Null(OptionExpr.FromJson(System.Text.Json.Nodes.JsonNode.Parse("""{"op":"eq","family":1,"value":"x"}""")));
    }

    [Fact]
    public void Plain_language_uses_display_names_and_reads_as_a_sentence()
    {
        string? F(string k) => k == "Engine" ? "Engine" : null;
        string? V(string f, string k) => (f, k) switch { ("Engine", "A") => "Turbo", ("Engine", "B") => "Electric", _ => null };
        Assert.Equal("Used when Engine is Turbo.", OptionExpressions.Describe(Parse("Engine = A"), F, V));
        Assert.Equal("Used when Engine is Turbo or Electric.", OptionExpressions.Describe(Parse("Engine IN (A, B)"), F, V));
        Assert.Equal("Used when Engine is neither Turbo nor Electric.", OptionExpressions.Describe(Parse("Engine NOT IN (A, B)"), F, V));
        Assert.Equal("Used when Engine is Turbo and Payload is X.", OptionExpressions.Describe(Parse("Payload = X AND Engine = A"), F, V));
        Assert.Equal("Used when (Engine is Turbo and Payload is X) or Engine is Electric.", OptionExpressions.Describe(Parse("(Engine = A AND Payload = X) OR Engine = B"), F, V));
        Assert.Equal("Used when Engine is not Turbo.", OptionExpressions.Describe(Parse("Engine != A"), F, V));
    }

    [Fact]
    public void Keys_follow_the_documented_rule()
    {
        Assert.True(OptionExpressions.IsKey("Engine"));
        Assert.True(OptionExpressions.IsKey("9A610.x-1_b"));
        Assert.False(OptionExpressions.IsKey(""));
        Assert.False(OptionExpressions.IsKey("-A"));
        Assert.False(OptionExpressions.IsKey("has space"));
        Assert.False(OptionExpressions.IsKey("OR"));
        Assert.False(OptionExpressions.IsKey("not"));
        Assert.False(OptionExpressions.IsKey(new string('a', 41)));
    }

    [Fact]
    public void Random_expressions_agree_between_the_canonical_text_and_the_tree()
    {
        // Property check: for generated expressions, parse(print(e)) evaluates exactly like e for every selection of a small space.
        var rng = new Random(20261029);
        var families = new[] { "A", "B", "C" };
        var values = new[] { "1", "2", "3" };
        OptionExpr Gen(int depth)
        {
            if (depth == 0 || rng.Next(4) == 0) return new OptionExpr.Eq(families[rng.Next(3)], values[rng.Next(3)]);
            return rng.Next(3) switch
            {
                0 => new OptionExpr.Not(Gen(depth - 1)),
                1 => new OptionExpr.And([Gen(depth - 1), Gen(depth - 1), .. (rng.Next(2) == 0 ? new[] { Gen(depth - 1) } : [])]),
                _ => new OptionExpr.Or([Gen(depth - 1), Gen(depth - 1)]),
            };
        }
        var selections = new List<Dictionary<string, string>>();
        foreach (var a in values.Append(null)) foreach (var b in values.Append(null)) foreach (var c in values.Append(null))
        {
            var d = new Dictionary<string, string>();
            if (a is not null) d["A"] = a;
            if (b is not null) d["B"] = b;
            if (c is not null) d["C"] = c;
            selections.Add(d);
        }
        for (var i = 0; i < 400; i++)
        {
            var e = Gen(4);
            var text = e.ToCanonical();
            var parsed = OptionExpressions.Parse(text);
            Assert.True(parsed.IsOk, text);
            var viaJson = OptionExpr.FromJson(e.ToJson())!;
            foreach (var s in selections)
            {
                var expected = e.Evaluate(s);
                Assert.Equal(expected, parsed.Expression!.Evaluate(s));
                Assert.Equal(expected, viaJson.Evaluate(s));
            }
        }
    }

    // ---------------------------------------------------------------- rule engine

    private static OptionFamilyDef Fam(string key, bool required, params string[] values) =>
        new(key, key, required, false, [.. values.Select(v => new OptionValueDef(v, v))]);

    private static OptionRuleDef Rule(RuleKind k, OptionRef? subject, params OptionRef[] members) => new(Guid.NewGuid(), k, subject, members, null);

    private static readonly OptionModelDef Base = new(
        [Fam("Engine", true, "A", "B"), Fam("Payload", true, "X", "Y")], []);

    [Fact]
    public void Without_rules_every_combination_is_valid()
    {
        var space = RuleEngine.Enumerate(Base);
        Assert.Equal(4, space.ValidCount);
        Assert.True(space.Complete);
        Assert.Equal(4, space.Sample.Count);
        Assert.Equal(4, RuleEngine.RawSpace(Base));
        Assert.True(RuleEngine.Check(Base, Sel(("Engine", "A"), ("Payload", "X"))).Valid);
    }

    [Fact]
    public void Requires_excludes_and_mutex_decide_validity()
    {
        var m = Base with
        {
            Families = [.. Base.Families, Fam("Cooling", false, "Air", "Liquid")],
            Rules =
            [
                Rule(RuleKind.Requires, new("Engine", "B"), new OptionRef("Cooling", "Liquid")),
                Rule(RuleKind.Excludes, new("Payload", "Y"), new OptionRef("Cooling", "Air")),
            ],
        };
        Assert.False(RuleEngine.Check(m, Sel(("Engine", "B"), ("Payload", "X"))).Valid);                                    // needs liquid cooling
        Assert.True(RuleEngine.Check(m, Sel(("Engine", "B"), ("Payload", "X"), ("Cooling", "Liquid"))).Valid);
        Assert.False(RuleEngine.Check(m, Sel(("Engine", "A"), ("Payload", "Y"), ("Cooling", "Air"))).Valid);                // excluded
        Assert.True(RuleEngine.Check(m, Sel(("Engine", "A"), ("Payload", "Y"))).Valid);                                     // optional family left out
        Assert.False(RuleEngine.Check(m, Sel(("Engine", "A"))).Valid);                                                      // required Payload missing

        var mutex = Base with { Rules = [Rule(RuleKind.Mutex, null, new OptionRef("Engine", "B"), new OptionRef("Payload", "Y"))] };
        Assert.False(RuleEngine.Check(mutex, Sel(("Engine", "B"), ("Payload", "Y"))).Valid);
        Assert.True(RuleEngine.Check(mutex, Sel(("Engine", "B"), ("Payload", "X"))).Valid);
        Assert.Equal(3, RuleEngine.Enumerate(mutex).ValidCount);
    }

    [Fact]
    public void Enumeration_agrees_with_brute_force_checking()
    {
        var m = new OptionModelDef(
            [Fam("Engine", true, "A", "B", "C"), Fam("Payload", true, "X", "Y"), Fam("Cooling", false, "Air", "Liquid"), Fam("Color", false, "red", "blue")],
            [
                Rule(RuleKind.Requires, new("Engine", "C"), new OptionRef("Payload", "Y"), new OptionRef("Cooling", "Liquid")),
                Rule(RuleKind.Excludes, new("Color", "red"), new OptionRef("Engine", "A")),
                Rule(RuleKind.Mutex, null, new OptionRef("Cooling", "Air"), new OptionRef("Payload", "Y"), new OptionRef("Color", "blue")),
            ]);
        var space = RuleEngine.Enumerate(m);
        var brute = 0;
        foreach (var e in new[] { "A", "B", "C" }) foreach (var p in new[] { "X", "Y" }) foreach (var c in new string?[] { null, "Air", "Liquid" }) foreach (var k in new string?[] { null, "red", "blue" })
        {
            var s = Sel(("Engine", e), ("Payload", p));
            if (c is not null) s["Cooling"] = c;
            if (k is not null) s["Color"] = k;
            if (RuleEngine.Check(m, s).Valid) brute++;
        }
        Assert.Equal(brute, space.ValidCount);
        Assert.All(space.Sample, s => Assert.True(RuleEngine.Check(m, s).Valid));
        Assert.Equal(space.ValidCount, space.Sample.Count);
    }

    [Fact]
    public void Model_validation_finds_contradictions_dead_options_and_empty_spaces()
    {
        // an option no valid configuration can use
        var dead = Base with { Rules = [Rule(RuleKind.Excludes, new("Engine", "B"), new OptionRef("Payload", "X"), new OptionRef("Payload", "Y"))] };
        var f1 = RuleEngine.ValidateModel(dead);
        Assert.Contains(f1, f => f.Code == "option.unusable" && f.Message.Contains("Engine = B", StringComparison.Ordinal));

        // requires and excludes the same option
        var clash = Base with { Rules = [Rule(RuleKind.Requires, new("Engine", "A"), new OptionRef("Payload", "X")), Rule(RuleKind.Excludes, new("Engine", "A"), new OptionRef("Payload", "X"))] };
        Assert.Contains(RuleEngine.ValidateModel(clash), f => f.Code == "rule.contradiction" && f.Severity == FindingSeverity.Error);

        // nothing valid at all
        var empty = Base with
        {
            Rules =
            [
                Rule(RuleKind.Excludes, new("Engine", "A"), new OptionRef("Payload", "X"), new OptionRef("Payload", "Y")),
                Rule(RuleKind.Excludes, new("Engine", "B"), new OptionRef("Payload", "X"), new OptionRef("Payload", "Y")),
            ],
        };
        Assert.Contains(RuleEngine.ValidateModel(empty), f => f.Code == "model.no_valid_configuration");

        // same family needs another value, unknown option, self rule, duplicate rule
        var weird = Base with
        {
            Rules =
            [
                Rule(RuleKind.Requires, new("Engine", "A"), new OptionRef("Engine", "B")),
                Rule(RuleKind.Requires, new("Engine", "A"), new OptionRef("Ghost", "1")),
                Rule(RuleKind.Excludes, new("Payload", "X"), new OptionRef("Payload", "X")),
                Rule(RuleKind.Excludes, new("Payload", "Y"), new OptionRef("Engine", "A")),
                Rule(RuleKind.Excludes, new("Payload", "Y"), new OptionRef("Engine", "A")),
            ],
        };
        var codes = RuleEngine.ValidateModel(weird).Select(f => f.Code).ToHashSet();
        Assert.Contains("rule.same_family", codes);
        Assert.Contains("rule.unknown_option", codes);
        Assert.Contains("rule.self", codes);
        Assert.Contains("rule.duplicate", codes);

        // a consistent model is clean
        Assert.Empty(RuleEngine.ValidateModel(Base));
    }

    [Fact]
    public void Retired_and_unknown_choices_are_reported_in_plain_words()
    {
        var m = new OptionModelDef([new OptionFamilyDef("Engine", "Engine", true, false, [new("A", "Turbo"), new("B", "Electric", true)])], []);
        Assert.Contains("retired", string.Join(" ", RuleEngine.Check(m, Sel(("Engine", "B"))).Problems), StringComparison.Ordinal);
        Assert.Contains("does not exist", string.Join(" ", RuleEngine.Check(m, Sel(("Ghost", "B"))).Problems), StringComparison.Ordinal);
        Assert.Contains("not a value", string.Join(" ", RuleEngine.Check(m, Sel(("Engine", "Z"))).Problems), StringComparison.Ordinal);
        Assert.Equal(1, RuleEngine.Enumerate(m).ValidCount); // retired values are not offered
    }

    [Fact]
    public void A_large_model_stops_at_the_work_limit_and_says_so()
    {
        var fams = Enumerable.Range(0, 12).Select(i => Fam("F" + i, true, "1", "2", "3", "4")).ToList();
        var big = new OptionModelDef(fams, []);
        var space = RuleEngine.Enumerate(big, maxNodes: 10_000);
        Assert.False(space.Complete);
        Assert.Contains(RuleEngine.ValidateModel(big), f => f.Code == "model.analysis_limited");
        Assert.Equal(16_777_216L, RuleEngine.RawSpace(big));
    }

    [Fact]
    public void Rule_text_names_the_options()
    {
        var r = Rule(RuleKind.Requires, new("Engine", "B"), new OptionRef("Cooling", "Liquid"));
        Assert.Equal("Choosing Engine is B needs Cooling is Liquid.", RuleEngine.Describe(r));
        Assert.Equal("At most one of these can be chosen: Engine is A or Payload is X.", RuleEngine.Describe(Rule(RuleKind.Mutex, null, new OptionRef("Engine", "A"), new OptionRef("Payload", "X"))));
    }
}
