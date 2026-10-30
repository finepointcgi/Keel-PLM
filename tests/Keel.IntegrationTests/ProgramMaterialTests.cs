using Keel.Api.Infrastructure.Programs;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Parts;
using Keel.Programs;
using Keel.Sourcing;

namespace Keel.IntegrationTests;

/// <summary>
/// Material cost of program end items (9.5, ADR-0049, A60) through the real BOM and sourcing services of the host: leaf parts of the released structure priced from active preferred or approved
/// entries, price breaks, alternates and other currencies ignored, parts the reader may not read left out and counted, nothing stored.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProgramMaterialTests(PostgresFixture pg)
{
    private static readonly Subject Carla = WorkflowEnv.User("carla", [Roles.Viewer, Roles.Author, Roles.ProgramCost]);
    private static readonly Subject LenaCost = WorkflowEnv.Lena with { Roles = WorkflowEnv.Lena.Roles.Add(Roles.ProgramCost) };

    private sealed record Env(SourcingEnv S, PartService Parts, BomService Bom, ProgramService P, ProgramMaterialSource Source, string Assembly, string Leaf1, string Leaf2, string LeafItar);

    private async Task<Env> SeedAsync()
    {
        var s = await SourcingEnv.CreateAsync(pg);
        var audit = new PostgresAuditLog(s.Ds, s.Clock);
        var parts = new PartService(s.Ds, audit, s.Clock, [new BomParticipant()]);
        var bom = new BomService(s.Ds, audit, s.Clock);
        var source = new ProgramMaterialSource(bom, s.Sourcing);
        var p = ProgramModule.Create(s.Ds, audit, s.Clock, [new PartObjectAccess(s.Ds)], null, null, source);

        var leaf1 = await s.NewPartAsync("Bracket, sensor");
        var leaf2 = await s.NewPartAsync("Fastener kit");
        var leafItar = await s.NewPartAsync("Restricted housing", SourcingEnv.Itar());
        var asm = (await parts.CreateAsync(SourcingEnv.Alice, new CreatePartCommand("Sensor pod", "Assembly", "EA", "Make", SourcingEnv.Open), null)).Value!.Number;
        Assert.True((await parts.CheckOutAsync(SourcingEnv.Alice, asm, null)).IsOk);
        foreach (var (child, qty) in new[] { (leaf1, 2m), (leaf2, 4m), (leafItar, 1m) })
            Assert.True((await bom.AddUsageAsync(SourcingEnv.Alice, asm, new AddUsageCommand(child, qty, null, null, null), null)).IsOk);
        Assert.True((await parts.CheckInAsync(SourcingEnv.Alice, asm, "structure", null)).IsOk);
        foreach (var n in new[] { leaf1, leaf2, leafItar, asm })
        {
            var r = await parts.SetStateAsync(SourcingEnv.Ada, n, "Released", "test", true, null);
            Assert.True(r.IsOk, $"{n}: {string.Join("; ", r.Errors)}");
        }
        var supplier = await s.NewSupplierAsync();
        await s.ApproveSupplierAsync(supplier);
        await PricedAsync(s, leaf1, supplier, EntryStatuses.Preferred, "USD", new PriceBreak(1, 10m), new PriceBreak(100, 8m));
        await PricedAsync(s, leaf2, supplier, EntryStatuses.Approved, "USD", new PriceBreak(500, 5m));
        await PricedAsync(s, leafItar, supplier, EntryStatuses.Approved, "USD", new PriceBreak(1, 300m));
        var p1 = await s.Parts.CreateAsync(SourcingEnv.Alice, new CreatePartCommand("Unpriced part", "Component", "EA", "Buy", SourcingEnv.Open), null);
        Assert.True(p1.IsOk);
        return new Env(s, parts, bom, p, source, asm, leaf1, leaf2, leafItar);
    }

    private static async Task PricedAsync(SourcingEnv s, string part, string supplier, string status, string currency, params PriceBreak[] breaks)
    {
        var r = await s.Sourcing.CreateEntryAsync(SourcingEnv.Alice, new CreateEntryCommand(SourceKinds.Avl, part, null, supplier, null, null, status, Currency: currency, PriceBreaks: breaks), null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        await s.ReviewAsync("source_entry", r.Value!.Number);
    }

    private static async Task<ProgramDetail> ProgramAsync(Env e)
    {
        var markings = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG", program: "ORCA").Value;
        var r = await e.P.CreateAsync(SourcingEnv.Alice, new CreateProgramCommand("ORCA", "Orca", null, null, null, "alice", new DateOnly(2027, 3, 1), new DateOnly(2028, 3, 1), markings), "c");
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        return r.Value!;
    }

    [Fact]
    public async Task The_estimate_prices_the_leaf_parts_with_price_breaks_and_counts_what_it_cannot_price_or_the_reader_may_not_read()
    {
        var e = await SeedAsync();
        // 50 pods: leaf 1 needs 100 (the 100 break, 8.00), leaf 2 needs 200 (below its first break of 500: 5.00), the restricted housing 50 at 300
        var all = await e.Source.EstimateAsync(Carla, e.Assembly, 50, new DateOnly(2027, 3, 1), "c");
        Assert.True(all.IsOk, string.Join("; ", all.Errors));
        Assert.Equal(100 * 8m + 200 * 5m + 50 * 300m, all.Value!.Total);
        Assert.Equal((3, 0, 0), (all.Value.PricedLines, all.Value.UnpricedLines, all.Value.RestrictedLines));
        Assert.Contains(all.Value.Lines, l => l.PartNumber == e.Leaf1 && l.UnitPrice == 8m && l.Basis.Contains("price break from 100", StringComparison.Ordinal));
        Assert.Contains(all.Value.Lines, l => l.PartNumber == e.Leaf2 && l.UnitPrice == 5m && l.Basis.Contains("below the first price break", StringComparison.Ordinal));
        // a smaller build uses the first break of leaf 1
        Assert.Equal(2 * 10m + 4 * 5m + 300m, (await e.Source.EstimateAsync(Carla, e.Assembly, 1, new DateOnly(2027, 3, 1), "c")).Value!.Total);

        // a foreign reader without authorization cannot read the ITAR housing: its structure line is restricted, it is in no total, and its price appears nowhere
        var lena = await e.Source.EstimateAsync(LenaCost, e.Assembly, 50, new DateOnly(2027, 3, 1), "c");
        Assert.True(lena.IsOk);
        Assert.Equal(100 * 8m + 200 * 5m, lena.Value!.Total);
        Assert.Equal(1, lena.Value.RestrictedLines);
        Assert.DoesNotContain(lena.Value.Lines, l => l.PartNumber == e.LeafItar);
        Assert.DoesNotContain(lena.Value.Markings, m => m.ExportJurisdiction == ExportJurisdiction.Itar);
        // an unknown part, and an unreadable one, are the same refusal
        Assert.Equal(OutcomeKind.NotFound, (await e.Source.EstimateAsync(Carla, "P-999999", 1, new DateOnly(2027, 3, 1), "c")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await e.Source.EstimateAsync(WorkflowEnv.User("eve", [Roles.Viewer, Roles.ProgramCost], programs: [], cui: []), e.LeafItar, 1, new DateOnly(2027, 3, 1), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await e.Source.EstimateAsync(Carla, e.Assembly, 0, new DateOnly(2027, 3, 1), "c")).Kind);
    }

    [Fact]
    public async Task Alternates_disapproved_entries_and_other_currencies_are_never_used_and_a_part_without_a_price_is_listed_unpriced()
    {
        var e = await SeedAsync();
        var cheap = await e.S.NewPartAsync("Cheap alternate only");
        var euro = await e.S.NewPartAsync("Priced in euros");
        var supplier = await e.S.NewSupplierAsync("Beta Supply");
        await e.S.ApproveSupplierAsync(supplier);
        await PricedAsync(e.S, cheap, supplier, EntryStatuses.Alternate, "USD", new PriceBreak(1, 1m));
        await PricedAsync(e.S, euro, supplier, EntryStatuses.Preferred, "EUR", new PriceBreak(1, 2m));
        foreach (var n in new[] { cheap, euro }) Assert.True((await e.Parts.SetStateAsync(SourcingEnv.Ada, n, "Released", "test", true, null)).IsOk);
        var asm = (await e.Parts.CreateAsync(SourcingEnv.Alice, new CreatePartCommand("Odd pod", "Assembly", "EA", "Make", SourcingEnv.Open), null)).Value!.Number;
        Assert.True((await e.Parts.CheckOutAsync(SourcingEnv.Alice, asm, null)).IsOk);
        foreach (var child in new[] { cheap, euro }) Assert.True((await e.Bom.AddUsageAsync(SourcingEnv.Alice, asm, new AddUsageCommand(child, 1, null, null, null), null)).IsOk);
        Assert.True((await e.Parts.CheckInAsync(SourcingEnv.Alice, asm, "structure", null)).IsOk);
        Assert.True((await e.Parts.SetStateAsync(SourcingEnv.Ada, asm, "Released", "test", true, null)).IsOk);
        var r = await e.Source.EstimateAsync(Carla, asm, 10, new DateOnly(2027, 3, 1), "c");
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        Assert.Equal((0m, 0, 2), (r.Value!.Total, r.Value.PricedLines, r.Value.UnpricedLines));
        Assert.Contains(r.Value.Lines, l => l.PartNumber == euro && l.Basis == "Priced in another currency");
        Assert.Contains(r.Value.Lines, l => l.PartNumber == cheap && l.Basis == "No preferred or approved price");
    }

    [Fact]
    public async Task A_material_line_is_costed_live_in_the_program_cost_for_readers_with_the_permission_and_stores_no_price()
    {
        var e = await SeedAsync();
        await ProgramAsync(e);
        var add = await e.P.AddMaterialLineAsync(Carla, "ORCA", new AddMaterialLineCommand(e.Assembly, 50, new DateOnly(2027, 6, 15), "First build"), "c");
        Assert.True(add.IsOk, string.Join("; ", add.Errors));
        Assert.Equal("ML-001", add.Value!.Key);
        Assert.Equal(OutcomeKind.Invalid, (await e.P.AddMaterialLineAsync(Carla, "ORCA", new AddMaterialLineCommand("P-999999", 1, new DateOnly(2027, 6, 15), null), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await e.P.AddMaterialLineAsync(Carla, "ORCA", new AddMaterialLineCommand(e.Assembly, 0, new DateOnly(2027, 6, 15), null), "c")).Kind);

        var cost = (await e.P.GetCostAsync(Carla, "ORCA", new DateOnly(2027, 3, 10), "c")).Value!;
        var line = Assert.Single(cost.Material);
        Assert.Equal(100 * 8m + 200 * 5m + 50 * 300m, line.Estimate);
        Assert.Equal((3, 0, 0), (line.PricedLines, line.UnpricedLines, line.RestrictedLines));
        Assert.Equal(16800m, cost.ByType[CostTypes.Material].Planned);
        Assert.Equal(16800m, cost.ByType[CostTypes.Material].Etc); // needed after the status date
        Assert.Contains("ITAR", cost.Banner, StringComparison.Ordinal); // the restricted housing went into the estimate for a reader who may read it

        // after the status date has passed the line is assumed to be in the actuals
        Assert.Equal(0m, (await e.P.GetCostAsync(Carla, "ORCA", new DateOnly(2027, 7, 1), "c")).Value!.ByType[CostTypes.Material].Etc);

        // the same program for a reader who may not read the housing: a smaller estimate, a restricted count, no ITAR banner
        var lena = (await e.P.GetCostAsync(LenaCost, "ORCA", new DateOnly(2027, 3, 10), "c")).Value!;
        Assert.Equal(1800m, lena.Material.Single().Estimate);
        Assert.Equal(1, lena.Material.Single().RestrictedLines);
        Assert.DoesNotContain("ITAR", lena.Banner, StringComparison.Ordinal);

        // removing the line removes it from the cost; the row stays (marked removed) for the audit trail
        Assert.True((await e.P.RemoveMaterialLineAsync(Carla, "ORCA", "ML-001", "c")).IsOk);
        Assert.Empty((await e.P.GetCostAsync(Carla, "ORCA", null, "c")).Value!.Material);
        Assert.Equal(OutcomeKind.NotFound, (await e.P.RemoveMaterialLineAsync(Carla, "ORCA", "ML-001", "c")).Kind);
        Assert.Equal(1L, await e.S.ScalarAsync("SELECT count(*) FROM prg.material_line WHERE removed_at IS NOT NULL"));
    }
}
