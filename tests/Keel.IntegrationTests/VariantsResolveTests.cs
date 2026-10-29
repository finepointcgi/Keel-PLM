using System.Text.Json;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Core.Variants;
using Keel.Variants;
using Npgsql;
using static Keel.IntegrationTests.VarEnv;

namespace Keel.IntegrationTests;

/// <summary>Usage conditions on lines and resolving a 150% structure to a variant (ADR-0039), through the normal read paths.</summary>
[Collection(PostgresCollection.Name)]
public sealed class VariantsResolveTests(PostgresFixture pg)
{
    private static IEnumerable<VariantSelection> AllSelections()
    {
        foreach (var e in new string?[] { "A", "B", null })
            foreach (var p in new string?[] { "X", "Y", null })
            {
                var d = new Dictionary<string, string>();
                if (e is not null) d["Engine"] = e;
                if (p is not null) d["Payload"] = p;
                yield return new VariantSelection($"{e ?? "-"}/{p ?? "-"}", d);
            }
    }

    [Fact]
    public async Task Conditions_are_validated_stored_as_canonical_text_and_carried_through_check_in()
    {
        var env = await VarEnv.CreateAsync(pg);
        var s = await env.RocketAsync();
        var full = await env.ExplodeAsync(s.Rocket);
        var byFind = full.Lines.Where(l => l.Level == 1).ToDictionary(l => l.FindNumber);
        Assert.Equal("Engine = A", byFind[10].Condition);
        Assert.Equal("Payload = Y", byFind[70].Condition);
        Assert.Null(byFind[50].Condition);                                         // no condition: used everywhere
        Assert.Equal("Engine = A", full.Lines.Single(l => l.Path == "70.20").Condition); // a condition on a line of the sub-assembly
        Assert.Equal(9, full.Lines.Count);                                          // no variant: the whole 150% structure

        // a condition is parsed, normalized and refused when wrong; nothing is evaluated as text
        await env.C.CheckOutAsync(s.Rocket);
        var line = await env.CondAsync(s.Rocket, 50, "  payload=Y  &&  ( Engine = B or Engine = A ) ");
        Assert.Equal("Engine IN (A, B) AND payload = Y", line.Condition);
        foreach (var bad in new[] { "Engine =", "Engine = A; DROP TABLE plm.part_master", "= 1", "Engine = A OR", "' OR ''='", "Engine IN ()", new string('x', 600)})
        {
            var r = await env.SetConditionAsync(s.Rocket, 50, bad);
            Assert.Equal(OutcomeKind.Invalid, r.Kind);
            Assert.Contains("Condition", string.Join(" ", r.Errors), StringComparison.Ordinal);
        }
        // validated against the product's options: unknown option and unknown value are refused with a plain message
        var unknownFamily = await env.SetConditionAsync(s.Rocket, 50, "Wings = Long", s.Rocket);
        Assert.Equal(OutcomeKind.Invalid, unknownFamily.Kind);
        Assert.Contains("does not exist", string.Join(" ", unknownFamily.Errors), StringComparison.Ordinal);
        var unknownValue = await env.SetConditionAsync(s.Rocket, 50, "Engine = Z", s.Rocket);
        Assert.Contains("not a value", string.Join(" ", unknownValue.Errors), StringComparison.Ordinal);
        Assert.Equal("Engine IN (A, B) AND payload = Y", (await env.Bom.GetWorkingAsync(Alice, s.Rocket, null)).Value!.Lines.Single(l => l.FindNumber == 50).Condition); // the refused ones changed nothing

        // clearing, and a line that is not there
        Assert.Null((await env.CondAsync(s.Rocket, 50, null)).Condition);
        Assert.Null((await env.CondAsync(s.Rocket, 50, "  ")).Condition);
        Assert.Equal(OutcomeKind.NotFound, (await env.Bom.SetLineConditionAsync(Alice, s.Rocket, Guid.NewGuid(), "Engine = A", null, null)).Kind);

        // only the check-out owner can edit
        var other = await env.Bom.SetLineConditionAsync(WorkflowEnv.Rita, s.Rocket, (await env.Bom.GetWorkingAsync(Alice, s.Rocket, null)).Value!.Lines[0].Id, "Engine = A", null, null);
        Assert.NotEqual(OutcomeKind.Ok, other.Kind);
        await env.C.CheckInAsync(s.Rocket);

        // after check-in the stored iteration holds the same conditions, and the next check-out starts from them
        var after = await env.ExplodeAsync(s.Rocket);
        Assert.Equal(["Engine = A", "Engine = B", "Payload = X", "Payload = Y", null, "Engine = A", "Payload = Y"], after.Lines.Where(l => l.Level == 1).Select(l => l.Condition));
        await env.C.CheckOutAsync(s.Rocket);
        Assert.Equal("Payload = X", (await env.Bom.GetWorkingAsync(Alice, s.Rocket, null)).Value!.Lines.Single(l => l.FindNumber == 30).Condition);
        await env.C.Parts.UndoCheckOutAsync(Alice, s.Rocket, null);

        // the condition change is audited with the old and the new text
        var audit = await env.C.AuditAsync();
        var set = audit.Where(a => a.Action == "bom.set_condition" && a.Outcome == AuditOutcome.Success).ToList();
        Assert.Contains(set, a => a.Payload.Contains("\"after\":\"Engine IN (A, B) AND payload = Y\"", StringComparison.Ordinal));
        Assert.Contains(set, a => a.Payload.Contains("\"before\":\"Engine IN (A, B) AND payload = Y\"", StringComparison.Ordinal) && (!a.Payload.Contains("\"after\"", StringComparison.Ordinal) || a.Payload.Contains("\"after\":null", StringComparison.Ordinal)));
        Assert.Contains(audit, a => a.Action == "bom.set_condition" && a.Outcome == AuditOutcome.Failure);
    }

