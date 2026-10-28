using System.Text.Json;
using Keel.Documents.Vault;
using Npgsql;

namespace Keel.Import;

/// <summary>
/// Where the importer connects. Same rule as the seed tool: there is NO implicit target. A connection must come from <c>--connection</c>, a
/// <c>--settings</c> file, or the environment (<c>KEEL_IMPORT_CONNECTION</c> / <c>Keel__Database__AppConnectionString</c>). The dev stack's
/// <c>src/Keel.Api/appsettings.Development.json</c> is read ONLY with <c>--use-dev-stack</c>, because loading writes data that cannot be deleted.
/// </summary>
public sealed record ImportSettings(string? ConnectionString, VaultOptions? Vault)
{
    public static ImportSettings Resolve(string? settingsFile, string? connection, bool useDevStack, Func<string, string?> env, IReadOnlyList<(string Key, string Value)> vaultFlags)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Merge(string json)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            Flatten(doc.RootElement, "", values);
        }
        if (useDevStack && FindUp("src/Keel.Api/appsettings.Development.json") is { } dev) Merge(File.ReadAllText(dev));
        if (settingsFile is not null) Merge(File.ReadAllText(Path.GetFullPath(settingsFile)));
        foreach (var key in new[] { "Keel:Database:AppConnectionString", "Keel:Vault:ServiceUrl", "Keel:Vault:AccessKey", "Keel:Vault:SecretKey", "Keel:Vault:Bucket", "Keel:Vault:Region" })
            if (env(key.Replace(":", "__", StringComparison.Ordinal)) is { Length: > 0 } v) values[key] = v;
        var conn = connection ?? env("KEEL_IMPORT_CONNECTION") ?? values.GetValueOrDefault("Keel:Database:AppConnectionString");
        foreach (var (key, value) in vaultFlags) values["Keel:Vault:" + key] = value;
        VaultOptions? vault = null;
        if (values.TryGetValue("Keel:Vault:ServiceUrl", out var serviceUrl))
        {
            vault = new VaultOptions
            {
                ServiceUrl = serviceUrl, AccessKey = values.GetValueOrDefault("Keel:Vault:AccessKey", ""), SecretKey = values.GetValueOrDefault("Keel:Vault:SecretKey", ""),
                Bucket = values.GetValueOrDefault("Keel:Vault:Bucket", "keel-vault"), Region = values.GetValueOrDefault("Keel:Vault:Region", "us-east-1"),
            };
        }
        return new ImportSettings(conn, vault);
    }

    /// <summary>host:port/database, never the password.</summary>
    public string DescribeTarget()
    {
        if (ConnectionString is null) return "(none)";
        var b = new NpgsqlConnectionStringBuilder(ConnectionString);
        return $"{b.Host}:{b.Port}/{b.Database} as {b.Username}";
    }

    private static void Flatten(JsonElement e, string prefix, Dictionary<string, string> into)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject()) Flatten(p.Value, prefix.Length == 0 ? p.Name : prefix + ":" + p.Name, into);
        else if (e.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            into[prefix] = e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();
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
