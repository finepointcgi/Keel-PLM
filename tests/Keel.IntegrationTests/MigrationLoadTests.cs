using System.Security.Cryptography;
using Keel.Core;
using Keel.Migration;
using Keel.Migration.Canonical;
using Keel.Migration.Fixtures;
using Keel.Migration.Load;
using Keel.Migration.Mapping;
using Keel.Migration.Reconcile;
using Keel.Migration.Reports;
using Keel.Parts;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>The importer against a real database and vault (ADR-0023): history, guards, idempotence, conflicts, fail-closed markings, access, reconciliation.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MigrationLoadTests(PostgresFixture pg)
{
    [Fact]
    public async Task Dry_run_writes_nothing_and_predicts_exactly_what_the_load_does()
    {
        using var k = await MigrationKit.Create(pg);
        var product = Samples.Build(salt: MigrationKit.Salt());
        var dir = k.Package(product);
        var before = await k.RowCounts();
        using var prepared = await k.Prepare(dir);
        var dry = await k.Pipeline.DryRunAsync(prepared, new LoadOptions { Operator = "tester" }, CancellationToken.None);
        Assert.True(dry.Succeeded);
        Assert.Equal(before, await k.RowCounts()); // rows, audit chain head, sequences: all unchanged
        // nothing reached the vault
        foreach (var f in product.Package.Documents.SelectMany(d => d.Revisions).SelectMany(r => r.Iterations).SelectMany(i => i.Files))
            Assert.False(await k.Vault.ExistsAsync(f.Sha256!, f.SizeBytes!.Value));
        Assert.True(dry.Results["files"].Created > 0);

        var (report, result) = await k.Pipeline.LoadAsync(prepared, dry, new LoadOptions { Operator = "tester" }, CancellationToken.None);
        Assert.Equal("Completed", result.Status);
        foreach (var (key, planned) in dry.Results)
            Assert.True(planned.Created == report.Results[key].Created, $"{key}: dry-run {planned.Created}, load {report.Results[key].Created}");
        Assert.Equal(dry.Planned["parts"], result.Created["parts"]);
        Assert.Equal(dry.Planned["bomUsages"], result.Created["bomUsages"]);
        Assert.Equal(dry.Planned["fileBytes"], result.Created["fileBytes"]);
        Assert.True((await k.VerifyAudit()).Valid);
    }

    [Fact]
    public async Task Load_preserves_history_and_the_database_guards_still_hold_afterwards()
    {
        using var k = await MigrationKit.Create(pg);
        var dir = k.Package(Samples.Build(salt: MigrationKit.Salt()));
        var loaded = await k.DryRunAndLoad(dir);
        Assert.Equal("Completed", loaded.Result.Status);

        // master: original timestamp and author (jdoe -> alice), provenance, source id
        Assert.Equal("2020-01-15 08:00:00", await k.Text("SELECT to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') FROM plm.part_master WHERE number = 'A-1000'"));
        Assert.Equal("alice", await k.Text("SELECT created_by FROM plm.part_master WHERE number = 'A-1000'"));
        Assert.Equal(loaded.Result.BatchId.ToString(), await k.Text("SELECT imported_batch_id::text FROM plm.part_master WHERE number = 'A-1000'"));
        Assert.Equal("A-1000", await k.Text("SELECT source_id FROM plm.part_master WHERE number = 'A-1000'"));

        // revisions: ASME labels in source order, legacy label kept, original release date and state
        Assert.Equal("A,B", await k.Text("SELECT string_agg(r.revision, ',' ORDER BY r.revision) FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'P-2002'"));
        Assert.Equal("01,02", await k.Text("SELECT string_agg(r.legacy_revision, ',' ORDER BY r.revision) FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'P-2002'"));
        Assert.Equal("Released", await k.Text("SELECT r.lifecycle_state FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'A'"));
        Assert.Equal("InWork", await k.Text("SELECT r.lifecycle_state FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'B'"));
        Assert.Equal(Samples.Day(20).ToString("yyyy-MM-dd"), (await k.Text("SELECT r.state_changed_at::date::text FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'A'")));
        Assert.Equal("true", (await k.Text("SELECT r.is_latest::text FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'B'")));
        Assert.Equal("false", (await k.Text("SELECT r.is_latest::text FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'A'")));
        // iterations: original sequence, author, comment, legacy properties kept
        Assert.Equal("1:alice:initial,2:alice:fix hole", await k.Text("SELECT string_agg(i.iteration || ':' || i.created_by || ':' || i.comment, ',' ORDER BY i.iteration) FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'P-2001' AND r.revision = 'A'"));
        Assert.Equal("1:alice,2:bob", await k.Text("SELECT string_agg(i.iteration || ':' || i.created_by, ',' ORDER BY i.iteration) FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'B'"));
        Assert.Equal("2", await k.Text("SELECT r.latest_iteration::text FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'B'"));
        Assert.Equal("Aluminum 6061", await k.Text("SELECT i.attributes->>'material' FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'A'"));
        Assert.Equal("ORCA", await k.Text("SELECT legacy_properties->'properties'->>'Project' FROM plm.part_master WHERE number = 'A-1000'"));
        Assert.Equal("jdoe", await k.Text("SELECT legacy_properties->'_source'->>'createdBy' FROM plm.part_master WHERE number = 'A-1000'")); // the original author survives the user mapping
        // BOM attaches to the right parent iteration
        Assert.Equal(3, await k.Count("SELECT count(*) FROM plm.bom_usage u JOIN plm.part_iteration i ON i.id = u.parent_iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'A' AND i.iteration = 1"));
        Assert.Equal(4, await k.Count("SELECT count(*) FROM plm.bom_usage u JOIN plm.part_iteration i ON i.id = u.parent_iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'B' AND i.iteration = 2"));
        Assert.Equal("R1,R2,R3,R4,R5,R6,R7,R8", await k.Text("SELECT array_to_string(u.reference_designators, ',') FROM plm.bom_usage u JOIN plm.part_master c ON c.id = u.child_master_id WHERE c.number = 'P-2005'"));
        // document links: describes pinned to the matching document revision, references to the master
        Assert.Equal("A", await k.Text("SELECT dr.revision FROM plm.part_document_link l JOIN plm.document_revision dr ON dr.id = l.document_revision_id JOIN plm.part_iteration i ON i.id = l.part_iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'A' AND l.link_type = 'describes'"));
        Assert.Equal("B", await k.Text("SELECT dr.revision FROM plm.part_document_link l JOIN plm.document_revision dr ON dr.id = l.document_revision_id JOIN plm.part_iteration i ON i.id = l.part_iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'B' AND l.link_type = 'describes'"));
        Assert.Equal(1, await k.Count("SELECT count(*) FROM plm.part_document_link WHERE link_type = 'references' AND document_revision_id IS NULL"));

        // guards: a Released revision rejects new iterations, new structure and check-out; iterations stay immutable
        var ex = await Assert.ThrowsAsync<PostgresException>(async () => await Exec(k.Ds,
            "INSERT INTO plm.part_iteration (id, revision_id, iteration, description, attributes, created_at, created_by) SELECT gen_random_uuid(), r.id, 9, 'x', '{}', now(), 'x' FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1000' AND r.revision = 'A'"));
        Assert.Contains("locked", ex.MessageText, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PostgresException>(async () => await Exec(k.Ds,
            "INSERT INTO plm.bom_usage (id, parent_iteration_id, child_master_id, quantity, unit, find_number, created_at, created_by) SELECT gen_random_uuid(), i.id, c.id, 1, 'EA', 99, now(), 'x' FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id, plm.part_master c WHERE m.number = 'A-1000' AND r.revision = 'A' AND c.number = 'P-2008'"));
        await Assert.ThrowsAsync<PostgresException>(async () => await Exec(k.Db.SuperuserConnectionString, "UPDATE plm.part_iteration SET description = 'tampered'"));
        Assert.False((await k.Parts.CheckOutAsync(MigrationKit.Alice, "P-2003", null)).IsOk); // Released and latest: locked

        // an imported in-work revision continues natively: check out and check in make the next iteration
        Assert.True((await k.Parts.CheckOutAsync(MigrationKit.Alice, "P-2002", null)).IsOk);
        Assert.True((await k.Parts.CheckInAsync(MigrationKit.Alice, "P-2002", "native edit", null)).IsOk);
        Assert.Equal("2", await k.Text("SELECT r.latest_iteration::text FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'P-2002' AND r.revision = 'B'"));
    }

    private static async Task Exec(NpgsqlDataSource ds, string sql)
    {
        await using var c = ds.CreateCommand(sql);
        await c.ExecuteNonQueryAsync();
    }

    private static async Task Exec(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var c = new NpgsqlCommand(sql, conn);
        await c.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Files_reach_the_vault_content_addressed_and_the_rows_carry_the_verified_hash()
    {
        using var k = await MigrationKit.Create(pg);
        var product = Samples.Build(salt: MigrationKit.Salt());
        await k.DryRunAndLoad(k.Package(product));
        var rows = 0;
        await using var c = k.Ds.CreateCommand("SELECT file_name, sha256, size_bytes, role FROM plm.document_file");
        await using var r = await c.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            rows++;
            var sha = r.GetString(1);
            var size = r.GetInt64(2);
            Assert.True(await k.Vault.ExistsAsync(sha, size));
            await k.Vault.VerifyAsync(sha, size);
            var source = product.Files.Values.Single(b => Convert.ToHexStringLower(SHA256.HashData(b)) == sha || false);
            Assert.Equal(size, source.Length);
        }
        Assert.Equal(5, rows);
        Assert.Equal(2, await k.Count("SELECT count(*) FROM plm.document_file WHERE role = 'primary' AND file_name LIKE 'drawing-b%'"));
    }

    [Fact]
    public async Task Loading_twice_is_idempotent_and_an_interrupted_load_is_completed_by_the_rerun()
    {
        using var k = await MigrationKit.Create(pg);
        var dir = k.Package(Samples.Build(salt: MigrationKit.Salt()));

        // crash in the middle of the part revision chains
        var seen = 0;
        await Assert.ThrowsAsync<FaultInjectionException>(() => k.DryRunAndLoad(dir, chunk: 3, fault: p =>
        {
            if (p.Stage == "part revisions" && ++seen == 7) throw new FaultInjectionException("simulated crash");
            return Task.CompletedTask;
        }));
        var partial = await k.RowCounts();
        Assert.Equal("Failed", await k.Text("SELECT status FROM mig.batch ORDER BY started_at LIMIT 1"));
        Assert.True(partial["plm.part_master"] > 0 && partial["plm.part_revision"] < 16, "the crash left a partial import behind");
        Assert.True((await k.VerifyAudit()).Valid);

        // the rerun skips what is mapped and completes the rest
        var rerun = await k.DryRunAndLoad(dir);
        Assert.Equal("Completed", rerun.Result.Status);
        Assert.Equal(16, await k.Count("SELECT count(*) FROM plm.part_revision"));
        Assert.Equal(13, await k.Count("SELECT count(*) FROM plm.part_master"));
        Assert.Equal(17, await k.Count("SELECT count(*) FROM plm.bom_usage"));
        Assert.True(rerun.Result.Skipped["parts"] > 0 || rerun.Result.Skipped.GetValueOrDefault("partRevisions") > 0);

        // and a third run finds nothing to do
        var before = await k.RowCounts();
        var third = await k.DryRunAndLoad(dir);
        Assert.DoesNotContain(third.Result.Created, kv => kv.Value != 0);
        var after = await k.RowCounts();
        foreach (var t in before.Keys.Where(t => !t.StartsWith("mig.", StringComparison.Ordinal) && !t.StartsWith("audit", StringComparison.Ordinal) && t != "ecn_seq"))
            Assert.Equal(before[t], after[t]);

        // every planned object is in the id map; reconcile agrees with the database
        using var prepared = await k.Prepare(dir);
        var rec = await k.Pipeline.ReconcileAsync(prepared, new ReconcileOptions(), CancellationToken.None);
        Assert.True(rec.Ok, string.Join("\n", rec.Checks.Where(c => !c.Ok).Select(c => c.Name + ": " + c.Expected + " vs " + c.Actual + " " + string.Join("; ", c.Details))));
    }

    [Fact]
    public async Task Changed_source_data_for_an_imported_object_is_a_conflict_and_never_overwrites()
    {
        using var k = await MigrationKit.Create(pg);
        var salt = MigrationKit.Salt();
        await k.DryRunAndLoad(k.Package(Samples.Build(salt: salt), "first"));

        var changed = Samples.Build(salt: salt);
        changed.Package.Items.First(i => i.Number == "A-1100").Name = "Renamed in the source";
        var dir2 = k.Package(changed, "second");
        // without quarantine the dry-run already reports the conflict and blocks the load
        using var prepared = await k.Prepare(dir2);
        var dry = await k.Pipeline.DryRunAsync(prepared, new LoadOptions(), CancellationToken.None);
        Assert.False(dry.Succeeded);
        Assert.Contains(dry.Problems, p => p.Code == "LOAD-CONFLICT" && p.SourceId == "A-1100");
        await Assert.ThrowsAsync<LoadRefusedException>(() => k.Pipeline.LoadAsync(prepared, dry, new LoadOptions(), CancellationToken.None));

        // with quarantine the rest proceeds, the conflict is listed, the released data is untouched
        var loaded = await k.DryRunAndLoad(dir2, quarantine: true);
        Assert.Equal("CompletedWithIssues", loaded.Result.Status);
        Assert.Contains(loaded.Result.Problems, p => p.Code == "LOAD-CONFLICT");
        Assert.Equal("Synthetic sub assembly", await k.Text("SELECT name FROM plm.part_master WHERE number = 'A-1100'"));
    }

    [Fact]
    public async Task Markings_fail_closed_missing_markings_are_quarantined_and_dry_run_never_weakens_them()
    {
        using var k = await MigrationKit.Create(pg);
        var dir = k.Package(Samples.Build(Defect.MarkingsMissing, salt: MigrationKit.Salt()));
        using var prepared = await k.Prepare(dir);
        var dry = await k.Pipeline.DryRunAsync(prepared, new LoadOptions(), CancellationToken.None);
        Assert.False(dry.Succeeded);
        await Assert.ThrowsAsync<LoadRefusedException>(() => k.Pipeline.LoadAsync(prepared, dry, new LoadOptions(), CancellationToken.None));
        Assert.Equal(0, await k.Count("SELECT count(*) FROM plm.part_master"));

        // quarantine mode: P-2008 is skipped and listed; nothing was imported with guessed markings
        var loaded = await k.DryRunAndLoad(dir, quarantine: true);
        Assert.Equal("CompletedWithIssues", loaded.Result.Status);
        Assert.Equal(0, await k.Count("SELECT count(*) FROM plm.part_master WHERE number = 'P-2008'"));
        Assert.Equal(1, await k.Count("SELECT count(*) FROM mig.quarantine WHERE source_id = 'P-2008' AND code = 'MARKINGS-UNDETERMINED'"));
        Assert.Contains(loaded.Report.Quarantined, q => q.SourceId == "P-2008");

        // every imported object carries exactly what the rules say: ITAR-flagged objects are ITAR/EXPT/D, the others open
        Assert.Equal(0, await k.Count("SELECT count(*) FROM plm.part_master m WHERE imported_batch_id IS NOT NULL AND ((legacy_properties->'properties'->>'ITAR' = 'Y') <> (export_jurisdiction = 'Itar'))"));
        Assert.Equal(0, await k.Count("SELECT count(*) FROM plm.part_master WHERE export_jurisdiction = 'Itar' AND (NOT 'EXPT' = ANY(cui_categories) OR distribution_statement <> 'D' OR program IS DISTINCT FROM 'ORCA')"));
    }

    [Fact]
    public async Task Default_markings_are_applied_to_undetermined_objects_and_listed_for_review()
    {
        using var k = await MigrationKit.Create(pg);
        var dir = k.Package(Samples.Build(Defect.MarkingsMissing, salt: MigrationKit.Salt()));
        var loaded = await k.DryRunAndLoad(dir, Samples.Mapping(defaultMarkings: true));
        Assert.Equal("Completed", loaded.Result.Status);
        Assert.Equal("F", await k.Text("SELECT distribution_statement FROM plm.part_master WHERE number = 'P-2008'"));
        Assert.Equal("Itar", await k.Text("SELECT export_jurisdiction FROM plm.part_master WHERE number = 'P-2008'"));
        Assert.Contains(loaded.Dry.DefaultedMarkings, d => d.Number == "P-2008");
        Assert.Contains("Defaulted markings", loaded.Report.ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_load_is_gated_by_a_dry_run_report_for_exactly_this_package_and_mapping()
    {
        using var k = await MigrationKit.Create(pg);
        var dir = k.Package(Samples.Build(salt: MigrationKit.Salt()));
        using var prepared = await k.Prepare(dir);
        var dry = await k.Pipeline.DryRunAsync(prepared, new LoadOptions(), CancellationToken.None);

        var tampered = MigrationReport.FromJson(dry.ToJson());
        tampered.Planned["parts"] = 1;
        await Assert.ThrowsAsync<LoadRefusedException>(() => k.Pipeline.LoadAsync(prepared, tampered, new LoadOptions(), CancellationToken.None));

        var otherMapping = Samples.Mapping();
        otherMapping.Name = "different";
        using var other = await k.Prepare(dir, otherMapping);
        var ex = await Assert.ThrowsAsync<LoadRefusedException>(() => k.Pipeline.LoadAsync(other, dry, new LoadOptions(), CancellationToken.None));
        Assert.Contains("mapping changed", ex.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<LoadRefusedException>(() => k.Pipeline.LoadAsync(prepared, dry, new LoadOptions { Quarantine = true }, CancellationToken.None));

        File.AppendAllText(Path.Combine(dir, "files", "spec.pdf"), "x");
        using var changedPackage = await k.Prepare(dir);
        var ex2 = await Assert.ThrowsAsync<LoadRefusedException>(() => k.Pipeline.LoadAsync(changedPackage, dry, new LoadOptions(), CancellationToken.None));
        Assert.Contains("package changed", ex2.Message, StringComparison.Ordinal);
        Assert.Equal(0, await k.Count("SELECT count(*) FROM mig.batch"));
    }

    [Fact]
    public async Task Change_history_is_imported_as_flagged_historical_records_without_workflow_artefacts()
    {
        using var k = await MigrationKit.Create(pg);
        await k.DryRunAndLoad(k.Package(Samples.Build(salt: MigrationKit.Salt())));
        Assert.Equal(3, await k.Count("SELECT count(*) FROM chg.change WHERE imported_batch_id IS NOT NULL"));
        Assert.Equal("Closed", await k.Text("SELECT lifecycle_state FROM chg.change WHERE legacy_number = 'PR-2021-001'"));
        Assert.Equal("Implemented", await k.Text("SELECT lifecycle_state FROM chg.change WHERE legacy_number = 'ECR-2021-003'"));
        Assert.Equal("Implemented", await k.Text("SELECT lifecycle_state FROM chg.change WHERE legacy_number = 'ECN-2021-007'"));
        Assert.Equal("I", await k.Text("SELECT change_class FROM chg.change WHERE legacy_number = 'ECN-2021-007'"));
        // numbers were assigned from the Keel sequences, source numbers kept as legacy_number
        Assert.Matches("^ECN-[0-9]{6}$", (await k.Text("SELECT number FROM chg.change WHERE legacy_number = 'ECN-2021-007'"))!);
        // the PR -> ECR -> ECN chain is intact
        Assert.Equal(1, await k.Count("SELECT count(*) FROM chg.change n JOIN chg.change e ON e.id = n.source_id JOIN chg.change p ON p.id = e.source_id WHERE n.change_type = 'ECN' AND e.change_type = 'ECR' AND p.change_type = 'PR'"));
        Assert.Equal(2, await k.Count("SELECT count(*) FROM chg.affected_object a JOIN chg.change c ON c.id = a.change_id WHERE c.legacy_number = 'ECN-2021-007'"));
        Assert.Equal("B", await k.Text("SELECT resulting_revision FROM chg.affected_object WHERE change_action = 'revise'"));
        // original timestamps and authors
        Assert.Equal(Samples.Day(60).ToString("yyyy-MM-dd"), await k.Text("SELECT state_changed_at::date::text FROM chg.change WHERE legacy_number = 'ECN-2021-007'"));
        Assert.Equal("alice", await k.Text("SELECT created_by FROM chg.change WHERE legacy_number = 'ECN-2021-007'"));
        // flagged as migrated records, no live artefacts
        Assert.Equal(3, await k.Count("SELECT count(*) FROM chg.history WHERE action = 'imported' AND detail LIKE '%signatures were not reproduced%'"));
        Assert.Equal(0, await k.Count("SELECT count(*) FROM wf.instance"));
        Assert.Equal(0, await k.Count("SELECT count(*) FROM wf.task"));
        Assert.Equal(0, await k.Count("SELECT count(*) FROM wf.signature"));
        Assert.Equal(0, await k.Count("SELECT count(*) FROM chg.change_task"));
        Assert.Equal(0, await k.Count("SELECT count(*) FROM chg.change_submission"));
        // markings of the change cover every part it lists
        Assert.Equal("Itar", await k.Text("SELECT export_jurisdiction FROM chg.change WHERE legacy_number = 'ECN-2021-007'"));
    }

    [Fact]
    public async Task Audit_has_one_event_per_batch_start_end_and_summaries_and_the_chain_stays_valid()
    {
        using var k = await MigrationKit.Create(pg);
        var before = await k.Count("SELECT count(*) FROM audit.event");
        var loaded = await k.DryRunAndLoad(k.Package(Samples.Build(salt: MigrationKit.Salt())));
        Assert.Equal(1, await k.Count("SELECT count(*) FROM audit.event WHERE action = 'migration.batch.start'"));
        Assert.Equal(1, await k.Count("SELECT count(*) FROM audit.event WHERE action = 'migration.batch.end'"));
        Assert.Equal(3, await k.Count("SELECT count(*) FROM audit.event WHERE action = 'migration.batch.entity-summary'"));
        Assert.Equal(0, await k.Count("SELECT count(*) FROM audit.event WHERE action = 'migration.object.import'"));
        Assert.Equal(before + 5, await k.Count("SELECT count(*) FROM audit.event"));
        var payload = await k.Text("SELECT payload FROM audit.event WHERE action = 'migration.batch.end'");
        Assert.Contains(loaded.Report.PackageSha256, payload!, StringComparison.Ordinal);
        Assert.True((await k.VerifyAudit()).Valid);
    }

    [Fact]
    public async Task Object_granularity_writes_one_audit_event_per_imported_master_and_document_and_change()
    {
        using var k = await MigrationKit.Create(pg);
        await k.DryRunAndLoad(k.Package(Samples.Build(salt: MigrationKit.Salt())), audit: "object");
        Assert.Equal(13 + 2 + 3, await k.Count("SELECT count(*) FROM audit.event WHERE action = 'migration.object.import'"));
        Assert.True((await k.VerifyAudit()).Valid);
    }

    [Fact]
    public async Task Imported_itar_objects_are_invisible_to_unauthorized_users_exactly_like_native_ones()
    {
        using var k = await MigrationKit.Create(pg);
        await k.DryRunAndLoad(k.Package(Samples.Build(salt: MigrationKit.Salt())));
        var native = await k.Parts.CreateAsync(BomTestKit.Alice, new CreatePartCommand("native itar", "Component", "EA", "Make", BomTestKit.Itar()), null);
        Assert.True(native.IsOk);
        foreach (var subject in new[] { BomTestKit.Alice, BomTestKit.Lena, BomTestKit.Carmen })
        {
            var nativeKind = (await k.Parts.GetAsync(subject, native.Value!.Number, null, null)).Kind;
            var importedKind = (await k.Parts.GetAsync(subject, "A-1000", null, null)).Kind;
            Assert.Equal(nativeKind, importedKind);
            var open = (await k.Parts.GetAsync(subject, "A-1200", null, null)).Kind;
            Assert.Equal(OutcomeKind.Ok, open); // not ITAR: everyone with a role reads it
            var found = (await k.Parts.SearchAsync(subject, null, null, 200, 0, null)).Items.Select(i => i.Number).ToHashSet();
            Assert.Equal(nativeKind == OutcomeKind.Ok, found.Contains("A-1000"));
        }
        Assert.Equal(OutcomeKind.NotFound, (await k.Parts.GetAsync(BomTestKit.Lena, "A-1000", null, null)).Kind);
        Assert.Equal(OutcomeKind.Ok, (await k.Parts.GetAsync(BomTestKit.Alice, "A-1000", null, null)).Kind);
    }

    [Fact]
    public async Task Reconcile_passes_after_a_clean_load_and_detects_an_altered_bom_quantity()
    {
        using var k = await MigrationKit.Create(pg);
        var dir = k.Package(Samples.Build(salt: MigrationKit.Salt()));
        await k.DryRunAndLoad(dir);
        using (var prepared = await k.Prepare(dir))
        {
            var ok = await k.Pipeline.ReconcileAsync(prepared, new ReconcileOptions { DeepVault = true }, CancellationToken.None);
            Assert.True(ok.Ok, string.Join("\n", ok.Checks.Where(c => !c.Ok).Select(c => c.Name + ": " + c.Expected + " vs " + c.Actual + " " + string.Join("; ", c.Details))));
            Assert.Contains(ok.Checks, c => c.Name.Contains("exploded", StringComparison.Ordinal));
            Assert.Contains(ok.Checks, c => c.Name.Contains("SHA-256", StringComparison.Ordinal));
            Assert.Equal(1, await k.Count("SELECT count(*) FROM mig.report WHERE kind = 'reconcile'"));
        }
        // someone with database superpowers bypasses the immutability trigger and changes one quantity (the importer itself never does this)
        await Exec(k.Db.SuperuserConnectionString,
            "ALTER TABLE plm.bom_usage DISABLE TRIGGER bom_usage_immutable; UPDATE plm.bom_usage SET quantity = 99 WHERE id = (SELECT u.id FROM plm.bom_usage u JOIN plm.part_master m ON m.id = u.parent_iteration_id OR true JOIN plm.part_master c ON c.id = u.child_master_id WHERE c.number = 'P-2006' LIMIT 1); ALTER TABLE plm.bom_usage ENABLE TRIGGER bom_usage_immutable;");
        using var again = await k.Prepare(dir);
        var bad = await k.Pipeline.ReconcileAsync(again, new ReconcileOptions(), CancellationToken.None);
        Assert.False(bad.Ok);
        var direct = bad.Checks.Single(c => c.Name.StartsWith("BOM direct", StringComparison.Ordinal));
        Assert.False(direct.Ok);
        Assert.Contains(direct.Details, d => d.Contains("A-1110", StringComparison.Ordinal));
        Assert.False(bad.Checks.Single(c => c.Name.StartsWith("BOM exploded", StringComparison.Ordinal)).Ok);
        Assert.Contains(bad.Checks.Single(c => c.Name.StartsWith("BOM exploded", StringComparison.Ordinal)).Details, d => d.StartsWith("A-1000", StringComparison.Ordinal)); // the change is visible from the top assembly
    }

    [Fact]
    public async Task Reconcile_detects_a_package_file_that_changed_after_the_load()
    {
        using var k = await MigrationKit.Create(pg);
        var product = Samples.Build(salt: MigrationKit.Salt());
        // a package that does not declare hashes: the importer computes them
        foreach (var f in product.Package.Documents.SelectMany(d => d.Revisions).SelectMany(r => r.Iterations).SelectMany(i => i.Files)) { f.Sha256 = null; f.SizeBytes = null; }
        var dir = k.Package(product);
        await k.DryRunAndLoad(dir);
        File.WriteAllText(Path.Combine(dir, "files", "spec.pdf"), "tampered after the load");
        using var prepared = await k.Prepare(dir);
        var rec = await k.Pipeline.ReconcileAsync(prepared, new ReconcileOptions(), CancellationToken.None);
        Assert.False(rec.Ok);
        var files = rec.Checks.Single(c => c.Name.StartsWith("file SHA-256", StringComparison.Ordinal));
        Assert.False(files.Ok);
        Assert.Contains(files.Details, d => d.StartsWith("D-9001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Source_errors_block_the_package_and_quarantine_does_not_hide_them()
    {
        using var k = await MigrationKit.Create(pg);
        var product = Samples.Build(Defect.DanglingIdRef, salt: MigrationKit.Salt());
        var dir = Path.Combine(k.Root, "plmxml");
        Samples.WritePlmXml(product, dir, Defect.DanglingIdRef);
        using var prepared = await k.Prepare(dir);
        var dry = await k.Pipeline.DryRunAsync(prepared, new LoadOptions { Quarantine = true }, CancellationToken.None);
        Assert.False(dry.Succeeded);
        Assert.Contains(dry.Blocking, b => b.Contains("SRC-", StringComparison.Ordinal));
        Assert.Equal(0, await k.Count("SELECT count(*) FROM plm.part_master"));
    }

    [Fact]
    public async Task An_existing_native_number_is_a_collision_not_an_overwrite()
    {
        using var k = await MigrationKit.Create(pg);
        var created = await k.Parts.CreateAsync(BomTestKit.User("root", roles: [Roles.Admin, Roles.Author, Roles.Viewer]), new CreatePartCommand("native", "Component", "EA", "Make", BomTestKit.Open, Number: "P-2003"), null);
        Assert.True(created.IsOk);
        using var prepared = await k.Prepare(k.Package(Samples.Build(salt: MigrationKit.Salt())));
        Assert.Contains(prepared.Plan.Problems, p => p.Code == "NUMBER-EXISTS" && p.SourceId == "P-2003");
        Assert.DoesNotContain(prepared.Plan.Parts, p => p.Number is "P-2003" or "A-1100" or "A-1000"); // and the assemblies that use it are not imported half-built
    }

    [Fact]
    public async Task The_admin_viewer_reads_batches_reports_and_quarantine_through_the_same_types_the_page_uses()
    {
        using var k = await MigrationKit.Create(pg);
        var dir = k.Package(Samples.Build(Defect.MarkingsMissing, salt: MigrationKit.Salt()));
        var loaded = await k.DryRunAndLoad(dir, quarantine: true);
        using var prepared = await k.Prepare(dir);
        await k.Pipeline.ReconcileAsync(prepared, new ReconcileOptions(), CancellationToken.None);
        var queries = new BatchQueries(k.Ds);
        var list = await queries.ListAsync(10, CancellationToken.None);
        var batch = Assert.Single(list);
        Assert.Equal(loaded.Result.BatchId, batch.Id);
        Assert.Equal("CompletedWithIssues", batch.Status);
        var detail = await queries.GetAsync(batch.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail.Quarantine, q => q.SourceId == "P-2008" && q.Code == "MARKINGS-UNDETERMINED");
        var load = MigrationReport.FromJson(detail.Reports.Single(r => r.Kind == "load").ContentJson);
        Assert.Equal(12, load.Results["parts"].Created);
        var rec = ReconcileReport.FromJson(detail.Reports.Single(r => r.Kind == "reconcile").ContentJson);
        Assert.True(rec.Ok);
        Assert.Null(await queries.GetAsync(Guid.NewGuid(), CancellationToken.None));
        // the stored report must not leak object names
        Assert.DoesNotContain("Synthetic top assembly", detail.Reports.Single(r => r.Kind == "load").ContentJson, StringComparison.Ordinal);
    }
}
