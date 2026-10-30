using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Parts;
using Npgsql;

// backup-test-tool populate --connection <app role connection string> --key <checkpoint pem> [--parts N]
//                           [--documents N --manifest FILE --vault-url U --vault-access-key K --vault-secret-key S --vault-bucket B]
// backup-test-tool counts   --connection <connection string>
// backup-test-tool check-documents --connection <cs> --manifest FILE --vault-url ... (every manifest document is in the database and its file reads back, verified, with the recorded SHA-256)
// backup-test-tool tamper-object  --sha <sha256> --vault-url ...   (overwrites the stored object with other bytes: a negative control for `keel-vault verify`)
// backup-test-tool delete-object  --sha <sha256> --vault-url ...   (removes the stored object: a negative control)
// populate creates N synthetic parts through the real PartService (so every change writes its audit event in the same
// transaction), optionally M synthetic documents with a file each through the real DocumentService and vault, and signs a checkpoint with the given key.
// counts prints "schema.table|rows" for every user table, sorted. Used by scripts/backup-restore-test.sh and scripts/ops/backup-restore-drill.sh.

if (args.Length == 0 || args[0] is not ("populate" or "counts" or "check-documents" or "tamper-object" or "delete-object")) { Console.Error.WriteLine("usage: populate|counts|check-documents|tamper-object|delete-object --connection <cs> ..."); return 2; }
if (args[0] is "tamper-object" or "delete-object")
{
    var sha = Option("--sha") ?? throw new InvalidOperationException("--sha is required");
    using var s3 = ObjectVault.CreateClient(VaultFromArgs());
    var bucket = VaultFromArgs().Bucket;
    if (args[0] == "delete-object") await s3.DeleteObjectAsync(bucket, sha);
    else await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = sha, InputStream = new MemoryStream(Encoding.UTF8.GetBytes("tampered " + Guid.NewGuid())) });
    Console.WriteLine($"{args[0]} {sha}");
    return 0;
}
var connection = Option("--connection") ?? throw new InvalidOperationException("--connection is required");
await using var dataSource = NpgsqlDataSource.Create(connection);

if (args[0] == "counts")
{
    await PrintCounts();
    return 0;
}

var keyPath = args[0] == "check-documents" ? "" : Option("--key") ?? throw new InvalidOperationException("--key is required");
var parts = int.Parse(Option("--parts") ?? "25", System.Globalization.CultureInfo.InvariantCulture);
var audit = new PostgresAuditLog(dataSource, TimeProvider.System);
var service = new PartService(dataSource, audit, TimeProvider.System);
var author = new Subject
{
    Id = "sub-backup-test", Username = "backup-test", DisplayName = "Backup test author", UsPerson = true, Org = "ENG",
    Programs = ["ORCA"], CuiCategories = ["EXPT"], ExportAuthorizations = [],
    Roles = [Roles.Viewer, Roles.Author],
};
var markings = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
    controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value!;

