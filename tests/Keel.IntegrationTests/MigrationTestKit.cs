using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Documents.Vault;
using Keel.Migration;
using Keel.Migration.Canonical;
using Keel.Migration.Fixtures;
using Keel.Migration.Load;
using Keel.Migration.Mapping;
using Keel.Migration.Reports;
using Keel.Parts;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Real Postgres (fresh migrated database) and a real vault for importer tests; no mocks (ADR-0013).</summary>
internal sealed class MigrationKit : IDisposable
{
    public required TestDatabase Db { get; init; }
    public required NpgsqlDataSource Ds { get; init; }
    public required ObjectVault Vault { get; init; }
    public required PostgresAuditLog Audit { get; init; }
    public required MigrationPipeline Pipeline { get; init; }
    public required string Root { get; init; }
    public required Keel.Admin.ClassificationService Classes { get; init; }
    public PartService Parts => new(Ds, Audit, TimeProvider.System, [new BomParticipant()]);

    public static async Task<MigrationKit> Create(PostgresFixture pg)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var audit = new PostgresAuditLog(ds, TimeProvider.System);
        var vault = new ObjectVault(await VaultContainer.OptionsAsync());
        var root = Path.Combine(Path.GetTempPath(), "keel-mig-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var classes = new Keel.Admin.ClassificationService(new Keel.Admin.AdminRunner(ds, audit, TimeProvider.System), audit);
        var pipeline = new MigrationPipeline(ds, audit, new VaultContentStore(vault), TimeProvider.System, new Keel.Admin.MigrationClassificationLoader(classes));
        return new MigrationKit { Db = db, Ds = ds, Vault = vault, Audit = audit, Pipeline = pipeline, Root = root, Classes = classes };
    }

    public void Dispose()
    {
        Vault.Dispose();
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }

    public static string Salt() => Guid.NewGuid().ToString("N")[..8] + "-";

    public string Package(SampleProduct p, string name = "pkg")
    {
        var dir = Path.Combine(Root, name);
        Samples.WriteCanonical(p, dir);
        return dir;
    }

    public Task<PreparedMigration> Prepare(string dir, MappingConfig? cfg = null, string? format = null) =>
        Pipeline.PrepareAsync(dir, format, cfg ?? Samples.Mapping(), CancellationToken.None);

    public sealed record Loaded(MigrationReport Dry, MigrationReport Report, ImportResult Result);

    public async Task<Loaded> DryRunAndLoad(string dir, MappingConfig? cfg = null, bool quarantine = false, Func<FaultPoint, Task>? fault = null, int chunk = 100, string audit = "batch")
    {
        cfg ??= Samples.Mapping();
        cfg.AuditGranularity = audit;
        using var prepared = await Prepare(dir, cfg);
        var dry = await Pipeline.DryRunAsync(prepared, new LoadOptions { Quarantine = quarantine, ChunkSize = chunk, Operator = "tester" }, CancellationToken.None);
        Assert.True(dry.Succeeded, "dry-run blocked: " + string.Join(" | ", dry.Blocking) + " " + string.Join(" | ", dry.Problems.Where(p => p.Severity == Severity.Error).Select(p => p.Code + " " + p.Message)));
        var (report, result) = await Pipeline.LoadAsync(prepared, dry, new LoadOptions { Quarantine = quarantine, ChunkSize = chunk, Operator = "tester", Fault = fault }, CancellationToken.None);
        return new Loaded(dry, report, result);
    }

    public async Task<long> Count(string sql, params (string, object)[] ps)
    {
        await using var c = Ds.CreateCommand(sql);
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(await c.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<string?> Text(string sql, params (string, object)[] ps)
    {
        await using var c = Ds.CreateCommand(sql);
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v);
        return (await c.ExecuteScalarAsync())?.ToString();
    }

    private static readonly string[] Tables =
    [
        "plm.part_master", "plm.part_revision", "plm.part_iteration", "plm.bom_usage", "plm.part_document_link", "plm.document_master", "plm.document_revision",
        "plm.document_iteration", "plm.document_file", "chg.change", "chg.affected_object", "chg.history", "wf.instance", "mig.batch", "mig.id_map", "mig.quarantine", "mig.report", "audit.event",
    ];

    public async Task<Dictionary<string, long>> RowCounts()
    {
        var d = new Dictionary<string, long>();
        foreach (var t in Tables) d[t] = await Count($"SELECT count(*) FROM {t}");
        d["audit.head"] = await Count("SELECT COALESCE(max(seq), 0) FROM audit.event");
        d["ecn_seq"] = await Count("SELECT COALESCE(last_value, 0) FROM pg_sequences WHERE schemaname = 'chg' AND sequencename = 'ecn_seq'");
        return d;
    }

    public async Task<VerificationResult> VerifyAudit()
    {
        await using var conn = await Ds.OpenConnectionAsync();
        return await AuditVerifier.VerifyAsync(conn, []);
    }

    public static readonly Subject Alice = BomTestKit.Alice;
}
