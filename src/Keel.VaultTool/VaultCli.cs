using System.Globalization;
using System.Text.Json;
using Keel.Audit;
using Keel.Documents.Vault;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Keel.VaultTool;

/// <summary>
/// <c>keel-vault</c>: operator tool for the document vault (ADR-0026).
/// <code>
/// keel-vault verify [--parallel N] [--json] [target options]     read-only bulk integrity check of every object the database refers to (ADR-0060)
/// keel-vault gc [--dry-run | --delete --confirm-bucket NAME] [--grace-days N] [--max-deletions N] [--page-size N] [--prefix P] [--continuation T] [--json]
///               [--connection CS | --settings FILE | --use-dev-stack] [--vault-url U --vault-access-key K --vault-secret-key S --vault-bucket B]
/// </code>
/// Like the seed and import tools it never defaults to the dev database: the target must be named (<c>--connection</c>, <c>--settings</c>, the
/// <c>Keel__Database__AppConnectionString</c> environment variable) or the dev stack asked for with <c>--use-dev-stack</c>. A sweep writes an audit
/// event, so the connection is the application role. Deleting needs <c>--delete</c> AND <c>--confirm-bucket</c> repeating the bucket it will delete from.
/// </summary>
public static class VaultCli
{
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static readonly HashSet<string> ValueOptions =
    [
        "--connection", "--settings", "--grace-days", "--max-deletions", "--page-size", "--prefix", "--continuation", "--confirm-bucket", "--operator",
        "--vault-url", "--vault-access-key", "--vault-secret-key", "--vault-bucket", "--vault-region", "--parallel",
    ];

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Func<string, string?> env, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Usage(output, 0);
        if (args[0] == "verify-archive") return ArchiveCli.Run(args.Skip(1).ToArray(), output, error); // 8.6: offline verification of archive packages, no database or vault needed
        if (args[0] == "verify") return await VerifyAsync(args.Skip(1).ToArray(), output, error, env, ct);
        if (args[0] != "gc") { error.WriteLine($"Unknown command '{args[0]}'."); return Usage(error, 2); }
        if (!TryParse(args.Skip(1).ToArray(), error, out var opts)) return 2;

        var delete = opts.Flags.Contains("--delete");
        if (delete && opts.Flags.Contains("--dry-run")) { error.WriteLine("Choose --dry-run or --delete, not both."); return 2; }

        var settings = Resolve(opts, env, error);
        if (settings is null) return 2;
        var (connection, vaultOptions) = settings.Value;
        if (delete && opts.Value("--confirm-bucket") != vaultOptions.Bucket)
        {
            error.WriteLine($"--delete needs --confirm-bucket {vaultOptions.Bucket} (the bucket this run would delete from). Nothing was deleted.");
            return 2;
        }

        var graceDays = IntOption(opts, "--grace-days", new GcOptions().GraceDays);
        var max = IntOption(opts, "--max-deletions", new GcOptions().MaxDeletionsPerRun);
        var pageSize = IntOption(opts, "--page-size", new GcOptions().PageSize);
        if (graceDays is null || max is null || pageSize is null) { error.WriteLine("--grace-days, --max-deletions and --page-size must be whole numbers."); return 2; }
        var gcOptions = new GcOptions { GraceDays = graceDays.Value, MaxDeletionsPerRun = max.Value, PageSize = pageSize.Value };
        var invalid = gcOptions.Validate();
        if (invalid.Count > 0) { error.WriteLine(string.Join(" ", invalid.Select(e => e.Replace("Keel:Vault:Gc:GraceDays", "--grace-days", StringComparison.Ordinal)
            .Replace("Keel:Vault:Gc:MaxDeletionsPerRun", "--max-deletions", StringComparison.Ordinal).Replace("Keel:Vault:Gc:PageSize", "--page-size", StringComparison.Ordinal)))); return 2; }
        var vaultErrors = vaultOptions.Validate();
        if (vaultErrors.Count > 0) { error.WriteLine("Vault settings: " + string.Join(" ", vaultErrors)); return 2; }

        await using var ds = NpgsqlDataSource.Create(connection);
        using var vault = new ObjectVault(vaultOptions, null, new PostgresVaultPendingGuard(ds, TimeProvider.System));
        var collector = new VaultGarbageCollector(vault, ds, new PostgresAuditLog(ds, TimeProvider.System), TimeProvider.System);
        var csb = new NpgsqlConnectionStringBuilder(connection);
        output.WriteLine($"Target database: {csb.Host}:{csb.Port}/{csb.Database} as {csb.Username}; vault: {vaultOptions.ServiceUrl}/{vaultOptions.Bucket}");
        output.WriteLine($"Mode: {(delete ? "DELETE" : "dry-run")}, grace {graceDays} day(s), at most {max} object(s){(opts.Value("--prefix") is { } p ? $", prefix '{p}'" : "")}.");

