using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Reports;
using Npgsql;

namespace Keel.Reports.Signing;

public sealed record SigningIdentityView(
    Guid Id, string Name, string KeySource, string Subject, string Thumbprint, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Algorithm, bool IsDevelopment, bool IsActive,
    string Reason, string Location, string KeyReference, string? PasswordReference, int CertificatesInBundle, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? RetiredAt)
{
    public string Status => RetiredAt is not null ? "Retired" : IsActive ? "Active" : "Standby";
}

public sealed record AddSigningIdentity(string Name, string KeySource, string CertificatePem, string KeyReference, string? PasswordReference, string Reason, string Location);

public sealed record SigningPolicyView(bool Required, bool ActiveIdentity, bool DevelopmentCertificatesAllowed);

public sealed record RecordedPackage(string Number, string Kind, string Key, DateTimeOffset CreatedAt, string CreatedBy, string Banner);

public sealed record VerifyResult(VerificationReport Report, RecordedPackage? Recorded);

/// <summary>A signing identity ready to sign one document. Dispose after use.</summary>
public sealed class PreparedSigner(Guid identityId, LoadedSigningKey key, string reason, string location) : IDisposable
{
    public Guid IdentityId { get; } = identityId;
    public LoadedSigningKey Key { get; } = key;
    public string Reason { get; } = reason;
    public string Location { get; } = location;
    public void Dispose() => Key.Dispose();
}