    [Fact]
    public async Task A_resolved_variant_equals_the_oracle_for_every_selection_and_for_a_restricted_reader()
    {
        var env = await VarEnv.CreateAsync(pg);
        var s = await env.RocketAsync();
        var full = await env.ExplodeAsync(s.Rocket);
        var chains = BomService.ConditionChains(full.Lines);
        var lena = WorkflowEnv.Lena;     // not a U.S. person: the ITAR part (find 60) is a placeholder
        var eve = Eve;                   // U.S. person without the program: also cannot read it

        foreach (var sel in AllSelections())
        {
            // oracle: a line is used when every condition on its way down holds, evaluated in memory on the stored canonical text
            var expected = full.Lines.Where(l => chains[l.LineId].All(c => OptionExpressions.Parse(c).Expression!.Evaluate(sel.Options))).Select(l => l.Path).ToList();

            foreach (var who in new[] { Alice, lena, eve })
            {
                var resolved = await env.ExplodeAsync(s.Rocket, who, sel);
                Assert.Equal(expected, resolved.Lines.Select(l => l.Path).ToList());
                Assert.Contains($"variant {sel.Label}", resolved.Configuration, StringComparison.Ordinal);
                if (who != Alice)
                {
                    var secret = resolved.Lines.SingleOrDefault(l => l.FindNumber == 60 && l.Level == 1);
                    Assert.Equal(sel.Options.GetValueOrDefault("Engine") == "A", secret is not null);
                    if (secret is not null)
                    {
                        Assert.True(secret.Redacted);
                        Assert.Null(secret.Condition);     // the condition of a restricted line is never shown
                        Assert.Null(secret.Number);
                    }
                    Assert.DoesNotContain(s.Secret, JsonSerializer.Serialize(resolved), StringComparison.Ordinal);
                }
                // counts agree with the lines, redaction counts only placeholders that remain
                Assert.Equal(resolved.Lines.Count(l => l.Redacted), resolved.RedactedCount);
            }

            // CSV and summary go through the same filter
            var csv = await env.Bom.ExportCsvAsync(Alice, new BomQuery(s.Rocket, Variant: sel), null);
            Assert.True(csv.IsOk);
            Assert.Equal(expected.Count, csv.Value!.Lines);
            Assert.Contains($"_variant_{sel.Label.Replace('/', '_').Replace('-', '_')}", csv.Value.FileName.Replace("-", "_", StringComparison.Ordinal), StringComparison.Ordinal);
            var summary = (await env.Bom.SummaryAsync(Alice, new BomQuery(s.Rocket, Variant: sel), true, null)).Value!;
            Assert.Equal(expected.Count(p => full.Lines.Single(l => l.Path == p) is { HasChildren: false }), summary.Lines.Sum(l => l.Occurrences));
        }

        // spot checks of meaning: Turbo + Radar uses the pod, its extra and the secret coupling; Electric + Camera uses neither
        var ar = await env.ExplodeAsync(s.Rocket, Alice, Sel("A", "Y"));
        Assert.Equal(["10", "40", "50", "60", "70", "70.10", "70.20"], ar.Lines.Select(l => l.Path));
        Assert.Equal(6m, ar.Lines.Single(l => l.Path == "70.10").ExtendedQuantity); // 2 pods x 3 widgets
        var bx = await env.ExplodeAsync(s.Rocket, Alice, Sel("B", "X"));
        Assert.Equal(["20", "30", "50"], bx.Lines.Select(l => l.Path));

        // lazy expansion honours the variant too
        var lazy = await env.Bom.ExplodeAsync(Alice, new BomQuery(s.Rocket, ParentLine: "70", Depth: 1, Variant: Sel("B", "Y")), null);
        Assert.Equal(["70.10"], lazy.Value!.Lines.Select(l => l.Path));
        var hiddenLine = await env.Bom.ExplodeAsync(Alice, new BomQuery(s.Rocket, ParentLine: "70", Variant: Sel("B", "X")), null);
        Assert.Equal(OutcomeKind.NotFound, hiddenLine.Kind); // line 70 is not used in this variant, so it cannot be expanded
    }

