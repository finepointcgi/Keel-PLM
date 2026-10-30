using System.Collections.Immutable;
using Keel.Api.Infrastructure;
using Keel.Audit;
using Keel.Bom;
using Keel.Cad;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Parts;

namespace Keel.IntegrationTests;

/// <summary>
/// A REAL STEP assembly (written by Open CASCADE, tools/freecad-keel/make_fixtures.py) through the structure reader, the proposal service and the real
/// part and BOM services: the BOM that results has the quantities of the CAD model (ADR-0047). Fixtures: tests/Keel.UnitTests/Fixtures/cad.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CadStepProposalTests(PostgresFixture pg)
{
    [Theory]
    [InlineData("frame-asm-ap214.step")]
    [InlineData("frame-asm-ap242.step")]
    public async Task A_real_step_assembly_becomes_a_reviewed_bom_with_the_quantities_of_the_model(string file)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var log = new PostgresAuditLog(ds, TimeProvider.System);
        var parts = new PartService(ds, log, TimeProvider.System, [new BomParticipant()]);
        var bom = new BomService(ds, log, TimeProvider.System);
        var partPort = new CadPartPort(ds, parts, bom);
        var proposals = new CadBomProposalService(ds, log, TimeProvider.System, partPort);
        var alice = new Subject
        {
            Id = "sub-alice", Username = "alice", DisplayName = "alice", UsPerson = true, Org = "ENG",
            Programs = ImmutableSortedSet.Create(StringComparer.Ordinal, "ORCA"), CuiCategories = ImmutableSortedSet<string>.Empty,
            ExportAuthorizations = ImmutableSortedSet<string>.Empty, Roles = ImmutableSortedSet.Create(StringComparer.Ordinal, Roles.Viewer, Roles.Author),
        };
        var open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cad", file));

        var created = await proposals.CreateAsync(alice, new CreateProposalRequest("Frame from STEP", "step", open), bytes, null);
        Assert.True(created.IsOk, string.Join(" | ", created.Errors));
        var proposal = created.Value!;
        Assert.Equal(6, proposal.Parts.Count);
        Assert.All(proposal.Parts, p => Assert.Equal("new", p.MatchKind));
        Assert.Equal(6, proposal.Lines.Count);

        var decided = await proposals.DecideAsync(alice, proposal.Number, [.. proposal.Parts.Select(p => new ProposalPartDecision(p.Ref, "create"))], null, null);
        Assert.True(decided.IsOk, string.Join(" | ", decided.Errors));
        Assert.True(decided.Value!.CanApply);
        var applied = await proposals.ApplyAsync(alice, proposal.Number, null);
        Assert.True(applied.IsOk, string.Join(" | ", applied.Errors));

        var after = (await proposals.GetAsync(alice, proposal.Number, null)).Value!;
        Assert.Equal("applied", after.Status);
        string NumberOf(string cadNumber) => after.Parts.Single(p => p.CadNumber == cadNumber).CreatedNumber!;

        var frame = (await partPort.GetBomAsync(alice, NumberOf("KA-3000"), null)).Value!;
        Assert.Equal(3, frame.Lines.Count);
        Assert.Equal(2m, frame.Lines.Single(l => l.ChildNumber == NumberOf("KA-3001")).Quantity);
        Assert.Equal(1m, frame.Lines.Single(l => l.ChildNumber == NumberOf("KP-1002")).Quantity);
        Assert.Equal(2m, frame.Lines.Single(l => l.ChildNumber == NumberOf("KF-2001")).Quantity);
        var bracketAsm = (await partPort.GetBomAsync(alice, NumberOf("KA-3001"), null)).Value!;
        Assert.Equal(3, bracketAsm.Lines.Count);
        Assert.Equal(4m, bracketAsm.Lines.Single(l => l.ChildNumber == NumberOf("KF-2001")).Quantity);
        Assert.Equal(4m, bracketAsm.Lines.Single(l => l.ChildNumber == NumberOf("KF-2002")).Quantity);
        Assert.Equal(1m, bracketAsm.Lines.Single(l => l.ChildNumber == NumberOf("KP-1001")).Quantity);
    }
}
