using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Parts;
using Keel.Web.Services;
using Keel.Workspace;
using Npgsql;
using static Keel.IntegrationTests.DocumentServiceTests;

namespace Keel.IntegrationTests;

/// <summary>
/// ADR 0061: the one-step flows (Edit then Save, Discard, Add document, Create document linked to a part) chain the modules' own calls, so they must leave
/// the same audit trail as the step-by-step controls and must be refused exactly where those are: release locks, other people's check-outs, access rules.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SimpleFlowsTests(PostgresFixture pg)
{
    private static readonly Subject Kim = User("kim", programs: ["ORCA", "KRAKEN"]);
    private static readonly Subject Admin = User("ada", roles: [Roles.Admin, Roles.Viewer]);
    private static readonly string[] Mutations = ["part.checkout", "part.edit", "part.checkin", "part.link_document", "part.undo_checkout"];

    private sealed record Env(SimpleFlows Flows, DocumentService Docs, PartService Parts, NpgsqlDataSource Ds, LastChoiceStore Last, WorkspaceService Workspace);

    private async Task<Env> Setup()
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var auditLog = new PostgresAuditLog(ds, TimeProvider.System);
        var docs = new DocumentService(ds, auditLog, TimeProvider.System, new ObjectVault(await VaultContainer.OptionsAsync()));
        var parts = new PartService(ds, auditLog, TimeProvider.System);
        return new Env(new SimpleFlows(parts, docs), docs, parts, ds, new LastChoiceStore(ds, TimeProvider.System), new WorkspaceService(ds, auditLog, TimeProvider.System));
    }

    private static FileUpload File(string name) => new(name, "text/plain", new MemoryStream(Encoding.UTF8.GetBytes($"{name} {Guid.NewGuid()}")));

    private static async Task<string> NewPart(Env env, string name = "Bracket")
    {
        var created = await env.Parts.CreateAsync(Alice, new CreatePartCommand(name, "Component", "EA", "Make", Itar(), "Synthetic", new JsonObject()), null);
        Assert.True(created.IsOk);
        return created.Value!.Number;
    }

    private static async Task<string> NewDoc(Env env, string name, Subject? by = null, Markings? m = null)
    {
        var created = await env.Docs.CreateAsync(by ?? Alice, new CreateDocumentCommand(name, "Drawing", m ?? Itar()), File(name + ".txt"), null);
        Assert.True(created.IsOk);
        return created.Value!.Number;
    }

    private static async Task<List<string>> Trail(NpgsqlDataSource ds, string objectNumber) =>
        [.. (await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 2000))).OrderBy(r => r.Seq)
            .Where(r => r.ObjectId == objectNumber && Mutations.Contains(r.Action)).Select(r => $"{r.Action}:{r.Outcome}")];

    // ---------------------------------------------------------------- edit and save

    [Fact]
    public async Task Edit_then_Save_leaves_the_same_audit_trail_as_check_out_save_and_check_in()
    {
        var env = await Setup();
        var simple = await NewPart(env, "Simple");
        var manual = await NewPart(env, "Manual");

        var begun = await env.Flows.BeginEditAsync(Alice, simple, null);
        Assert.True(begun.IsOk);
        Assert.True(begun.Value!.Checkout!.IsMine);
        var saved = await env.Flows.SaveAsync(Alice, simple, "Edited", new JsonObject(), "Thicker wall", null);
        Assert.True(saved.IsOk);
        Assert.Null(saved.Value!.Checkout);
        Assert.Equal("A.2", saved.Value.Iteration.Label);
        Assert.Equal("Thicker wall", saved.Value.History.First(h => h.Label == "A.2").Comment);
        Assert.Equal("Edited", saved.Value.Iteration.Description);

        Assert.True((await env.Parts.CheckOutAsync(Alice, manual, null)).IsOk);
        Assert.True((await env.Parts.UpdateWorkingCopyAsync(Alice, manual, "Edited", new JsonObject(), null)).IsOk);
        Assert.True((await env.Parts.CheckInAsync(Alice, manual, "Thicker wall", null)).IsOk);

        var expected = new[] { "part.checkout:Success", "part.edit:Success", "part.checkin:Success" };
        Assert.Equal(expected, await Trail(env.Ds, manual));
        Assert.Equal(expected, await Trail(env.Ds, simple));
    }

    [Fact]
    public async Task Edit_when_the_part_is_already_checked_out_by_me_continues_the_draft()
    {
        var env = await Setup();
        var number = await NewPart(env);
        await env.Parts.CheckOutAsync(Alice, number, null);
        var again = await env.Flows.BeginEditAsync(Alice, number, null);
        Assert.True(again.IsOk);
        Assert.True(again.Value!.Checkout!.IsMine);
        Assert.Equal(["part.checkout:Success"], await Trail(env.Ds, number)); // no second check-out
    }

    [Fact]
    public async Task Edit_is_refused_on_a_released_part_and_nobody_gets_a_check_out()
    {
        var env = await Setup();
        var number = await NewPart(env);
        Assert.True((await env.Parts.SetStateAsync(Admin, number, "Released", "Test: release", true, null)).IsOk);

        var begun = await env.Flows.BeginEditAsync(Alice, number, null);
        Assert.Equal(OutcomeKind.Conflict, begun.Kind);
        Assert.Contains("change order", begun.Errors[0], StringComparison.Ordinal);
        Assert.DoesNotContain("part.checkout:Success", await Trail(env.Ds, number));
        Assert.Null((await env.Parts.GetAsync(Alice, number, null, null)).Value!.Checkout);

        // and the module itself still refuses it if a client skips the advice
        Assert.Equal(OutcomeKind.Conflict, (await env.Parts.CheckOutAsync(Alice, number, null)).Kind);
    }

    [Fact]
    public async Task Edit_is_refused_while_somebody_else_is_editing_and_names_them()
    {
        var env = await Setup();
        var number = await NewPart(env);
        await env.Parts.CheckOutAsync(Alice, number, null);
        var begun = await env.Flows.BeginEditAsync(Bob, number, null);
        Assert.False(begun.IsOk);
        Assert.Contains("alice is editing this part", begun.Errors[0], StringComparison.Ordinal);
        // Bob cannot save into Alice's draft either: the module refuses, so Save does not bypass the lock
        Assert.False((await env.Flows.SaveAsync(Bob, number, "x", new JsonObject(), null, null)).IsOk);
        Assert.True((await env.Parts.GetAsync(Alice, number, null, null)).Value!.Checkout!.IsMine);
    }

    [Fact]
    public async Task Edit_respects_the_access_policy_and_the_denial_is_audited()
    {
        var env = await Setup();
        var number = await NewPart(env); // ITAR, program ORCA
        var outsider = User("olga", programs: ["KRAKEN"]);
        var begun = await env.Flows.BeginEditAsync(outsider, number, null);
        Assert.False(begun.IsOk); // cannot even see it: the same answer as a part that does not exist
        var viewer = User("vic", roles: [Roles.Viewer]);
        var denied = await env.Flows.BeginEditAsync(viewer, number, null);
        Assert.Equal(OutcomeKind.Forbidden, denied.Kind);
        Assert.Contains("part.checkout:Denied", await Trail(env.Ds, number));
        Assert.DoesNotContain("part.checkout:Success", await Trail(env.Ds, number));
    }

    [Fact]
    public async Task Discard_is_the_modules_undo_check_out_and_loses_only_the_draft()
    {
        var env = await Setup();
        var number = await NewPart(env);
        await env.Flows.BeginEditAsync(Alice, number, null);
        await env.Parts.UpdateWorkingCopyAsync(Alice, number, "Unsaved words", new JsonObject(), null);
        var discarded = await env.Flows.DiscardAsync(Alice, number, null);
        Assert.True(discarded.IsOk);
        Assert.Null(discarded.Value!.Checkout);
        Assert.Equal("A.1", discarded.Value.Iteration.Label);
        Assert.Equal("Synthetic", discarded.Value.Iteration.Description);
        Assert.Equal(["part.checkout:Success", "part.edit:Success", "part.undo_checkout:Success"], await Trail(env.Ds, number));
        Assert.False((await env.Flows.DiscardAsync(Bob, number, null)).IsOk); // nothing to discard
    }

    // ---------------------------------------------------------------- add document

    [Fact]
    public async Task Add_document_checks_out_links_and_checks_in_with_the_audit_trail_of_the_manual_steps()
    {
        var env = await Setup();
        var simple = await NewPart(env, "Simple");
        var manual = await NewPart(env, "Manual");
        var doc = await NewDoc(env, "Bracket drawing");

        var result = await env.Flows.AddDocumentAsync(Alice, simple, doc, "describes", null, null);
        Assert.True(result.IsOk, result.Message);
        Assert.True(result.CheckedIn);
        Assert.Equal("A.2", result.PartVersion);
        var links = (await env.Docs.GetPartLinksAsync(Alice, simple, null, null, null)).Value!;
        Assert.Equal(doc, Assert.Single(links.Links).Document!.Number);
        Assert.Null((await env.Parts.GetAsync(Alice, simple, null, null)).Value!.Checkout); // nothing left locked

        await env.Parts.CheckOutAsync(Alice, manual, null);
        await env.Docs.AddLinkToWorkingCopyAsync(Alice, manual, doc, "describes", null, null);
        await env.Parts.CheckInAsync(Alice, manual, "Added document " + doc, null);
        var expected = new[] { "part.checkout:Success", "part.link_document:Success", "part.checkin:Success" };
        Assert.Equal(expected, await Trail(env.Ds, manual));
        Assert.Equal(expected, await Trail(env.Ds, simple));
    }

    [Fact]
    public async Task Add_document_to_my_own_draft_does_not_check_in()
    {
        var env = await Setup();
        var number = await NewPart(env);
        var doc = await NewDoc(env, "Spec");
        await env.Flows.BeginEditAsync(Alice, number, null);
        var result = await env.Flows.AddDocumentAsync(Alice, number, doc, "references", null, null);
        Assert.True(result.IsOk, result.Message);
        Assert.False(result.CheckedIn);
        Assert.Contains("applied when you save", result.Message, StringComparison.Ordinal);
        var links = (await env.Docs.GetPartLinksAsync(Alice, number, null, null, null)).Value!;
        Assert.Empty(links.Links);
        Assert.Single(links.WorkingLinks!);
        Assert.Equal(["part.checkout:Success", "part.link_document:Success"], await Trail(env.Ds, number));
    }

    [Fact]
    public async Task A_failed_link_gives_back_the_check_out_and_says_why()
    {
        var env = await Setup();
        var number = await NewPart(env);
        var hidden = await NewDoc(env, "Kraken report", Kim, Itar("KRAKEN")); // Alice cannot read it
        var result = await env.Flows.AddDocumentAsync(Alice, number, hidden, "references", null, null);
        Assert.False(result.IsOk);
        Assert.Contains($"Document {hidden} was not found.", result.Message, StringComparison.Ordinal);
        Assert.Null((await env.Parts.GetAsync(Alice, number, null, null)).Value!.Checkout);
        Assert.Equal("A.1", (await env.Parts.GetAsync(Alice, number, null, null)).Value!.Iteration.Label);
        Assert.Contains("part.checkout:Success", await Trail(env.Ds, number));
        Assert.Contains("part.undo_checkout:Success", await Trail(env.Ds, number));
        Assert.DoesNotContain("part.checkin:Success", await Trail(env.Ds, number));
    }

    [Fact]
    public async Task Add_document_is_refused_for_a_released_part_and_for_a_part_somebody_else_is_editing()
    {
        var env = await Setup();
        var released = await NewPart(env, "Released");
        var busy = await NewPart(env, "Busy");
        var doc = await NewDoc(env, "Spec");
        await env.Parts.SetStateAsync(Admin, released, "Released", "Test: release", true, null);
        await env.Parts.CheckOutAsync(Bob, busy, null);

        var a = await env.Flows.AddDocumentAsync(Alice, released, doc, "describes", null, null);
        Assert.False(a.IsOk);
        Assert.Contains("change order", a.Message, StringComparison.Ordinal);
        var b = await env.Flows.AddDocumentAsync(Alice, busy, doc, "describes", null, null);
        Assert.False(b.IsOk);
        Assert.Contains("bob is editing this part", b.Message, StringComparison.Ordinal);
        Assert.Empty((await env.Docs.GetPartLinksAsync(Alice, released, null, null, null)).Value!.Links);
        Assert.DoesNotContain("part.link_document:Success", await Trail(env.Ds, released));
        Assert.True((await env.Parts.GetAsync(Bob, busy, null, null)).Value!.Checkout!.IsMine); // Bob still holds his
    }

    [Fact]
    public async Task A_document_created_with_a_link_is_linked_and_one_that_would_be_refused_is_not_created()
    {
        var env = await Setup();
        var number = await NewPart(env);
        var (doc, link) = await env.Flows.CreateDocumentAndLinkAsync(Alice, new CreateDocumentCommand("Assembly drawing", "Drawing", Itar()), File("asm.txt"), number, "describes", null);
        Assert.True(doc.IsOk);
        Assert.True(link!.IsOk, link.Message);
        Assert.Equal(doc.Value!.Number, Assert.Single((await env.Docs.GetPartLinksAsync(Alice, number, null, null, null)).Value!.Links).Document!.Number);

        await env.Parts.SetStateAsync(Admin, number, "Released", "Test: release", true, null);
        var before = (await env.Docs.SearchAsync(Alice, null, null, null, 50, 0, null)).Total;
        var (refused, none) = await env.Flows.CreateDocumentAndLinkAsync(Alice, new CreateDocumentCommand("Late drawing", "Drawing", Itar()), null, number, "describes", null);
        Assert.False(refused.IsOk);
        Assert.Null(none);
        Assert.Contains("The document was not created", refused.Errors[0], StringComparison.Ordinal);
        Assert.Equal(before, (await env.Docs.SearchAsync(Alice, null, null, null, 50, 0, null)).Total);

        // no part chosen: just a document
        var (plain, noLink) = await env.Flows.CreateDocumentAndLinkAsync(Alice, new CreateDocumentCommand("Standalone", "Drawing", Itar()), null, null, "describes", null);
        Assert.True(plain.IsOk);
        Assert.Null(noLink);
    }

    [Fact]
    public async Task The_part_picker_offers_only_readable_parts_and_marks_the_ones_that_cannot_take_a_document()
    {
        var env = await Setup();
        var open = await NewPart(env, "Pickable bracket");
        var released = await NewPart(env, "Pickable released");
        await env.Parts.SetStateAsync(Admin, released, "Released", "Test: release", true, null);
        var hidden = await env.Parts.CreateAsync(Kim, new CreatePartCommand("Pickable kraken", "Component", "EA", "Make", Itar("KRAKEN"), "x", new JsonObject()), null);
        Assert.True(hidden.IsOk);

        var items = await env.Flows.FindPartsAsync(Alice, "Pickable");
        Assert.Contains(items, i => i.Number == open && !i.Disabled);
        Assert.Contains(items, i => i.Number == released && i.Disabled && i.Note!.Contains("change order", StringComparison.Ordinal));
        Assert.DoesNotContain(items, i => i.Number == hidden.Value!.Number); // not readable: not offered
        var viewer = await env.Flows.FindPartsAsync(User("vic", roles: [Roles.Viewer]), "Pickable");
        Assert.All(viewer, i => Assert.True(i.Disabled)); // can read, cannot change
    }

    // ---------------------------------------------------------------- remembered choices and Simple view

    [Fact]
    public async Task Last_choices_are_remembered_per_user_and_unknown_keys_are_ignored()
    {
        var env = await Setup();
        await env.Last.SetAsync(Alice, new Dictionary<string, string> { [LastChoiceKeys.PartType] = "Assembly", [LastChoiceKeys.LinkType] = "references", ["evil.key"] = "x", [LastChoiceKeys.PartUnit] = " " });
        await env.Last.SetAsync(Alice, new Dictionary<string, string> { [LastChoiceKeys.PartType] = "Standard" });
        var mine = await env.Last.GetAsync(Alice);
        Assert.Equal("Standard", mine[LastChoiceKeys.PartType]);
        Assert.Equal("references", mine[LastChoiceKeys.LinkType]);
        Assert.Equal(2, mine.Count);
        Assert.Empty(await env.Last.GetAsync(Bob));
        await using var cmd = env.Ds.CreateCommand("INSERT INTO ws.last_choice (user_sub, key, value, updated_at) VALUES ('x', 'nope', 'v', now())");
        await Assert.ThrowsAnyAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync()); // the table itself only holds the known keys
    }

    [Fact]
    public async Task Simple_view_is_saved_with_the_display_preferences()
    {
        var env = await Setup();
        Assert.False((await env.Workspace.GetPreferencesAsync(Alice)).SimpleView);
        await env.Workspace.SetPreferencesAsync(Alice, new UserPreferences(ThemeNames.Dark, DensityNames.Compact, true, SimpleView: true));
        Assert.Equal(new UserPreferences("dark", "compact", true, true), await env.Workspace.GetPreferencesAsync(Alice));
        Assert.True((await env.Workspace.GetPreferencesForShellAsync(Alice.Id)).SimpleView);
        Assert.False((await env.Workspace.GetPreferencesAsync(Bob)).SimpleView);
    }
}