        var who = opts.Value("--operator") ?? env("USER") ?? "operator";
        var actor = ActorSnapshot.System with { Name = $"keel-vault ({who})" };
        GcReport report;
        try
        {
            report = await collector.RunAsync(new GcRequest(delete ? GcMode.Delete : GcMode.DryRun, TimeSpan.FromDays(graceDays.Value), max.Value, pageSize.Value,
                opts.Value("--continuation"), actor, null, opts.Value("--prefix")), ct);
        }
        catch (GcAlreadyRunningException e)
        {
            error.WriteLine(e.Message);
            return 1;
        }
        catch (Exception e) when (e is VaultUnavailableException or NpgsqlException)
        {
            error.WriteLine("The run failed: " + e.Message);
            return 1;
        }

        if (opts.Flags.Contains("--json")) output.WriteLine(JsonSerializer.Serialize(report, ReportJson));
        else Print(report, output);
        return 0;
    }

    /// <summary>
    /// <c>keel-vault verify [--parallel N] [--json]</c>: reads every object the database refers to and checks that it exists and hashes to its key at the recorded size (ADR-0060).
    /// Read-only. Exit 0 when everything is intact, 1 when something is missing or damaged, 2 for a usage or configuration error.
    /// </summary>
    private static async Task<int> VerifyAsync(string[] args, TextWriter output, TextWriter error, Func<string, string?> env, CancellationToken ct)
    {
        if (!TryParse(args, error, out var opts)) return 2;
        if (opts.Flags.Contains("--delete") || opts.Flags.Contains("--dry-run")) { error.WriteLine("verify takes no --delete or --dry-run: it never changes anything."); return 2; }
        var parallel = IntOption(opts, "--parallel", 4);
        if (parallel is null or < 1 or > 32) { error.WriteLine("--parallel must be a whole number from 1 to 32."); return 2; }
        var settings = Resolve(opts, env, error);
        if (settings is null) return 2;
        var (connection, vaultOptions) = settings.Value;
        var vaultErrors = vaultOptions.Validate();
        if (vaultErrors.Count > 0) { error.WriteLine("Vault settings: " + string.Join(" ", vaultErrors)); return 2; }
        await using var ds = NpgsqlDataSource.Create(connection);
        using var vault = new ObjectVault(vaultOptions, null, new PostgresVaultPendingGuard(ds, TimeProvider.System));
        var csb = new NpgsqlConnectionStringBuilder(connection);
        output.WriteLine($"Target database: {csb.Host}:{csb.Port}/{csb.Database} as {csb.Username}; vault: {vaultOptions.ServiceUrl}/{vaultOptions.Bucket}");
        VaultVerifyReport report;
        try
        {
            report = await VaultVerifier.VerifyAsync(vault, ds, parallel.Value, ct);
        }
        catch (Exception e) when (e is VaultUnavailableException or NpgsqlException)
        {
            error.WriteLine("The run failed: " + e.Message);
            return 1;
        }
        var inv = CultureInfo.InvariantCulture;
        if (opts.Flags.Contains("--json")) output.WriteLine(JsonSerializer.Serialize(report, ReportJson));
        else
        {
            output.WriteLine($"References: {report.References.ToString(inv)} row(s) naming {report.Distinct.ToString(inv)} distinct object(s).");
            output.WriteLine($"Verified: {report.Verified.ToString(inv)} object(s), {report.BytesVerified.ToString(inv)} bytes (existence, size and SHA-256 of every byte).");
            foreach (var p in report.Problems) output.WriteLine($"PROBLEM {p.Kind}: {p.Sha256} ({p.SizeBytes.ToString(inv)} bytes) referenced by {string.Join(", ", p.Sources)}: {p.Detail}");
            output.WriteLine(report.Intact ? "RESULT: INTACT" : $"RESULT: NOT INTACT ({report.Problems.Count.ToString(inv)} problem(s))");
        }
        return report.Intact ? 0 : 1;
    }

    private static void Print(GcReport r, TextWriter w)
    {
        var inv = CultureInfo.InvariantCulture;
        w.WriteLine($"Listed {r.ObjectsListed.ToString(inv)} object(s): {r.ObjectsReferenced.ToString(inv)} referenced, {r.ObjectsYoung.ToString(inv)} younger than the grace period, {r.ObjectsUnrecognized.ToString(inv)} not vault keys (never touched).");
        w.WriteLine($"Orphan candidates: {r.Candidates.ToString(inv)} ({r.BytesCandidates.ToString(inv)} bytes).");
        w.WriteLine(r.Mode == GcMode.Delete
            ? $"Deleted: {r.Deleted.ToString(inv)} ({r.BytesDeleted.ToString(inv)} bytes); kept after the final re-check: {r.SkippedByRecheck.ToString(inv)}."
            : "Dry run: nothing was deleted.");
        foreach (var key in r.SampleKeys) w.WriteLine($"  {key}");
        w.WriteLine($"Report hash: {r.ReportHash}");
        w.WriteLine(r.Complete ? "The sweep covered the whole bucket." : $"Stopped at the limit. Continue with: --continuation {r.Continuation}");
    }

    private static int? IntOption(Opts o, string name, int fallback) =>
        o.Value(name) is { } text ? (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null) : fallback;

    private static (string Connection, VaultOptions Vault)? Resolve(Opts o, Func<string, string?> env, TextWriter error)
    {
        var builder = new ConfigurationBuilder();
        var useDev = o.Flags.Contains("--use-dev-stack");
        if (useDev)
        {
            var dev = FindUp("src/Keel.Api/appsettings.Development.json");
            if (dev is not null) builder.AddJsonFile(dev, optional: true);
        }
        if (o.Value("--settings") is { } file) builder.AddJsonFile(Path.GetFullPath(file), optional: false);
        builder.AddEnvironmentVariables();
        var overrides = new Dictionary<string, string?>();
        if (o.Value("--connection") is { } cs) overrides["Keel:Database:AppConnectionString"] = cs;
        foreach (var (flag, key) in new[] { ("--vault-url", "ServiceUrl"), ("--vault-access-key", "AccessKey"), ("--vault-secret-key", "SecretKey"), ("--vault-bucket", "Bucket"), ("--vault-region", "Region") })
            if (o.Value(flag) is { } v) overrides[$"Keel:Vault:{key}"] = v;
        builder.AddInMemoryCollection(overrides);
        var config = builder.Build();
        var connection = config["Keel:Database:AppConnectionString"];
        if (string.IsNullOrWhiteSpace(connection))
        {
            error.WriteLine("No database target. Set Keel__Database__AppConnectionString, pass --connection or --settings, or pass --use-dev-stack to target the dev stack explicitly.");
            return null;
        }
        _ = env;
        return (connection, config.GetSection(VaultOptions.Section).Get<VaultOptions>() ?? new VaultOptions());
    }

    private static string? FindUp(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    private static int Usage(TextWriter w, int code)
    {
        w.WriteLine("usage: keel-vault gc [--dry-run | --delete --confirm-bucket NAME] [--grace-days N] [--max-deletions N] [--page-size N] [--prefix P]");
        w.WriteLine("                     [--continuation TOKEN] [--json] [--operator NAME]");
        w.WriteLine("                     [--connection CS | --settings FILE | --use-dev-stack] [--vault-url URL --vault-access-key K --vault-secret-key S --vault-bucket B]");
        w.WriteLine("       keel-vault verify [--parallel N] [--json] [--connection CS | --settings FILE | --use-dev-stack] [vault options]   (read-only: every referenced object exists and hashes to its key)");
        w.WriteLine("       keel-vault verify-archive --package FILE --public-key PEM [--extract SHA256 --out FILE] [--json]   (offline check of a records archive package)");
        w.WriteLine("The target database is never implied: set Keel__Database__AppConnectionString, or pass --connection / --settings / --use-dev-stack.");
        w.WriteLine("Default is a dry run. A delete run removes only unreferenced objects older than the grace period (default 7 days, at least 1).");
        return code;
    }

    private static bool TryParse(string[] args, TextWriter error, out Opts opts)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (ValueOptions.Contains(a))
            {
                if (i + 1 >= args.Length) { error.WriteLine($"{a} needs a value."); opts = default!; return false; }
                values[a] = args[++i];
            }
            else if (a is "--dry-run" or "--delete" or "--json" or "--use-dev-stack") flags.Add(a);
            else { error.WriteLine($"Unknown option '{a}'."); opts = default!; return false; }
        }
        opts = new Opts(values, flags);
        return true;
    }

    private sealed record Opts(Dictionary<string, string> Values, HashSet<string> Flags)
    {
        public string? Value(string name) => Values.GetValueOrDefault(name);
    }
}
