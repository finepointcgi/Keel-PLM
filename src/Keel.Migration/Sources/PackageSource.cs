using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Keel.Migration.Sources;

/// <summary>Size and SHA-256 of every file of a package, and the package hash derived from them (ADR-0023).</summary>
public sealed record PackageManifest(IReadOnlyDictionary<string, (long Size, string Sha256)> Entries, string Sha256)
{
    public long TotalBytes => Entries.Values.Sum(e => e.Size);
}

/// <summary>
/// A migration package on disk: a directory or a .zip. Reading is by relative path with forward slashes; paths that leave the
/// package (absolute, "..") are refused so a hostile package cannot make the importer read arbitrary files.
/// </summary>
public abstract class PackageSource : IDisposable
{
    public abstract string Location { get; }
    public abstract IEnumerable<string> ListFiles();
    public abstract bool Exists(string relativePath);
    public abstract long Length(string relativePath);
    public abstract Stream OpenRead(string relativePath);
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) { }

    public static PackageSource Open(string path)
    {
        if (Directory.Exists(path)) return new DirectoryPackage(Path.GetFullPath(path));
        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return new ZipPackage(Path.GetFullPath(path));
        if (File.Exists(path)) return new SingleFilePackage(Path.GetFullPath(path));
        throw new FileNotFoundException($"Package '{path}' is not a directory, a .zip or a file.");
    }

    /// <summary>Normalizes a package path; null when it is empty, rooted or escapes the package.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Replace('\\', '/').Trim();
        if (p.StartsWith('/') || (p.Length > 1 && p[1] == ':')) return null;
        var parts = new List<string>();
        foreach (var seg in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") return null;
            parts.Add(seg);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    public PackageManifest ComputeManifest()
    {
        var entries = new SortedDictionary<string, (long, string)>(StringComparer.Ordinal);
        foreach (var f in ListFiles())
        {
            using var s = OpenRead(f);
            var hash = Convert.ToHexStringLower(SHA256.HashData(s));
            entries[f] = (Length(f), hash);
        }
        var sb = new StringBuilder();
        foreach (var (path, (size, sha)) in entries) sb.Append(path).Append('\t').Append(size).Append('\t').Append(sha).Append('\n');
        var package = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        return new PackageManifest(entries.ToDictionary(e => e.Key, e => e.Value), package);
    }

    private sealed class DirectoryPackage(string root) : PackageSource
    {
        public override string Location => root;
        public override IEnumerable<string> ListFiles() =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal);
        private string? Resolve(string rel)
        {
            var n = Normalize(rel);
            if (n is null) return null;
            var full = Path.GetFullPath(Path.Combine(root, n));
            return full.StartsWith(root, StringComparison.Ordinal) ? full : null;
        }
        public override bool Exists(string relativePath) => Resolve(relativePath) is { } f && File.Exists(f);
        public override long Length(string relativePath) => new FileInfo(Resolve(relativePath) ?? throw new FileNotFoundException(relativePath)).Length;
        public override Stream OpenRead(string relativePath) =>
            new FileStream(Resolve(relativePath) ?? throw new FileNotFoundException(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
    }

    private sealed class SingleFilePackage(string file) : PackageSource
    {
        private string Name => Path.GetFileName(file);
        public override string Location => file;
        public override IEnumerable<string> ListFiles() => [Name];
        public override bool Exists(string relativePath) => relativePath == Name;
        public override long Length(string relativePath) => new FileInfo(file).Length;
        public override Stream OpenRead(string relativePath) => relativePath == Name
            ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)
            : throw new FileNotFoundException(relativePath);
    }

    private sealed class ZipPackage : PackageSource
    {
        private readonly ZipArchive _zip;
        private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
        private readonly string _location;

        public ZipPackage(string file)
        {
            _location = file;
            _zip = ZipFile.OpenRead(file);
            foreach (var e in _zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                var n = Normalize(e.FullName);
                if (n is not null) _entries[n] = e;
            }
        }

        public override string Location => _location;
        public override IEnumerable<string> ListFiles() => _entries.Keys.Order(StringComparer.Ordinal);
        public override bool Exists(string relativePath) => Normalize(relativePath) is { } n && _entries.ContainsKey(n);
        public override long Length(string relativePath) => _entries[Normalize(relativePath) ?? throw new FileNotFoundException(relativePath)].Length;
        public override Stream OpenRead(string relativePath) => _entries[Normalize(relativePath) ?? throw new FileNotFoundException(relativePath)].Open();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _zip.Dispose();
            base.Dispose(disposing);
        }
    }
}
