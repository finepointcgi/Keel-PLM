using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Keel.Audit;
using Keel.Records;

namespace Keel.VaultTool;

/// <summary>
/// <c>keel-vault verify-archive</c>: checks an archive package (records management, ADR-0041) with no database and no vault, so an auditor can verify a package that was copied out:
/// <code>
/// keel-vault verify-archive --package FILE --public-key PEM [--public-key PEM ...] [--extract SHA256 --out FILE] [--json]
/// </code>
/// It checks the signed manifest under the given trusted public keys, the SHA-256 and size of every entry, and the hash of every audit event in the extract. Exit code 0 when the package
/// is intact, 1 when anything is wrong, 2 for a usage error. <c>--extract</c> writes one verified file out of a package that verified.
/// </summary>
public static class ArchiveCli
{
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? package = null, extract = null, outFile = null;
        var keys = new List<string>();
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--package" when i + 1 < args.Length: package = args[++i]; break;
                case "--public-key" when i + 1 < args.Length: keys.Add(args[++i]); break;
                case "--extract" when i + 1 < args.Length: extract = args[++i]; break;
                case "--out" when i + 1 < args.Length: outFile = args[++i]; break;
                case "--json": json = true; break;
                default:
                    error.WriteLine($"Unknown or incomplete option '{args[i]}'.");
                    return Usage(error);
            }
        }
        if (package is null || keys.Count == 0 || (extract is null) != (outFile is null)) return Usage(error);
        if (!File.Exists(package)) { error.WriteLine($"The package file '{package}' does not exist."); return 2; }

        var trusted = new List<ECDsa>();
        try
        {
            foreach (var k in keys) trusted.Add(CheckpointKeys.LoadPem(k));
        }
        catch (Exception e) when (e is IOException or CryptographicException or ArgumentException)
        {
            error.WriteLine("A public key could not be read: " + e.Message);
            return 2;
        }

        using var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = ArchiveReader.Read(stream, trusted);
        var v = read.Verification;
        if (json) output.WriteLine(JsonSerializer.Serialize(new { valid = v.Valid, archive = read.Number, manifestSha256 = read.ManifestSha256, keyId = read.KeyId, entries = v.Entries, auditEvents = v.Events, manifestSigned = v.ManifestSigned, checkpointVerified = v.CheckpointVerified, problems = v.Problems, note = v.Note }, ReportJson));
        else
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Archive {read.Number ?? "(unknown)"}: {(v.Valid ? "VALID" : "NOT VALID")}"));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  manifest signature {(v.ManifestSigned ? "verified" : "NOT verified")} (key {read.KeyId ?? "unknown"}); {v.Entries} entries; {v.Events} audit events; checkpoint {(v.CheckpointVerified ? "verified" : "not present or not verified")}"));
            foreach (var p in v.Problems) output.WriteLine("  problem: " + p);
            if (v.Note is not null) output.WriteLine("  note: " + v.Note);
        }
        if (!v.Valid) return 1;
        if (extract is not null)
        {
            stream.Position = 0;
            using var dest = new FileStream(outFile!, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!ArchiveReader.TryExtractFile(stream, extract, dest)) { error.WriteLine("The file is not in the package, or does not match its name."); return 1; }
            output.WriteLine($"Wrote {outFile}.");
        }
        return 0;
    }

    private static int Usage(TextWriter w)
    {
        w.WriteLine("usage: keel-vault verify-archive --package FILE --public-key PEM [--public-key PEM ...] [--extract SHA256 --out FILE] [--json]");
        w.WriteLine("Checks an archive package offline: the signed manifest, every entry's SHA-256 and size, and the hash of every audit event. Exit 0 = intact, 1 = not intact, 2 = usage.");
        return 2;
    }
}
