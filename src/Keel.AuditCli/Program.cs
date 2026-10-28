using System.Security.Cryptography;
using Keel.Audit;
using Npgsql;

// keel-audit: offline tooling for the tamper-evident audit log (ADR-0005).
//   keel-audit keygen --out <dir>
//   keel-audit verify --public-key <pem> [--public-key <pem> ...] [--json]
// verify reads the connection string from KEEL_AUDIT_CONNECTION so passwords stay out of process listings.

return args.FirstOrDefault() switch
{
    "keygen" => Keygen(Option(args, "--out") ?? "."),
    "verify" => await Verify(Options(args, "--public-key"), args.Contains("--json")),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: keel-audit keygen --out <dir>");
    Console.Error.WriteLine("       keel-audit verify --public-key <pem> [--public-key <pem> ...] [--json]   (connection: $KEEL_AUDIT_CONNECTION)");
    return 2;
}

static int Keygen(string dir)
{
    Directory.CreateDirectory(dir);
    var priv = Path.Combine(dir, "checkpoint-signing.pem");
    var pub = Path.Combine(dir, "checkpoint-signing.pub.pem");
    if (File.Exists(priv)) { Console.Error.WriteLine($"{priv} already exists; refusing to overwrite a signing key."); return 1; }
    using var key = CheckpointKeys.Generate();
    File.WriteAllText(priv, key.ExportPkcs8PrivateKeyPem());
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(priv, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    File.WriteAllText(pub, key.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine($"key id {CheckpointKeys.KeyId(key)}\nprivate {priv}\npublic  {pub}");
    return 0;
}

static async Task<int> Verify(IReadOnlyList<string> keyPaths, bool json)
{
    var cs = Environment.GetEnvironmentVariable("KEEL_AUDIT_CONNECTION");
    if (string.IsNullOrWhiteSpace(cs)) { Console.Error.WriteLine("KEEL_AUDIT_CONNECTION is not set."); return 2; }
    var keys = keyPaths.Select(CheckpointKeys.LoadPem).ToList();
    if (keys.Count == 0) Console.Error.WriteLine("warning: no --public-key given; checkpoint signatures cannot be trusted.");

    await using var conn = new NpgsqlConnection(cs);
    await conn.OpenAsync();
    var result = await AuditVerifier.VerifyAsync(conn, keys);
    if (json)
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    }
    else
    {
        Console.WriteLine($"events checked:      {result.EventsChecked}");
        Console.WriteLine($"checkpoints checked: {result.CheckpointsChecked}");
        Console.WriteLine($"chain head:          seq {result.LastSeq?.ToString() ?? "-"} {result.LastHash ?? ""}");
        foreach (var f in result.Failures) Console.WriteLine($"FAIL {f}");
        Console.WriteLine(result.Valid ? "RESULT: VALID" : "RESULT: INVALID");
    }
    foreach (var k in keys) k.Dispose();
    return result.Valid ? 0 : 1;
}

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static List<string> Options(string[] args, string name) =>
    args.Select((a, i) => (a, i)).Where(t => t.a == name && t.i + 1 < args.Length).Select(t => args[t.i + 1]).ToList();