var documents = int.Parse(Option("--documents") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
var contentSeed = int.Parse(Option("--content-seed") ?? "0", System.Globalization.CultureInfo.InvariantCulture);

if (args[0] == "check-documents")
{
    var manifestIn = JsonNode.Parse(await File.ReadAllTextAsync(Option("--manifest") ?? throw new InvalidOperationException("--manifest is required")))!.AsArray();
    using var checkVault = new ObjectVault(VaultFromArgs());
    var bad = 0;
    foreach (var entry in manifestIn)
    {
        var sha = entry!["sha256"]!.GetValue<string>();
        var size = entry["size"]!.GetValue<long>();
        await using var cmd = dataSource.CreateCommand("SELECT count(*) FROM plm.document_file WHERE sha256 = @s AND size_bytes = @n");
        cmd.Parameters.AddWithValue("s", sha); cmd.Parameters.AddWithValue("n", size);
        if ((long)(await cmd.ExecuteScalarAsync())! < 1) { Console.WriteLine($"MISSING row {entry["number"]} {sha}"); bad++; continue; }
        try
        {
            await using var stream = await checkVault.OpenReadAsync(sha, size);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            if (hash != sha) { Console.WriteLine($"DIFFERENT {entry["number"]}"); bad++; }
        }
        catch (VaultIntegrityException e) { Console.WriteLine($"DAMAGED {entry["number"]}: {e.Message}"); bad++; }
    }
    Console.WriteLine($"documents checked: {manifestIn.Count}, problems: {bad}");
    return bad == 0 ? 0 : 1;
}

for (var i = 1; i <= parts; i++)
{
    var created = await service.CreateAsync(author,
        new CreatePartCommand($"Synthetic backup test part {i:D3}", "Component", "EA", "Make", markings, "Synthetic",
            new JsonObject { ["material"] = "6061-T6", ["mass"] = 0.1 * i }), $"backup-test-{i}");
    if (!created.IsOk) throw new InvalidOperationException($"part {i}: {string.Join("; ", created.Errors)}");
}

if (documents > 0)
{
    using var vault = new ObjectVault(VaultFromArgs());
    var docs = new DocumentService(dataSource, audit, TimeProvider.System, vault);
    var manifest = new JsonArray();
    for (var i = 1; i <= documents; i++)
    {
        // deterministic, distinct content of varying size (about 1 KB to 3 MB) so that restore is judged on real bytes
        var body = new byte[1024 + (i * 97_531 % 3_000_000)];
        new Random(contentSeed + i).NextBytes(body);   // --content-seed makes a later batch differ from an earlier one
        var created = await docs.CreateAsync(author, new CreateDocumentCommand($"Synthetic drill document {contentSeed + i:D3}", "Specification", markings, "Synthetic drill document"),
            new FileUpload($"drill-{contentSeed + i:D3}.bin", "application/octet-stream", new MemoryStream(body)), $"drill-doc-{i}");
        if (!created.IsOk) throw new InvalidOperationException($"document {i}: {string.Join("; ", created.Errors)}");
        var file = created.Value!.Iteration.Files.Single();
        manifest.Add(new JsonObject { ["number"] = created.Value.Number, ["sha256"] = file.Sha256, ["size"] = file.SizeBytes });
    }
    var manifestPath = Option("--manifest");
    if (manifestPath is not null)
    {
        var existing = File.Exists(manifestPath) ? JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsArray() : [];
        foreach (var m in manifest) existing.Add(JsonNode.Parse(m!.ToJsonString()));
        await File.WriteAllTextAsync(manifestPath, existing.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    Console.WriteLine($"populated {documents} documents with files ({manifest.Sum(m => m!["size"]!.GetValue<long>())} bytes)");
}

using var key = CheckpointKeys.LoadPem(keyPath);
var checkpoint = await new CheckpointSigner(key, dataSource, TimeProvider.System).CreateAsync();
Console.WriteLine($"populated {parts} parts; checkpoint {(checkpoint is null ? "none" : "at seq " + checkpoint.Seq)}");
return 0;

VaultOptions VaultFromArgs() => new()
{
    ServiceUrl = Option("--vault-url") ?? throw new InvalidOperationException("--vault-url is required"),
    AccessKey = Option("--vault-access-key") ?? "keel_vault_dev", SecretKey = Option("--vault-secret-key") ?? "keel_vault_dev_secret", Bucket = Option("--vault-bucket") ?? "keel-vault",
};

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

async Task PrintCounts()
{
    await using var conn = await dataSource.OpenConnectionAsync();
    var tables = new List<string>();
    await using (var list = new NpgsqlCommand(
        "SELECT schemaname || '.' || tablename FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema') ORDER BY 1", conn))
    await using (var r = await list.ExecuteReaderAsync())
        while (await r.ReadAsync()) tables.Add(r.GetString(0));
    foreach (var table in tables)
    {
        // Table names come from the catalog query above, never from input.
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", conn);
        Console.WriteLine($"{table}|{await count.ExecuteScalarAsync()}");
    }
}
