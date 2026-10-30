using System.Text;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Compare;
using Keel.Documents.Vault;
using Keel.Notifications;
using Keel.Security;
using Keel.TestSupport;
using Npgsql;
using UglyToad.PdfPig;

namespace Keel.IntegrationTests;

/// <summary>
/// Cross-document compare and redline review (increment 9.5, ADR-0053) against real PostgreSQL and a real vault: word-level comparison of versions and of documents, honest
/// "not comparable" results, the access rules on BOTH documents (including the SQL oracle for the review list), review threads, notifications, exports and the database guards.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RedlineTests(PostgresFixture pg)
{
    private static readonly Subject Alice = WorkflowEnv.User("alice", [Roles.Viewer, Roles.Author]);
    private static readonly Subject Bob = WorkflowEnv.User("bob", [Roles.Viewer, Roles.Approver]);
    private static readonly Subject Rita = WorkflowEnv.User("rita", [Roles.Viewer]);
    private static readonly Subject Ada = WorkflowEnv.User("ada", [Roles.Admin, Roles.Viewer]);
    private static readonly Subject Audrey = WorkflowEnv.User("audrey", [Roles.Auditor]);
    private static readonly Subject Erp = WorkflowEnv.User("erp", [Roles.Integration]);
    private static readonly Subject Lena = WorkflowEnv.User("lena", [Roles.Viewer], us: false);
    private static readonly Subject Eve = WorkflowEnv.User("eve", [Roles.Viewer, Roles.Author], programs: []);

    private sealed record Env(DocumentService Docs, AwarenessEnv Aw)
    {
        public NpgsqlDataSource Ds => Aw.Ds;

        public async Task<List<AuditRecord>> AuditAsync() => [.. await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 5000))];

        public async Task ExecAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(Aw.Db.SuperuserConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<long> CountAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(Aw.Db.SuperuserConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private async Task<Env> SetupAsync()
    {
        var aw = await AwarenessEnv.CreateAsync(pg);
        foreach (var u in new[] { Alice, Bob, Rita, Ada, Audrey, Erp, Lena, Eve }) await aw.Directory.UpsertAsync(u);
        var docs = new DocumentService(aw.Ds, aw.Audit, aw.Clock, new ObjectVault(await VaultContainer.OptionsAsync()), null, aw.Sink);
        return new Env(docs, aw);
    }

    private static FileUpload Text(string text, string name = "spec.txt") => new(name, "text/plain", new MemoryStream(Encoding.UTF8.GetBytes(text)));

    private static async Task<string> CreateAsync(Env env, string text, Markings? m = null, string name = "Pod specification", string file = "spec.txt", Subject? by = null)
    {
        var r = await env.Docs.CreateAsync(by ?? Alice, new CreateDocumentCommand(name, "Specification", m ?? DocumentServiceTests.Itar(), "Synthetic"), Text(text, file), "corr-create");
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        return r.Value!.Number;
    }

    private static async Task NewVersionAsync(Env env, string number, string text, string note = "Edited", string file = "spec.txt")
    {
        var r = await env.Docs.UploadNewVersionAsync(Alice, number, Text(text, file), note, false, null, "corr-version");
        Assert.True(r.IsOk, $"{r.Kind}: {string.Join("; ", r.Errors)}");
    }

    private static async Task<CompareVersionList> VersionsAsync(Env env, string number, Subject? by = null)
    {
        var r = await env.Docs.ListCompareVersionsAsync(by ?? Alice, number, "corr");
        Assert.True(r.IsOk, $"{r.Kind}: {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    /// <summary>A document with two text versions and the comparison refs of them.</summary>
    private async Task<(Env Env, string Number, CompareRef A, CompareRef B)> TwoVersionsAsync(string v1 = "The wall is 4 mm thick.\nThe finish is black.", string v2 = "The wall is 6 mm thick.\nThe finish is black anodize.")
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env, v1);
        await NewVersionAsync(env, number, v2);
        var list = await VersionsAsync(env, number);
        return (env, number, new CompareRef(number, list.Versions[0].IterationId), new CompareRef(number, list.Versions[1].IterationId));
    }

    // ------------------------------------------------------------------ comparison

    [Fact]
    public async Task Two_versions_of_a_text_document_are_compared_word_by_word_and_the_view_is_audited_without_text()
    {
        var (env, number, a, b) = await TwoVersionsAsync();
        var r = await env.Docs.CompareDocumentsAsync(Alice, a, b, "corr-cmp");
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        var c = r.Value!;
        Assert.Equal(CompareModes.Text, c.Mode);
        Assert.True(c.SameDocument);
        Assert.Equal("A.1", c.Left.Label);
        Assert.Equal("A.2", c.Right.Label);
        Assert.Equal("text", c.Left.Format);
        Assert.Equal(2, c.Diff!.Counts.Changes); // "4" -> "6", and "black" -> "black anodize" (an insertion)
        Assert.Equal(2, c.Diff.Counts.InsertedWords);
        Assert.Equal(1, c.Diff.Counts.DeletedWords);
        Assert.Contains("ITAR", c.Banner);
        Assert.Contains(c.Metadata, m => m.Field == "SHA-256" && !m.Same);
        Assert.Empty(c.Threads);

        var audits = (await env.AuditAsync()).Where(e => e.Action == DocumentService.CompareAction).ToList();
        var one = Assert.Single(audits); // one event: both sides are the same document
        Assert.Equal(AuditOutcome.Success, one.Outcome);
        Assert.Equal(number, one.ObjectId);
        Assert.Contains("A.1", one.Payload);
        Assert.DoesNotContain("anodize", one.Payload);
        Assert.DoesNotContain("thick", one.Payload);
    }

    [Fact]
    public async Task Two_documents_compare_the_banner_is_the_aggregate_and_each_document_gets_an_audit_event()
    {
        var env = await SetupAsync();
        var open = await CreateAsync(env, "Common clause.\nOpen only clause.", DocumentServiceTests.Open, "Open note");
        var itar = await CreateAsync(env, "Common clause.\nControlled clause.", DocumentServiceTests.Itar(), "Controlled note");
        var r = await env.Docs.CompareDocumentsAsync(Alice, new CompareRef(open), new CompareRef(itar), "corr");
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        var c = r.Value!;
        Assert.False(c.SameDocument);
        Assert.Contains("ITAR", c.Banner);
        Assert.Contains("DISTRIBUTION D", c.Banner);
        Assert.Contains("PROGRAM ORCA", c.Banner);
        Assert.Contains(c.Notices, n => n.Contains("DISTRIBUTION STATEMENT D"));
        Assert.Contains(c.Notices, n => n.Contains("DISTRIBUTION STATEMENT A"));
        Assert.Equal(1, c.Diff!.Counts.Changes);
        var events = (await env.AuditAsync()).Where(e => e.Action == DocumentService.CompareAction).Select(e => e.ObjectId!).Order().ToList();
        Assert.Equal(new[] { open, itar }.Order().ToList(), events);
    }

    [Fact]
    public async Task Identical_bytes_unreadable_formats_and_mixed_formats_get_honest_results()
    {
        var env = await SetupAsync();
        var same1 = await CreateAsync(env, "Same content\n", name: "Copy one");
        var same2 = await CreateAsync(env, "Same content\n", name: "Copy two");
        var identical = (await env.Docs.CompareDocumentsAsync(Alice, new CompareRef(same1), new CompareRef(same2), "corr")).Value!;
        Assert.Equal(CompareModes.Identical, identical.Mode);
        Assert.True(identical.SameBytes);
        Assert.Null(identical.Diff);

        var bin1 = await env.Docs.CreateAsync(Alice, new CreateDocumentCommand("Model one", "CAD", DocumentServiceTests.Itar(), "Synthetic"), new FileUpload("a.dwg", "application/octet-stream", new MemoryStream([1, 2, 3, 4])), "corr");
        var bin2 = await env.Docs.CreateAsync(Alice, new CreateDocumentCommand("Model two", "CAD", DocumentServiceTests.Itar(), "Synthetic"), new FileUpload("b.dwg", "application/octet-stream", new MemoryStream([1, 2, 3, 5, 6])), "corr");
        var bothBinary = (await env.Docs.CompareDocumentsAsync(Alice, new CompareRef(bin1.Value!.Number), new CompareRef(bin2.Value!.Number), "corr")).Value!;
        Assert.Equal(CompareModes.Metadata, bothBinary.Mode);
        Assert.False(bothBinary.SameBytes);
        Assert.Contains("Content not comparable", bothBinary.NotComparableReason);
        Assert.Null(bothBinary.Diff);
        Assert.Contains(bothBinary.Metadata, m => m.Field == "Size" && m.Left == "4 bytes" && m.Right == "5 bytes");
        Assert.Contains(bothBinary.Metadata, m => m.Field == "SHA-256" && !m.Same);

        var mixed = (await env.Docs.CompareDocumentsAsync(Alice, new CompareRef(same1), new CompareRef(bin1.Value.Number), "corr")).Value!;
        Assert.Equal(CompareModes.Metadata, mixed.Mode);
        Assert.Contains(bin1.Value.Number, mixed.NotComparableReason);

        // Text that is really binary says why.
        var fake = await CreateAsync(env, "x", name: "Fake text");
        var upload = await env.Docs.UploadNewVersionAsync(Alice, fake, new FileUpload("spec.txt", "text/plain", new MemoryStream([65, 0, 0, 66, 1])), "binary now", false, null, "corr");
        Assert.True(upload.IsOk);
        var list = await VersionsAsync(env, fake);
        var binaryText = (await env.Docs.CompareDocumentsAsync(Alice, new CompareRef(fake, list.Versions[0].IterationId), new CompareRef(fake, list.Versions[1].IterationId), "corr")).Value!;
        Assert.Equal(CompareModes.Metadata, binaryText.Mode);
        Assert.Contains("binary", binaryText.NotComparableReason);
    }

    [Fact]
    public async Task Invalid_requests_are_refused_plainly()
    {
        var (env, number, a, _) = await TwoVersionsAsync();
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.CompareDocumentsAsync(Alice, a, a, "corr")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.CompareDocumentsAsync(Alice, a, new CompareRef(number, Guid.NewGuid()), "corr")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.CompareDocumentsAsync(Alice, a, new CompareRef("D-999999"), "corr")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.CompareDocumentsAsync(Alice, new CompareRef(""), a, "corr")).Kind);
    }

    // ------------------------------------------------------------------ access

    [Fact]
    public async Task A_document_the_caller_cannot_read_makes_the_whole_comparison_a_404_and_the_denial_is_audited_on_it()
    {
        var env = await SetupAsync();
        var open = await CreateAsync(env, "Open text.", DocumentServiceTests.Open, "Open note");
        var secret = await CreateAsync(env, "Secret text.", DocumentServiceTests.Itar(), "Secret note");
        foreach (var nobody in new[] { Lena, Eve })
        {
            var one = await env.Docs.CompareDocumentsAsync(nobody, new CompareRef(open), new CompareRef(secret), "corr-deny");
            Assert.Equal(OutcomeKind.NotFound, one.Kind);
            Assert.Null(one.Value);
            // Which side refused is not disclosed: the other order gives the same answer.
            Assert.Equal(OutcomeKind.NotFound, (await env.Docs.CompareDocumentsAsync(nobody, new CompareRef(secret), new CompareRef(open), "corr-deny")).Kind);
            Assert.Equal(OutcomeKind.NotFound, (await env.Docs.AddRedlineCommentAsync(nobody, new CompareRef(open), new CompareRef(secret), 0, "Sneaky", null, "corr")).Kind);
            Assert.Equal(OutcomeKind.NotFound, (await env.Docs.ExportRedlineAsync(nobody, new CompareRef(open), new CompareRef(secret), "pdf", "corr")).Kind);
            Assert.Equal(OutcomeKind.NotFound, (await env.Docs.ListCompareVersionsAsync(nobody, secret, "corr")).Kind);
        }
        var denied = (await env.AuditAsync()).Where(e => e.Action == DocumentService.CompareAction && e.Outcome == AuditOutcome.Denied).ToList();
        Assert.NotEmpty(denied);
        Assert.All(denied, e => Assert.Equal(secret, e.ObjectId)); // the denial is written on the document that refused, never on the readable one
        Assert.DoesNotContain("Secret text", string.Join(' ', (await env.AuditAsync()).Select(e => e.Payload)));
    }

    [Fact]
    public async Task Without_permission_to_download_both_only_the_facts_are_compared_and_no_text_is_returned()
    {
        var (env, _, a, b) = await TwoVersionsAsync("Controlled words one.", "Controlled words two.");
        var facts = await env.Docs.CompareDocumentsAsync(Erp, a, b, "corr"); // the integration role may read, not download
        Assert.True(facts.IsOk, string.Join("; ", facts.Errors));
        Assert.Equal(CompareModes.Metadata, facts.Value!.Mode);
        Assert.Null(facts.Value.Diff);
        Assert.Contains("download", facts.Value.NotComparableReason);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Docs.AddRedlineCommentAsync(Erp, a, b, 0, "Not allowed", null, "corr")).Kind);
        // The auditor may download but not write comments.
        Assert.Equal(CompareModes.Text, (await env.Docs.CompareDocumentsAsync(Audrey, a, b, "corr")).Value!.Mode);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Docs.AddRedlineCommentAsync(Audrey, a, b, 0, "Auditors read", null, "corr")).Kind);
    }

    // ------------------------------------------------------------------ threads

    [Fact]
    public async Task Comments_replies_and_resolution_follow_the_rules_and_are_audited_without_text()
    {
        var (env, number, a, b) = await TwoVersionsAsync();
        var posted = await env.Docs.AddRedlineCommentAsync(Alice, a, b, 0, "Why six millimetres?", null, "corr-c1");
        Assert.True(posted.IsOk, string.Join("; ", posted.Errors));
        Assert.False(posted.Value!.IsReply);
        var reply = await env.Docs.AddRedlineCommentAsync(Bob, a, b, 0, "Mass budget allows it.", posted.Value.Id, "corr-c2");
        Assert.True(reply.IsOk, string.Join("; ", reply.Errors));
        Assert.Equal(posted.Value.ReviewId, reply.Value!.ReviewId);

        var seen = (await env.Docs.CompareDocumentsAsync(Rita, a, b, "corr")).Value!;
        var thread = Assert.Single(seen.Threads);
        Assert.Equal(0, thread.ChangeIndex);
        Assert.False(thread.Detached);
        Assert.False(thread.Resolved);
        Assert.Equal("Why six millimetres?", thread.Body);
        Assert.Equal("Mass budget allows it.", Assert.Single(thread.Replies).Body);
        Assert.Equal(1, seen.OpenThreads);
        Assert.False(thread.CanResolve); // Rita is a plain reviewer and not the author

        // Rules.
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.AddRedlineCommentAsync(Alice, a, b, 99, "No such change", null, "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.AddRedlineCommentAsync(Alice, a, b, 0, "   ", null, "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.AddRedlineCommentAsync(Alice, a, b, 0, new string('x', DocumentService.MaxRedlineCommentLength + 1), null, "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.AddRedlineCommentAsync(Alice, a, b, 1, "Wrong change for the thread", posted.Value.Id, "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.AddRedlineCommentAsync(Alice, a, b, 0, "Reply to a reply", reply.Value.Id, "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.AddRedlineCommentAsync(Alice, a, b, 0, "Bad\u0007char", null, "c")).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Docs.SetRedlineThreadResolvedAsync(Rita, posted.Value.Id, true, "c")).Kind);

        // The author, and an approver (who may sign the document), can resolve; resolving twice and replying to a resolved thread are refused.
        Assert.True((await env.Docs.SetRedlineThreadResolvedAsync(Bob, posted.Value.Id, true, "corr-r")).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.SetRedlineThreadResolvedAsync(Alice, posted.Value.Id, true, "c")).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.AddRedlineCommentAsync(Alice, a, b, 0, "Late reply", posted.Value.Id, "c")).Kind);
        var resolved = (await env.Docs.CompareDocumentsAsync(Alice, a, b, "c")).Value!;
        Assert.True(resolved.Threads[0].Resolved);
        Assert.Equal("Bob Example", resolved.Threads[0].ResolvedByName);
        Assert.Equal(0, resolved.OpenThreads);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.SetRedlineThreadResolvedAsync(Alice, reply.Value.Id, true, "c")).Kind); // only whole threads
        Assert.True((await env.Docs.SetRedlineThreadResolvedAsync(Alice, posted.Value.Id, false, "c")).IsOk);
        Assert.Equal(1, (await env.Docs.CompareDocumentsAsync(Alice, a, b, "c")).Value!.OpenThreads);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.SetRedlineThreadResolvedAsync(Alice, posted.Value.Id, false, "c")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.SetRedlineThreadResolvedAsync(Alice, Guid.NewGuid(), true, "c")).Kind);

        // Audit: names the review and the comment, never the text; one event per distinct document (here one document).
        var events = (await env.AuditAsync()).Where(e => e.Action is DocumentService.RedlineCommentAction or DocumentService.RedlineResolveAction).ToList();
        Assert.Contains(events, e => e.Action == DocumentService.RedlineCommentAction && e.Outcome == AuditOutcome.Success && e.Payload.Contains(posted.Value.Id.ToString("D"), StringComparison.Ordinal));
        Assert.Contains(events, e => e.Action == DocumentService.RedlineResolveAction && e.Outcome == AuditOutcome.Success && e.Payload.Contains("resolved", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Action == DocumentService.RedlineResolveAction && e.Outcome == AuditOutcome.Denied);
        Assert.All(events, e => { Assert.DoesNotContain("millimetres", e.Payload); Assert.DoesNotContain("Mass budget", e.Payload); Assert.Equal(number, e.ObjectId); });
    }

    [Fact]
    public async Task A_comment_between_two_documents_is_audited_on_both_and_hidden_from_someone_who_cannot_read_either()
    {
        var env = await SetupAsync();
        var one = await CreateAsync(env, "Alpha beta gamma.", DocumentServiceTests.Open, "Open one");
        var two = await CreateAsync(env, "Alpha delta gamma.", DocumentServiceTests.Itar(), "Itar two");
        var posted = await env.Docs.AddRedlineCommentAsync(Alice, new CompareRef(one), new CompareRef(two), 0, "Which word is right?", null, "corr");
        Assert.True(posted.IsOk, string.Join("; ", posted.Errors));
        var events = (await env.AuditAsync()).Where(e => e.Action == DocumentService.RedlineCommentAction && e.Outcome == AuditOutcome.Success).Select(e => e.ObjectId!).Order().ToList();
        Assert.Equal(new[] { one, two }.Order().ToList(), events);

        // Lena can read the open document but not the ITAR one: she sees nothing of the review, by any route.
        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.CompareDocumentsAsync(Lena, new CompareRef(one), new CompareRef(two), "c")).Kind);
        Assert.Empty(await env.Docs.ListRedlineReviewsAsync(Lena, one));
        Assert.Empty(await env.Docs.ListRedlineReviewsAsync(Lena, two));
        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.ResolveRedlineReviewAsync(Lena, posted.Value!.ReviewId, "c")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.SetRedlineThreadResolvedAsync(Lena, posted.Value.Id, true, "c")).Kind);
        Assert.Equal(0, await env.Docs.CountOpenRedlineThreadsAsync(Lena, one));
        var denied = (await env.AuditAsync()).Where(e => e.Outcome == AuditOutcome.Denied && e.Action is DocumentService.RedlineResolveAction or DocumentService.CompareAction).ToList();
        Assert.NotEmpty(denied);
        Assert.DoesNotContain("Which word", string.Join(' ', (await env.AuditAsync()).Select(e => e.Payload)));

        // Alice sees it, from either document.
        Assert.Single(await env.Docs.ListRedlineReviewsAsync(Alice, one));
        var summary = Assert.Single(await env.Docs.ListRedlineReviewsAsync(Alice, two));
        Assert.Equal(1, summary.OpenThreads);
        Assert.Contains("ITAR", summary.Banner);
        var target = (await env.Docs.ResolveRedlineReviewAsync(Alice, posted.Value.ReviewId, "c")).Value!;
        Assert.Equal(one, target.Left.Number);
        Assert.Equal(two, target.Right.Number);
    }

    [Fact]
    public async Task A_comment_whose_change_no_longer_matches_is_shown_as_detached_not_drawn_on_the_wrong_change()
    {
        var (env, _, a, b) = await TwoVersionsAsync();
        var posted = (await env.Docs.AddRedlineCommentAsync(Alice, a, b, 0, "About the wall", null, "c")).Value!;
        // Simulate a future diff version that numbers changes differently: the stored digest no longer matches change 1.
        await env.ExecAsync("ALTER TABLE rdl.annotation DISABLE TRIGGER annotation_guard");
        await env.ExecAsync($"UPDATE rdl.annotation SET change_digest = repeat('0', 64) WHERE id = '{posted.Id}'");
        await env.ExecAsync("ALTER TABLE rdl.annotation ENABLE TRIGGER annotation_guard");
        var c = (await env.Docs.CompareDocumentsAsync(Alice, a, b, "c")).Value!;
        var thread = Assert.Single(c.Threads);
        Assert.True(thread.Detached);
        Assert.Equal("About the wall", thread.Body);
    }

    [Fact]
    public async Task Comments_notify_the_watchers_of_the_right_hand_document_and_the_pointer_opens_the_review()
    {
        var (env, number, a, b) = await TwoVersionsAsync();
        Assert.True((await env.Aw.Watches.SetAsync(Rita, "document", number, WatchLevel.All, null)).IsOk);
        var posted = (await env.Docs.AddRedlineCommentAsync(Alice, a, b, 0, "Please check.", null, "c")).Value!;
        var item = Assert.Single((await env.Aw.Center.ListAsync(Rita, NotificationFilter.Watching, 0, 10)).Items);
        Assert.Equal("Alice Example commented on a redline", item.Text);
        Assert.Equal($"compare/review/{posted.ReviewId:D}", item.Href);
        // The commenter is not told about their own comment.
        Assert.DoesNotContain((await env.Aw.Center.ListAsync(Alice, NotificationFilter.Watching, 0, 10)).Items, i => i.Text.Contains("redline", StringComparison.Ordinal));
        // A reply tells the author of the thread directly.
        Assert.True((await env.Docs.AddRedlineCommentAsync(Bob, a, b, 0, "Done.", posted.Id, "c")).IsOk);
        Assert.Contains((await env.Aw.Center.ListAsync(Alice, NotificationFilter.All, 0, 10)).Items, i => i.Text.Contains("commented on a redline", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the SQL oracle

    [Fact]
    public async Task The_review_list_agrees_with_the_policy_for_every_subject_and_every_pair_of_markings()
    {
        var env = await SetupAsync();
        Markings Make(ExportJurisdiction j, DistributionStatement d, string? usml = null, string? eccn = null, string[]? cui = null, string? program = null, string[]? auths = null) =>
            Markings.Create(j, d, "ENG", eccn, usml, cui, d == DistributionStatement.A ? null : "ACME", d == DistributionStatement.A ? null : "Reason", program, auths).Value;
        Markings[] variants =
        [
            DocumentServiceTests.Open,
            Make(ExportJurisdiction.None, DistributionStatement.B, cui: ["CTI"]),
            DocumentServiceTests.Itar(),
            Make(ExportJurisdiction.Itar, DistributionStatement.D, usml: "XI(d)", cui: ["EXPT"], program: "ORCA", auths: ["TAA-0042"]),
            Make(ExportJurisdiction.Ear, DistributionStatement.C, eccn: "9A610", cui: ["EXPT"]),
            Make(ExportJurisdiction.Ear, DistributionStatement.C, eccn: "EAR99"),
            Make(ExportJurisdiction.None, DistributionStatement.B, program: "KRAKEN"),
        ];
        var farid = WorkflowEnv.User("farid", [Roles.Viewer], us: false, auths: ["TAA-0042"]);
        var kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);
        Subject[] subjects = [Alice, Bob, Rita, Ada, Audrey, Erp, Lena, Eve, farid, kim];
        foreach (var u in new[] { farid, kim }) await env.Aw.Directory.UpsertAsync(u);

        // A hub document H (the one the list is asked about) and one document per variant, a review between H and each. Reviews are written as an administrator would
        // after a comment: the oracle is about who may READ the list, so the rows are created directly.
        var hub = await CreateAsync(env, "Hub text", DocumentServiceTests.Open, "Hub");
        var others = new List<(string Number, Markings M)>();
        foreach (var m in variants) others.Add((await CreateAsync(env, "Other text " + others.Count, m, "Other " + others.Count, by: kim), m));
        foreach (var (number, _) in others)
            await env.ExecAsync($"""
                INSERT INTO rdl.review (id, left_master_id, left_iteration_id, right_master_id, right_iteration_id, left_sha256, right_sha256, diff_version, extractor_version, created_at, created_by, created_by_name)
                SELECT gen_random_uuid(), lm.id, li.id, rm.id, ri.id, lf.sha256, rf.sha256, 1, 1, now(), 'sub-alice', 'Alice'
                FROM plm.document_master lm JOIN plm.document_revision lr ON lr.master_id = lm.id JOIN plm.document_iteration li ON li.revision_id = lr.id JOIN plm.document_file lf ON lf.iteration_id = li.id AND lf.role = 'primary',
                     plm.document_master rm JOIN plm.document_revision rr ON rr.master_id = rm.id JOIN plm.document_iteration ri ON ri.revision_id = rr.id JOIN plm.document_file rf ON rf.iteration_id = ri.id AND rf.role = 'primary'
                WHERE lm.number = '{hub}' AND rm.number = '{number}'
                """);
        Assert.Equal(others.Count, await env.CountAsync("SELECT count(*) FROM rdl.review"));

        foreach (var subject in subjects)
        {
            bool Allowed(Markings m) => AccessPolicy.Evaluate(subject, m, PlmAction.Read).Permitted && AccessPolicy.Evaluate(subject, m, PlmAction.Download).Permitted;
            var hubAllowed = Allowed(DocumentServiceTests.Open);
            var expected = others.Where(o => hubAllowed && Allowed(o.M)).Select(o => o.Number).Order(StringComparer.Ordinal).ToList();
            var listed = await env.Docs.ListRedlineReviewsAsync(subject, hub);
            Assert.True(expected.SequenceEqual(listed.Select(r => r.RightNumber).Order(StringComparer.Ordinal)), $"reviews listed for {subject.Username}");
            // Asked from the other side the answer is the same, one by one.
            foreach (var (number, m) in others)
                Assert.Equal(Allowed(m) && hubAllowed ? 1 : 0, (await env.Docs.ListRedlineReviewsAsync(subject, number)).Count);
            Assert.Equal(0, listed.Sum(r => r.OpenThreads)); // no comments were written: counts only come from readable reviews
        }
    }

    // ------------------------------------------------------------------ guards

    [Fact]
    public async Task The_database_keeps_reviews_immutable_comments_unalterable_and_refuses_forged_reviews()
    {
        var (env, _, a, b) = await TwoVersionsAsync();
        var posted = (await env.Docs.AddRedlineCommentAsync(Alice, a, b, 0, "Original words", null, "c")).Value!;

        // Defence in depth: the application role has no UPDATE on a review and no DELETE anywhere...
        await using (var app = await env.Ds.OpenConnectionAsync())
        {
            foreach (var sql in new[] { "UPDATE rdl.review SET created_by = 'x'", "DELETE FROM rdl.review", "DELETE FROM rdl.annotation", $"UPDATE rdl.annotation SET body = 'Changed' WHERE id = '{posted.Id}'" })
            {
                await using var cmd = new NpgsqlCommand(sql, app);
                var e = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.True(e.MessageText.Contains("permission denied", StringComparison.Ordinal) || e.MessageText.Contains("immutable", StringComparison.Ordinal) || e.MessageText.Contains("never deleted", StringComparison.Ordinal), e.MessageText);
            }
        }
        // ...and the triggers refuse the same changes even to the schema owner.
        await using var conn = new NpgsqlConnection(env.Aw.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        async Task<string> Fail(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var e = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            return e.MessageText;
        }
        Assert.Contains("immutable", await Fail("UPDATE rdl.review SET created_by = 'x'"));
        Assert.Contains("immutable", await Fail("DELETE FROM rdl.review"));
        Assert.Contains("immutable", await Fail("TRUNCATE rdl.review CASCADE"));
        Assert.Contains("immutable", await Fail($"UPDATE rdl.annotation SET body = 'Changed' WHERE id = '{posted.Id}'"));
        Assert.Contains("immutable", await Fail($"UPDATE rdl.annotation SET author_name = 'Mallory' WHERE id = '{posted.Id}'"));
        Assert.Contains("never deleted", await Fail("DELETE FROM rdl.annotation"));
        // A review that names a file the iteration does not have is refused.
        Assert.Contains("primary file hash", await Fail("""
            INSERT INTO rdl.review (id, left_master_id, left_iteration_id, right_master_id, right_iteration_id, left_sha256, right_sha256, diff_version, extractor_version, created_at, created_by, created_by_name)
            SELECT gen_random_uuid(), r.left_master_id, r.right_iteration_id, r.right_master_id, r.left_iteration_id, repeat('a', 64), repeat('b', 64), 1, 1, now(), 'x', 'x' FROM rdl.review r
            """));
        // A reply must answer a root of the same review and change; a reply cannot be resolved.
        Assert.Contains("same review and change", await Fail($"""
            INSERT INTO rdl.annotation (id, review_id, parent_id, change_index, change_kind, change_digest, body, author_sub, author_name, created_at)
            SELECT gen_random_uuid(), review_id, id, change_index + 1, 'insert', repeat('c', 64), 'wrong change', 'u', 'U', now() FROM rdl.annotation WHERE id = '{posted.Id}'
            """));
        // Resolution is the one thing a root may change.
        await using (var ok = new NpgsqlCommand($"UPDATE rdl.annotation SET resolved_at = now(), resolved_by = 'u', resolved_by_name = 'U' WHERE id = '{posted.Id}'", conn)) Assert.Equal(1, await ok.ExecuteNonQueryAsync());
    }

    // ------------------------------------------------------------------ exports

    [Fact]
    public async Task The_html_and_pdf_reports_carry_the_banner_the_notices_and_the_threads_and_are_audited()
    {
        var (env, number, a, b) = await TwoVersionsAsync();
        Assert.True((await env.Docs.AddRedlineCommentAsync(Alice, a, b, 0, "Review remark one", null, "c")).IsOk);

        var html = (await env.Docs.ExportRedlineAsync(Alice, a, b, "html", "corr-html")).Value!;
        Assert.Equal("text/html; charset=utf-8", html.ContentType);
        Assert.EndsWith(".html", html.FileName);
        var text = Encoding.UTF8.GetString(html.Content);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(html.Banner), text);
        Assert.Contains("Review remark one", text);
        Assert.Contains("DISTRIBUTION STATEMENT D", text);
        Assert.Contains("<ins ", text);

        var pdf = (await env.Docs.ExportRedlineAsync(Alice, a, b, "pdf", "corr-pdf")).Value!;
        Assert.Equal("application/pdf", pdf.ContentType);
        using var doc = PdfDocument.Open(pdf.Content);
        foreach (var page in doc.GetPages()) Assert.True(System.Text.RegularExpressions.Regex.Count(page.Text, "DISTRIBUTION D") >= 2, $"banner on page {page.Number}");
        var all = string.Concat(doc.GetPages().Select(p => p.Text));
        Assert.Contains("Review remark one", all);
        Assert.Contains("DISTRIBUTION STATEMENT D", all);

        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.ExportRedlineAsync(Alice, a, b, "docx", "c")).Kind);
        var exports = (await env.AuditAsync()).Where(e => e.Action == DocumentService.RedlineExportAction && e.Outcome == AuditOutcome.Success).ToList();
        Assert.Equal(2, exports.Count);
        Assert.All(exports, e => { Assert.Equal(number, e.ObjectId); Assert.DoesNotContain("Review remark", e.Payload); });
    }

    [Fact]
    public async Task A_report_of_files_that_are_not_compared_is_still_a_marked_honest_report()
    {
        var env = await SetupAsync();
        var d1 = await env.Docs.CreateAsync(Alice, new CreateDocumentCommand("Model one", "CAD", DocumentServiceTests.Itar(), "Synthetic"), new FileUpload("a.dwg", "application/octet-stream", new MemoryStream([1, 2, 3])), "c");
        var d2 = await env.Docs.CreateAsync(Alice, new CreateDocumentCommand("Model two", "CAD", DocumentServiceTests.Itar(), "Synthetic"), new FileUpload("b.dwg", "application/octet-stream", new MemoryStream([4, 5, 6])), "c");
        var pdf = (await env.Docs.ExportRedlineAsync(Alice, new CompareRef(d1.Value!.Number), new CompareRef(d2.Value!.Number), "pdf", "c")).Value!;
        using var doc = PdfDocument.Open(pdf.Content);
        var all = string.Concat(doc.GetPages().Select(p => p.Text));
        Assert.Contains("Content not comparable", all);
        Assert.Contains("DISTRIBUTION D", all);
    }
}
