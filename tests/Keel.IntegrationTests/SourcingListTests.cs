using Keel.Audit;
using Keel.Core;
using Keel.Sourcing;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Manufacturer parts, the approved manufacturer and vendor lists, their approval, coverage, relations, queries and the export-control rule (ADR-0037).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SourcingListTests(PostgresFixture pg)
{
    private static Subject Alice => SourcingEnv.Alice;

    [Fact]
    public async Task A_foreign_supplier_is_never_selected_for_an_ITAR_marked_part_in_the_service_or_the_database()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var itarPart = await env.NewPartAsync("Housing", SourcingEnv.Itar());
        var openPart = await env.NewPartAsync("Washer");
        var foreign = await env.NewSupplierAsync("Berlin Parts GmbH", "DEU", foreign: true);
        var domestic = await env.NewSupplierAsync("Alpine Machining");

        // The service refuses, says why, and audits the denial.
        var refused = await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Avl, itarPart, null, foreign, null, null, EntryStatuses.Approved), "corr-foreign");
        Assert.Equal(OutcomeKind.Forbidden, refused.Kind);
        Assert.Equal([SourcingRules.ForeignSupplierItarPart], refused.Errors);
        var refusedAml = await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Aml, itarPart, null, foreign, "DE-1", null, EntryStatuses.Approved), null);
        Assert.Equal(OutcomeKind.Forbidden, refusedAml.Kind);
        // A foreign vendor of a domestic manufacturer's part is refused too when the manufacturer is foreign.
        var viaForeignManufacturer = await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Avl, itarPart, null, domestic, "DE-1", foreign, EntryStatuses.Approved), null);
        Assert.Equal(OutcomeKind.Forbidden, viaForeignManufacturer.Kind);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "sourcing.entry_create" && e.Outcome == AuditOutcome.Denied && e.Payload.Contains(SourcingRules.ForeignSupplierItarPart, StringComparison.Ordinal));
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM src.source_entry"));

        // The same supplier is fine for a part that is not ITAR-marked, and a domestic one is fine for the ITAR part.
        Assert.True((await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Avl, openPart, null, foreign, null, null, EntryStatuses.Approved), null)).IsOk);
        Assert.True((await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Avl, itarPart, null, domestic, null, null, EntryStatuses.Approved), null)).IsOk);

        // Defense in depth: even a statement that bypasses the service (here with full database rights) is refused by the database.
        await using var conn = new NpgsqlConnection(env.Db.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO src.source_entry (id, number, kind, part_master_id, supplier_id, status, approval_state, created_at, created_by)
            SELECT gen_random_uuid(), 'SRC-BYPASS', 'avl', m.id, s.id, 'approved', 'Draft', now(), 'x' FROM plm.part_master m, src.supplier s WHERE m.number = @p AND s.number = @s
            """, conn);
        cmd.Parameters.AddWithValue("p", itarPart);
        cmd.Parameters.AddWithValue("s", foreign);
        var e = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", e.SqlState);
    }

    [Fact]
    public async Task A_list_entry_goes_through_a_review_and_only_an_active_entry_in_its_dates_counts()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var part = await env.NewPartAsync("Bracket");
        var supplier = await env.NewSupplierAsync();
        await env.ApproveSupplierAsync(supplier);

        var draft = await env.DraftEntryAsync(SourceKinds.Aml, part, supplier, EntryStatuses.Preferred, "ACME-77");
        Assert.Equal(EntryApprovalStates.Draft, draft.ApprovalState);
        Assert.Equal(CoverageBuckets.NotCovered, (await Coverage(env, part)).Bucket); // a draft is not a source
        var edited = await env.Sourcing.UpdateEntryAsync(Alice, draft.Number, new UpdateEntryCommand(EntryStatuses.Approved, null, null, "V-1", 14, 100, "USD", [new PriceBreak(100, 2.5m), new PriceBreak(1000, 2.1m)], "Lead time firm"), draft.Revision, null);
        Assert.True(edited.IsOk, string.Join("; ", edited.Errors));
        Assert.Equal(2, edited.Value!.PriceBreaks.Count);
        Assert.Equal(OutcomeKind.Conflict, (await env.Sourcing.UpdateEntryAsync(Alice, draft.Number, new UpdateEntryCommand(EntryStatuses.Approved, null, null, null, null, null, null, null, null), draft.Revision, null)).Kind); // stale

        await env.ReviewAsync("source_entry", draft.Number);
        var active = (await env.Sourcing.GetEntryAsync(Alice, draft.Number, null)).Value!;
        Assert.Equal(EntryApprovalStates.Active, active.ApprovalState);
        Assert.True(active.Effective);
        Assert.Equal(CoverageBuckets.Covered, (await Coverage(env, part)).Bucket);
        // An active entry is immutable (except its end date and withdrawal).
        Assert.Equal(OutcomeKind.Conflict, (await env.Sourcing.UpdateEntryAsync(Alice, draft.Number, new UpdateEntryCommand(EntryStatuses.Alternate, null, null, null, null, null, null, null, null), active.Revision, null)).Kind);
        // Ending it stops it counting after that day.
        var ended = await env.Sourcing.EndEntryAsync(Alice, draft.Number, DateOnly.FromDateTime(env.Clock.GetUtcNow().UtcDateTime).AddDays(-1), "Contract ended", null);
        Assert.True(ended.IsOk, string.Join("; ", ended.Errors));
        Assert.False(ended.Value!.Effective);
        Assert.Equal(CoverageBuckets.NotCovered, (await Coverage(env, part)).Bucket);

        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "sourcing.entry_approve" && e.ObjectType == "part" && e.ObjectId == part);
        Assert.Contains(audit, e => e.Action == "sourcing.entry_end");
    }

    [Fact]
    public async Task Coverage_distinguishes_approved_conditional_and_missing_sources()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var supplier = await env.NewSupplierAsync();
        await env.ApproveSupplierAsync(supplier);
        var conditional = await env.NewSupplierAsync("Conditional Co");
        await env.ApproveSupplierAsync(conditional, SupplierStatuses.Conditional);
        var prospective = await env.NewSupplierAsync("Prospective Co");

        var a = await env.NewPartAsync("Approved source");
        var b = await env.NewPartAsync("Alternate only");
        var c = await env.NewPartAsync("Conditional supplier");
        var d = await env.NewPartAsync("No entry");
        var e = await env.NewPartAsync("Disapproved entry");
        var f = await env.NewPartAsync("Prospective supplier entry");
        var g = await env.NewPartAsync("Revision scoped");
        await env.ActiveEntryAsync(SourceKinds.Avl, a, supplier, EntryStatuses.Preferred, null);
        await env.ActiveEntryAsync(SourceKinds.Avl, b, supplier, EntryStatuses.Alternate, null);
        await env.ActiveEntryAsync(SourceKinds.Avl, c, conditional, EntryStatuses.Approved, null);
        await env.ActiveEntryAsync(SourceKinds.Avl, e, supplier, EntryStatuses.Disapproved, null);
        await env.ActiveEntryAsync(SourceKinds.Avl, g, supplier, EntryStatuses.Approved, null, revision: "A"); // scoped to revision A; the BOM line asks for B
        var draft = await env.DraftEntryAsync(SourceKinds.Avl, f, prospective, EntryStatuses.Approved, null);
        // A prospective supplier's entry cannot even be submitted.
        var started = await env.Flow.StartAsync(Alice, new Keel.Workflow.StartReviewCommand("source_entry", draft.Number, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null);
        Assert.Equal(OutcomeKind.Conflict, started.Kind);

        var map = (await env.Sourcing.CoverageAsync(Alice, new Dictionary<string, string?> { [a] = "A", [b] = "A", [c] = "A", [d] = "A", [e] = "A", [f] = "A", [g] = "B", ["P-999999"] = null }, null)).Value!;
        Assert.Equal(CoverageBuckets.Covered, map[a].Bucket);
        Assert.True(map[a].Preferred);
        Assert.Equal(CoverageBuckets.Conditional, map[b].Bucket);
        Assert.Equal(CoverageBuckets.Conditional, map[c].Bucket);
        Assert.Equal(CoverageBuckets.NotCovered, map[d].Bucket);
        Assert.Equal(CoverageBuckets.NotCovered, map[e].Bucket);
        Assert.Equal(CoverageBuckets.NotCovered, map[f].Bucket);
        Assert.Equal(CoverageBuckets.NotCovered, map[g].Bucket);
        Assert.False(map.ContainsKey("P-999999"));
        Assert.Equal("Buy", map[a].Source);

        // An expired certification demotes an otherwise approved supplier to conditional.
        env.Clock.Advance(TimeSpan.FromDays(400));
        Assert.Equal(CoverageBuckets.Conditional, (await Coverage(env, a)).Bucket);
    }

    [Fact]
    public async Task Coverage_and_part_sourcing_never_reveal_parts_or_suppliers_the_caller_cannot_read()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var openSupplier = await env.NewSupplierAsync("Open Co");
        var restrictedSupplier = await env.NewSupplierAsync("Restricted Co", markings: SourcingEnv.Cui());
        await env.ApproveSupplierAsync(openSupplier);
        await env.ApproveSupplierAsync(restrictedSupplier);
        var itarPart = await env.NewPartAsync("Secret housing", SourcingEnv.Itar());
        var openPart = await env.NewPartAsync("Washer");
        await env.ActiveEntryAsync(SourceKinds.Avl, itarPart, openSupplier, EntryStatuses.Preferred, null);
        await env.ActiveEntryAsync(SourceKinds.Avl, openPart, restrictedSupplier, EntryStatuses.Preferred, null);

        // Eve (no program) cannot read the ITAR part: not found, and the part is absent from a coverage answer, not "uncovered".
        Assert.Equal(OutcomeKind.NotFound, (await env.Sourcing.GetPartSourcingAsync(SourcingEnv.Eve, itarPart, null)).Kind);
        var coverage = (await env.Sourcing.CoverageAsync(SourcingEnv.Eve, new Dictionary<string, string?> { [itarPart] = "A", [openPart] = "A" }, null)).Value!;
        Assert.Equal([openPart], coverage.Keys);
        // Vic (no CUI) cannot read the restricted supplier: its entry is invisible and does not count for him, while Alice sees it.
        Assert.Empty((await env.Sourcing.GetPartSourcingAsync(SourcingEnv.Vic, openPart, null)).Value!.Entries);
        Assert.Equal(CoverageBuckets.NotCovered, (await env.Sourcing.CoverageAsync(SourcingEnv.Vic, new Dictionary<string, string?> { [openPart] = "A" }, null)).Value![openPart].Bucket);
        Assert.Single((await env.Sourcing.GetPartSourcingAsync(Alice, openPart, null)).Value!.Entries);
        Assert.Equal(CoverageBuckets.Covered, (await Coverage(env, openPart)).Bucket);
        // Denials are audited.
        Assert.Contains(await env.AuditAsync(), e => e.Action == "sourcing.part_read" && e.Outcome == AuditOutcome.Denied && e.ObjectId == itarPart);
    }

    [Fact]
    public async Task Where_sourced_lists_only_readable_parts_and_never_counts_hidden_ones()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var supplier = await env.NewSupplierAsync();
        await env.ApproveSupplierAsync(supplier);
        var openPart = await env.NewPartAsync("Washer");
        var itarPart = await env.NewPartAsync("Housing", SourcingEnv.Itar());
        await env.ActiveEntryAsync(SourceKinds.Aml, openPart, supplier, EntryStatuses.Approved, "WASH-1");
        await env.ActiveEntryAsync(SourceKinds.Aml, itarPart, supplier, EntryStatuses.Approved, "HOUS-1");

        var alice = (await env.Sourcing.WhereSourcedAsync(Alice, new WhereSourcedQuery(SupplierNumber: supplier), null)).Value!;
        Assert.Equal(2, alice.Total);
        var eve = (await env.Sourcing.WhereSourcedAsync(SourcingEnv.Eve, new WhereSourcedQuery(SupplierNumber: supplier), null)).Value!;
        Assert.Equal(1, eve.Total);
        Assert.Equal([openPart], eve.Rows.Select(r => r.PartNumber));
        // Asked by manufacturer part number the hidden part is just as absent; asking for nothing is refused.
        Assert.Equal(0, (await env.Sourcing.WhereSourcedAsync(SourcingEnv.Eve, new WhereSourcedQuery(Mpn: "HOUS-1"), null)).Value!.Total);
        Assert.Equal(1, (await env.Sourcing.WhereSourcedAsync(Alice, new WhereSourcedQuery(Mpn: "hous"), null)).Value!.Total);
        Assert.Equal(OutcomeKind.Invalid, (await env.Sourcing.WhereSourcedAsync(Alice, new WhereSourcedQuery(), null)).Kind);
        // A supplier the caller cannot read answers like an unknown one.
        var restricted = await env.NewSupplierAsync("Restricted Co", markings: SourcingEnv.Cui());
        await env.ApproveSupplierAsync(restricted);
        await env.ActiveEntryAsync(SourceKinds.Avl, openPart, restricted, EntryStatuses.Approved, null);
        Assert.Equal(1, (await env.Sourcing.WhereSourcedAsync(Alice, new WhereSourcedQuery(SupplierNumber: restricted), null)).Value!.Total);
        Assert.Equal(0, (await env.Sourcing.WhereSourcedAsync(SourcingEnv.Vic, new WhereSourcedQuery(SupplierNumber: restricted), null)).Value!.Total);
    }

    [Fact]
    public async Task Alternate_and_replacement_relations_need_both_parts_and_list_only_readable_ones()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var a = await env.NewPartAsync("Bolt A");
        var b = await env.NewPartAsync("Bolt B");
        var secret = await env.NewPartAsync("Secret bolt", SourcingEnv.Itar());

        Assert.True((await env.Sourcing.AddRelationAsync(Alice, a, b, PartRelations.Alternate, "Same thread", null)).IsOk);
        Assert.True((await env.Sourcing.AddRelationAsync(Alice, a, secret, PartRelations.Replaces, null, null)).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Sourcing.AddRelationAsync(Alice, a, b, PartRelations.Alternate, null, null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Sourcing.AddRelationAsync(Alice, secret, a, PartRelations.Replaces, null, null)).Kind); // would be a cycle of two
        Assert.Equal(OutcomeKind.Invalid, (await env.Sourcing.AddRelationAsync(Alice, a, a, PartRelations.Alternate, null, null)).Kind);
        // Eve cannot see the secret part: she cannot relate it, and its relation is not listed on the open part for her.
        Assert.Equal(OutcomeKind.NotFound, (await env.Sourcing.AddRelationAsync(SourcingEnv.Eve, a, secret, PartRelations.Alternate, null, null)).Kind);
        var forEve = (await env.Sourcing.GetPartSourcingAsync(SourcingEnv.Eve, a, null)).Value!;
        Assert.Equal([b], forEve.Relations.Select(r => r.PartNumber));
        var forAlice = (await env.Sourcing.GetPartSourcingAsync(Alice, a, null)).Value!;
        Assert.Equal(2, forAlice.Relations.Count);
        Assert.Contains(forAlice.Relations, r => r.Direction == "This part replaces");
        // From the other side the direction reads the other way round; a viewer cannot change relations.
        Assert.Contains((await env.Sourcing.GetPartSourcingAsync(Alice, b, null)).Value!.Relations, r => r.Relation == PartRelations.Alternate && r.PartNumber == a);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Sourcing.AddRelationAsync(SourcingEnv.Mallory, a, b, PartRelations.Replaces, null, null)).Kind);
        var removed = await env.Sourcing.RemoveRelationAsync(Alice, a, forAlice.Relations.First(r => r.Relation == PartRelations.Alternate).Id, null);
        Assert.True(removed.IsOk);
        Assert.Single(removed.Value!.Relations);
    }

    [Fact]
    public async Task Entries_validate_their_data_and_refuse_duplicates_and_unknown_things()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        var vendorOnly = await env.NewSupplierAsync("Vendor Only", roles: ["vendor"]);
        var maker = await env.NewSupplierAsync("Maker", roles: ["manufacturer"]);
        // An AML entry needs a manufacturer; an AVL entry a vendor.
        Assert.Equal(OutcomeKind.Invalid, (await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Aml, part, null, vendorOnly, "X-1", null, EntryStatuses.Approved), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Avl, part, null, maker, null, null, EntryStatuses.Approved), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Aml, part, null, maker, null, null, EntryStatuses.Approved), null)).Kind); // no MPN
        Assert.Equal(OutcomeKind.Invalid, (await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Avl, part, null, vendorOnly, null, null, "excellent"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Avl, part, null, vendorOnly, null, null, EntryStatuses.Approved, Currency: "usd$"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Avl, part, null, vendorOnly, null, null, EntryStatuses.Approved, PriceBreaks: [new PriceBreak(10, 1), new PriceBreak(5, 1)], Currency: "USD"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Avl, part, "Q", vendorOnly, null, null, EntryStatuses.Approved), null)).Kind); // no such revision
        Assert.Equal(OutcomeKind.NotFound, (await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Avl, "P-999999", null, vendorOnly, null, null, EntryStatuses.Approved), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Avl, part, null, "SUP-999999", null, null, EntryStatuses.Approved), null)).Kind);

        var first = await env.DraftEntryAsync(SourceKinds.Aml, part, maker, EntryStatuses.Approved, "ABC-1");
        // The same MPN of the same manufacturer for the same part is a duplicate; another part may use the MPN (many to many).
        Assert.Equal(OutcomeKind.Conflict, (await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Aml, part, null, maker, "abc-1", null, EntryStatuses.Alternate), null)).Kind);
        var other = await env.NewPartAsync("Other");
        var second = await env.DraftEntryAsync(SourceKinds.Aml, other, maker, EntryStatuses.Approved, "ABC-1");
        Assert.NotEqual(first.Number, second.Number);
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM src.mpn"));
        // After a withdrawal the slot is free again, and a viewer cannot withdraw.
        Assert.Equal(OutcomeKind.Forbidden, (await env.Sourcing.WithdrawEntryAsync(SourcingEnv.Mallory, first.Number, "no", null)).Kind);
        Assert.True((await env.Sourcing.WithdrawEntryAsync(Alice, first.Number, "Superseded", null)).IsOk);
        Assert.True((await env.Sourcing.CreateEntryAsync(Alice, new CreateEntryCommand(SourceKinds.Aml, part, null, maker, "ABC-1", null, EntryStatuses.Preferred), null)).IsOk);
    }

    private static async Task<PartCoverage> Coverage(SourcingEnv env, string part) =>
        (await env.Sourcing.CoverageAsync(Alice, new Dictionary<string, string?> { [part] = "A" }, null)).Value![part];
}
