using System.Text.Json;
using Keel.Audit;
using Keel.Admin;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Integration;
using Keel.Parts;
using Keel.Workflow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keel.Seed;

/// <summary>Where the seed tool connects, resolved like the API host does: appsettings, then environment (<c>Keel__Database__AppConnectionString</c>), then flags.</summary>
public sealed record SeedSettings(string AppConnectionString, VaultOptions Vault, string? CheckpointPublicKeyPath)
{
    /// <summary>
    /// Resolution order (later wins): an explicit <c>--settings</c> file, environment variables with the API's keys, then <c>--connection</c>.
    /// The dev stack's <c>src/Keel.Api/appsettings.Development.json</c> is used ONLY with <paramref name="useDevStack"/> (<c>--use-dev-stack</c>):
    /// seeding writes data, so it must never reach the shared dev database by omission.
    /// </summary>
    public static SeedSettings Resolve(string? settingsFile, string? connection, string? publicKey, bool useDevStack = false)
    {
        var builder = new ConfigurationBuilder();
        var dev = useDevStack ? FindUp("src/Keel.Api/appsettings.Development.json") : null;
        if (dev is not null) builder.AddJsonFile(dev, optional: true);
        if (settingsFile is not null) builder.AddJsonFile(Path.GetFullPath(settingsFile), optional: false);
        builder.AddEnvironmentVariables();
        var config = builder.Build();
        var conn = connection ?? config["Keel:Database:AppConnectionString"]
            ?? throw new InvalidOperationException(
                "No database connection. Set Keel__Database__AppConnectionString, pass --connection, or pass --use-dev-stack to target the dev stack from src/Keel.Api/appsettings.Development.json.");
        var vault = config.GetSection(VaultOptions.Section).Get<VaultOptions>() ?? new VaultOptions();
        return new SeedSettings(conn, vault, publicKey);
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
}

/// <summary>
/// The real services of every module wired exactly like <c>Keel.Api</c> (same registrations, same gateways), over one database.
/// The seed goes through these, so every action is authorized, validated and audited like a user's click.
/// </summary>
public sealed class SeedHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string? _tempDirectory;

    private SeedHost(ServiceProvider services, string? tempDirectory) { _services = services; _tempDirectory = tempDirectory; }

    public NpgsqlDataSource DataSource => _services.GetRequiredService<NpgsqlDataSource>();
    public PartService Parts => _services.GetRequiredService<PartService>();
    public BomService Bom => _services.GetRequiredService<BomService>();
    public DocumentService Documents => _services.GetRequiredService<DocumentService>();
    public ChangeService Change => _services.GetRequiredService<ChangeService>();
    public ImpactAnalysisService Impact => _services.GetRequiredService<ImpactAnalysisService>();
    public WorkflowService Workflow => _services.GetRequiredService<WorkflowService>();
    public UserDirectory Directory => _services.GetRequiredService<UserDirectory>();
    public ObjectVault Vault => _services.GetRequiredService<ObjectVault>();
    public ClassificationService Classification => _services.GetRequiredService<ClassificationService>();
    public BaselineService Baselines => _services.GetRequiredService<BaselineService>();
    public EffectivityService Effectivity => _services.GetRequiredService<EffectivityService>();
    public MbomService Mbom => _services.GetRequiredService<MbomService>();
    public TdpService Tdp => _services.GetRequiredService<TdpService>();
    public IntegrationAdminService IntegrationAdmin => _services.GetRequiredService<IntegrationAdminService>();
    public OutboxQueries Outbox => _services.GetRequiredService<OutboxQueries>();

    public static SeedHost Create(SeedSettings settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => NpgsqlDataSource.Create(settings.AppConnectionString));
        services.AddSingleton<PostgresAuditLog>();
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<PostgresAuditLog>());
        services.AddSingleton<AuditQueries>();
        services.AddSingleton<PartService>();
        services.AddSingleton(settings.Vault);
        services.AddSingleton(sp => new ObjectVault(sp.GetRequiredService<VaultOptions>()));
        services.AddSingleton<DocumentService>();
        BomModule.AddKeelBom(services);
        services.AddSingleton<IWorkflowObjectGateway, PartWorkflowGateway>();
        services.AddSingleton<IWorkflowObjectGateway, DocumentWorkflowGateway>();
        services.AddSingleton<IChangeObjectGateway, PartChangeGateway>();
        services.AddSingleton<ILinkedDocumentProvider, LinkedDocumentProvider>();
        ChangeModule.AddKeelChange(services);
        services.AddKeelWorkflow(new ConfigurationBuilder().Build());
        // Phase 2 and 4 modules, registered like the API host (the seed goes through the real services). The ERP outbox sink is registered, so releases
        // write events; no dispatcher runs here and no webhook sink is created. The TDP signing key and the file-drop root are throwaway: a temp directory
        // that disappears with the seed (the demo host configures its own).
        var temp = Path.Combine(Path.GetTempPath(), "keel-seed-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temp);
        using (var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
            File.WriteAllText(Path.Combine(temp, "tdp-signing.pem"), key.ExportPkcs8PrivateKeyPem());
        var integration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Keel:Integration:FileDropRoot"] = Path.Combine(temp, "drop"),
            ["Keel:Integration:TdpSigningKeyPath"] = Path.Combine(temp, "tdp-signing.pem"),
        }).Build();
        System.IO.Directory.CreateDirectory(Path.Combine(temp, "drop"));
        services.AddSingleton<IDocumentPinProvider, DocumentPinProvider>();
        AdminModule.AddKeelAdmin(services);
        services.AddSingleton<ITdpPartSource, PartTdpSource>();
        services.AddSingleton<ITdpDocumentSource, TdpDocumentSource>();
        IntegrationModule.AddKeelIntegration(services, integration);
        return new SeedHost(services.BuildServiceProvider(), temp);
    }

    /// <summary>Records the realm users in <c>identity.user_directory</c>, the same upsert the application runs at sign-in.</summary>
    public async Task UpsertDirectoryAsync(IEnumerable<Subject> users)
    {
        foreach (var u in users) await Directory.UpsertAsync(u);
    }

    /// <summary>Walks the whole audit chain and checks any checkpoint signatures against <paramref name="trustedKeys"/>.</summary>
    public async Task<VerificationResult> VerifyAuditAsync(IReadOnlyCollection<System.Security.Cryptography.ECDsa> trustedKeys)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        return await AuditVerifier.VerifyAsync(conn, trustedKeys);
    }

    public async Task<long> AuditEventCountAsync()
    {
        await using var cmd = DataSource.CreateCommand("SELECT count(*) FROM audit.event");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Events in the outbox as an administrator sees them (through the service, not a table read).</summary>
    public async Task<long> OutboxCountAsync(Subject admin) => (await Outbox.ListForAdminAsync(admin, null, null, null, 1000, null)).Must("list the outbox").Count;

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        if (_tempDirectory is not null) try { System.IO.Directory.Delete(_tempDirectory, true); } catch (IOException) { }
    }
}

/// <summary>A seed step failed through a service: the message carries the service's own reasons.</summary>
public sealed class SeedException(string message) : Exception(message);

public static class SeedExtensions
{
    /// <summary>Unwraps a service result, failing the seed with the service's reasons (never skipping a business rule).</summary>
    public static T Must<T>(this Outcome<T> outcome, string what) =>
        outcome.IsOk ? outcome.Value! : throw new SeedException($"{what}: {outcome.Kind} {string.Join("; ", outcome.Errors)}");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Json(this object value) => JsonSerializer.Serialize(value, Indented);
}