/// <summary>
/// The signing identity, the signing policy and the verification of signed release packages (ADR-0051). The database holds the certificates (public) and a reference to the
/// private key; the key is read when a package is signed. Every administrative act and every signature and verification is audited; the audit entries carry thumbprints,
/// numbers and hashes, never key material, passwords or paths of secrets.
/// </summary>
public sealed class PackageSigningService(
    NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IReleasePackageSource source, SigningOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record ObjRef(string T, string K);
    private const string IdentityColumns =
        "id, name, key_source, certificate_pem, key_reference, password_reference, reason, location, thumbprint, subject_dn, not_before, not_after, algorithm, is_development, is_active, created_at, created_by, retired_at";

    public SigningOptions Options => options;

    // ------------------------------------------------------------------ administration

    public async Task<Outcome<IReadOnlyList<SigningIdentityView>>> ListIdentitiesAsync(Subject admin, CancellationToken ct = default)
    {
        if (!IsAdmin(admin)) return Outcome<IReadOnlyList<SigningIdentityView>>.Forbidden(["signing.admin_only"]);
        var list = new List<SigningIdentityView>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {IdentityColumns} FROM rpt.signing_identity ORDER BY created_at DESC", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return Outcome<IReadOnlyList<SigningIdentityView>>.Ok(list);
    }

    public async Task<SigningPolicyView> GetPolicyAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT (SELECT required FROM rpt.signing_policy WHERE id = 1), EXISTS (SELECT 1 FROM rpt.signing_identity WHERE is_active)", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new SigningPolicyView(r.GetBoolean(0), r.GetBoolean(1), options.AllowDevelopmentCertificate);
    }

    public async Task<Outcome<SigningIdentityView>> AddIdentityAsync(Subject admin, AddSigningIdentity request, string? correlationId, CancellationToken ct = default)
        => await AddCoreAsync(admin, request, development: false, correlationId, ct);

    private async Task<Outcome<SigningIdentityView>> AddCoreAsync(Subject admin, AddSigningIdentity request, bool development, string? correlationId, CancellationToken ct)
    {
        if (!IsAdmin(admin)) return Outcome<SigningIdentityView>.Forbidden(["signing.admin_only"]);
        var errors = new List<string>();
        var name = (request.Name ?? "").Trim();
        var reason = (request.Reason ?? "").Trim();
        var location = (request.Location ?? "").Trim();
        if (name.Length is 0 or > 120) errors.Add("Give the identity a name of at most 120 characters.");
        if (reason.Length is 0 or > 200) errors.Add("Say what a signature means (for example \"Approved for release\"), at most 200 characters.");
        if (location.Length is 0 or > 200) errors.Add("Give the signing location (for example the site name), at most 200 characters.");
        if (!SigningKeySources.All.Contains(request.KeySource)) errors.Add("Choose where the private key is kept.");
        var keyRef = (request.KeyReference ?? "").Trim();
        if (keyRef.Length is 0 or > 1000) errors.Add("Say where the private key is.");
        var pwd = string.IsNullOrWhiteSpace(request.PasswordReference) ? null : request.PasswordReference.Trim();
        if (pwd is not null && !System.Text.RegularExpressions.Regex.IsMatch(pwd, "^(env|file):.{1,500}$")) errors.Add("A password is never typed here: name an environment variable (env:NAME) or a file (file:/path) that holds it.");
        if (errors.Count > 0) return Outcome<SigningIdentityView>.Invalid(errors);

        IReadOnlyList<X509Certificate2> bundle;
        try { bundle = SigningKeyLoader.ParseBundle(request.CertificatePem ?? ""); }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return Outcome<SigningIdentityView>.Invalid(["The certificate text is not a PEM certificate bundle. Paste the signing certificate first, then the certificates above it, root last."]); }
        foreach (var c in bundle)
            if (SigningPolicy.Reject(c) is { } why) return Outcome<SigningIdentityView>.Invalid([why]);
        var leaf = bundle[0];
        var now = clock.GetUtcNow();
        if (now < leaf.NotBefore || now > leaf.NotAfter) return Outcome<SigningIdentityView>.Invalid(["The signing certificate is not valid now."]);
        if (!(bundle[^1].SubjectName.RawData.AsSpan().SequenceEqual(bundle[^1].IssuerName.RawData)))
            return Outcome<SigningIdentityView>.Invalid(["The bundle must end with the root certificate of your PKI: it becomes the trust anchor used to verify packages."]);
        foreach (var ku in leaf.Extensions.OfType<X509KeyUsageExtension>())
            if ((ku.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0) return Outcome<SigningIdentityView>.Invalid(["The signing certificate's key usage does not allow digital signatures."]);

        var thumb = SigningKeyLoader.Sha256(leaf);
        var reference = new SigningKeyReference(request.KeySource, request.CertificatePem!, keyRef, pwd, thumb);
        try
        {
            using var key = SigningKeyLoader.Load(reference, options);
            SigningKeyLoader.ProveKeyMatches(key);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        {
            await audit.AppendAsync(Entry(admin, "output.signing.identity_add", AuditOutcome.Failure, thumb, correlationId, ("keySource", request.KeySource)), ct);
            return Outcome<SigningIdentityView>.Invalid([$"The private key cannot be used: {Safe(e)}"]);
        }

        var id = Guid.CreateVersion7();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using var ins = new NpgsqlCommand(
                """
                INSERT INTO rpt.signing_identity (id, name, key_source, certificate_pem, key_reference, password_reference, reason, location, thumbprint, subject_dn, not_before, not_after, algorithm,
                                                  is_development, is_active, created_at, created_by, created_by_sub)
                VALUES (@id, @name, @src, @pem, @kref, @pref, @reason, @loc, @thumb, @subj, @nb, @na, @alg, @dev, false, @at, @by, @sub)
                """, conn, tx);
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("name", name);
            ins.Parameters.AddWithValue("src", request.KeySource);
            ins.Parameters.AddWithValue("pem", request.CertificatePem!.Trim());
            ins.Parameters.AddWithValue("kref", keyRef);
            ins.Parameters.AddWithValue("pref", (object?)pwd ?? DBNull.Value);
            ins.Parameters.AddWithValue("reason", reason);
            ins.Parameters.AddWithValue("loc", location);
            ins.Parameters.AddWithValue("thumb", thumb);
            ins.Parameters.AddWithValue("subj", leaf.Subject);
            ins.Parameters.AddWithValue("nb", new DateTimeOffset(leaf.NotBefore.ToUniversalTime(), TimeSpan.Zero));
            ins.Parameters.AddWithValue("na", new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero));
            ins.Parameters.AddWithValue("alg", SigningPolicy.Describe(leaf));
            ins.Parameters.AddWithValue("dev", development);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", admin.Username ?? admin.DisplayName);
            ins.Parameters.AddWithValue("sub", admin.Id);
            await ins.ExecuteNonQueryAsync(ct);
            await audit.AppendAsync(tx, Entry(admin, development ? "output.signing.dev_generate" : "output.signing.identity_add", AuditOutcome.Success, thumb, correlationId,
                ("keySource", request.KeySource), ("subject", leaf.Subject), ("algorithm", SigningPolicy.Describe(leaf)), ("development", development ? "true" : "false")), ct);
            await tx.CommitAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return Outcome<SigningIdentityView>.Conflict("A signing identity with that name exists."); }
        return Outcome<SigningIdentityView>.Ok(await GetViewAsync(id, ct));
    }

    /// <summary>DEVELOPMENT ONLY: creates a throw-away chain and key file and registers it (inactive). Refused unless the host allows it.</summary>
    public async Task<Outcome<SigningIdentityView>> GenerateDevelopmentIdentityAsync(Subject admin, string? name, string? correlationId, CancellationToken ct = default)
    {
        if (!IsAdmin(admin)) return Outcome<SigningIdentityView>.Forbidden(["signing.admin_only"]);
        if (!options.AllowDevelopmentCertificate) return Outcome<SigningIdentityView>.Forbidden(["signing.dev_certificate_disabled"]);
        var (bundle, keyPem) = DevSigningCertificate.Generate(clock.GetUtcNow());
        var dir = options.DevelopmentKeyDirectory is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "keel-dev-signing");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(Path.GetFullPath(dir), $"dev-signing-{Guid.NewGuid():N}.key.pem");
        var fso = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fso.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(path, fso)) await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(keyPem), ct);
        var label = string.IsNullOrWhiteSpace(name) ? "DEV ONLY " + clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : name.Trim();
        var added = await AddCoreAsync(admin, new AddSigningIdentity(label, SigningKeySources.PemFile, bundle, path, null, "Development signature (not for production)", "Development host"), development: true, correlationId, ct);
        if (!added.IsOk) File.Delete(path);
        return added;
    }

    public async Task<Outcome<SigningIdentityView>> ActivateAsync(Subject admin, Guid id, string? correlationId, CancellationToken ct = default)
    {
        if (!IsAdmin(admin)) return Outcome<SigningIdentityView>.Forbidden(["signing.admin_only"]);
        var row = await LoadRowAsync(id, ct);
        if (row is null) return Outcome<SigningIdentityView>.NotFound();
        if (row.Value.Retired) return Outcome<SigningIdentityView>.Conflict("A retired identity cannot be activated again.");
        var now = clock.GetUtcNow();
        if (now < row.Value.View.NotBefore || now > row.Value.View.NotAfter) return Outcome<SigningIdentityView>.Invalid(["The certificate is not valid now."]);
        try
        {
            using var key = SigningKeyLoader.Load(new SigningKeyReference(row.Value.View.KeySource, row.Value.Pem, row.Value.View.KeyReference, row.Value.View.PasswordReference, row.Value.View.Thumbprint), options);
            SigningKeyLoader.ProveKeyMatches(key);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        {
            return Outcome<SigningIdentityView>.Invalid([$"The private key cannot be used: {Safe(e)}"]);
        }
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var off = new NpgsqlCommand("UPDATE rpt.signing_identity SET is_active = false WHERE is_active AND id <> @id", conn, tx)) { off.Parameters.AddWithValue("id", id); await off.ExecuteNonQueryAsync(ct); }
        await using (var on = new NpgsqlCommand("UPDATE rpt.signing_identity SET is_active = true WHERE id = @id", conn, tx)) { on.Parameters.AddWithValue("id", id); await on.ExecuteNonQueryAsync(ct); }
        await audit.AppendAsync(tx, Entry(admin, "output.signing.identity_activate", AuditOutcome.Success, row.Value.View.Thumbprint, correlationId, ("subject", row.Value.View.Subject)), ct);
        await tx.CommitAsync(ct);
        return Outcome<SigningIdentityView>.Ok(await GetViewAsync(id, ct));
    }

    public async Task<Outcome<SigningIdentityView>> RetireAsync(Subject admin, Guid id, string? correlationId, CancellationToken ct = default)
    {
        if (!IsAdmin(admin)) return Outcome<SigningIdentityView>.Forbidden(["signing.admin_only"]);
        var row = await LoadRowAsync(id, ct);
        if (row is null) return Outcome<SigningIdentityView>.NotFound();
        if (row.Value.Retired) return Outcome<SigningIdentityView>.Conflict("The identity is already retired.");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("UPDATE rpt.signing_identity SET is_active = false, retired_at = @at, retired_by = @by WHERE id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("by", admin.Username ?? admin.DisplayName);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(admin, "output.signing.identity_retire", AuditOutcome.Success, row.Value.View.Thumbprint, correlationId, ("wasActive", row.Value.View.IsActive ? "true" : "false")), ct);
        await tx.CommitAsync(ct);
        return Outcome<SigningIdentityView>.Ok(await GetViewAsync(id, ct));
    }

    public async Task<Outcome<SigningPolicyView>> SetRequiredAsync(Subject admin, bool required, string? correlationId, CancellationToken ct = default)
    {
        if (!IsAdmin(admin)) return Outcome<SigningPolicyView>.Forbidden(["signing.admin_only"]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("UPDATE rpt.signing_policy SET required = @r, updated_at = @at, updated_by = @by WHERE id = 1", conn, tx))
        {
            cmd.Parameters.AddWithValue("r", required);
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("by", admin.Username ?? admin.DisplayName);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(admin, "output.signing.policy_set", AuditOutcome.Success, "policy", correlationId, ("required", required ? "true" : "false")), ct);
        await tx.CommitAsync(ct);
        return Outcome<SigningPolicyView>.Ok(await GetPolicyAsync(ct));
    }

    // ------------------------------------------------------------------ signing

    /// <summary>
    /// The identity to sign the next package with, or null when signing is off. Refuses (never silently skips signing) when signing is required and no identity is active, or when
    /// an identity is active but its key cannot be used now: an operator who turned signing on must not receive unsigned packages.
    /// </summary>
    public async Task<Outcome<PreparedSigner?>> PrepareAsync(CancellationToken ct = default)
    {
        bool required; SigningIdentityView? active = null; string pem = "";
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        {
            await using (var cmd = new NpgsqlCommand("SELECT required FROM rpt.signing_policy WHERE id = 1", conn)) required = (bool)(await cmd.ExecuteScalarAsync(ct))!;
            await using var cmd2 = new NpgsqlCommand($"SELECT {IdentityColumns} FROM rpt.signing_identity WHERE is_active", conn);
            await using var r = await cmd2.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { active = Read(r); pem = r.GetString(3); }
        }
        if (active is null)
            return required ? Outcome<PreparedSigner?>.Unavailable("Release packages must be signed and no signing identity is active. Ask an administrator to activate one.") : Outcome<PreparedSigner?>.Ok(null);
        var now = clock.GetUtcNow();
        if (now < active.NotBefore || now > active.NotAfter)
            return Outcome<PreparedSigner?>.Unavailable("The active signing certificate is not valid now. Ask an administrator to activate a current one.");
        try
        {
            var key = SigningKeyLoader.Load(new SigningKeyReference(active.KeySource, pem, active.KeyReference, active.PasswordReference, active.Thumbprint), options);
            return Outcome<PreparedSigner?>.Ok(new PreparedSigner(active.Id, key, active.Reason, active.Location));
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        {
            return Outcome<PreparedSigner?>.Unavailable($"The signing key cannot be used: {Safe(e)} Ask an administrator.");
        }
    }

    /// <summary>Inside the artifact's transaction: records who signed, with which certificate and what digest, and audits the signing.</summary>
    internal async Task RecordAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid artifactId, string number, Subject subject, PreparedSigner signer, SignedPdf signed, DateTimeOffset signedAt, string? correlationId, CancellationToken ct)
    {
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO rpt.artifact_signature (artifact_id, identity_id, thumbprint, subject_dn, algorithm, signed_at, reason, location, signed_digest) VALUES (@a, @i, @t, @s, @alg, @at, @r, @l, @d)", conn, tx))
        {
            ins.Parameters.AddWithValue("a", artifactId);
            ins.Parameters.AddWithValue("i", signer.IdentityId);
            ins.Parameters.AddWithValue("t", SigningKeyLoader.Sha256(signer.Key.Certificate));
            ins.Parameters.AddWithValue("s", signer.Key.Certificate.Subject);
            ins.Parameters.AddWithValue("alg", signed.Algorithm);
            ins.Parameters.AddWithValue("at", signedAt);
            ins.Parameters.AddWithValue("r", signer.Reason);
            ins.Parameters.AddWithValue("l", signer.Location);
            ins.Parameters.AddWithValue("d", signed.SignedDigest);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "output.package.sign", AuditOutcome.Success, number, correlationId,
            ("thumbprint", SigningKeyLoader.Sha256(signer.Key.Certificate)), ("algorithm", signed.Algorithm), ("signedDigest", signed.SignedDigest), ("signedAt", signedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))), ct);
    }

    public async Task AuditSigningFailureAsync(Subject subject, string kind, string key, string? correlationId, string reason, CancellationToken ct)
        => await audit.AppendAsync(Entry(subject, "output.package.sign", AuditOutcome.Failure, key, correlationId, ("kind", kind), ("reason", reason)), ct);

    public async Task<(string? SignedBy, DateTimeOffset? SignedAt)> SignatureOfAsync(string number, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT s.subject_dn, s.signed_at FROM rpt.artifact_signature s JOIN rpt.artifact a ON a.id = s.artifact_id WHERE a.number = @n", conn);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetString(0), r.GetFieldValue<DateTimeOffset>(1)) : (null, null);
    }

    // ------------------------------------------------------------------ verification

    /// <summary>
    /// Checks an uploaded PDF. The cryptographic result needs no database; when the file's SHA-256 is that of a package Keel made, the package number is shown too, but only to
    /// someone who may read every object in it (the same rule as the download).
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(Subject subject, byte[] pdf, string? correlationId, CancellationToken ct = default)
    {
        var anchors = await TrustAnchorsAsync(ct);
        var report = PdfSignatureVerifier.Verify(pdf, anchors, clock.GetUtcNow());
        foreach (var a in anchors) a.Dispose();
        RecordedPackage? recorded = null;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand("SELECT number, subject_kind, subject_key, created_at, created_by, banner, objects::text FROM rpt.artifact WHERE sha256 = @h", conn))
        {
            cmd.Parameters.AddWithValue("h", report.FileSha256);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                var objects = JsonSerializer.Deserialize<List<ObjRef>>(r.GetString(6), Json) ?? [];
                var rec = new RecordedPackage(r.GetString(0), "release_package", r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4), r.GetString(5));
                var kind = r.GetString(1);
                await r.CloseAsync();
                if (await source.CanReadAllAsync(subject, [.. objects.Select(o => new PackageObjectRef(o.T, o.K))], ct)) recorded = rec with { Kind = kind };
            }
        }
        var first = report.Signatures.Count > 0 ? report.Signatures[0] : null;
        await audit.AppendAsync(Entry(subject, "output.package.verify", AuditOutcome.Success, recorded?.Number ?? report.FileSha256[..16], correlationId,
            ("verdict", report.Verdict), ("sha256", report.FileSha256), ("signatures", report.Signatures.Count.ToString(CultureInfo.InvariantCulture)), ("thumbprint", first?.Thumbprint)), ct);
        return new VerifyResult(report, recorded);
    }

    /// <summary>The root certificates of every identity the operator configured (including retired ones, so old packages still verify).</summary>
    private async Task<IReadOnlyList<X509Certificate2>> TrustAnchorsAsync(CancellationToken ct)
    {
        var pems = new List<string>();
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand("SELECT certificate_pem FROM rpt.signing_identity", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) pems.Add(r.GetString(0));
        var roots = new Dictionary<string, X509Certificate2>(StringComparer.Ordinal);
        foreach (var pem in pems)
            foreach (var c in SigningKeyLoader.ParseBundle(pem))
            {
                if (c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData)) roots.TryAdd(SigningKeyLoader.Sha256(c), c); else c.Dispose();
            }
        return [.. roots.Values];
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsAdmin(Subject s) => s.Roles.Contains(Roles.Admin);

    /// <summary>Failure texts that are safe to show: the exception type's message, but never a path or secret (loader messages are written without them).</summary>
    private static string Safe(Exception e) => e is CryptographicException or PlatformNotSupportedException ? e.Message.TrimEnd('.') + "." : e is FileNotFoundException or DirectoryNotFoundException ? "The key file was not found." : e is UnauthorizedAccessException ? "The key file cannot be read by the Keel service account." : "The key or its password could not be read.";

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? objectId, string? correlationId, params (string, string?)[] details) =>
        ReportsAudit.Entry(s, action, outcome, "output", objectId, correlationId, details);

    private static SigningIdentityView Read(NpgsqlDataReader r)
    {
        var pem = r.GetString(3);
        int count;
        try { count = SigningKeyLoader.ParseBundle(pem).Count; } catch (CryptographicException) { count = 0; }
        return new SigningIdentityView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(9), r.GetString(8), r.GetFieldValue<DateTimeOffset>(10), r.GetFieldValue<DateTimeOffset>(11), r.GetString(12),
            r.GetBoolean(13), r.GetBoolean(14), r.GetString(6), r.GetString(7), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), count, r.GetFieldValue<DateTimeOffset>(15), r.GetString(16),
            r.IsDBNull(17) ? null : r.GetFieldValue<DateTimeOffset>(17));
    }

    private async Task<SigningIdentityView> GetViewAsync(Guid id, CancellationToken ct) => (await LoadRowAsync(id, ct))!.Value.View;

    private async Task<(SigningIdentityView View, string Pem, bool Retired)?> LoadRowAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {IdentityColumns} FROM rpt.signing_identity WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var v = Read(r);
        return (v, r.GetString(3), v.RetiredAt is not null);
    }
}
