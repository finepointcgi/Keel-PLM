using System.Collections.Immutable;
using System.Text;
using Keel.Api.Infrastructure;
using Keel.Audit;
using Keel.Bom;
using Keel.Cad;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Parts;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// The CAD services over a real PostgreSQL with the real document, part and BOM services (ADR-0035). Documents carry no files here (the vault is not
/// needed for sets, dependencies, associations and proposals); byte handling is covered by <see cref="CadApiTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CadServiceTests(PostgresFixture pg)
{
    [Fact]
    public async Task Packages_staleness_associations_and_proposals_behave_and_never_leak()
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var log = new PostgresAuditLog(ds, TimeProvider.System);
        var vault = new ObjectVault(new VaultOptions { ServiceUrl = "http://127.0.0.1:1", AccessKey = "x", SecretKey = "y", Bucket = "b" });
        var docs = new DocumentService(ds, log, TimeProvider.System, vault);
        var parts = new PartService(ds, log, TimeProvider.System, [new BomParticipant()]);
        var bom = new BomService(ds, log, TimeProvider.System);
        ICadDocumentPort docPort = new CadDocumentPort(docs);
        var partPort = new CadPartPort(ds, parts, bom);
        var graph = new CadGraphService(ds, log, TimeProvider.System);
        var packages = new CadPackageService(ds, log, TimeProvider.System, docPort);
        var proposals = new CadBomProposalService(ds, log, TimeProvider.System, partPort);
        static Subject User(string n, bool us = true, params string[] roles) => new()
        {
            Id = "sub-" + n, Username = n, DisplayName = n, UsPerson = us, Org = "ENG",
            Programs = ImmutableSortedSet.Create(StringComparer.Ordinal, "ORCA"), CuiCategories = ImmutableSortedSet.Create(StringComparer.Ordinal, "EXPT", "CTI"),
            ExportAuthorizations = ImmutableSortedSet<string>.Empty, Roles = (roles.Length == 0 ? [Roles.Viewer, Roles.Author] : roles).ToImmutableSortedSet(StringComparer.Ordinal),
        };
        var alice = User("alice"); var bob = User("bob"); var lena = User("lena", false); var admin = User("root", true, Roles.Admin, Roles.Author, Roles.Viewer);
        var itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;
        var open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;
        static void Check(bool ok, string what) => Assert.True(ok, what);
        static string? Err<T>(Outcome<T> o) => o.IsOk ? null : $"{o.Kind}: {string.Join(" | ", o.Errors)}";

        var m = (await docs.CreateAsync(alice, new CreateDocumentCommand("Housing model", "CAD", itar), null, null)).Value!.Number;
        var d = (await docs.CreateAsync(alice, new CreateDocumentCommand("Housing drawing", "Drawing", itar), null, null)).Value!.Number;

        // drawing alone, then declare the model
        var s1 = await packages.CheckOutAsync(alice, d, null, null);
        Check(s1.IsOk && s1.Value!.Members.Count == 1, "checkout of the drawing alone " + Err(s1));
        var ci1 = await packages.CheckInAsync(alice, s1.Value!.Id, new PackageCheckInRequest("link", [new CheckInMemberRequest(d, null, [m])]), null);
        Check(ci1.IsOk && ci1.Value!.SetClosed, "check-in with a declared reference " + Err(ci1));

        var s2 = await packages.CheckOutAsync(alice, m, null, null);
        Check(s2.IsOk && s2.Value!.Members.Count == 2 && s2.Value.Members.Any(x => x.Number == d && x.CadRole == "drawing"), "model package brings the drawing " + Err(s2));
        var denied = await packages.CheckOutAsync(bob, d, null, null);
        Check(denied.Kind == OutcomeKind.Conflict && denied.Errors[0].StartsWith("cad.locked"), "second user is locked out with cad.locked: " + Err(denied));
        var lenaTry = await packages.CheckOutAsync(lena, m, null, null);
        Check(lenaTry.Kind == OutcomeKind.NotFound, "non-US person sees nothing: " + lenaTry.Kind);
        var lenaView = await graph.GetPackageAsync(lena, m, 5, null);
        Check(lenaView.Kind == OutcomeKind.NotFound, "non-US person has no package view");

        var ci2 = await packages.CheckInAsync(alice, s2.Value!.Id, new PackageCheckInRequest("model changed"), null);
        Check(ci2.IsOk && ci2.Value!.Created.Count == 2, "set check-in creates two iterations " + Err(ci2));
        var view = await graph.GetPackageAsync(alice, d, 5, null);
        Check(view.IsOk && view.Value!.StaleCount == 1 && view.Value.Uses[0].Stale, "drawing is stale after the model iterated " + Err(view));
        var stale = await graph.ListStaleAsync(alice, 50, null);
        Check(stale.Items.Count == 1 && stale.Items[0].Number == d, "stale list names the drawing");
        var staleLena = await graph.ListStaleAsync(lena, 50, null);
        Check(staleLena.Items.Count == 0, "stale list is empty for the non-US person");
        var up = await graph.WhereUsedAsync(alice, m, 5, null);
        Check(up.IsOk && up.Value!.Count == 1 && up.Value[0].Number == d, "where-used of the model lists the drawing");

        var s3 = await packages.CheckOutAsync(alice, d, null, null);
        var ci3 = await packages.CheckInAsync(alice, s3.Value!.Id, new PackageCheckInRequest(null, [new CheckInMemberRequest(d, null, [m]), new CheckInMemberRequest(m)], SkipUnchanged: true), null);
        Check(ci3.IsOk && ci3.Value!.Released.Count == 1 && ci3.Value.Created.Count == 1, "skip-unchanged: drawing iterated with references, model released " + Err(ci3));
        var view2 = await graph.GetPackageAsync(alice, d, 5, null);
        Check(view2.IsOk && view2.Value!.StaleCount == 0, "stale cleared after re-declaring the reference");

        var s4 = await packages.CheckOutAsync(alice, m, null, null);
        var undo = await packages.UndoAsync(alice, s4.Value!.Id, null);
        Check(undo.IsOk && undo.Value!.Count == 2, "undo releases both locks " + Err(undo));
        Check((await packages.ListOpenAsync(alice)).Count == 0, "no open sets remain");

        // new member
        var s5 = await packages.CheckOutAsync(alice, m, null, null);
        var add = await packages.AddMemberAsync(alice, s5.Value!.Id, new AddMemberRequest("Section view", "Drawing", "drawing", null, [m]), null);
        Check(add.IsOk, "new member can be added " + Err(add));
        var ci5 = await packages.CheckInAsync(alice, s5.Value.Id, new PackageCheckInRequest("with new drawing", [new CheckInMemberRequest(m), new CheckInMemberRequest(d), new CheckInMemberRequest(add.Value!.Number, null, [m])]), null);
        Check(ci5.IsOk && ci5.Value!.Created.Count == 3, "set with a new member checks in " + Err(ci5));

        // associations
        var p1 = (await parts.CreateAsync(alice, new CreatePartCommand("Housing", "Component", "EA", "Make", itar), null)).Value!.Number;
        var assoc = await graph.AddAssociationAsync(alice, d, p1, "drawing-of", null);
        Check(assoc.IsOk, "drawing-of association " + Err(assoc));
        var forPart = await graph.ListForPartAsync(alice, p1, null);
        Check(forPart.IsOk && forPart.Value!.Count == 1 && forPart.Value[0].Stale, "documents of a part (stale because the model iterated in the same set without the drawing declaring it) " + Err(forPart));
        var forPartLena = await graph.ListForPartAsync(lena, p1, null);
        Check(forPartLena.Kind == OutcomeKind.NotFound, "part hidden from the non-US person");
        var dup = await graph.AddAssociationAsync(alice, d, p1, "drawing-of", null);
        Check(dup.Kind == OutcomeKind.Conflict, "duplicate association is refused");

        // proposals
        var asm = (await parts.CreateAsync(alice, new CreatePartCommand("Gimbal assembly", "Assembly", "EA", "Make", open), null)).Value!.Number;
        var screw = (await parts.CreateAsync(alice, new CreatePartCommand("Screw", "Component", "EA", "Buy", open), null)).Value!.Number;
        var csv = $"parent,child,name,qty,find,refdes\n{asm},{screw},Screw,4,10,S1 S2 S3 S4\n{asm},NEW-WASHER,Washer,4,20,\n";
        var created = await proposals.CreateAsync(alice, new CreateProposalRequest("Gimbal from CAD", "csv", open), Encoding.UTF8.GetBytes(csv), null);
        Check(created.IsOk, "csv proposal created " + Err(created));
        var pr = created.Value!;
        Check(pr.Parts.Count(p => p.MatchKind == "number") == 2 && pr.Parts.Count(p => p.MatchKind == "new") == 1, "two matched by number, one new");
        Check(pr.Diff.Count(r => r.Kind == DiffKind.Added) == 2 && pr.CanApply, "diff shows two additions and is applicable");
        var lenaProp = await proposals.GetAsync(lena, pr.Number, null);
        var openLena = await proposals.GetAsync(lena, "CBP-999999", null);
        Check(lenaProp.Kind == OutcomeKind.NotFound || lenaProp.IsOk, "open proposal readable (open markings)");
        var itarProp = await proposals.CreateAsync(alice, new CreateProposalRequest("Secret", "csv", itar), Encoding.UTF8.GetBytes(csv), null);
        Check(itarProp.IsOk, "itar proposal created " + Err(itarProp));
        var lenaItar = await proposals.GetAsync(lena, itarProp.Value!.Number, null);
        Check(lenaItar.Kind == OutcomeKind.NotFound && openLena.Kind == OutcomeKind.NotFound, "marked proposal is a 404 for the non-US person, like a missing one");
        var lenaList = await proposals.ListAsync(lena, null, 50, 0, null);
        Check(lenaList.Items.All(i => i.Number != itarProp.Value.Number), "marked proposal missing from her list");
        var viewer = User("vic", true, Roles.Viewer);
        var viewerApply = await proposals.ApplyAsync(viewer, pr.Number, null);
        Check(viewerApply.Kind == OutcomeKind.Forbidden, "a viewer cannot apply " + viewerApply.Kind);

        var applied = await proposals.ApplyAsync(alice, pr.Number, null);
        Check(applied.IsOk, "apply " + Err(applied));
        var after = await partPort.GetBomAsync(alice, asm, null);
        Check(after.IsOk && after.Value!.Lines.Count == 2 && after.Value.Lines.Any(l => l.ChildNumber == screw && l.Quantity == 4 && l.ReferenceDesignators.Count == 4), "BOM now has the two lines " + Err(after));
        var again = await proposals.GetAsync(alice, pr.Number, null);
        Check(again.Value!.Status == "applied", "status applied");
        var reapply = await proposals.ApplyAsync(alice, pr.Number, null);
        Check(reapply.Kind == OutcomeKind.Conflict, "applied proposal cannot be applied again");

        // second proposal changes quantity, then a released parent blocks it
        var csv2 = $"parent,child,qty,find\n{asm},{screw},6,10\n";
        var p2 = (await proposals.CreateAsync(alice, new CreateProposalRequest("Change qty", "csv", open), Encoding.UTF8.GetBytes(csv2), null)).Value!;
        Check(p2.Diff.Count(r => r.Kind == DiffKind.Changed) == 1 && p2.Diff.Count(r => r.Kind == DiffKind.Removed) == 1 && !p2.Diff.First(r => r.Kind == DiffKind.Removed).Applies, "changed shown, removal kept without applyRemovals");
        Check((await parts.SetStateAsync(admin, asm, "Released", "test", true, null)).IsOk, "released the assembly");
        var blocked = await proposals.ApplyAsync(alice, p2.Number, null);
        Check(blocked.Kind == OutcomeKind.Conflict && blocked.Errors[0].Contains("nothing was changed"), "released parent blocks apply: " + Err(blocked));
        var after2 = await partPort.GetBomAsync(alice, asm, null);
        Check(after2.Value!.Lines.First(l => l.ChildNumber == screw).Quantity == 4, "BOM unchanged by the blocked apply");

        // decisions: ambiguous attribute match
        var ambJson = $$"""{"schema":"keel.cad.structure/v1","root":"a","parts":[{"ref":"a","number":"{{asm}}","name":"A"},{"ref":"b","name":"Mystery"}],"lines":[{"parent":"a","child":"b","quantity":1}]}""";
        var amb = await proposals.CreateAsync(alice, new CreateProposalRequest("Json", "json", open), Encoding.UTF8.GetBytes(ambJson), null);
        Check(amb.IsOk && amb.Value!.Parts.Any(p => p.Ref == "b" && p.Decision == "create"), "json proposal with a new part " + Err(amb));
        var skip = await proposals.DecideAsync(alice, amb.Value!.Number, [new ProposalPartDecision("b", "skip")], null, null);
        Check(skip.IsOk && skip.Value!.Diff.All(r => r.Kind != DiffKind.Added || !r.Applies), "skipping a part drops its lines " + Err(skip));
        var bad = await proposals.CreateAsync(alice, new CreateProposalRequest("x", "csv", open), Encoding.UTF8.GetBytes("child,qty\nA,1\n"), null);
        Check(bad.Kind == OutcomeKind.Invalid && bad.Errors[0].StartsWith("cad.proposal"), "invalid csv is a protocol error " + Err(bad));

        // audit does not carry names
        await using (var c = await ds.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM audit.event WHERE action LIKE 'cad.%' AND (payload::text LIKE '%Gimbal%' OR payload::text LIKE '%Housing%' OR payload::text LIKE '%Washer%')", c))
            Check((long)(await cmd.ExecuteScalarAsync())! == 0, "no part or document names in cad audit payloads");
    }
}
