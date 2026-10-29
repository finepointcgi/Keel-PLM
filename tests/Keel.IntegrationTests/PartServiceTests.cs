using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Parts;
using Npgsql;

namespace Keel.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PartServiceTests(PostgresFixture pg)
{
    private static Subject User(string name, bool usPerson = true, string[]? roles = null, string[]? programs = null, string[]? auths = null, string org = "ENG") => new()
    {
        Id = "sub-" + name,
        Username = name,
        DisplayName = name,
        UsPerson = usPerson,
        Org = org,
        Programs = (programs ?? ["ORCA"]).ToImmutableSortedSet(StringComparer.Ordinal),
        CuiCategories = ImmutableSortedSet.Create(StringComparer.Ordinal, "EXPT", "CTI"),
        ExportAuthorizations = (auths ?? []).ToImmutableSortedSet(StringComparer.Ordinal),
        Roles = (roles ?? [Roles.Viewer, Roles.Author]).ToImmutableSortedSet(StringComparer.Ordinal),
    };

    private static readonly Subject Alice = User("alice");
    private static readonly Subject Bob = User("bob");
    private static readonly Subject Lena = User("lena", usPerson: false);
    private static readonly Subject Carmen = User("carmen", programs: [], org: "MFG");
    private static readonly Subject Ada = User("ada", roles: [Roles.Admin, Roles.Viewer]);