    [Fact]
    public async Task Without_conditions_a_variant_changes_nothing_and_exports_are_byte_for_byte_the_same()
    {
        var env = await VarEnv.CreateAsync(pg);
        var leaf1 = await env.C.NewPartAsync("Leaf 1");
        var leaf2 = await env.C.NewPartAsync("Leaf 2");
        var sub = await env.C.AssemblyAsync("Sub", (leaf1, 2));
        var top = await env.C.AssemblyAsync("Top", (sub, 3), (leaf2, 1));
        var plain = await env.ExplodeAsync(top);
        foreach (var sel in AllSelections().Take(4))
        {
            var withVariant = await env.ExplodeAsync(top, Alice, sel);
            Assert.Equal(plain.Lines.Select(l => (l.Path, l.Quantity, l.ExtendedQuantity, l.Number, l.HasChildren)), withVariant.Lines.Select(l => (l.Path, l.Quantity, l.ExtendedQuantity, l.Number, l.HasChildren)));
        }
        Assert.All(plain.Lines, l => Assert.Null(l.Condition));
        var csv = (await env.Bom.ExportCsvAsync(Alice, new BomQuery(top), null)).Value!.Csv;
        Assert.DoesNotContain("Condition", csv, StringComparison.Ordinal);
        Assert.Contains("Effectivity,Flags\r\n", csv, StringComparison.Ordinal);
        // a baseline is a frozen list and cannot be resolved for a variant
        var refused = await env.Bom.ExplodeAsync(Alice, new BomQuery(top, Spec: BomConfigSpec.Baseline, Baseline: "BL-000001", Variant: Sel("A", "X")), null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
    }

    [Fact]
    public async Task Options_combine_with_effectivity_by_serial()
    {
        var env = await VarEnv.CreateAsync(pg);
        await env.C.NewContextAsync("ORCA-POD");
        var part1 = await env.C.NewPartAsync("Turbo gizmo");
        var part2 = await env.C.NewPartAsync("Any gizmo");
        var top = await env.C.NewPartAsync("Top", "Assembly");
        await env.EngineAndPayloadAsync(top);
        await env.C.CheckOutAsync(top);
        await env.C.AddLineAsync(top, part1, 1, 10, ConfigEnv.Serial("ORCA-POD", 100, 199));
        await env.CondAsync(top, 10, "Engine = A", top);
        await env.C.AddLineAsync(top, part2, 1, 20, ConfigEnv.Serial("ORCA-POD", 100, 199));
        await env.C.CheckInAsync(top);
        await env.C.ReleaseAsync(top);

        string[] Paths(BomExplosion e) => [.. e.Lines.Select(l => l.Path)];
        var unit150 = new EffectivityRequest("ORCA-POD", Serial: 150);
        var unit250 = new EffectivityRequest("ORCA-POD", Serial: 250);
        Assert.Equal(["10", "20"], Paths(await env.ExplodeAsync(top, Alice, Sel("A", "X"), unit150)));
        Assert.Equal(["20"], Paths(await env.ExplodeAsync(top, Alice, Sel("B", "X"), unit150)));          // effectivity holds, option does not
        Assert.Empty(Paths(await env.ExplodeAsync(top, Alice, Sel("A", "X"), unit250)));                   // option holds, effectivity does not
        Assert.Equal(["10", "20"], Paths(await env.ExplodeAsync(top, Alice, null, unit150)));              // no variant: effectivity only
        var line = (await env.ExplodeAsync(top, Alice, null, unit150)).Lines[0];
        Assert.Equal("Engine = A", line.Condition);
        Assert.NotNull(line.Effectivity);

        // the same through the as-of comparison of two units: options are not an effectivity dimension
        var csv = await env.Bom.ExportCsvAsync(Alice, new BomQuery(top, Spec: BomConfigSpec.Effectivity, Effectivity: unit150, Variant: Sel("A", "X")), null);
        Assert.Equal(2, csv.Value!.Lines);
    }

    [Fact]
    public async Task A_condition_on_a_released_iteration_changes_only_by_revising()
    {
        var env = await VarEnv.CreateAsync(pg);
        var c1 = await env.C.NewPartAsync("Turbo part");
        var c2 = await env.C.NewPartAsync("Electric part");
        var mini = await env.C.NewPartAsync("Mini", "Assembly");
        await env.C.CheckOutAsync(mini);
        await env.C.AddLineAsync(mini, c1, 1, 10);
        await env.C.AddLineAsync(mini, c2, 1, 20);
        await env.CondAsync(mini, 10, "Engine = A");
        await env.CondAsync(mini, 20, "Engine = B");
        await env.C.CheckInAsync(mini);
        await env.C.ReleaseAsync(mini);

        // released: there is no working copy, so the service refuses; the table refuses a direct change
        var refused = await env.Bom.SetLineConditionAsync(Alice, mini, Guid.NewGuid(), "Engine = B", null, null);
        Assert.NotEqual(OutcomeKind.Ok, refused.Kind);
        await using (var conn = new NpgsqlConnection(env.C.Db.AppConnectionString))
        {
            await conn.OpenAsync();
            await using var upd = new NpgsqlCommand("UPDATE plm.bom_usage SET opt_cond = NULL, opt_ast = NULL", conn);
            await Assert.ThrowsAsync<PostgresException>(() => upd.ExecuteNonQueryAsync());
            await using var ins = new NpgsqlCommand("DELETE FROM plm.bom_usage", conn);
            await Assert.ThrowsAsync<PostgresException>(() => ins.ExecuteNonQueryAsync());
        }
        // a malformed tree cannot even be stored: text without a tree (or the reverse) violates the shape constraint
        await using (var conn = new NpgsqlConnection(env.C.Db.SuperuserConnectionString))
        {
            await conn.OpenAsync();
            await using var bad = new NpgsqlCommand("INSERT INTO plm.bom_working_usage (id, checkout_revision_id, child_master_id, quantity, unit, find_number, created_at, created_by, opt_cond) VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1, 'EA', 1, now(), 'x', 'Engine = A')", conn);
            await Assert.ThrowsAnyAsync<PostgresException>(() => bad.ExecuteNonQueryAsync());
        }

        // revise through a change notice: revision B starts with A's conditions and may change them; A stays as it was
        await env.M.ReviseAsync(mini, async () =>
        {
            var working = (await env.Bom.GetWorkingAsync(Alice, mini, null)).Value!;
            Assert.Equal(["Engine = A", "Engine = B"], working.Lines.Select(l => l.Condition));
            await env.CondAsync(mini, 20, "Engine IN (B, C)");
        });
        var revA = await env.Bom.ExplodeAsync(Alice, new BomQuery(mini, "A", Spec: BomConfigSpec.Latest), null);
        var revB = await env.Bom.ExplodeAsync(Alice, new BomQuery(mini, "B", Spec: BomConfigSpec.Latest), null);
        Assert.Equal(["Engine = A", "Engine = B"], revA.Value!.Lines.Select(l => l.Condition));
        Assert.Equal(["Engine = A", "Engine IN (B, C)"], revB.Value!.Lines.Select(l => l.Condition));
        var resolvedA = await env.Bom.ExplodeAsync(Alice, new BomQuery(mini, "A", Variant: new VariantSelection("C", new Dictionary<string, string> { ["Engine"] = "C" })), null);
        var resolvedB = await env.Bom.ExplodeAsync(Alice, new BomQuery(mini, "B", Variant: new VariantSelection("C", new Dictionary<string, string> { ["Engine"] = "C" })), null);
        Assert.Empty(resolvedA.Value!.Lines);
        Assert.Equal(["20"], resolvedB.Value!.Lines.Select(l => l.Path));
    }

    [Fact]
    public async Task An_mbom_inherits_conditions_from_the_ebom_lines_it_implements()
    {
        var env = await VarEnv.CreateAsync(pg);
        var c1 = await env.C.NewPartAsync("Turbo part");
        var c2 = await env.C.NewPartAsync("Electric part");
        var c3 = await env.C.NewPartAsync("Common part");
        var mini = await env.C.NewPartAsync("Mini", "Assembly");
        await env.C.CheckOutAsync(mini);
        await env.C.AddLineAsync(mini, c1, 1, 10);
        await env.C.AddLineAsync(mini, c2, 1, 20);
        await env.C.AddLineAsync(mini, c3, 4, 30);
        await env.CondAsync(mini, 10, "Engine = A");
        await env.CondAsync(mini, 20, "Engine = B");
        await env.C.CheckInAsync(mini);
        await env.C.ReleaseAsync(mini);

        var mbom = await env.M.GenerateAsync(mini);
        string[] Paths(MbomExplosion e) => [.. e.Lines.Select(l => l.Path)];
        var all = await env.M.Mbom.ExplodeAsync(Alice, new MbomQuery(mbom.Summary.Number), "c");
        Assert.Equal(["10", "20", "30"], Paths(all.Value!));
        var a = await env.M.Mbom.ExplodeAsync(Alice, new MbomQuery(mbom.Summary.Number, Variant: Sel("A", "X")), "c");
        Assert.Equal(["10", "30"], Paths(a.Value!));
        Assert.Contains("variant A/X", a.Value!.Configuration, StringComparison.Ordinal);
        var b = await env.M.Mbom.ExplodeAsync(Alice, new MbomQuery(mbom.Summary.Number, Variant: Sel("B", "X")), "c");
        Assert.Equal(["20", "30"], Paths(b.Value!));
        var csv = await env.M.Mbom.ExportCsvAsync(Alice, new MbomQuery(mbom.Summary.Number, Variant: Sel("B", "X")), "c");
        Assert.Equal(2, csv.Value!.Lines);
    }

    [Fact]
    public async Task A_conditioned_structure_resolves_inside_the_budget()
    {
        var env = await VarEnv.CreateAsync(pg);
        var top = await env.C.NewPartAsync("Big", "Assembly");
        await env.EngineAndPayloadAsync(top);
        // 60 direct lines, half of them conditioned, resolved for a variant: the filter runs inside the recursive query
        var parts = new List<string>();
        for (var i = 0; i < 60; i++) parts.Add(await env.C.NewPartAsync("Leaf " + i));
        await env.C.CheckOutAsync(top);
        for (var i = 0; i < parts.Count; i++)
        {
            await env.C.AddLineAsync(top, parts[i], 1, (i + 1) * 10);
            if (i % 2 == 0) await env.CondAsync(top, (i + 1) * 10, i % 4 == 0 ? "Engine = A" : "Payload IN (X, Y) AND Engine = B");
        }
        await env.C.CheckInAsync(top);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resolved = await env.ExplodeAsync(top, Alice, Sel("A", "X"));
        sw.Stop();
        Assert.Equal(30 + 15, resolved.Lines.Count);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"resolve took {sw.ElapsedMilliseconds} ms");
    }
}
