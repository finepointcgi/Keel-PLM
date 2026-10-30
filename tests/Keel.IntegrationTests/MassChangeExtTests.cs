using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Documents;
using Keel.Parts;
using Keel.Reports;
using Keel.Reports.Tabular;
using Keel.Requirements;
using Keel.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Mass change beyond parts (migration 0029, ADR-0050) against the real host and PostgreSQL: documents, assembly structure with where-used selection and change notices, requirements,
/// and the file upload path, with the same safety model as parts (preview, per-object access and release locks, per-item results, one audit trail, undo).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MassChangeExtTests(PostgresFixture pg)
{
    private static readonly Subject Alice = WorkflowEnv.Alice;
    private static readonly Subject Lena = WorkflowEnv.Lena;
    private static readonly Subject Bob = WorkflowEnv.User("bob2", [Roles.Viewer, Roles.Author]);
    private static readonly Subject Boss = WorkflowEnv.User("boss", [Roles.Admin, Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);
    private const string Hidden = "Not found, or you may not read it.";

    private sealed record Env(ApiFactory Api) : IDisposable
    {
        public void Dispose() => Api.Dispose();
        public PartService Parts => Api.Services.GetRequiredService<PartService>();
        public BomService Bom => Api.Services.GetRequiredService<BomService>();
        public MassChangeService Mass => Api.Services.GetRequiredService<MassChangeService>();
        public DocumentService Docs => Api.Services.GetRequiredService<DocumentService>();
        public RequirementService Req => Api.Services.GetRequiredService<RequirementService>();
        public ChangeService Changes => Api.Services.GetRequiredService<ChangeService>();
        public ICadPartPort PartPort => Api.Services.GetRequiredService<ICadPartPort>();

        public async Task<string> Part(string name, string type = "Component", Markings? m = null)
        {
            var r = await Parts.CreateAsync(Boss, new CreatePartCommand(name, type, "EA", "Make", m ?? WorkflowEnv.Open), null);
            Assert.True(r.IsOk, string.Join("; ", r.Errors));
            return r.Value!.Number;
        }

        public async Task Release(params string[] numbers)
        {
            foreach (var n in numbers) Assert.True((await Parts.SetStateAsync(Boss, n, "Released", "synthetic", true, null)).IsOk);
        }

        public async Task<string> Assembly(string name, Markings? m, params string[] children)
        {
            var asm = await Part(name, "Assembly", m);
            Assert.True((await Parts.CheckOutAsync(Boss, asm, null)).IsOk);
            var find = 10;
            foreach (var c in children) { Assert.True((await Bom.AddUsageAsync(Boss, asm, new AddUsageCommand(c, 2m, null, find, null), null)).IsOk); find += 10; }
            Assert.True((await Parts.CheckInAsync(Boss, asm, "structure", null)).IsOk);
            return asm;
        }

        public async Task<string> Document(string name, Markings? m = null, string? description = "first")
        {
            var r = await Docs.CreateAsync(Boss, new CreateDocumentCommand(name, "Specification", m ?? WorkflowEnv.Open, description), null, null);
            Assert.True(r.IsOk, string.Join("; ", r.Errors));
            return r.Value!.Number;
        }

        public async Task<string> Description(string number) => (await Docs.GetAsync(Boss, number, null, null)).Value!.Iteration.Description;

        public async Task<IReadOnlyList<(string Child, decimal Qty, int Find)>> Lines(string asm) =>
            [.. (await PartPort.GetBomAsync(Boss, asm, null)).Value!.Lines.Select(l => (l.ChildNumber, l.Quantity, l.FindNumber))];

        public async Task<List<AuditRecord>> Audit() => [.. await new AuditQueries(Api.Services.GetRequiredService<NpgsqlDataSource>()).ListAsync(new AuditQuery(Limit: 20000))];

        public async Task<string> Ecn(string title)
        {
            var c = await Changes.CreateAsync(Boss, new CreateChangeCommand("ECN", title, "d", "Medium", "r", WorkflowEnv.Open, "II"), null);
            Assert.True(c.IsOk, string.Join("; ", c.Errors));
            return c.Value!.Number;
        }
    }

    private async Task<Env> NewEnv() => new(await ApiFactory.CreateAsync(pg));

    private static JsonObject Obj(params (string, JsonNode?)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv) o[k] = v;
        return o;
    }

    // ---------------------------------------------------------------- documents

    [Fact]
    public async Task Documents_preview_refuses_locked_and_unreadable_with_the_same_words_apply_per_document_with_one_correlation_and_undo()
    {
        using var e = await NewEnv();
        var d1 = await e.Document("One");
        var d2 = await e.Document("Two");
        var released = await e.Document("Released one");
        var secret = await e.Document("Secret spec", WorkflowEnv.Itar("KRAKEN"));
        Assert.True((await e.Docs.SetStateAsync(Boss, released, "Released", "synthetic", true, null)).IsOk);
        var p = Obj(("description", "Updated by mass change"));

        // Alice is not on KRAKEN: the secret document and a missing one are refused in the same words, naming nothing.
        var preview = await e.Mass.PreviewAsync(Alice, new MassRequest("document", [d1, d2, released, secret, "D-999999"], MassOperations.SetDescription, p), null);
        Assert.True(preview.IsOk, string.Join("; ", preview.Errors));
        var items = preview.Value!.Items.ToDictionary(i => i.Key);
        Assert.True(items[d1].CanApply && items[d2].CanApply);
        Assert.False(items[released].CanApply);
        Assert.Contains("locked", items[released].Reason!, StringComparison.Ordinal);
        Assert.Equal(Hidden, items[secret].Reason);
        Assert.Equal(Hidden, items["D-999999"].Reason);
        Assert.Null(items[secret].Name);
        Assert.Null(items[secret].Banner);
        Assert.Equal("first", await e.Description(d1)); // a preview changes nothing

        // Bob (no author-owned lock): another user holds d2's check-out, so d2 fails, d1 changes, and a retry touches only d2.
        Assert.True((await e.Docs.CheckOutAsync(Bob, d2, null)).IsOk);
        var partial = await e.Mass.ApplyAsync(Alice, preview.Value.Number, "req-corr");
        Assert.Equal(OutcomeKind.Conflict, partial.Kind);
        Assert.Equal(1, partial.Value!.Applied);
        Assert.Equal(1, partial.Value.Failed);
        Assert.Equal("Updated by mass change", await e.Description(d1));
        Assert.Equal("first", await e.Description(d2));
        Assert.True((await e.Docs.UndoCheckOutAsync(Bob, d2, null)).IsOk);
        var retry = await e.Mass.ApplyAsync(Alice, preview.Value.Number, null);
        Assert.True(retry.IsOk, string.Join("; ", retry.Errors));
        Assert.Equal("Updated by mass change", await e.Description(d2));
        var audit = await e.Audit();
        var corr = preview.Value.CorrelationId;
        Assert.Contains(audit, r => r.Action == "document.checkin" && r.ObjectId == d1 && r.CorrelationId == corr);
        Assert.Contains(audit, r => r.Action == "document.checkin" && r.ObjectId == d2 && r.CorrelationId == corr);

        var undone = await e.Mass.UndoAsync(Alice, preview.Value.Number, null);
        Assert.True(undone.IsOk, string.Join("; ", undone.Errors));
        Assert.Equal("first", await e.Description(d1));
        Assert.Equal("first", await e.Description(d2));
        // The same value again is refused as "already", not silently counted as a change.
        var again = (await e.Mass.PreviewAsync(Alice, new MassRequest("document", [d1], MassOperations.SetDescription, Obj(("description", "first"))), null)).Value!;
        Assert.True(again.Items[0].CanApply); // the preview cannot know; apply reports it
        var applied = await e.Mass.ApplyAsync(Alice, again.Number, null);
        Assert.Equal(OutcomeKind.Conflict, applied.Kind);
        Assert.Contains("already has that description", applied.Value!.Items[0].Detail!, StringComparison.Ordinal);
        // Documents cannot be attached to a change notice, and a mass change belongs to its creator.
        var ecn = await e.Ecn("Docs");
        Assert.Equal(OutcomeKind.Invalid, (await e.Mass.PreviewAsync(Boss, new MassRequest("document", [d1], MassOperations.SetDescription, p, ecn), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await e.Mass.GetAsync(Bob, preview.Value.Number)).Kind);
    }

    [Fact]
    public async Task Document_markings_watch_favorite_and_review_follow_the_authority_of_the_user_and_undo()
    {
        using var e = await NewEnv();
        var a = await e.Document("A");
        var b = await e.Document("B");
        var kraken = JsonNode.Parse("""{"jurisdiction":"None","distributionStatement":"B","owningOrg":"ENG","controllingOffice":"ACME","distributionReason":"Test","program":"KRAKEN"}""")!.AsObject();
        var refused = (await e.Mass.PreviewAsync(Alice, new MassRequest("document", [a, b], MassOperations.ChangeMarkings, Obj(("reason", "reclassify"), ("markings", kraken.DeepClone()))), null)).Value!;
        Assert.Equal(0, refused.WillChange);
        Assert.All(refused.Items, i => Assert.Contains("may not set these markings", i.Reason!, StringComparison.Ordinal));
        var ok = (await e.Mass.PreviewAsync(Boss, new MassRequest("document", [a, b], MassOperations.ChangeMarkings, Obj(("reason", "reclassify"), ("markings", kraken.DeepClone()))), null)).Value!;
        Assert.Equal(2, ok.WillChange);
        Assert.True((await e.Mass.ApplyAsync(Boss, ok.Number, null)).IsOk);
        Assert.Equal("KRAKEN", (await e.Docs.GetAsync(Boss, a, null, null)).Value!.Markings.Program);
        Assert.True((await e.Mass.UndoAsync(Boss, ok.Number, null)).IsOk);
        Assert.Null((await e.Docs.GetAsync(Boss, a, null, null)).Value!.Markings.Program);

        var w = (await e.Mass.PreviewAsync(Alice, new MassRequest("document", [a, b], MassOperations.Watch, Obj(("level", "decisions"))), null)).Value!;
        Assert.True((await e.Mass.ApplyAsync(Alice, w.Number, null)).IsOk);
        Assert.True((await e.Mass.UndoAsync(Alice, w.Number, null)).IsOk);
        var f = (await e.Mass.PreviewAsync(Alice, new MassRequest("document", [a], MassOperations.Favorite, []), null)).Value!;
        Assert.True((await e.Mass.ApplyAsync(Alice, f.Number, null)).IsOk);
        Assert.True((await e.Mass.UndoAsync(Alice, f.Number, null)).IsOk);
        // Baseline proposals hold parts only.
        Assert.Equal(OutcomeKind.Invalid, (await e.Mass.PreviewAsync(Alice, new MassRequest("document", [a], MassOperations.BaselineProposal, Obj(("name", "x"))), null)).Kind);
        Assert.DoesNotContain("document", e.Mass.KindsFor(MassOperations.BaselineProposal));
    }

    // ---------------------------------------------------------------- assembly structure

    [Fact]
    public async Task Where_used_selects_only_readable_assemblies_and_replace_add_remove_change_structure_per_assembly_with_undo()
    {
        using var e = await NewEnv();
        var bolt = await e.Part("Bolt");
        var screw = await e.Part("Screw");
        var nut = await e.Part("Nut");
        var a1 = await e.Assembly("Pod one", null, bolt, nut);
        var a2 = await e.Assembly("Pod two", null, bolt);
        var asmReleased = await e.Assembly("Pod released", null, bolt);
        var asmSecret = await e.Assembly("Pod secret", WorkflowEnv.Itar("KRAKEN"), bolt);
        var unrelated = await e.Assembly("Other pod", null, nut);
        await e.Release(asmReleased);

        // Where-used picks direct parents the caller may read; the secret assembly is not offered and not counted.
        var alice = (await e.Mass.SelectAsync(Alice, "assembly", MassSelectors.WhereUsed, bolt, null)).Value!;
        Assert.Equal(new[] { a1, a2, asmReleased }.Order(StringComparer.Ordinal), alice.Keys);
        Assert.DoesNotContain(unrelated, alice.Keys);
        var boss = (await e.Mass.SelectAsync(Boss, "assembly", MassSelectors.WhereUsed, bolt, null)).Value!;
        Assert.Contains(asmSecret, boss.Keys);
        Assert.Equal(OutcomeKind.NotFound, (await e.Mass.SelectAsync(Alice, "assembly", MassSelectors.WhereUsed, "P-999999", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await e.Mass.SelectAsync(Alice, "part", MassSelectors.WhereUsed, bolt, null)).Kind);
        Assert.Contains(await e.Audit(), r => r.Action == "output.mass.select" && r.Outcome == AuditOutcome.Success);

        // Replace the bolt by the screw. The released assembly and the unreadable one are refused; a missing component refuses everything.
        var replace = Obj(("component", bolt), ("replacement", screw));
        var keys = new[] { a1, a2, asmReleased, asmSecret, unrelated };
        var pv = (await e.Mass.PreviewAsync(Alice, new MassRequest("assembly", keys, MassOperations.ReplaceComponent, replace, null, null, "where_used", bolt), null)).Value!;
        var items = pv.Items.ToDictionary(i => i.Key);
        Assert.True(items[a1].CanApply && items[a2].CanApply);
        Assert.Contains("ECN", items[asmReleased].Reason!, StringComparison.Ordinal);
        Assert.Equal(Hidden, items[asmSecret].Reason);
        Assert.Contains("does not use", items[unrelated].Reason!, StringComparison.Ordinal);
        Assert.Equal("where_used", pv.Source);
        Assert.Equal(bolt, pv.SourceDetail);
        var missing = (await e.Mass.PreviewAsync(Alice, new MassRequest("assembly", [a1], MassOperations.ReplaceComponent, Obj(("component", bolt), ("replacement", "P-999999"))), null)).Value!;
        Assert.Contains("not found", missing.Items[0].Reason!, StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await e.Mass.PreviewAsync(Alice, new MassRequest("assembly", [a1], MassOperations.ReplaceComponent, Obj(("component", bolt), ("replacement", bolt))), null)).Kind);

        var before = await e.Lines(a1);
        var applied = await e.Mass.ApplyAsync(Alice, pv.Number, null);
        Assert.True(applied.IsOk, string.Join("; ", applied.Errors));
        var after = await e.Lines(a1);
        Assert.DoesNotContain(after, l => l.Child == bolt);
        Assert.Equal(before.Single(l => l.Child == bolt).Find, after.Single(l => l.Child == screw).Find); // the find number and quantity carry over
        Assert.Equal(before.Single(l => l.Child == bolt).Qty, after.Single(l => l.Child == screw).Qty);
        Assert.Contains(await e.Audit(), r => r.Action == "part.checkin" && r.ObjectId == a2 && r.CorrelationId == pv.CorrelationId);
        Assert.Contains(await e.Lines(asmReleased), l => l.Child == bolt); // refused: untouched
        var undone = await e.Mass.UndoAsync(Alice, pv.Number, null);
        Assert.True(undone.IsOk, string.Join("; ", undone.Errors));
        Assert.Equal(before.OrderBy(l => l.Find), (await e.Lines(a1)).OrderBy(l => l.Find));

        // Add (system find number) then remove.
        var add = (await e.Mass.PreviewAsync(Alice, new MassRequest("assembly", [a1, a2], MassOperations.AddComponent, Obj(("component", screw), ("quantity", 4m))), null)).Value!;
        Assert.True((await e.Mass.ApplyAsync(Alice, add.Number, null)).IsOk);
        Assert.Contains(await e.Lines(a2), l => l.Child == screw && l.Qty == 4m);
        var addAgain = (await e.Mass.PreviewAsync(Alice, new MassRequest("assembly", [a1], MassOperations.AddComponent, Obj(("component", screw), ("quantity", 1m))), null)).Value!;
        Assert.Contains("already uses", addAgain.Items[0].Reason!, StringComparison.Ordinal);
        Assert.True((await e.Mass.UndoAsync(Alice, add.Number, null)).IsOk);
        Assert.DoesNotContain(await e.Lines(a2), l => l.Child == screw);
        var remove = (await e.Mass.PreviewAsync(Alice, new MassRequest("assembly", [a1, a2], MassOperations.RemoveComponent, Obj(("component", bolt))), null)).Value!;
        Assert.True((await e.Mass.ApplyAsync(Alice, remove.Number, null)).IsOk);
        Assert.DoesNotContain(await e.Lines(a1), l => l.Child == bolt);
        Assert.True((await e.Mass.UndoAsync(Alice, remove.Number, null)).IsOk);
        Assert.Contains(await e.Lines(a1), l => l.Child == bolt);
    }

    [Fact]
    public async Task A_released_assembly_is_changed_only_through_a_change_notice_which_lists_it_and_revises_it_first()
    {
        using var e = await NewEnv();
        var bolt = await e.Part("Bolt");
        var screw = await e.Part("Screw");
        var asm = await e.Assembly("Pod", null, bolt);
        var released = await e.Part("Released part");
        await e.Release(asm, released);
        var replace = Obj(("component", bolt), ("replacement", screw));

        // A reserved parameter cannot smuggle a change notice in: without a checked notice the release lock refuses.
        var smuggled = (await e.Mass.PreviewAsync(Boss, new MassRequest("assembly", [asm], MassOperations.ReplaceComponent, Obj(("component", bolt), ("replacement", screw), (MassParameterNames.Change, "ECN-000001"))), null)).Value!;
        Assert.False(smuggled.Items[0].CanApply);
        Assert.Null(smuggled.ChangeNumber);

        Assert.Equal(OutcomeKind.Invalid, (await e.Mass.PreviewAsync(Boss, new MassRequest("assembly", [asm], MassOperations.ReplaceComponent, replace, "ECN-999999"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await e.Mass.PreviewAsync(Boss, new MassRequest("assembly", [asm], MassOperations.Watch, [], "ECN-000001"), null)).Kind);
        var ecn = await e.Ecn("Swap bolt for screw");
        var pv = await e.Mass.PreviewAsync(Boss, new MassRequest("assembly", [asm], MassOperations.ReplaceComponent, replace, ecn), null);
        Assert.True(pv.IsOk, string.Join("; ", pv.Errors));
        Assert.Equal(ecn, pv.Value!.ChangeNumber);
        Assert.True(pv.Value.Items[0].CanApply);
        Assert.Contains("revised through " + ecn, pv.Value.Items[0].Reason!, StringComparison.Ordinal);

        var applied = await e.Mass.ApplyAsync(Boss, pv.Value.Number, null);
        Assert.True(applied.IsOk, string.Join("; ", applied.Errors));
        var detail = (await e.Parts.GetAsync(Boss, asm, null, null)).Value!;
        Assert.Equal("B", detail.Revision.Revision); // revised through the notice, then edited in the new revision
        var lines = await e.Lines(asm);
        Assert.Contains(lines, l => l.Child == screw);
        Assert.DoesNotContain(lines, l => l.Child == bolt);
        var notice = (await e.Changes.GetAsync(Boss, ecn, null)).Value!;
        var row = Assert.Single(notice.Affected, a => a.Key == asm);
        Assert.Equal("revise", row.Action);
        Assert.Equal("B", row.ResultingRevision);
        Assert.Contains(await e.Audit(), r => r.Action == "output.mass.apply" && r.CorrelationId == pv.Value.CorrelationId);

        // A part attribute of a released part goes the same way, through another notice.
        var ecn2 = await e.Ecn("Material change");
        var attr = (await e.Mass.PreviewAsync(Boss, new MassRequest("part", [released], MassOperations.SetAttribute, Obj(("name", "material"), ("value", "7075-T6")), ecn2), null)).Value!;
        Assert.True(attr.Items[0].CanApply);
        Assert.True((await e.Mass.ApplyAsync(Boss, attr.Number, null)).IsOk);
        Assert.Equal("B", (await e.Parts.GetAsync(Boss, released, null, null)).Value!.Revision.Revision);
        Assert.Contains((await e.Changes.GetAsync(Boss, ecn2, null)).Value!.Affected, a => a.Key == released && a.Action == "revise");
        // The same part without a notice is still refused, and a released part is never changed in place.
        var plain = (await e.Mass.PreviewAsync(Boss, new MassRequest("part", [released], MassOperations.SetAttribute, Obj(("name", "material"), ("value", "x"))), null)).Value!;
        Assert.True(plain.Items[0].CanApply); // revision B is in work now: that revision may be edited
    }

    [Fact]
    public async Task Selection_and_previews_of_documents_assemblies_and_requirements_match_the_policy_oracle_for_every_subject_and_marking_variant()
    {
        using var e = await NewEnv();
        Markings[] variants =
        [
            WorkflowEnv.Open, WorkflowEnv.Itar(), WorkflowEnv.Itar("KRAKEN"),
            Markings.Create(ExportJurisdiction.Ear, DistributionStatement.C, "ENG", eccn: "9A610", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "r").Value,
            Markings.Create(ExportJurisdiction.None, DistributionStatement.B, "ENG", controllingOffice: "ACME", distributionReason: "r", program: "ORCA").Value,
            Markings.Create(ExportJurisdiction.None, DistributionStatement.B, "ENG", cuiCategories: ["CTI"], controllingOffice: "ACME", distributionReason: "r").Value,
        ];
        var bolt = await e.Part("Bolt");
        var assemblies = new Dictionary<string, Markings>();
        var documents = new Dictionary<string, Markings>();
        foreach (var m in variants)
        {
            assemblies[await e.Assembly($"Asm {assemblies.Count}", m, bolt)] = m;
            documents[await e.Document($"Doc {documents.Count}", m)] = m;
        }
        var spec = (await e.Req.CreateAsync(Boss, new CreateSpecCommand("Spec", "ORCA pod", "d", WorkflowEnv.Open), null)).Value!.Summary.Number;
        var requirements = new Dictionary<string, Markings>();
        foreach (var m in variants)
        {
            var iteration = (await e.Req.GetAsync(Boss, spec, null, null)).Value!.Displayed.LatestIteration;
            var added = await e.Req.AddAsync(Boss, spec, iteration, new RequirementInput($"Req {requirements.Count}", "The pod shall float.", "functional", "high", "test", "Because.", ReqStatuses.Approved, null, null, m, true), null);
            Assert.True(added.IsOk, string.Join("; ", added.Errors));
            requirements[$"{spec}/{added.Value!.CreatedKeys.Single()}"] = m;
        }

        Subject[] subjects = [Alice, WorkflowEnv.Ada, Lena, WorkflowEnv.Mallory, WorkflowEnv.Audrey, WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]), WorkflowEnv.Farid];
        foreach (var subject in subjects)
        {
            bool Readable(Markings m) => AccessPolicy.Evaluate(subject, m, PlmAction.Read).Permitted;
            bool CanModify(Markings m) => AccessPolicy.Evaluate(subject, m, PlmAction.Modify).Permitted;

            // Where-used: exactly the readable assemblies, in order, and nothing else.
            var sel = await e.Mass.SelectAsync(subject, "assembly", MassSelectors.WhereUsed, bolt, null);
            if (!AccessPolicy.HasRoleFor(subject, PlmAction.Read)) { Assert.False(sel.IsOk, $"{subject.Username} without a read role selected"); continue; }
            Assert.True(sel.IsOk, $"{subject.Username}: {sel.Kind}");
            var expected = assemblies.Where(kv => Readable(kv.Value)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            Assert.True(expected.SequenceEqual(sel.Value!.Keys), $"where-used of {subject.Username}: expected {string.Join(",", expected)} got {string.Join(",", sel.Value.Keys)}");

            // Previews: unreadable objects get the missing-object words and no name, banner or state; readable ones get a verdict that agrees with Modify.
            async Task Check(string kind, string op, JsonObject p, Dictionary<string, Markings> all)
            {
                var r = await e.Mass.PreviewAsync(subject, new MassRequest(kind, [.. all.Keys], op, p), null);
                Assert.True(r.IsOk, $"{kind} {subject.Username}: {string.Join("; ", r.Errors)}");
                foreach (var item in r.Value!.Items)
                {
                    var m = all[item.Key];
                    if (!Readable(m)) { Assert.False(item.CanApply); Assert.Equal(Hidden, item.Reason); Assert.Null(item.Name); Assert.Null(item.Banner); Assert.Null(item.State); }
                    else if (!CanModify(m)) Assert.False(item.CanApply, $"{kind} {item.Key} for {subject.Username} cannot be changed");
                    else Assert.True(item.CanApply, $"{kind} {item.Key} for {subject.Username}: {item.Reason}");
                }
            }
            await Check("document", MassOperations.SetDescription, Obj(("description", "oracle")), documents);
            await Check("assembly", MassOperations.AddComponent, Obj(("component", await e.Part($"Extra for {subject.Username}")), ("quantity", 1m)), assemblies);
            await Check("requirement", MassOperations.SetRequirementField, Obj(("field", "priority"), ("value", "low")), requirements);
        }
    }

    // ---------------------------------------------------------------- requirements

    [Fact]
    public async Task Requirement_fields_change_through_the_specification_edit_with_the_revision_lock_hidden_requirements_and_undo()
    {
        using var e = await NewEnv();
        var spec = (await e.Req.CreateAsync(Boss, new CreateSpecCommand("Pod requirements", "ORCA pod", "d", WorkflowEnv.Open), null)).Value!.Summary.Number;
        async Task<string> Add(string title, Markings? m = null)
        {
            var it = (await e.Req.GetAsync(Boss, spec, null, null)).Value!.Displayed.LatestIteration;
            return $"{spec}/{(await e.Req.AddAsync(Boss, spec, it, new RequirementInput(title, "The pod shall float.", "functional", "high", "test", "Because.", ReqStatuses.Approved, null, null, m, true), null)).Value!.CreatedKeys.Single()}";
        }
        var r1 = await Add("Depth");
        var r2 = await Add("Speed");
        var secret = await Add("Secret", WorkflowEnv.Itar("KRAKEN"));
        var p = Obj(("field", "priority"), ("value", "Critical"));

        var pv = (await e.Mass.PreviewAsync(Alice, new MassRequest("requirement", [r1, r2, secret, spec + "/R-9999", "not-a-key"], MassOperations.SetRequirementField, p), null)).Value!;
        var items = pv.Items.ToDictionary(i => i.Key);
        Assert.True(items[r1].CanApply && items[r2].CanApply);
        Assert.Equal(Hidden, items[secret].Reason);
        Assert.Equal(Hidden, items[spec + "/R-9999"].Reason);
        Assert.Equal(Hidden, items["not-a-key"].Reason);
        var bad = (await e.Mass.PreviewAsync(Alice, new MassRequest("requirement", [r1], MassOperations.SetRequirementField, Obj(("field", "priority"), ("value", "urgent"))), null)).Value!;
        Assert.Contains("must be one of", bad.Items[0].Reason!, StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await e.Mass.PreviewAsync(Alice, new MassRequest("requirement", [r1], MassOperations.SetRequirementField, Obj(("field", "title"), ("value", "x"))), null)).Kind);

        var iterationBefore = (await e.Req.GetAsync(Boss, spec, null, null)).Value!.Displayed.LatestIteration;
        var applied = await e.Mass.ApplyAsync(Alice, pv.Number, null);
        Assert.True(applied.IsOk, string.Join("; ", applied.Errors));
        var set = (await e.Req.GetRequirementsAsync(Boss, spec, null, null)).Value!;
        Assert.Equal("critical", set.Rows.Single(r => r.Key == "R-0001").Priority);
        Assert.Equal("critical", set.Rows.Single(r => r.Key == "R-0002").Priority);
        Assert.Equal("high", set.Rows.Single(r => r.Key == "R-0003").Priority); // the hidden one was not touched
        Assert.Equal(iterationBefore + 2, (await e.Req.GetAsync(Boss, spec, null, null)).Value!.Displayed.LatestIteration); // one new iteration per requirement edit
        Assert.Contains(await e.Audit(), r => r.Action == "requirement.edit" && r.CorrelationId == pv.CorrelationId);

        Assert.True((await e.Mass.UndoAsync(Alice, pv.Number, null)).IsOk);
        Assert.Equal("high", (await e.Req.GetRequirementsAsync(Boss, spec, null, null)).Value!.Rows.Single(r => r.Key == "R-0001").Priority);

        // Markings of requirements: only within the user's authority and the specification's markings.
        var itarMarkings = MassParameters.MarkingsJson(WorkflowEnv.Itar());
        var mk = (await e.Mass.PreviewAsync(Alice, new MassRequest("requirement", [r1], MassOperations.ChangeMarkings, Obj(("reason", "raise"), ("markings", itarMarkings))), null)).Value!;
        Assert.True(mk.Items[0].CanApply);
        Assert.True((await e.Mass.ApplyAsync(Alice, mk.Number, null)).IsOk);
        Assert.Equal(ExportJurisdiction.Itar, (await e.Req.GetRequirementsAsync(Boss, spec, null, null)).Value!.Rows.Single(r => r.Key == "R-0001").Markings!.ExportJurisdiction);
        Assert.True((await e.Mass.UndoAsync(Alice, mk.Number, null)).IsOk);

        // A specification that is no longer In Work is refused (a released or obsolete one is revised first).
        Assert.True((await e.Req.ObsoleteAsync(Boss, spec, "retired", null)).IsOk);
        var locked = (await e.Mass.PreviewAsync(Alice, new MassRequest("requirement", [r1], MassOperations.SetRequirementField, p), null)).Value!;
        Assert.False(locked.Items[0].CanApply);
        Assert.Contains("In Work", locked.Items[0].Reason!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- upload

    private static byte[] Csv(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");

    [Fact]
    public async Task A_csv_or_workbook_names_the_objects_and_their_own_values_and_is_previewed_like_a_typed_selection()
    {
        using var e = await NewEnv();
        var p1 = await e.Part("One");
        var p2 = await e.Part("Two");
        var secret = await e.Part("Secret", m: WorkflowEnv.Itar("KRAKEN"));
        var file = Csv("Part,Name,Value", $"{p1},material,7075-T6", $"{p2},material,6061-T6", $"{secret},material,x", "P-999999,material,y");
        var pv = await e.Mass.PreviewUploadAsync(Alice, new MassUploadRequest("changes.csv", file, "part", MassOperations.SetAttribute, null, null), "c");
        Assert.True(pv.IsOk, string.Join("; ", pv.Errors));
        Assert.Equal("upload", pv.Value!.Source);
        Assert.Equal("changes.csv", pv.Value.SourceDetail);
        var items = pv.Value.Items.ToDictionary(i => i.Key);
        Assert.True(items[p1].CanApply && items[p2].CanApply);
        Assert.Equal(Hidden, items[secret].Reason); // unreadable and missing look alike
        Assert.Equal(Hidden, items["P-999999"].Reason);
        Assert.Equal(1, await Count(e, $"SELECT count(*) FROM rpt.mass_change_item WHERE change_id = (SELECT id FROM rpt.mass_change WHERE number = '{pv.Value.Number}') AND item_parameters ->> 'value' = '7075-T6'"));

        var applied = await e.Mass.ApplyAsync(Alice, pv.Value.Number, null);
        Assert.True(applied.IsOk, string.Join("; ", applied.Errors));
        Assert.Equal("7075-T6", (await e.Parts.GetAsync(Alice, p1, null, null)).Value!.Iteration.Attributes["material"]!.GetValue<string>());
        Assert.Equal("6061-T6", (await e.Parts.GetAsync(Alice, p2, null, null)).Value!.Iteration.Attributes["material"]!.GetValue<string>());
        Assert.True((await e.Mass.UndoAsync(Alice, pv.Value.Number, null)).IsOk);

        // The file name and its SHA-256 are audited; the bytes are not kept.
        var audit = await e.Audit();
        var upload = Assert.Single(audit, r => r.Action == "output.mass.upload" && r.Outcome == AuditOutcome.Success);
        Assert.Contains("changes.csv", upload.Payload, StringComparison.Ordinal);

        // A workbook works the same way, with the kind and the operation inside the file, and a leading apostrophe on text removed.
        var xlsx = XlsxWriter.Write([new SheetData("Changes", ["kind", "operation", "part", "name", "value"], [[Cell.T("part"), Cell.T("set_attribute"), Cell.T(p1), Cell.T("material"), Cell.T("=cmd|' /C calc'!A0")]])]);
        var wb = await e.Mass.PreviewUploadAsync(Alice, new MassUploadRequest("changes.xlsx", xlsx, null, null, null, null), null);
        Assert.True(wb.IsOk, string.Join("; ", wb.Errors));
        Assert.Equal(MassOperations.SetAttribute, wb.Value!.Operation);

        // A file that asks for something it may not: nothing is stored, the reasons are in words.
        async Task<Outcome<MassPreview>> Try(string name, byte[] bytes, string? kind = "part", string? op = MassOperations.SetAttribute) =>
            await e.Mass.PreviewUploadAsync(Alice, new MassUploadRequest(name, bytes, kind, op, null, null), null);
        var unknownColumn = await Try("a.csv", Csv("Part,Name,Value,Markings", $"{p1},material,x,ITAR"));
        Assert.Equal(OutcomeKind.Invalid, unknownColumn.Kind);
        Assert.Contains("not one of the columns", unknownColumn.Errors[0], StringComparison.Ordinal);
        Assert.Contains("listed twice", (await Try("a.csv", Csv("Part,Name,Value", $"{p1},material,x", $"{p1},material,y"))).Errors[0], StringComparison.Ordinal);
        Assert.Contains("positive number", (await Try("a.csv", Csv("Assembly,Component,Quantity", $"{p1},{p2},zero"), "assembly", MassOperations.AddComponent)).Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await Try("a.csv", Csv("Part", p1), "part", null)).Kind); // no operation anywhere
        Assert.Contains("you chose", string.Join(" ", (await Try("a.csv", Csv("Part,Kind", $"{p1},document"), "part")).Errors), StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await Try("a.csv", Encoding.UTF8.GetBytes("Part\r\n" + string.Join("\r\n", Enumerable.Range(1, 501).Select(i => $"P-{i:D6}"))), "part", MassOperations.Favorite)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await Try("a.xlsx", Encoding.UTF8.GetBytes("not a workbook"))).Kind);
        Assert.Contains(await e.Audit(), r => r.Action == "output.mass.upload" && r.Outcome == AuditOutcome.Failure);

        // Per-row BOM values: each assembly gets its own replacement.
        var bolt = await e.Part("Bolt"); var s1 = await e.Part("Screw one"); var s2 = await e.Part("Screw two");
        var a1 = await e.Assembly("Pod one", null, bolt); var a2 = await e.Assembly("Pod two", null, bolt);
        var bom = await e.Mass.PreviewUploadAsync(Alice, new MassUploadRequest("bom.csv", Csv("Assembly,Component,Replacement", $"{a1},{bolt},{s1}", $"{a2},{bolt},{s2}"), "assembly", MassOperations.ReplaceComponent, null, null), null);
        Assert.True(bom.IsOk, string.Join("; ", bom.Errors));
        Assert.True((await e.Mass.ApplyAsync(Alice, bom.Value!.Number, null)).IsOk);
        Assert.Contains(await e.Lines(a1), l => l.Child == s1);
        Assert.Contains(await e.Lines(a2), l => l.Child == s2);
        Assert.True((await e.Mass.UndoAsync(Alice, bom.Value.Number, null)).IsOk);
        Assert.Contains(await e.Lines(a2), l => l.Child == bolt);
    }

    private static async Task<long> Count(Env e, string sql)
    {
        await using var conn = new NpgsqlConnection(e.Api.Database.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task The_database_accepts_the_new_operations_and_kinds_only_and_the_rest_surface_selects_uploads_and_serves_templates()
    {
        using var e = await NewEnv();
        await Assert.ThrowsAnyAsync<PostgresException>(() => e.Api.Database.ExecuteAsSuperuserAsync(
            "INSERT INTO rpt.mass_change (id, number, correlation_id, operation, object_kind, parameters, status, created_at, created_by, created_by_sub) VALUES (gen_random_uuid(), 'X-1', 'c', 'delete_everything', 'part', '{}', 'previewed', now(), 'a', 'a')"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => e.Api.Database.ExecuteAsSuperuserAsync(
            "INSERT INTO rpt.mass_change (id, number, correlation_id, operation, object_kind, parameters, status, created_at, created_by, created_by_sub) VALUES (gen_random_uuid(), 'X-2', 'c', 'favorite', 'widget', '{}', 'previewed', now(), 'a', 'a')"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => e.Api.Database.ExecuteAsSuperuserAsync(
            "INSERT INTO rpt.mass_change (id, number, correlation_id, operation, object_kind, parameters, status, created_at, created_by, created_by_sub, source) VALUES (gen_random_uuid(), 'X-3', 'c', 'favorite', 'part', '{}', 'previewed', now(), 'a', 'a', 'telepathy')"));

        var bolt = await e.Part("Bolt");
        var asm = await e.Assembly("Pod", null, bolt);
        var client = e.Api.ClientFor(ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]));
        var sel = JsonNode.Parse(await client.GetStringAsync($"/api/v1/output/mass/select?kind=assembly&selector=where_used&value={bolt}"))!;
        Assert.Equal(asm, sel["keys"]![0]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/output/mass/select?kind=assembly&selector=where_used&value=P-999999")).StatusCode);
        var template = await client.GetAsync("/api/v1/output/mass/template.csv?kind=assembly&operation=replace_component");
        Assert.Equal(HttpStatusCode.OK, template.StatusCode);
        Assert.Equal("assembly,component,replacement,quantity,unit,notes", (await template.Content.ReadAsStringAsync()).TrimStart('﻿').Trim());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/output/mass/template.csv?kind=nope&operation=favorite")).StatusCode);

        var body = new ByteArrayContent(Csv("Assembly,Component,Replacement", $"{asm},{bolt},{await e.Part("Screw")}"));
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        var up = await client.PostAsync("/api/v1/output/mass/upload?fileName=b.csv&kind=assembly&operation=replace_component", body);
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var created = JsonNode.Parse(await up.Content.ReadAsStringAsync())!;
        Assert.Equal("upload", created["source"]!.GetValue<string>());
        var typed = await client.PostAsync("/api/v1/output/mass/preview", JsonContent.Create(new { objectKind = "assembly", keys = new[] { asm }, operation = "remove_component", parameters = new { component = bolt }, changeNumber = "ECN-999999" }));
        Assert.Equal(HttpStatusCode.BadRequest, typed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await e.Api.CreateClient().GetAsync("/api/v1/output/mass/select?kind=assembly&selector=where_used&value=x")).StatusCode);
    }
}
