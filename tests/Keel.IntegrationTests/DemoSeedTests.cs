using System.Diagnostics;
using Keel.Admin;
using Keel.Core;
using Keel.Seed;

namespace Keel.IntegrationTests;

/// <summary>The ORCA demo seed against a throwaway database and vault: it completes, the audit chain verifies, and the second half of the data (FUP-4) is really there.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DemoSeedTests(PostgresFixture pg)
{
    private sealed class Lines : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public List<string> All { get; } = [];
        public override void WriteLine(string? value) => All.Add(value ?? "");
    }

    [Fact]
    public async Task The_demo_seed_completes_through_the_real_services_with_classification_baselines_effectivity_mboms_tdp_and_integration_data()
    {
        var db = await pg.CreateDatabaseAsync();
        var vault = await VaultContainer.OptionsAsync();
        var log = new Lines();
        var clock = Stopwatch.StartNew();
        DemoManifest manifest;
        await using (var host = SeedHost.Create(new SeedSettings(db.AppConnectionString, vault, null)))
        {
            manifest = await new DemoSeeder(host, RealmUsers.Load(), log).RunAsync();
            var verdict = await host.VerifyAuditAsync([]);
            Assert.True(verdict.Valid, string.Join("; ", verdict.Failures));
            Assert.True(verdict.EventsChecked >= manifest.AuditEvents);
        }
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(2), $"the seed took {clock.Elapsed}");
        var x = manifest.Extras!;
        if (Environment.GetEnvironmentVariable("KEEL_SEED_MANIFEST_OUT") is { Length: > 0 } outPath) await File.WriteAllTextAsync(outPath, manifest.Json());

        async Task<long> Count(string sql)
        {
            await using var c = new Npgsql.NpgsqlConnection(db.SuperuserConnectionString);
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
        async Task<string?> Text(string sql)
        {
            await using var c = new Npgsql.NpgsqlConnection(db.SuperuserConnectionString);
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
            return Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        // classification: tree, a dozen classified parts, values frozen with the iteration they were set in (before release)
        Assert.Equal(7, await Count("SELECT count(*) FROM cfg.class_node"));
        Assert.Equal(13, x.ClassifiedParts);
        Assert.Equal(13, await Count("SELECT count(DISTINCT m.id) FROM cfg.part_classification pc JOIN plm.part_iteration i ON i.id = pc.iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id"));
        Assert.Equal("BRACKETS", await Text($"SELECT pc.class_code FROM cfg.part_classification pc JOIN plm.part_iteration i ON i.id = pc.iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = '{manifest.GimbalBracket}' ORDER BY i.iteration DESC LIMIT 1"));
        Assert.Equal("Released", await Text($"SELECT r.lifecycle_state FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = '{manifest.GimbalBracket}' AND r.revision = 'A'"));

        // baselines: Design established directly, Release signed
        Assert.Equal("Established", await Text($"SELECT lifecycle_state FROM cfg.baseline WHERE number = '{x.DesignBaseline}'"));
        Assert.Equal("Established", await Text($"SELECT lifecycle_state FROM cfg.baseline WHERE number = '{x.ReleaseBaseline}'"));
        Assert.True(await Count($"SELECT count(*) FROM wf.instance WHERE object_key = '{x.ReleaseBaseline}' AND status = 'Approved'") >= 1);

        // effectivity: context, two alternates for one find number, revision B of the yoke effective from serial 101
        Assert.Equal(1, await Count($"SELECT count(*) FROM cfg.effectivity_context WHERE id = '{x.EffectivityContext}'"));
        Assert.Equal(2, await Count($"SELECT count(*) FROM plm.bom_usage u JOIN plm.part_iteration i ON i.id = u.parent_iteration_id JOIN plm.part_revision r ON r.id = i.revision_id AND r.latest_iteration = i.iteration JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = '{x.AlternatesAssembly}' AND u.find_number = 30 AND u.eff_kind = 'serial'"));
        Assert.Equal("Released", await Text($"SELECT r.lifecycle_state FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = '{x.RevisedPart}' AND r.revision = 'B'"));
        Assert.Equal(1, await Count($"SELECT count(*) FROM cfg.revision_effectivity e JOIN plm.part_revision r ON r.id = e.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = '{x.RevisedPart}' AND r.revision = 'B' AND e.eff_from = 101"));
        Assert.Equal("Released", await Text($"SELECT r.lifecycle_state FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = '{x.RevisedPart}' AND r.revision = 'A'")); // A stays released for serials up to 100

        // MBOMs: one released through review, one in work whose reconciliation is stale
        Assert.Equal("Released", await Text($"SELECT r.lifecycle_state FROM mbom.revision r JOIN mbom.mbom m ON m.id = r.mbom_id WHERE m.number = '{x.ReleasedMbom}'"));
        Assert.Equal("InWork", await Text($"SELECT r.lifecycle_state FROM mbom.revision r JOIN mbom.mbom m ON m.id = r.mbom_id WHERE m.number = '{x.StaleMbom}'"));
        Assert.True(await Count($"SELECT count(*) FROM mbom.reconciliation_run rr JOIN mbom.mbom m ON m.id = rr.mbom_id WHERE m.number = '{x.StaleMbom}'") >= 2); // reconciled, then again after the EBOM moved

        // TDP export record, profiles, a disabled file-drop sink, outbox events of the releases
        Assert.Equal(1, await Count($"SELECT count(*) FROM integration.tdp_export WHERE id = '{x.TdpExportId}'"));
        Assert.Equal(3, x.ClearanceProfiles.Count);
        Assert.Equal("false", await Text($"SELECT enabled::text FROM integration.sink WHERE name = '{x.DisabledSink}'"));
        Assert.True(x.OutboxEvents > 20);
        Assert.Equal(1, await Count("SELECT count(*) FROM integration.outbox WHERE event_type = 'mbom.released'"));
        Assert.Equal(1, await Count("SELECT count(*) FROM integration.outbox WHERE event_type = 'baseline.established' AND aggregate_number = '" + x.ReleaseBaseline + "'"));

        // the manifest the demo script and the browser tests read
        Assert.Equal(manifest.Parts, await Count("SELECT count(*) FROM plm.part_master"));
        Assert.Contains(log.All, l => l.StartsWith("Classification:", StringComparison.Ordinal));
        Assert.Contains(log.All, l => l.StartsWith("MBOMs", StringComparison.Ordinal));
    }
}