    private static Markings Itar(string program = "ORCA") => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: program).Value;

    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    private static CreatePartCommand Cmd(string name, Markings? m = null, JsonObject? attrs = null) =>
        new(name, "Component", "EA", "Make", m ?? Itar(), "Synthetic test part", attrs ?? new JsonObject { ["material"] = "6061-T6", ["mass"] = 0.42 });

    private async Task<(PartService Svc, TestDatabase Db, NpgsqlDataSource Ds)> Setup()
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        return (new PartService(ds, new PostgresAuditLog(ds, TimeProvider.System), TimeProvider.System), db, ds);
    }

    private static async Task<List<AuditRecord>> Audit(NpgsqlDataSource ds) => [.. await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 1000))];

    [Fact]
    public async Task Create_assigns_number_initial_revision_and_audits()
    {
        var (svc, _, ds) = await Setup();
        var p = (await svc.CreateAsync(Alice, Cmd("Bracket, mounting"), "corr-1")).Value!;

        Assert.Equal("P-000001", p.Number);
        Assert.Equal(("A", 1, "InWork", "A.1"), (p.Revision.Revision, p.Revision.LatestIteration, p.Revision.State, p.Iteration.Label));
        Assert.Equal("""{"mass":0.42,"material":"6061-T6"}""", p.Iteration.Attributes.ToJsonString());
        Assert.Equal("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", p.Banner);
        Assert.Equal("alice", p.CreatedBy);
        Assert.True(p.Permissions.CanModify);
        var transition = Assert.Single(p.Transitions, t => t.To == "UnderReview");
        // Review states belong to the workflow engine since 1.5: the manual path no longer enters Under Review.
        Assert.Equal(("UnderReview", false), (transition.To, transition.Allowed));
        // 1.6: a revision a change abandons becomes Obsolete through the change process only.
        Assert.Contains(p.Transitions, t => t.To == "Obsolete" && t.Via == "change" && !t.Allowed);

        var e = Assert.Single(await Audit(ds));
        Assert.Equal(("part.create", AuditOutcome.Success, "P-000001", "corr-1"), (e.Action, e.Outcome, e.ObjectId, e.CorrelationId));
        Assert.Equal("P-000002", (await svc.CreateAsync(Alice, Cmd("Second"), null)).Value!.Number);
    }

    [Fact]
    public async Task Create_is_denied_when_the_author_could_not_access_the_markings()
    {
        var (svc, _, ds) = await Setup();
        var denied = await svc.CreateAsync(Lena with { Roles = [Roles.Author] }, Cmd("Restricted"), null);
        Assert.Equal(OutcomeKind.Forbidden, denied.Kind);
        Assert.Contains("export.us_person_or_authorization_required", denied.Errors);

        var explicitNumber = await svc.CreateAsync(Alice, Cmd("Migrated") with { Number = "LEGACY-1" }, null);
        Assert.Equal(["number.explicit_requires_admin"], explicitNumber.Errors);

        Assert.All(await Audit(ds), e => Assert.Equal(AuditOutcome.Denied, e.Outcome));
        Assert.Empty((await svc.SearchAsync(Alice, null, null, 50, 0, null)).Items);
    }

    [Fact]
    public async Task Create_validates_fields_and_attributes()
    {
        var (svc, _, _) = await Setup();
        var r = await svc.CreateAsync(Alice, new CreatePartCommand("", "Widget", "furlong", "Steal", Itar(), null, new JsonObject { ["mass"] = "lots" }), null);
        Assert.Equal(OutcomeKind.Invalid, r.Kind);
        Assert.Equal(5, r.Errors.Count);
    }

    [Fact]
    public async Task Unauthorized_read_returns_not_found_and_is_audited()
    {
        var (svc, _, ds) = await Setup();
        await svc.CreateAsync(Alice, Cmd("Secret bracket"), null);

        Assert.Equal(OutcomeKind.NotFound, (await svc.GetAsync(Lena, "P-000001", null, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await svc.GetAsync(Carmen, "P-000001", null, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await svc.CheckOutAsync(Lena, "P-000001", null)).Kind);
        Assert.True((await svc.GetAsync(Bob, "P-000001", null, null)).IsOk);

        var reads = (await Audit(ds)).Where(e => e.Action is "part.read" or "part.checkout").ToList();
        Assert.Equal([AuditOutcome.Denied, AuditOutcome.Denied, AuditOutcome.Denied, AuditOutcome.Success], reads.Select(e => e.Outcome));
        Assert.Contains("program.not_member", reads[1].Payload);
    }

    [Fact]
    public async Task Search_returns_only_accessible_parts_with_aggregate_banner()
    {
        var (svc, _, ds) = await Setup();
        await svc.CreateAsync(Alice, Cmd("Housing, sensor"), null);                           // ITAR, ORCA
        await svc.CreateAsync(Alice, Cmd("Housing, cover", Open), null);                      // open
        await svc.CreateAsync(Alice, Cmd("Gasket", Open), null);
        await svc.CreateAsync(Ada, Cmd("Housing, kraken", Itar("KRAKEN")) with { }, null);    // ada is not on KRAKEN -> forbidden

        var alice = await svc.SearchAsync(Alice, "housing", null, 50, 0, null);
        Assert.Equal(["P-000001", "P-000002"], alice.Items.Select(i => i.Number));
        Assert.Equal(2, alice.Total);
        Assert.Equal("CUI//EXPT · ITAR · DISTRIBUTION D", alice.PageBanner);

        var lena = await svc.SearchAsync(Lena, "housing", null, 50, 0, null);
        Assert.Equal(["P-000002"], lena.Items.Select(i => i.Number));
        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", lena.PageBanner);

        Assert.Equal(["P-000001"], (await svc.SearchAsync(Alice, "P-000001", null, 50, 0, null)).Items.Select(i => i.Number));
        Assert.Equal(3, (await svc.SearchAsync(Alice, "", "InWork", 50, 0, null)).Total);
        var page = await svc.SearchAsync(Alice, "", null, 2, 2, null);
        Assert.Equal((3L, "P-000003"), (page.Total, page.Items.Single().Number));
        Assert.Empty((await svc.SearchAsync(Alice, "100%_", null, 50, 0, null)).Items);

        var search = (await Audit(ds)).First(e => e.Action == "part.search");
        Assert.Contains("P-000001,P-000002", search.Payload);
    }

    [Fact]
    public async Task Checkout_edit_checkin_creates_next_iteration()
    {
        var (svc, _, _) = await Setup();
        await svc.CreateAsync(Alice, Cmd("Shaft"), null);

        var co = (await svc.CheckOutAsync(Alice, "P-000001", null)).Value!;
        Assert.True(co.Checkout!.IsMine);
        Assert.False(co.Transitions.Single(t => t.To == "UnderReview").Allowed);

        Assert.Equal(OutcomeKind.Conflict, (await svc.CheckOutAsync(Bob, "P-000001", null)).Kind);
        var bobView = (await svc.GetAsync(Bob, "P-000001", null, null)).Value!;
        Assert.Equal(("alice", false, null), (bobView.Checkout!.UserName, bobView.Checkout.IsMine, bobView.Checkout.Description));
        Assert.Equal(OutcomeKind.Conflict, (await svc.UpdateWorkingCopyAsync(Bob, "P-000001", "x", null, null)).Kind);

        var bad = await svc.UpdateWorkingCopyAsync(Alice, "P-000001", "Shaft, hardened", new JsonObject { ["hardness"] = "60 HRC" }, null);
        Assert.Equal(["Unknown attribute 'hardness'."], bad.Errors);
        var edited = (await svc.UpdateWorkingCopyAsync(Alice, "P-000001", "Shaft, hardened", new JsonObject { ["material"] = "4340", ["finish"] = "Black oxide" }, null)).Value!;
        Assert.Equal("Shaft, hardened", edited.Checkout!.Description);
        Assert.Equal("Synthetic test part", edited.Iteration.Description); // the vaulted iteration is untouched until check-in

        Assert.Equal(OutcomeKind.Conflict, (await svc.CheckInAsync(Bob, "P-000001", "no", null)).Kind);
        var ci = (await svc.CheckInAsync(Alice, "P-000001", "Changed material", null)).Value!;
        Assert.Null(ci.Checkout);
        Assert.Equal(("A.2", "Shaft, hardened", "Changed material"), (ci.Iteration.Label, ci.Iteration.Description, ci.Iteration.Comment));
        Assert.Equal(["A.2", "A.1"], ci.History.Select(h => h.Label));
        Assert.Equal("""{"finish":"Black oxide","material":"4340"}""", ci.Iteration.Attributes.ToJsonString());
    }

    [Fact]
    public async Task Concurrent_checkouts_admit_exactly_one_user()
    {
        var (svc, _, _) = await Setup();
        await svc.CreateAsync(Alice, Cmd("Contended"), null);
        var users = Enumerable.Range(0, 12).Select(i => User($"user{i}")).ToList();
        var results = await Task.WhenAll(users.Select(u => Task.Run(() => svc.CheckOutAsync(u, "P-000001", null))));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.Equal(11, results.Count(r => r.Kind == OutcomeKind.Conflict));
    }

    [Fact]
    public async Task Lifecycle_locks_released_data()
    {
        var (svc, db, _) = await Setup();
        await svc.CreateAsync(Alice, Cmd("Spacer"), null);

        Assert.Contains("approved review", (await svc.SetStateAsync(Alice, "P-000001", "UnderReview", null, false, null)).Errors[0]);
        var review = (await svc.SetStateAsync(Ada, "P-000001", "UnderReview", "Test setup: put the part under review", true, null)).Value!;
        Assert.True(review.Revision.IsLocked);
        Assert.Contains("locked", (await svc.CheckOutAsync(Alice, "P-000001", null)).Errors[0]);
        Assert.Contains("approved review", (await svc.SetStateAsync(Alice, "P-000001", "Released", null, false, null)).Errors[0]);
        Assert.Contains("No transition", (await svc.SetStateAsync(Alice, "P-000001", "Obsolete", null, false, null)).Errors[0]);
        Assert.Equal(OutcomeKind.Forbidden, (await svc.SetStateAsync(Alice, "P-000001", "Released", "x", true, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await svc.SetStateAsync(Ada, "P-000001", "Released", " ", true, null)).Kind);

        var released = (await svc.SetStateAsync(Ada, "P-000001", "Released", "Released on paper ECN-0001 before cutover", true, null)).Value!;
        Assert.Equal(("Released", "ada"), (released.Revision.State, released.Revision.StateChangedBy));
        Assert.Contains("change notice", released.Transitions.Single().Reason);
        Assert.Equal(OutcomeKind.Conflict, (await svc.ChangeMarkingsAsync(Alice, "P-000001", Itar() with { }, "x", null)).Kind);

        // Defence in depth: the database itself refuses new content on a locked revision.
        await using var app = db.AppDataSource();
        await using var insert = app.CreateCommand(
            "INSERT INTO plm.part_iteration (id, revision_id, iteration, created_at, created_by) SELECT gen_random_uuid(), id, 9, now(), 'x' FROM plm.part_revision");
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync())).SqlState);
        await using var bump = app.CreateCommand("UPDATE plm.part_revision SET latest_iteration = 2");
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => bump.ExecuteNonQueryAsync())).SqlState);
        await using var edit = app.CreateCommand("UPDATE plm.part_iteration SET description = 'tampered'");
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => edit.ExecuteNonQueryAsync())).SqlState);
        await using var delete = app.CreateCommand("DELETE FROM plm.part_master");
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task Markings_change_requires_reason_and_keeps_actor_access()
    {
        var (svc, _, ds) = await Setup();
        await svc.CreateAsync(Alice, Cmd("Lens"), null);

        Assert.Equal(OutcomeKind.Invalid, (await svc.ChangeMarkingsAsync(Alice, "P-000001", Open, "", null)).Kind);
        var lockout = await svc.ChangeMarkingsAsync(Alice, "P-000001", Itar("KRAKEN"), "Program transfer", null);
        Assert.Equal(["new_markings:program.not_member"], lockout.Errors);
        Assert.Equal(OutcomeKind.NotFound, (await svc.ChangeMarkingsAsync(Carmen, "P-000001", Open, "x", null)).Kind);

        var changed = (await svc.ChangeMarkingsAsync(Alice, "P-000001", Open, "Determined EAR99-free commercial item by export office", null)).Value!;
        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", changed.Banner);
        Assert.True((await svc.GetAsync(Lena, "P-000001", null, null)).IsOk);

        var e = (await Audit(ds)).Single(a => a.Action == "part.markings_change" && a.Outcome == AuditOutcome.Success);
        using var doc = System.Text.Json.JsonDocument.Parse(e.Payload);
        Assert.Contains("\"jurisdiction\":\"Itar\"", doc.RootElement.GetProperty("details").GetProperty("before").GetString());
        Assert.Contains("export office", e.Payload);
    }

    [Fact]
    public async Task Undo_checkout_by_owner_or_administrator_only()
    {
        var (svc, _, ds) = await Setup();
        await svc.CreateAsync(Alice, Cmd("Pin"), null);
        await svc.CheckOutAsync(Alice, "P-000001", null);

        Assert.Equal(["checkout.not_owner"], (await svc.UndoCheckOutAsync(Bob, "P-000001", null)).Errors);
        var undone = (await svc.UndoCheckOutAsync(Ada, "P-000001", null)).Value!;
        Assert.Null(undone.Checkout);
        Assert.Equal("A.1", undone.Iteration.Label);
        Assert.Contains("\"override\":\"True\"", (await Audit(ds)).Last(a => a.Action == "part.undo_checkout").Payload);
    }
}
