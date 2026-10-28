using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Security;
using Keel.Workflow;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Baselines (ADR-0021): creation, immutability, markings, redaction, explosion from a baseline, comparison, establishment.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BaselineTests(PostgresFixture pg)
{
    /// <summary>top = sub (qty 1, find 10; sub = bolt x4) + plate x2 (find 20). All released.</summary>
    private static async Task<(ConfigEnv Env, string Top, string Sub, string Bolt, string Plate)> Tree(PostgresFixture pg, bool release = true)
    {
        var env = await ConfigEnv.CreateAsync(pg);
        var bolt = await env.ReleasedComponentAsync("Bolt");
        var plate = await env.ReleasedComponentAsync("Plate");
        var sub = release ? await env.ReleasedAssemblyAsync("Sub", (bolt, 4m)) : await env.AssemblyAsync("Sub", (bolt, 4m));
        var top = release ? await env.ReleasedAssemblyAsync("Top", (sub, 1m), (plate, 2m)) : await env.AssemblyAsync("Top", (sub, 1m), (plate, 2m));
        return (env, top, sub, bolt, plate);
    }

    // ---------------------------------------------------------------- creation

    [Fact]
    public async Task A_baseline_is_a_numbered_snapshot_of_the_exploded_structure()
    {
        var (env, top, sub, bolt, plate) = await Tree(pg);
        var bl = await env.BaselineAsync(top);

        Assert.Equal("BL-000001", bl.Summary.Number);
        Assert.Equal("Draft", bl.Summary.State);
        Assert.Equal(4, bl.Summary.ItemCount);   // root + sub + bolt + plate
        Assert.False(bl.Permissions.RequiresSignoff);
        Assert.Equal("Latest released", bl.Summary.SpecText);
        Assert.Equal((top, "A.2"), (bl.Summary.RootNumber, bl.Summary.RootVersion));

        var items = await env.ExplodeAsync(top, BomConfigSpec.Baseline, baseline: bl.Summary.Number);
        Assert.Equal(["10", "10.10", "20"], items.Lines.Select(l => l.Path));
        Assert.Equal([sub, bolt, plate], items.Lines.Select(l => l.Number));
        Assert.Equal([1m, 4m, 2m], items.Lines.Select(l => l.Quantity));
        Assert.Equal(4m, items.Lines[1].ExtendedQuantity);
        Assert.True(items.Lines[0].HasChildren);
        Assert.False(items.Lines[2].HasChildren);
        Assert.Equal(top, items.RootNumber);

        // A second baseline gets the next number; the audit log records the creation with the content hash.
        Assert.Equal("BL-000002", (await env.BaselineAsync(top, name: "Second")).Summary.Number);
        var created = (await env.AuditAsync()).First(e => e.Action == "baseline.create" && e.Outcome == AuditOutcome.Success);
        Assert.Contains("contentSha256", created.Payload, StringComparison.Ordinal);
        Assert.Equal("baseline", created.ObjectType);
    }

    [Fact]
    public async Task A_baseline_stays_reproducible_while_the_structure_evolves()
    {
        var (env, top, sub, bolt, plate) = await Tree(pg, release: false);
        var bl = await env.BaselineAsync(top, spec: BomConfigSpec.Latest);

        // The structure moves on: sub's bolt quantity doubles and top gets a new part.
        await env.CheckOutAsync(sub);
        var line = (await env.Bom.GetWorkingAsync(ConfigEnv.Alice, sub, null)).Value!.Lines.Single();
        Assert.True((await env.Bom.UpdateUsageAsync(ConfigEnv.Alice, sub, line.Id, new UpdateUsageCommand(8m, "EA", line.FindNumber), null)).IsOk);
        await env.CheckInAsync(sub);
        var washer = await env.NewPartAsync("Washer");
        await env.CheckOutAsync(top);
        await env.AddLineAsync(top, washer, 6m);
        await env.CheckInAsync(top);

        var frozen = await env.ExplodeAsync(top, BomConfigSpec.Baseline, baseline: bl.Summary.Number);
        Assert.Equal([sub, bolt, plate], frozen.Lines.Select(l => l.Number));
        Assert.Equal(4m, frozen.Lines[1].Quantity);
        var live = await env.ExplodeAsync(top, BomConfigSpec.Latest);
        Assert.Equal(8m, live.Lines.Single(l => l.Number == bolt).Quantity);
        Assert.Contains(live.Lines, l => l.Number == washer);

        // Compared with the current structure the baseline shows exactly what changed since.
        var diff = (await env.Bom.CompareBaselineToCurrentAsync(ConfigEnv.Alice, bl.Summary.Number, BomConfigSpec.Latest, null)).Value!;
        Assert.Equal((1, 0, 2), (diff.Added, diff.Removed, diff.Changed));
        Assert.Contains(diff.Lines, l => l.Kind == BomChangeKind.Added && l.Number == washer);
        var changedBolt = diff.Lines.Single(l => l.Kind == BomChangeKind.Changed && l.Number == bolt);
        Assert.Contains("Quantity 4 to 8", changedBolt.Changes);
        // Sub's iteration moved on (A.2 to A.3), top's too.
        Assert.Contains(diff.Lines.Single(l => l.Number == sub).Changes, c => c.StartsWith("Revision A.", StringComparison.Ordinal));

        // Baseline against baseline: the second one is what the first became.
        var second = await env.BaselineAsync(top, spec: BomConfigSpec.Latest, name: "After");
        var both = (await env.Bom.CompareBaselinesAsync(ConfigEnv.Alice, bl.Summary.Number, second.Summary.Number, null)).Value!;
        Assert.Equal((1, 0, 2), (both.Added, both.Removed, both.Changed));
        var csv = DeepDiffCsv.Render(both);
        var lines = csv.TrimEnd().Split("\r\n");
        Assert.Equal(lines[0], lines[^1]);   // banner first and last
        Assert.Contains("Added", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Released_only_mode_refuses_unreleased_items_naming_the_numbers_the_creator_can_read()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        var bolt = await env.ReleasedComponentAsync("Bolt");
        var draft = await env.NewPartAsync("Unreleased part");
        var top = await env.ReleasedAssemblyAsync("Top", (bolt, 1m), (draft, 1m));   // released assembly holding a part that never was

        var refused = await env.Baselines.CreateAsync(ConfigEnv.Alice, new CreateBaselineCommand(top, "Release baseline", "Design", BomConfigSpec.Latest, ReleasedOnly: true), null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("Released-only", refused.Errors[0], StringComparison.Ordinal);
        Assert.Contains(draft, refused.Errors[0], StringComparison.Ordinal);
        Assert.DoesNotContain(bolt, refused.Errors[0], StringComparison.Ordinal);

        // Under LatestReleased an item without any released revision cannot be represented at all.
        var noRelease = await env.Baselines.CreateAsync(ConfigEnv.Alice, new CreateBaselineCommand(top, "x", "Design", BomConfigSpec.LatestReleased), null);
        Assert.Equal(OutcomeKind.Invalid, noRelease.Kind);
        Assert.Contains(draft, noRelease.Errors[0], StringComparison.Ordinal);

        // An unreleased root is refused in released-only mode as well.
        var unreleasedRoot = await env.AssemblyAsync("Draft assembly", (bolt, 1m));
        var rootRefused = await env.Baselines.CreateAsync(ConfigEnv.Alice, new CreateBaselineCommand(unreleasedRoot, "y", "Design", BomConfigSpec.Latest, ReleasedOnly: true), null);
        Assert.Contains(unreleasedRoot, rootRefused.Errors[0], StringComparison.Ordinal);

        // Nothing was stored and the refusals are audited as failures.
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM cfg.baseline"));
        Assert.Contains(await env.AuditAsync(), e => e.Action == "baseline.create" && e.Outcome == AuditOutcome.Failure);

        // Latest without released-only accepts the same structure.
        Assert.Equal(3, (await env.BaselineAsync(top, spec: BomConfigSpec.Latest)).Summary.ItemCount);
    }

    [Fact]
    public async Task Hidden_items_block_creation_with_a_count_and_no_names()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        var open = await env.NewPartAsync("Open part");
        var secret = await env.NewPartAsync("Secret part", "Component", ConfigEnv.Itar());
        // Eve may not read the ORCA part; build the assembly as Alice (open markings, so Eve can read it).
        var top = await env.AssemblyAsync("Top", (open, 1m), (secret, 2m));

        var refused = await env.Baselines.CreateAsync(ConfigEnv.Eve, new CreateBaselineCommand(top, "Eve's baseline", "Design", BomConfigSpec.Latest), "corr-eve");
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("1 item", refused.Errors[0], StringComparison.Ordinal);
        Assert.DoesNotContain(secret, refused.Errors[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", refused.Errors[0], StringComparison.Ordinal);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM cfg.baseline"));

        var denial = (await env.AuditAsync()).Single(e => e.Action == "baseline.create" && e.Outcome == AuditOutcome.Denied && e.CorrelationId == "corr-eve");
        Assert.Contains("items.unreadable", denial.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, denial.Payload, StringComparison.Ordinal);

        // A viewer cannot create baselines at all (role), and the denial is audited.
        var viewer = await env.Baselines.CreateAsync(ConfigEnv.Mallory, new CreateBaselineCommand(top, "x", "Design", BomConfigSpec.Latest), "corr-viewer");
        Assert.Equal(OutcomeKind.Forbidden, viewer.Kind);
        Assert.Contains(await env.AuditAsync(), e => e.CorrelationId == "corr-viewer" && e.Outcome == AuditOutcome.Denied);
        // A root nobody can see is a plain 404.
        Assert.Equal(OutcomeKind.NotFound, (await env.Baselines.CreateAsync(ConfigEnv.Eve, new CreateBaselineCommand(secret, "x", "Design", BomConfigSpec.Latest), null)).Kind);
    }

    // ---------------------------------------------------------------- markings

    [Fact]
    public async Task Markings_must_cover_every_item_and_the_creator_confirms_the_minimal_cover()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        var open = await env.NewPartAsync("Open part");
        var secret = await env.NewPartAsync("Secret part", "Component", ConfigEnv.Itar());
        var top = await env.AssemblyAsync("Top", (open, 1m), (secret, 1m));   // open assembly holding an ITAR/ORCA part

        var refused = await env.Baselines.CreateAsync(ConfigEnv.Alice, new CreateBaselineCommand(top, "Needs cover", "Design", BomConfigSpec.Latest), null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("do not cover every item", refused.Errors[0], StringComparison.Ordinal);
        Assert.Contains(ConfigEnv.Itar().BannerText(), refused.Errors[0], StringComparison.Ordinal);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM cfg.baseline"));

        var bl = await env.BaselineAsync(top, spec: BomConfigSpec.Latest, confirm: true);
        Assert.Equal(ConfigEnv.Itar().BannerText(), bl.Summary.Banner);
        Assert.Contains(bl.Notices, n => n.Contains("raised", StringComparison.Ordinal));
        Assert.Equal("ORCA", await env.TextAsync("SELECT program FROM cfg.baseline"));

        // Whoever can read the baseline can read every item in it (the dominance property), for several subjects.
        var items = await env.ExplodeAsync(top, BomConfigSpec.Baseline, baseline: bl.Summary.Number);
        foreach (var who in new[] { ConfigEnv.Alice, ConfigEnv.Eve, ConfigEnv.Mallory, ConfigEnv.Audrey, WorkflowEnv.Lena, ConfigEnv.Ada })
            if (AccessPolicy.Evaluate(who, bl.Summary.Markings, PlmAction.Read).Permitted)
                Assert.All(items.Lines.Where(l => l.Markings is not null), l => Assert.True(AccessPolicy.Evaluate(who, l.Markings!, PlmAction.Read).Permitted, who.Username));

        // Eve (no program ORCA) cannot read the baseline: 404 and a denial in the audit log; nor can she list it.
        Assert.Equal(OutcomeKind.NotFound, (await env.Baselines.GetAsync(ConfigEnv.Eve, bl.Summary.Number, "corr-eve-read")).Kind);
        Assert.Contains(await env.AuditAsync(), e => e.CorrelationId == "corr-eve-read" && e.Outcome == AuditOutcome.Denied && e.Action == "baseline.read");
        Assert.Empty((await env.Baselines.SearchAsync(ConfigEnv.Eve, new BaselineSearch(), null)).Value!.Items);
        Assert.Single((await env.Baselines.SearchAsync(ConfigEnv.Alice, new BaselineSearch(), null)).Value!.Items);
        Assert.Equal(OutcomeKind.NotFound, (await env.Bom.ExplodeAsync(ConfigEnv.Eve, new BomQuery("", null, null, BomConfigSpec.Baseline, null, null, bl.Summary.Number), null)).Kind);
    }

    [Fact]
    public async Task Items_of_two_programs_cannot_share_one_baseline()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        var kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);
        var orca = await env.NewPartAsync("Orca part", "Component", ConfigEnv.Itar("ORCA"), kim);
        var kraken = await env.NewPartAsync("Kraken part", "Component", ConfigEnv.Itar("KRAKEN"), kim);
        var top = await env.NewPartAsync("Mixed", "Assembly", ConfigEnv.Itar("ORCA"), kim);
        await env.CheckOutAsync(top, kim);
        await env.AddLineAsync(top, orca, 1m, by: kim);
        // Parts of another program cannot even be added to the ORCA assembly's baseline later; check the cover refusal directly.
        await env.AddLineAsync(top, kraken, 1m, by: kim);
        await env.CheckInAsync(top, kim);
        var refused = await env.Baselines.CreateAsync(kim, new CreateBaselineCommand(top, "Mixed", "Design", BomConfigSpec.Latest, Markings: ConfigEnv.Itar("ORCA"), ConfirmRaiseMarkings: true), null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("program", refused.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- redaction (A4)

    [Fact]
    public async Task Readers_who_lost_access_to_an_item_see_a_placeholder_and_nothing_below_it()
    {
        var (env, top, sub, bolt, plate) = await Tree(pg, release: false);
        var bl = await env.BaselineAsync(top, spec: BomConfigSpec.Latest);

        // After the baseline exists, sub becomes ITAR/ORCA. Eve can still read the (open) baseline but no longer sub.
        var raised = await env.Parts.ChangeMarkingsAsync(ConfigEnv.Alice, sub, ConfigEnv.Itar(), "Reclassified after review", null);
        Assert.True(raised.IsOk, string.Join("; ", raised.Errors));

        var eve = await env.ExplodeAsync(top, BomConfigSpec.Baseline, ConfigEnv.Eve, baseline: bl.Summary.Number);
        Assert.Equal(["10", "20"], eve.Lines.Select(l => l.Path));          // bolt (below sub) is not shown at all
        var placeholder = eve.Lines[0];
        Assert.True(placeholder.Redacted);
        Assert.Null(placeholder.Number);
        Assert.Null(placeholder.Name);
        Assert.Null(placeholder.Markings);
        Assert.Equal((10, 1m, "EA"), (placeholder.FindNumber, placeholder.Quantity, placeholder.Unit));   // A4: only quantity, find number, unit
        Assert.False(placeholder.HasChildren);
        Assert.Equal(1, eve.RedactedCount);
        Assert.Equal(plate, eve.Lines[1].Number);
        Assert.DoesNotContain(sub, BomCsv.Render(eve.Banner, eve.Lines), StringComparison.Ordinal);
        Assert.DoesNotContain(bolt, BomCsv.Render(eve.Banner, eve.Lines), StringComparison.Ordinal);

        // Alice still sees everything, and her banner includes the raised markings.
        var alice = await env.ExplodeAsync(top, BomConfigSpec.Baseline, baseline: bl.Summary.Number);
        Assert.Equal(3, alice.Lines.Count);
        Assert.Contains("ITAR", alice.Banner, StringComparison.Ordinal);
        Assert.DoesNotContain("ITAR", eve.Banner, StringComparison.Ordinal);

        // Compared with the live structure Eve gets placeholders too, never the hidden part's identity.
        var diff = (await env.Bom.CompareBaselineToCurrentAsync(ConfigEnv.Eve, bl.Summary.Number, BomConfigSpec.Latest, null)).Value!;
        Assert.All(diff.Lines, l => Assert.DoesNotContain(sub, l.Number ?? "", StringComparison.Ordinal));

        // CSV export applies the same filter and records the export in the audit log.
        var export = (await env.Bom.ExportCsvAsync(ConfigEnv.Eve, new BomQuery("", null, null, BomConfigSpec.Baseline, null, null, bl.Summary.Number), null)).Value!;
        Assert.Equal(2, export.Lines);
        Assert.Contains(BomCsv.RestrictedLabel, export.Csv, StringComparison.Ordinal);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "baseline.export" && e.Outcome == AuditOutcome.Success);
    }

    // ---------------------------------------------------------------- immutability in the database

    [Fact]
    public async Task Items_and_established_baselines_are_immutable_in_the_database()
    {
        var (env, top, _, _, _) = await Tree(pg);
        var bl = await env.BaselineAsync(top);
        Assert.True((await env.Baselines.EstablishAsync(ConfigEnv.Alice, bl.Summary.Number, "Design review passed", null)).IsOk);

        // The application role has no UPDATE or DELETE on items at all.
        await using (var cmd = env.Ds.CreateCommand("UPDATE cfg.baseline_item SET quantity = 99"))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
        await using (var cmd = env.Ds.CreateCommand("DELETE FROM cfg.baseline"))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);

        // Triggers hold even for a superuser (who could only bypass them deliberately, which the signature hash then reveals).
        async Task<string> Rejected(string sql)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync(sql));
            return ex.MessageText;
        }
        Assert.Contains("immutable", await Rejected("UPDATE cfg.baseline_item SET quantity = 99"), StringComparison.Ordinal);
        Assert.Contains("immutable", await Rejected("DELETE FROM cfg.baseline_item"), StringComparison.Ordinal);
        Assert.Contains("only be added to a draft", await Rejected(
            "INSERT INTO cfg.baseline_item (baseline_id, seq, parent_seq, level, path, find_number, quantity, unit, ext_quantity, master_id, revision_id, iteration_id) " +
            "SELECT baseline_id, 99, 0, 1, '99', 99, 1, 'EA', 1, master_id, revision_id, iteration_id FROM cfg.baseline_item WHERE seq = 1"), StringComparison.Ordinal);
        Assert.Contains("frozen", await Rejected("UPDATE cfg.baseline SET name = 'renamed'"), StringComparison.Ordinal);
        Assert.Contains("immutable", await Rejected("UPDATE cfg.baseline SET distribution_statement = 'B'"), StringComparison.Ordinal);
        Assert.Contains("immutable", await Rejected("UPDATE cfg.baseline SET item_count = 1"), StringComparison.Ordinal);
        Assert.Contains("never modified", await Rejected("DELETE FROM cfg.baseline"), StringComparison.Ordinal);
        Assert.Contains("establishment cannot change", await Rejected("UPDATE cfg.baseline SET establish_reason = 'forged'"), StringComparison.Ordinal);
        Assert.Contains("no transition", await Rejected("UPDATE cfg.baseline SET lifecycle_state = 'Draft'"), StringComparison.Ordinal);
        Assert.Contains("never modified", await Rejected("TRUNCATE cfg.baseline_document"), StringComparison.Ordinal);

        // A baseline that requires sign-off cannot skip the review even by SQL.
        var release = await env.BaselineAsync(top, "Release", name: "Needs review");
        Assert.Contains("requires sign-off", await Rejected($"UPDATE cfg.baseline SET lifecycle_state = 'Established', establish_reason = 'sneaky', established_at = now() WHERE number = '{release.Summary.Number}'"), StringComparison.Ordinal);
        Assert.Equal("Draft", await env.TextAsync($"SELECT lifecycle_state FROM cfg.baseline WHERE number = '{release.Summary.Number}'"));
    }

    // ---------------------------------------------------------------- lifecycle

    [Fact]
    public async Task Direct_establishment_needs_a_reason_and_is_refused_for_types_that_require_sign_off()
    {
        var (env, top, _, _, _) = await Tree(pg);
        var design = await env.BaselineAsync(top, "Design");
        var release = await env.BaselineAsync(top, "Release", name: "Release");
        var product = await env.BaselineAsync(top, "Product", name: "Product");
        Assert.True(release.Permissions.RequiresSignoff);
        Assert.True(product.Permissions.RequiresSignoff);
        Assert.False(design.Permissions.RequiresSignoff);

        Assert.Equal(OutcomeKind.Invalid, (await env.Baselines.EstablishAsync(ConfigEnv.Alice, design.Summary.Number, "  ", null)).Kind);
        var viewer = await env.Baselines.EstablishAsync(ConfigEnv.Mallory, design.Summary.Number, "Because", "corr-v");
        Assert.Equal(OutcomeKind.Forbidden, viewer.Kind);
        Assert.Contains(await env.AuditAsync(), e => e.CorrelationId == "corr-v" && e.Outcome == AuditOutcome.Denied);

        var refused = await env.Baselines.EstablishAsync(ConfigEnv.Alice, release.Summary.Number, "Trust me", "corr-r");
        Assert.Equal(OutcomeKind.Conflict, refused.Kind);
        Assert.Contains("requires sign-off", refused.Errors[0], StringComparison.Ordinal);
        Assert.Contains(await env.AuditAsync(), e => e.CorrelationId == "corr-r" && e.Outcome == AuditOutcome.Failure);

        var done = (await env.Baselines.EstablishAsync(ConfigEnv.Alice, design.Summary.Number, "PDR design baseline agreed", "corr-ok")).Value!;
        Assert.Equal(("Established", "PDR design baseline agreed", "alice"), (done.Summary.State, done.EstablishReason, done.EstablishedBy));
        Assert.NotNull(done.Summary.EstablishedAt);
        Assert.Equal(OutcomeKind.Conflict, (await env.Baselines.EstablishAsync(ConfigEnv.Alice, design.Summary.Number, "again", null)).Kind);

        // A design baseline can be forced to require sign-off, but a sign-off type cannot opt out.
        Assert.True((await env.Baselines.CreateAsync(ConfigEnv.Alice, new CreateBaselineCommand(top, "Forced", "Design", RequireSignoff: true), null)).Value!.Permissions.RequiresSignoff);
        Assert.Equal(OutcomeKind.Invalid, (await env.Baselines.CreateAsync(ConfigEnv.Alice, new CreateBaselineCommand(top, "Opt out", "Release", RequireSignoff: false), null)).Kind);

        // Drafts can be edited; established baselines are frozen.
        Assert.Equal("Renamed", (await env.Baselines.UpdateAsync(ConfigEnv.Alice, product.Summary.Number, "Renamed", "New purpose", "CDR", null)).Value!.Summary.Name);
        Assert.Equal(OutcomeKind.Conflict, (await env.Baselines.UpdateAsync(ConfigEnv.Alice, design.Summary.Number, "Nope", "", null, null)).Kind);
    }

    [Fact]
    public async Task Supersede_and_archive_follow_the_lifecycle_and_nothing_is_deleted()
    {
        var (env, top, _, _, _) = await Tree(pg);
        var first = await env.BaselineAsync(top);
        var second = await env.BaselineAsync(top, name: "Newer");
        var other = await env.BaselineAsync((await env.ReleasedAssemblyAsync("Other", (await env.ReleasedComponentAsync("Nut"), 1m))), name: "Other root");
        foreach (var b in new[] { first, second, other }) Assert.True((await env.Baselines.EstablishAsync(ConfigEnv.Alice, b.Summary.Number, "Agreed", null)).IsOk);

        Assert.Equal(OutcomeKind.Invalid, (await env.Baselines.SupersedeAsync(ConfigEnv.Alice, first.Summary.Number, other.Summary.Number, "x", null)).Kind);   // other part
        Assert.Equal(OutcomeKind.Invalid, (await env.Baselines.SupersedeAsync(ConfigEnv.Alice, first.Summary.Number, first.Summary.Number, "x", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Baselines.SupersedeAsync(ConfigEnv.Alice, first.Summary.Number, second.Summary.Number, " ", null)).Kind);
        var superseded = (await env.Baselines.SupersedeAsync(ConfigEnv.Alice, first.Summary.Number, second.Summary.Number, "Design change", null)).Value!;
        Assert.Equal(("Superseded", second.Summary.Number), (superseded.Summary.State, superseded.SupersededBy));
        Assert.Equal([first.Summary.Number], (await env.Baselines.GetAsync(ConfigEnv.Alice, second.Summary.Number, null)).Value!.Supersedes);
        Assert.Equal(OutcomeKind.Invalid, (await env.Baselines.SupersedeAsync(ConfigEnv.Alice, first.Summary.Number, second.Summary.Number, "again", null)).Kind);   // no longer established

        Assert.Equal("Archived", (await env.Baselines.ArchiveAsync(ConfigEnv.Alice, first.Summary.Number, "Obsolete program phase", null)).Value!.Summary.State);
        Assert.Equal(OutcomeKind.Conflict, (await env.Baselines.ArchiveAsync(ConfigEnv.Alice, first.Summary.Number, "again", null)).Kind);
        // An archived baseline is still readable and still explodes.
        Assert.Equal(3, (await env.ExplodeAsync(top, BomConfigSpec.Baseline, baseline: first.Summary.Number)).Lines.Count);
        Assert.Equal(2, await env.ScalarAsync("SELECT count(*) FROM cfg.baseline WHERE root_master_id = (SELECT id FROM plm.part_master WHERE number = '" + top + "')"));

        var search = (await env.Baselines.SearchAsync(ConfigEnv.Alice, new BaselineSearch(RootNumber: top, State: "Established"), null)).Value!;
        Assert.Equal([second.Summary.Number], search.Items.Select(i => i.Number));
    }

    [Fact]
    public async Task Two_users_establishing_the_same_baseline_serialize_and_exactly_one_wins()
    {
        var (env, top, _, _, _) = await Tree(pg);
        var bl = await env.BaselineAsync(top);
        var rita = ConfigEnv.Rita;
        var results = await Task.WhenAll(
            env.Baselines.EstablishAsync(ConfigEnv.Alice, bl.Summary.Number, "Alice's reason", null),
            env.Baselines.EstablishAsync(rita, bl.Summary.Number, "Rita's reason", null),
            env.Baselines.EstablishAsync(ConfigEnv.Ada, bl.Summary.Number, "Ada's reason", null));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.Equal(2, results.Count(r => r.Kind == OutcomeKind.Conflict));
        var winner = results.Single(r => r.IsOk).Value!;
        Assert.Equal(winner.EstablishReason, await env.TextAsync("SELECT establish_reason FROM cfg.baseline"));
        Assert.Equal(1, (await env.AuditAsync()).Count(e => e.Action == "baseline.establish" && e.Outcome == AuditOutcome.Success));
    }

    // ---------------------------------------------------------------- establishment through the review workflow

    [Fact]
    public async Task A_program_milestone_is_established_by_e_signatures_with_step_up_and_tamper_detection()
    {
        var (env, top, _, _, _) = await Tree(pg);
        var bl = await env.BaselineAsync(top, "Release", name: "CDR release baseline");

        var start = await env.Flow.StartAsync(ConfigEnv.Alice, new StartReviewCommand("baseline", bl.Summary.Number,
            new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob", "carol"] }), "corr-start");
        Assert.True(start.IsOk, string.Join("; ", start.Errors));
        Assert.Equal("UnderReview", (await env.Baselines.GetAsync(ConfigEnv.Alice, bl.Summary.Number, null)).Value!.Summary.State);

        // The signer sees what is signed: the baseline's summary and its content hash.
        var task = start.Value!.Tasks.Single(t => t.Status == "Open");
        var detail = (await env.Flow.GetTaskAsync(WorkflowEnv.Fresh(ConfigEnv.Rita), task.Id, null)).Value!;
        Assert.Contains("CDR release baseline", detail.Description, StringComparison.Ordinal);
        Assert.Contains("contentSha256", detail.Attributes!.ToJsonString(), StringComparison.Ordinal);

        // Signing needs a fresh authentication.
        var stale = ConfigEnv.Rita with { AuthTime = DateTimeOffset.UtcNow.AddSeconds(-301) };
        Assert.Equal(OutcomeKind.StepUpRequired, (await env.Flow.SignAsync(stale, task.Id, "Reviewed", null, "corr-stale")).Kind);
        Assert.Equal("UnderReview", (await env.Baselines.GetAsync(ConfigEnv.Alice, bl.Summary.Number, null)).Value!.Summary.State);

        var done = await env.SignAllAsync(start.Value!);
        Assert.Equal("Approved", done.Status);
        var established = (await env.Baselines.GetAsync(ConfigEnv.Alice, bl.Summary.Number, null)).Value!;
        Assert.Equal("Established", established.Summary.State);
        Assert.Contains("every approver signed", established.EstablishReason, StringComparison.Ordinal);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "baseline.establish" && e.Payload.Contains("workflow", StringComparison.Ordinal));

        var clean = (await env.Flow.VerifyInstanceAsync(ConfigEnv.Alice, done.Id, null)).Value!;
        Assert.Equal(3, clean.Count);
        Assert.All(clean, v => Assert.True(v.Valid, string.Join("; ", v.Problems)));
        Assert.True((await env.Baselines.VerifyAsync(ConfigEnv.Alice, bl.Summary.Number, null)).Value!.Intact);

        // An attacker with database access changes a stored item, bypassing the triggers: signatures and the integrity check both notice.
        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE cfg.baseline_item SET quantity = 99 WHERE seq = 2");
        var tampered = (await env.Flow.VerifyInstanceAsync(ConfigEnv.Alice, done.Id, null)).Value!;
        Assert.All(tampered, v =>
        {
            Assert.False(v.Valid);
            Assert.Contains(v.Problems, p => p.Contains("changed after signing", StringComparison.Ordinal));
        });
        var integrity = (await env.Baselines.VerifyAsync(ConfigEnv.Alice, bl.Summary.Number, null)).Value!;
        Assert.False(integrity.Intact);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "baseline.verify" && e.Outcome == AuditOutcome.Failure);
    }

    [Fact]
    public async Task A_rejected_review_returns_the_baseline_to_draft_and_a_tampered_one_cannot_be_submitted()
    {
        var (env, top, _, _, _) = await Tree(pg);
        var bl = await env.BaselineAsync(top, "Product", name: "PCA product baseline");
        var start = (await env.Flow.StartAsync(ConfigEnv.Alice, new StartReviewCommand("baseline", bl.Summary.Number,
            new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null)).Value!;
        var cancelled = await env.Flow.CancelAsync(ConfigEnv.Ada, start.Id, "Not ready", null);
        Assert.True(cancelled.IsOk, string.Join("; ", cancelled.Errors));
        Assert.Equal("Draft", (await env.Baselines.GetAsync(ConfigEnv.Alice, bl.Summary.Number, null)).Value!.Summary.State);

        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE cfg.baseline_item SET quantity = 3 WHERE seq = 3");
        var again = await env.Flow.StartAsync(ConfigEnv.Alice, new StartReviewCommand("baseline", bl.Summary.Number,
            new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null);
        Assert.Equal(OutcomeKind.Conflict, again.Kind);
        Assert.Contains("no longer matches", again.Errors[0], StringComparison.Ordinal);
    }
}
