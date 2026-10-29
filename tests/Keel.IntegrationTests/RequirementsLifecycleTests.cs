using Keel.Core;
using Keel.Requirements;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Specifications and their requirements: hierarchy, edits as iterations, marking dominance, release by review with locks, revisions, and what the audit log may hold.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RequirementsLifecycleTests(PostgresFixture pg)
{
    [Fact]
    public async Task Requirements_form_a_hierarchy_and_every_edit_is_one_new_iteration()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync();
        var a = await env.AddAsync(spec, ReqEnv.Input("Depth rating"));
        var b = await env.AddAsync(spec, ReqEnv.Input("Rated to 6000 m", parent: a));
        var c = await env.AddAsync(spec, ReqEnv.Input("Operating temperature", type: "environmental"));
        var d = await env.AddAsync(spec, ReqEnv.Input("Survives 4 C", parent: c, priority: "medium"));
        Assert.Equal(["R-0001", "R-0002", "R-0003", "R-0004"], new[] { a, b, c, d });

        var set = await env.SetAsync(spec);
        Assert.Equal(["1", "1.1", "2", "2.1"], set.Rows.Select(r => r.Outline));
        Assert.Equal([0, 1, 0, 1], set.Rows.Select(r => r.Depth));
        Assert.Equal(5, (await env.Req.GetAsync(ReqEnv.Alice, spec, null, null)).Value!.Displayed.LatestIteration);

        // Move the second parent's child under the first parent, before its sibling: the outline renumbers; the key stays.
        var moved = await env.Req.EditAsync(ReqEnv.Alice, spec, new EditCommand(5, "Reorganized", [new EditOp("move", d, new RequirementInput("", "", "", "", "", ParentKey: a, BeforeKey: b))]), null);
        Assert.True(moved.IsOk, string.Join("; ", moved.Errors));
        var after = await env.SetAsync(spec);
        Assert.Equal([a, d, b, c], after.Rows.Select(r => r.Key));
        Assert.Equal(["1", "1.1", "1.2", "2"], after.Rows.Select(r => r.Outline));

        // A stale base iteration is a conflict, a cycle is refused, and a refused batch changes nothing.
        var stale = await env.Req.EditAsync(ReqEnv.Alice, spec, new EditCommand(1, null, [new EditOp("move", a, new RequirementInput("", "", "", "", "", ParentKey: null))]), null);
        Assert.Equal(OutcomeKind.Conflict, stale.Kind);
        var cycle = await env.Req.EditAsync(ReqEnv.Alice, spec, new EditCommand(6, null, [new EditOp("move", a, new RequirementInput("", "", "", "", "", ParentKey: b))]), null);
        Assert.Equal(OutcomeKind.Invalid, cycle.Kind);
        Assert.Contains("below itself", cycle.Errors[0], StringComparison.Ordinal);
        var batch = await env.Req.EditAsync(ReqEnv.Alice, spec, new EditCommand(6, null, [new EditOp("add", null, ReqEnv.Input("Fine")), new EditOp("add", null, ReqEnv.Input("", "no title"))]), null);
        Assert.Equal(OutcomeKind.Invalid, batch.Kind);
        Assert.Equal(6, await env.IterationAsync(spec));

        // Iterations are immutable, requirements are never deleted.
        await Assert.ThrowsAnyAsync<PostgresException>(() => env.C.Db.ExecuteAsSuperuserAsync("UPDATE req.requirement_version SET title = 'x'"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => env.C.Db.ExecuteAsSuperuserAsync("DELETE FROM req.requirement"));
        // A new iteration can add children in the same batch by reference (#n).
        var batchOk = await env.Req.EditAsync(ReqEnv.Alice, spec, new EditCommand(6, null, [new EditOp("add", null, ReqEnv.Input("Parent")), new EditOp("add", null, ReqEnv.Input("Child", parent: "#0"))]), null);
        Assert.True(batchOk.IsOk, string.Join("; ", batchOk.Errors));
        Assert.Equal(["R-0005", "R-0006"], batchOk.Value!.CreatedKeys);
    }

    [Fact]
    public async Task A_requirements_markings_must_cover_the_specifications_and_can_be_raised_on_confirmation()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync("Restricted set", ReqEnv.Itar);
        var less = await env.Req.AddAsync(ReqEnv.Alice, spec, 1, ReqEnv.Input("Too open", markings: ReqEnv.Open), null);
        Assert.Equal(OutcomeKind.Invalid, less.Kind);
        Assert.Contains("at least as restrictive", less.Errors[0], StringComparison.Ordinal);
        var raised = await env.Req.AddAsync(ReqEnv.Alice, spec, 1, ReqEnv.Input("Raised", markings: ReqEnv.Open, confirm: true), null);
        Assert.True(raised.IsOk, string.Join("; ", raised.Errors));
        Assert.Contains("ITAR", (await env.SetAsync(spec)).Rows.Single().Banner!, StringComparison.Ordinal);

        // A requirement more restrictive than its specification is allowed (the specification's reader may then see a placeholder).
        var spec2 = await env.SpecAsync("Open set");
        var key = await env.AddAsync(spec2, ReqEnv.Input("Secret detail", markings: ReqEnv.Itar));
        var eve = await env.SetAsync(spec2, ReqEnv.Eve);
        Assert.Single(eve.Rows);
        Assert.True(eve.Rows[0].Redacted);
        Assert.Null(eve.Rows[0].Key);
        Assert.Null(eve.Rows[0].Title);
        Assert.Equal("1", eve.Rows[0].Outline);
        Assert.NotNull(key);
    }

    [Fact]
    public async Task A_specification_is_released_by_review_and_then_locked_until_revised()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync();
        // An empty specification, or one with Draft requirements, is refused at submission.
        var empty = await env.Flow.StartAsync(ReqEnv.Alice, ReqEnv.Start(spec), null);
        Assert.False(empty.IsOk);
        var key = await env.AddAsync(spec, ReqEnv.Input("Draft one", status: ReqStatuses.Draft));
        Assert.False((await env.Flow.StartAsync(ReqEnv.Alice, ReqEnv.Start(spec), null)).IsOk);
        await env.UpdateAsync(spec, key, ReqEnv.Input("Draft one", status: ReqStatuses.Approved));

        await env.ReleaseAsync(spec);
        var detail = (await env.Req.GetAsync(ReqEnv.Alice, spec, null, null)).Value!;
        Assert.Equal("Released", detail.Summary.State);
        Assert.False(detail.Editable);

        // Locked: the service refuses, and so does the database (even for the table owner).
        var edit = await env.Req.AddAsync(ReqEnv.Alice, spec, detail.Displayed.LatestIteration, ReqEnv.Input("Late"), null);
        Assert.Equal(OutcomeKind.Invalid, edit.Kind);
        await Assert.ThrowsAnyAsync<PostgresException>(() => env.C.Db.ExecuteAsSuperuserAsync(
            "INSERT INTO req.spec_iteration (id, revision_id, iteration, content_sha256, requirement_count, created_at, created_by) SELECT gen_random_uuid(), id, 2, repeat('a', 64), 0, now(), 'x' FROM req.spec_revision"));
        Assert.Equal(1, await env.C.ScalarAsync("SELECT count(*) FROM req.review_binding"));

        // Revise: B is a copy, In Work, A stays Released until B is.
        var revised = await env.Req.ReviseAsync(ReqEnv.Alice, spec, "Add a requirement", null);
        Assert.True(revised.IsOk, string.Join("; ", revised.Errors));
        Assert.Equal("B", revised.Value!.Displayed.Revision);
        Assert.Single((await env.SetAsync(spec)).Rows);
        Assert.Equal("Released", (await env.Req.GetAsync(ReqEnv.Alice, spec, "A", null)).Value!.Summary.State);
        await env.AddAsync(spec, ReqEnv.Input("Second"));
        await env.ReleaseAsync(spec);
        Assert.Equal("Obsolete", (await env.Req.GetAsync(ReqEnv.Alice, spec, "A", null)).Value!.Summary.State);
        Assert.Equal("Released", (await env.Req.GetAsync(ReqEnv.Alice, spec, "B", null)).Value!.Summary.State);
        Assert.Single((await env.SetAsync(spec, revision: "A")).Rows);
        Assert.Equal(2, (await env.SetAsync(spec, revision: "B")).Rows.Count);
    }

    [Fact]
    public async Task A_row_edited_after_the_review_began_makes_the_release_refuse()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync();
        await env.AddAsync(spec, ReqEnv.Input("One"));
        var started = (await env.Flow.StartAsync(ReqEnv.Alice, ReqEnv.Start(spec), null)).Value!;
        await env.C.Db.ExecuteAsSuperuserAsync("ALTER TABLE req.requirement_version DISABLE TRIGGER requirement_version_immutable");
        await env.C.Db.ExecuteAsSuperuserAsync("UPDATE req.requirement_version SET body = 'tampered after submission'");
        var rita = started.Tasks.Single(t => t.AssigneeUsername == "rita");
        var signed = await env.Flow.SignAsync(WorkflowEnv.Fresh(ConfigEnv.Rita), rita.Id, "Reviewed", null, null);
        // The signature covers the live digest: it was computed from the tampered row, so the later steps fail verification or the release refuses.
        var v = signed.Value ?? started;
        foreach (var user in new[] { ConfigEnv.Bob, ConfigEnv.Carol })
            if (v.Tasks.FirstOrDefault(t => t.Status == "Open" && t.AssigneeUsername == user.Username) is { } t)
            {
                var r = await env.Flow.SignAsync(WorkflowEnv.Fresh(user), t.Id, "Approved", null, null);
                if (r.IsOk) v = r.Value!;
            }
        Assert.NotEqual("Released", (await env.Req.GetAsync(ReqEnv.Alice, spec, null, null)).Value!.Summary.State);
    }

    [Fact]
    public async Task Requirement_text_never_reaches_the_audit_log_or_a_notification_payload()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync("Name-marker-QRS");
        var key = await env.AddAsync(spec, ReqEnv.Input("Title-marker-ABC", "Text-marker-XYZ shall be secret."));
        await env.UpdateAsync(spec, key, ReqEnv.Input("Title-marker-ABC", "Text-marker-XYZ changed."));
        await env.Req.SetMarkingsAsync(ReqEnv.Alice, spec, ReqEnv.Itar, "Reason-text-visible", true, null);
        _ = await env.Req.GetRequirementsAsync(ReqEnv.Alice, spec, null, null);
        _ = await env.Req.GetTraceAsync(ReqEnv.Alice, spec, null, null);
        var verified = await env.Req.RecordVerificationAsync(ReqEnv.Alice, spec, key, new RecordVerificationCommand("pass", null, "Notes-marker-NNN", null, null, false, null), null);
        Assert.True(verified.IsOk, string.Join("; ", verified.Errors));
        var json = await env.AuditJsonAsync();
        foreach (var marker in new[] { "Title-marker-ABC", "Text-marker-XYZ", "Notes-marker-NNN", "Name-marker-QRS" })
            Assert.DoesNotContain(marker, json, StringComparison.Ordinal);
        Assert.Contains("requirement.edit", json, StringComparison.Ordinal);
        Assert.Contains("requirement.verify", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_actions_are_audited_and_look_like_not_found()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync("Orca set", ReqEnv.Itar);
        Assert.Equal(OutcomeKind.NotFound, (await env.Req.GetAsync(ReqEnv.Eve, spec, null, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Req.GetRequirementsAsync(ReqEnv.Eve, spec, null, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Req.GetAsync(ReqEnv.Alice, "RS-999999", null, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Req.AddAsync(ReqEnv.Mallory, await env.SpecAsync("Open"), 1, ReqEnv.Input("x"), null)).Kind);
        Assert.Contains(await env.C.AuditAsync(), e => e.Action == "requirement.read" && e.Outcome == Keel.Audit.AuditOutcome.Denied);
        var page = (await env.Req.SearchAsync(ReqEnv.Eve, new SpecSearch(), null)).Value!;
        Assert.DoesNotContain(page.Items, i => i.Number == spec);
    }
}
