using System.Globalization;
using System.Text;
using Keel.Audit;
using Keel.Core;
using Keel.Documents.Vault;
using Keel.Migration;
using Keel.Migration.Canonical;
using Keel.Migration.Load;
using Keel.Migration.Mapping;
using Keel.Migration.Reconcile;
using Keel.Migration.Reports;
using Keel.Migration.Sources;
using Npgsql;

namespace Keel.Import;

/// <summary>Process exit codes of keel-import (docs/migration/runbook.md).</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 1;
    /// <summary>Validation or dry-run failed: the package or mapping has errors.</summary>
    public const int Invalid = 2;
    /// <summary>The load finished but objects were quarantined or conflicted.</summary>
    public const int CompletedWithIssues = 3;
    /// <summary>Reconciliation found differences.</summary>
    public const int ReconcileDifferences = 4;
    /// <summary>A precondition failed: the load was refused, or the target is not reachable or not configured.</summary>
    public const int Refused = 5;
    public const int NotSupported = 6;
    public const int Cancelled = 130;
}

/// <summary>
/// The keel-import command line (a small hand-written parser; no third-party dependency in an air-gapped tool). Commands: inspect, map-template, validate,
/// dry-run, load, reconcile, report. There is no implicit database target (see <see cref="ImportSettings"/>).
/// </summary>
public static class ImportCli
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "--quarantine", "--use-dev-stack", "--yes", "--deep", "--offline", "--help" };
    private static readonly HashSet<string> Valued = new(StringComparer.Ordinal)
    {
        "--mapping", "--format", "--connection", "--settings", "--vault-url", "--vault-access-key", "--vault-secret-key", "--vault-bucket", "--vault-region",
        "--dry-run-report", "--out", "--max-errors", "--operator", "--batch-name", "--batch", "--chunk-size",
    };

    public const string Usage = """
        keel-import: migration from Teamcenter and Windchill into Keel PLM (docs/migration/README.md)

        Commands
          inspect <package> [--format F]                        counts, states, units, properties, unknown elements (no database)
          map-template <package> [--out mapping.json]           starter mapping from the inventory (no database)
          validate <package> --mapping M [--offline]            read, map and validate; writes validate-report.*
          dry-run <package> --mapping M [--quarantine]          full simulated load, always rolled back; writes dry-run-report.*
          load <package> --mapping M --dry-run-report R --yes [--quarantine]
          reconcile <package> --mapping M [--batch ID] [--deep] source vs Keel comparison; non-zero exit on any difference
          report <batch-id|latest>                              stored load and reconcile reports of a batch

        Target (required for everything except inspect, map-template, validate --offline; there is no default)
          --connection CS | --settings FILE | env KEEL_IMPORT_CONNECTION / Keel__Database__AppConnectionString
          --use-dev-stack reads src/Keel.Api/appsettings.Development.json (the shared dev database; explicit on purpose)
          --vault-url U --vault-access-key K --vault-secret-key S [--vault-bucket B --vault-region R]  (or Keel__Vault__* env / settings file)

        Options
          --format plmxml|windchill-json|windchill-csv|canonical   (default: detected)
          --out DIR            report directory (default ./keel-import-reports)
          --max-errors N       print at most N problems (default 100); the report files always list all of them
          --operator NAME      recorded in the audit log and the batch (default: OS user)
          --batch-name NAME    label of the batch
          --chunk-size N       objects per transaction (default 100)

        Exit codes: 0 ok, 1 usage or I/O error, 2 invalid (errors), 3 completed with quarantined or conflicting objects,
                    4 reconcile differences, 5 refused or target unavailable, 6 not supported yet, 130 cancelled
        """;

    private sealed record Args(string Command, List<string> Positional, Dictionary<string, string> Options, HashSet<string> Switches)
    {
        public string? Opt(string name) => Options.GetValueOrDefault(name);
        public bool Has(string name) => Switches.Contains(name);
    }

    private static Args? Parse(string[] argv, TextWriter error)
    {
        if (argv.Length == 0) return null;
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var switches = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < argv.Length; i++)
        {
            var a = argv[i];
            if (Flags.Contains(a)) switches.Add(a);
            else if (Valued.Contains(a))
            {
                if (i + 1 >= argv.Length) { error.WriteLine($"Option {a} needs a value."); return null; }
                options[a] = argv[++i];
            }
            else if (a.StartsWith("--", StringComparison.Ordinal)) { error.WriteLine($"Unknown option {a}."); return null; }
            else positional.Add(a);
        }
        return new Args(argv[0], positional, options, switches);
    }

    public static async Task<int> RunAsync(string[] argv, TextWriter output, TextWriter error, Func<string, string?>? env, CancellationToken ct)
    {
        var args = Parse(argv, error);
        if (args is null || args.Command is "--help" or "help" or "-h" || args.Has("--help")) { await output.WriteLineAsync(Usage); return args is null ? ExitCodes.Usage : ExitCodes.Ok; }
        env ??= Environment.GetEnvironmentVariable;
        try
        {
            return args.Command switch
            {
                "inspect" => await InspectAsync(args, output, error, ct),
                "map-template" => await TemplateAsync(args, output, error, ct),
                "validate" => await ValidateAsync(args, output, error, env, ct),
                "dry-run" => await DryRunAsync(args, output, error, env, ct),
                "load" => await LoadAsync(args, output, error, env, ct),
                "reconcile" => await ReconcileAsync(args, output, error, env, ct),
                "report" => await ReportAsync(args, output, error, env, ct),
                _ => Fail(error, $"Unknown command '{args.Command}'.\n{Usage}", ExitCodes.Usage),
            };
        }
        catch (OperationCanceledException) { error.WriteLine("Cancelled."); return ExitCodes.Cancelled; }
        catch (NotSupportedException e) { error.WriteLine("Not supported: " + e.Message); return ExitCodes.NotSupported; }
        catch (LoadRefusedException e) { error.WriteLine("Load refused: " + e.Message); return ExitCodes.Refused; }
        catch (MappingException e) { error.WriteLine("Mapping file: " + e.Message); return ExitCodes.Invalid; }
        catch (NpgsqlException e) { error.WriteLine("Database error: " + e.Message); return ExitCodes.Refused; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            error.WriteLine("Error: " + e.Message);
            return ExitCodes.Usage;
        }
    }

    private static int Fail(TextWriter error, string message, int code) { error.WriteLine(message); return code; }

    private static string? PackagePath(Args a, TextWriter error)
    {
        if (a.Positional.Count != 1) { error.WriteLine($"{a.Command} needs exactly one <package> argument (a directory or .zip)."); return null; }
        return a.Positional[0];
    }

    private static string OutDir(Args a)
    {
        var d = Path.GetFullPath(a.Opt("--out") ?? "keel-import-reports");
        Directory.CreateDirectory(d);
        return d;
    }

    private static MappingConfig LoadMapping(Args a)
    {
        var path = a.Opt("--mapping") ?? throw new InvalidOperationException("--mapping <file> is required.");
        return MappingConfig.Load(path);
    }

    private static ImportSettings Settings(Args a, Func<string, string?> env)
    {
        var vault = new List<(string, string)>();
        foreach (var (flag, key) in new[] { ("--vault-url", "ServiceUrl"), ("--vault-access-key", "AccessKey"), ("--vault-secret-key", "SecretKey"), ("--vault-bucket", "Bucket"), ("--vault-region", "Region") })
            if (a.Opt(flag) is { } v) vault.Add((key, v));
        return ImportSettings.Resolve(a.Opt("--settings"), a.Opt("--connection"), a.Has("--use-dev-stack"), env, vault);
    }

    private sealed class Host(NpgsqlDataSource ds, ObjectVault? vault, MigrationPipeline pipeline, ImportSettings settings) : IDisposable
    {
        public NpgsqlDataSource DataSource { get; } = ds;
        public ObjectVault? Vault { get; } = vault;
        public MigrationPipeline Pipeline { get; } = pipeline;
        public ImportSettings Settings { get; } = settings;
        public void Dispose() { Vault?.Dispose(); DataSource.Dispose(); }
    }

    private static Host? OpenHost(Args a, TextWriter output, TextWriter error, Func<string, string?> env, bool vaultRequired)
    {
        var settings = Settings(a, env);
        if (settings.ConnectionString is null)
        {
            error.WriteLine("No database target. Pass --connection, --settings, set KEEL_IMPORT_CONNECTION (or Keel__Database__AppConnectionString), or pass --use-dev-stack to target the dev stack explicitly.");
            return null;
        }
        ObjectVault? vault = null;
        if (settings.Vault is { } vo)
        {
            var errors = vo.Validate();
            if (errors.Count > 0) { error.WriteLine("Vault settings: " + string.Join(" ", errors)); return null; }
            vault = new ObjectVault(vo);
        }
        else if (vaultRequired)
        {
            error.WriteLine("Loading files needs the vault: pass --vault-url, --vault-access-key, --vault-secret-key (or Keel__Vault__* settings).");
            return null;
        }
        output.WriteLine($"Target database: {settings.DescribeTarget()}" + (settings.Vault is null ? "; vault: not configured" : $"; vault: {settings.Vault.ServiceUrl}/{settings.Vault.Bucket}"));
        var ds = NpgsqlDataSource.Create(settings.ConnectionString);
        var pipeline = new MigrationPipeline(ds, new PostgresAuditLog(ds, TimeProvider.System), vault is null ? null : new VaultContentStore(vault), TimeProvider.System);
        return new Host(ds, vault, pipeline, settings);
    }

    // ------------------------------------------------------------------------------------------------ commands
    private static async Task<int> InspectAsync(Args a, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var path = PackagePath(a, error);
        if (path is null) return ExitCodes.Usage;
        var r = await MigrationPipeline.InspectAsync(path, a.Opt("--format"), ct);
        output.WriteLine($"Format: {r.Format}   Package SHA-256: {r.Manifest.Sha256}   Files: {r.Manifest.Entries.Count} ({r.Manifest.TotalBytes} bytes)");
        void Table(string title, IEnumerable<KeyValuePair<string, long>> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0) return;
            output.WriteLine($"\n{title}");
            foreach (var (k, v) in list) output.WriteLine($"  {k,-48} {v,10}");
        }
        Table("Counts", r.Inventory.Counts);
        Table("Item states", r.Inventory.ItemStates); Table("Document states", r.Inventory.DocumentStates); Table("Change states", r.Inventory.ChangeStates);
        Table("Units", r.Inventory.Units); Table("Item types", r.Inventory.ItemTypes); Table("Document types", r.Inventory.DocumentTypes); Table("Change types", r.Inventory.ChangeTypes);
        Table("Users", r.Inventory.Users); Table("Properties", r.Inventory.Properties);
        Table("Ignored elements (recognized, not migrated)", r.Inventory.IgnoredElements);
        if (r.Inventory.UnknownElements.Count > 0)
        {
            output.WriteLine("\nUnknown elements (reported, never silently dropped)");
            foreach (var (k, v) in r.Inventory.UnknownElements) output.WriteLine($"  {k,-30} {v.Count,8}  {v.FirstPath}  {(v.Preserved ? "[preserved as properties]" : "[counted only]")}");
        }
        foreach (var p in r.Problems) output.WriteLine($"{p.Severity.ToString().ToUpperInvariant()} {p.Code} {p.EntityType} {p.SourceId}: {p.Message}");
        return r.Problems.Any(p => p.Severity == Severity.Error) ? ExitCodes.Invalid : ExitCodes.Ok;
    }

    private static async Task<int> TemplateAsync(Args a, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var path = PackagePath(a, error);
        if (path is null) return ExitCodes.Usage;
        var r = await MigrationPipeline.InspectAsync(path, a.Opt("--format"), ct);
        var text = MappingTemplate.Generate(r.Inventory);
        if (a.Opt("--out") is { } outFile) { await File.WriteAllTextAsync(outFile, text, ct); output.WriteLine($"Wrote {outFile}. Fill every empty value, then run validate."); }
        else output.Write(text);
        return ExitCodes.Ok;
    }

    private static void PrintProblems(MigrationReport report, TextWriter output, int max)
    {
        output.WriteLine($"Result: {(report.Succeeded ? "SUCCEEDED" : "BLOCKED")}; {report.ErrorCount} errors, {report.WarningCount} warnings; report hash {report.Hash[..16]}...");
        foreach (var b in report.Blocking) output.WriteLine("  BLOCKING: " + b);
        foreach (var p in report.Problems.OrderByDescending(p => p.Severity).Take(max))
            output.WriteLine($"  {p.Severity.ToString().ToUpperInvariant(),-7} {p.Code,-24} {p.EntityType} {p.SourceId}: {p.Message}" + (p.Hint is null ? "" : $" Hint: {p.Hint}"));
        if (report.Problems.Count > max) output.WriteLine($"  ... {report.Problems.Count - max} more (see the report files)");
    }

    private static async Task WriteReportFilesAsync(MigrationReport report, string dir, string stem, TextWriter output, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(dir, stem + ".json"), report.ToJson(), ct);
        await File.WriteAllTextAsync(Path.Combine(dir, stem + ".md"), report.ToMarkdown(), ct);
        await File.WriteAllTextAsync(Path.Combine(dir, stem + "-problems.csv"), report.ToCsv(), ct);
        output.WriteLine($"Reports written to {dir} ({stem}.json, .md, -problems.csv). They are CUI-handling artifacts.");
    }

    private static LoadOptions Options(Args a, bool quarantine)
    {
        var o = new LoadOptions { Quarantine = quarantine, BatchName = a.Opt("--batch-name") ?? "migration " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) };
        if (a.Opt("--operator") is { } op) o.Operator = op;
        if (a.Opt("--chunk-size") is { } cs) o.ChunkSize = int.Parse(cs, CultureInfo.InvariantCulture);
        return o;
    }

    private static int MaxErrors(Args a) => a.Opt("--max-errors") is { } m ? int.Parse(m, CultureInfo.InvariantCulture) : 100;

    private static async Task<int> ValidateAsync(Args a, TextWriter output, TextWriter error, Func<string, string?> env, CancellationToken ct)
    {
        var path = PackagePath(a, error);
        if (path is null) return ExitCodes.Usage;
        var cfg = LoadMapping(a);
        PreparedMigration prepared;
        MigrationPipeline pipeline;
        Host? host = null;
        try
        {
            if (a.Has("--offline"))
            {
                output.WriteLine("Offline validation: Keel reference data (lifecycles, attributes) is the built-in default of migrations 0002/0003/0007; users and existing numbers are NOT checked.");
                prepared = await MigrationPipeline.PrepareAsync(path, a.Opt("--format"), cfg, ReferenceData.Defaults(), ct);
                pipeline = new MigrationPipeline(null, null, null, TimeProvider.System);
            }
            else
            {
                host = OpenHost(a, output, error, env, vaultRequired: false);
                if (host is null) return ExitCodes.Refused;
                prepared = await host.Pipeline.PrepareAsync(path, a.Opt("--format"), cfg, ct);
                pipeline = host.Pipeline;
            }
            using (prepared)
            {
                var report = pipeline.Validate(prepared, Options(a, a.Has("--quarantine")));
                PrintProblems(report, output, MaxErrors(a));
                await WriteReportFilesAsync(report, OutDir(a), "validate-report", output, ct);
                return report.Succeeded ? ExitCodes.Ok : ExitCodes.Invalid;
            }
        }
        finally { host?.Dispose(); }
    }

    private static async Task<int> DryRunAsync(Args a, TextWriter output, TextWriter error, Func<string, string?> env, CancellationToken ct)
    {
        var path = PackagePath(a, error);
        if (path is null) return ExitCodes.Usage;
        var cfg = LoadMapping(a);
        using var host = OpenHost(a, output, error, env, vaultRequired: false);
        if (host is null) return ExitCodes.Refused;
        using var prepared = await host.Pipeline.PrepareAsync(path, a.Opt("--format"), cfg, ct);
        var opt = Options(a, a.Has("--quarantine"));
        opt.Progress = s => output.WriteLine("  " + s);
        var report = await host.Pipeline.DryRunAsync(prepared, opt, ct);
        PrintProblems(report, output, MaxErrors(a));
        output.WriteLine($"Planned: {string.Join(", ", report.Planned.Select(kv => $"{kv.Key}={kv.Value}"))}");
        output.WriteLine($"Estimate: about {report.EstimatedRows} rows, {report.EstimatedUploadBytes} bytes of new files, roughly {report.EstimatedLoadSeconds:F0} s. Nothing was written (the transaction was rolled back).");
        await WriteReportFilesAsync(report, OutDir(a), "dry-run-report", output, ct);
        return report.Succeeded ? ExitCodes.Ok : ExitCodes.Invalid;
    }

    private static async Task<int> LoadAsync(Args a, TextWriter output, TextWriter error, Func<string, string?> env, CancellationToken ct)
    {
        var path = PackagePath(a, error);
        if (path is null) return ExitCodes.Usage;
        var cfg = LoadMapping(a);
        var reportPath = a.Opt("--dry-run-report");
        if (reportPath is null) { error.WriteLine("load needs --dry-run-report <dry-run-report.json> from a successful dry-run of this package and mapping."); return ExitCodes.Usage; }
        if (!a.Has("--yes")) { error.WriteLine("Loading cannot be undone (nothing in Keel is ever deleted). Review the dry-run report, then pass --yes."); return ExitCodes.Usage; }
        using var host = OpenHost(a, output, error, env, vaultRequired: true);
        if (host is null) return ExitCodes.Refused;
        var dryRun = MigrationReport.FromJson(await File.ReadAllTextAsync(reportPath, ct));
        using var prepared = await host.Pipeline.PrepareAsync(path, a.Opt("--format"), cfg, ct);
        var opt = Options(a, a.Has("--quarantine"));
        opt.Progress = s => output.WriteLine("  " + s);
        try
        {
            var (report, result) = await host.Pipeline.LoadAsync(prepared, dryRun, opt, ct);
            output.WriteLine($"Batch {result.BatchId}: {result.Status}. Created: {string.Join(", ", result.Created.Select(kv => $"{kv.Key}={kv.Value}"))}");
            output.WriteLine($"Skipped (already imported): {(result.Skipped.Count == 0 ? "none" : string.Join(", ", result.Skipped.Select(kv => $"{kv.Key}={kv.Value}")))}; quarantined {result.Quarantined.Count}; conflicts {result.Conflicts}; files uploaded {result.FilesUploaded} ({result.BytesUploaded} bytes); audit events {result.AuditEventsWritten}; {result.Elapsed.TotalSeconds:F1} s");
            PrintProblems(report, output, MaxErrors(a));
            await WriteReportFilesAsync(report, OutDir(a), "load-report", output, ct);
            output.WriteLine("Next: keel-import reconcile <package> --mapping <file>");
            return result.Status == "Completed" ? ExitCodes.Ok : ExitCodes.CompletedWithIssues;
        }
        catch (LoadFailedException e)
        {
            PrintProblems(e.Report, output, MaxErrors(a));
            await WriteReportFilesAsync(e.Report, OutDir(a), "load-report", output, ct);
            error.WriteLine(e.Message);
            return ExitCodes.CompletedWithIssues;
        }
    }

    private static async Task<int> ReconcileAsync(Args a, TextWriter output, TextWriter error, Func<string, string?> env, CancellationToken ct)
    {
        var path = PackagePath(a, error);
        if (path is null) return ExitCodes.Usage;
        var cfg = LoadMapping(a);
        using var host = OpenHost(a, output, error, env, vaultRequired: false);
        if (host is null) return ExitCodes.Refused;
        using var prepared = await host.Pipeline.PrepareAsync(path, a.Opt("--format"), cfg, ct);
        var opt = new ReconcileOptions { DeepVault = a.Has("--deep") };
        if (a.Opt("--batch") is { } b) opt.BatchId = Guid.Parse(b);
        var report = await host.Pipeline.ReconcileAsync(prepared, opt, ct);
        foreach (var c in report.Checks) output.WriteLine($"  {(c.Ok ? "ok       " : "DIFFERENT")} {c.Name}: source {c.Expected}, Keel {c.Actual}");
        foreach (var c in report.Checks.Where(c => !c.Ok)) foreach (var d in c.Details.Take(MaxErrors(a))) output.WriteLine($"      {c.Name}: {d}");
        var dir = OutDir(a);
        await File.WriteAllTextAsync(Path.Combine(dir, "reconcile-report.json"), report.ToJson(), ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "reconcile-report.md"), report.ToMarkdown(), ct);
        output.WriteLine(report.Ok ? "RECONCILED: no differences." : $"{report.Differences} DIFFERENCES.");
        return report.Ok ? ExitCodes.Ok : ExitCodes.ReconcileDifferences;
    }

    private static async Task<int> ReportAsync(Args a, TextWriter output, TextWriter error, Func<string, string?> env, CancellationToken ct)
    {
        if (a.Positional.Count != 1) { error.WriteLine("report needs a <batch-id> or 'latest'."); return ExitCodes.Usage; }
        using var host = OpenHost(a, output, error, env, vaultRequired: false);
        if (host is null) return ExitCodes.Refused;
        var queries = new BatchQueries(host.DataSource);
        Guid? id;
        if (a.Positional[0] == "latest") { var latest = await queries.ListAsync(1, ct); id = latest.Count > 0 ? latest[0].Id : null; }
        else id = Guid.Parse(a.Positional[0]);
        var detail = id is null ? null : await queries.GetAsync(id.Value, ct);
        if (detail is null) { error.WriteLine("No such batch."); return ExitCodes.Usage; }
        var b = detail.Batch;
        output.WriteLine($"Batch {b.Id} '{b.Name}': {b.Status}; source {b.SourceSystem}; package {b.PackageSha256}; mapping {b.MappingSha256}; quarantine {b.Quarantine}; audit {b.AuditGranularity}; started {b.StartedAt:u} by {b.StartedBy}");
        output.WriteLine($"Summary: {b.SummaryJson}");
        output.WriteLine($"Quarantined/rejected objects: {detail.Quarantine.Count}");
        foreach (var q in detail.Quarantine.Take(MaxErrors(a))) output.WriteLine($"  {q.EntityType} {q.SourceId} {q.Code}: {q.Message}");
        foreach (var r in detail.Reports) { output.WriteLine($"\n--- {r.Kind} report {r.CreatedAt:u} ---"); output.WriteLine(r.Kind == "load" ? MigrationReport.FromJson(r.ContentJson).ToMarkdown() : r.ContentJson); }
        return ExitCodes.Ok;
    }
}
