using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Parts;
using Npgsql;

// backup-test-tool populate --connection <app role connection string> --key <checkpoint pem> [--parts N]
// backup-test-tool counts   --connection <connection string>
// populate creates N synthetic parts through the real PartService (so every change writes its audit event in the same
// transaction) and signs a checkpoint with the given key. counts prints "schema.table|rows" for every user table, sorted.

if (args.Length == 0 || args[0] is not ("populate" or "counts")) { Console.Error.WriteLine("usage: populate|counts --connection <cs> ..."); return 2; }
var connection = Option("--connection") ?? throw new InvalidOperationException("--connection is required");
await using var dataSource = NpgsqlDataSource.Create(connection);

if (args[0] == "counts")
{
    await PrintCounts();
    return 0;
}

var keyPath = Option("--key") ?? throw new InvalidOperationException("--key is required");
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

for (var i = 1; i <= parts; i++)
{
    var created = await service.CreateAsync(author,
        new CreatePartCommand($"Synthetic backup test part {i:D3}", "Component", "EA", "Make", markings, "Synthetic",
            new JsonObject { ["material"] = "6061-T6", ["mass"] = 0.1 * i }), $"backup-test-{i}");
    if (!created.IsOk) throw new InvalidOperationException($"part {i}: {string.Join("; ", created.Errors)}");
}

using var key = CheckpointKeys.LoadPem(keyPath);
var checkpoint = await new CheckpointSigner(key, dataSource, TimeProvider.System).CreateAsync();
Console.WriteLine($"populated {parts} parts; checkpoint {(checkpoint is null ? "none" : "at seq " + checkpoint.Seq)}");
return 0;

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
