using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Records;
using Npgsql;

namespace Keel.UnitTests;

/// <summary>The pure parts of records management (ADR-0041): the retention rules, hold scopes, signatures and the archive package format.</summary>
public sealed class RecordsRulesTests
{
    private static Markings Open(string? program = null, ExportJurisdiction j = ExportJurisdiction.None, string? usml = null, IEnumerable<string>? cui = null, DistributionStatement d = DistributionStatement.A) =>
        Markings.Create(j, d, "ENG", usmlCategory: usml, program: program, cuiCategories: cui, controllingOffice: d == DistributionStatement.A ? null : "ACME", distributionReason: d == DistributionStatement.A ? null : "Proprietary").Value;

    private static ObjectFacts Facts(string kind = "document", string? type = "Specification", Markings? m = null, DateTimeOffset? released = null, DateTimeOffset? obsolete = null, bool open = false, DateOnly? deferred = null) =>
        new(kind, Guid.NewGuid(), "D-000001", "Name", type, m ?? Open(), released, obsolete, open, deferred);

    private static ClassRule Rule(string code = "R1", int priority = 100, string[]? kinds = null, string[]? types = null, string[]? programs = null, string[]? jurisdictions = null, string[]? cui = null,
        string[]? statements = null, int months = 12, string trigger = TriggerEvents.Release, string action = DispositionActions.Destroy) =>
        new(code, "Class " + code, priority, kinds ?? [], types ?? [], programs ?? [], jurisdictions ?? [], cui ?? [], statements ?? [], months, trigger, action);

    private static readonly Func<string, string, DateOnly?> NoEvents = (_, _) => null;
    private static readonly DateTimeOffset T0 = new(2020, 1, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_empty_criterion_matches_anything_and_each_set_criterion_must_hold()
    {
        Assert.True(RetentionRules.Matches(Rule(), Facts()));
        Assert.False(RetentionRules.Matches(Rule(kinds: ["part"]), Facts("document")));
        Assert.True(RetentionRules.Matches(Rule(types: ["specification"]), Facts(type: "Specification"))); // case-insensitive
        Assert.False(RetentionRules.Matches(Rule(types: ["Drawing"]), Facts(type: "Specification")));
        Assert.False(RetentionRules.Matches(Rule(types: ["Drawing"]), Facts(type: null)));
        Assert.True(RetentionRules.Matches(Rule(programs: ["ORCA", "KRAKEN"]), Facts(m: Open("ORCA"))));
        Assert.False(RetentionRules.Matches(Rule(programs: ["ORCA"]), Facts(m: Open())));
        Assert.True(RetentionRules.Matches(Rule(jurisdictions: ["Itar"]), Facts(m: Open(j: ExportJurisdiction.Itar, usml: "VIII", cui: ["EXPT"], d: DistributionStatement.C))));
        Assert.False(RetentionRules.Matches(Rule(jurisdictions: ["Itar"]), Facts()));
        Assert.True(RetentionRules.Matches(Rule(cui: ["CTI", "PROPIN"]), Facts(m: Open(cui: ["CTI"], d: DistributionStatement.C))));
        Assert.False(RetentionRules.Matches(Rule(cui: ["PROPIN"]), Facts(m: Open(cui: ["CTI"], d: DistributionStatement.C))));
        Assert.True(RetentionRules.Matches(Rule(statements: ["C", "D"]), Facts(m: Open(d: DistributionStatement.C))));
        Assert.False(RetentionRules.Matches(Rule(statements: ["B"]), Facts()));
    }

    [Fact]
    public void The_retention_period_runs_from_the_trigger_and_the_first_matching_class_by_priority_wins()
    {
        var rules = new[] { Rule("GENERIC", priority: 500, months: 120), Rule("SPECS", priority: 100, types: ["Specification"], months: 36) };
        var a = RetentionRules.Assign(rules, Facts(released: T0), NoEvents)!;
        Assert.Equal("SPECS", a.Code);
        Assert.Equal(new DateOnly(2020, 1, 31), a.TriggerDate);
        Assert.Equal(new DateOnly(2023, 1, 31), a.EligibleOn);
        Assert.Equal("GENERIC", RetentionRules.Assign(rules, Facts(type: "Drawing", released: T0), NoEvents)!.Code);
        // Equal priority: the code decides, so the result never depends on storage order.
        Assert.Equal("A1", RetentionRules.Assign([Rule("B1"), Rule("A1")], Facts(released: T0), NoEvents)!.Code);
    }

    [Fact]
    public void An_object_is_not_due_without_a_trigger_or_while_work_is_open()
    {
        var rules = new[] { Rule(trigger: TriggerEvents.Release) };
        Assert.Null(RetentionRules.Assign(rules, Facts(), NoEvents)); // never released
        Assert.Null(RetentionRules.Assign(rules, Facts(released: T0, open: true), NoEvents)); // a newer revision is in work
        Assert.Null(RetentionRules.Assign([Rule(trigger: TriggerEvents.Obsolescence)], Facts(released: T0), NoEvents));
        Assert.NotNull(RetentionRules.Assign([Rule(trigger: TriggerEvents.Obsolescence)], Facts(obsolete: T0), NoEvents));
        // A matching class decides even when it is not due: a lower-priority class does not take over.
        Assert.Null(RetentionRules.Assign([Rule("FIRST", 1, trigger: TriggerEvents.Obsolescence), Rule("SECOND", 2)], Facts(released: T0), NoEvents));
    }

    [Fact]
    public void Program_events_start_the_clock_only_for_objects_of_that_program()
    {
        DateOnly? Events(string program, string ev) => program == "ORCA" && ev == TriggerEvents.ProgramClose ? new DateOnly(2022, 6, 30) : null;
        var rules = new[] { Rule(programs: ["ORCA", "KRAKEN"], months: 24, trigger: TriggerEvents.ProgramClose) };
        Assert.Equal(new DateOnly(2024, 6, 30), RetentionRules.Assign(rules, Facts(m: Open("ORCA")), Events)!.EligibleOn);
        Assert.Null(RetentionRules.Assign(rules, Facts(m: Open("KRAKEN")), Events)); // not closed
        Assert.Null(RetentionRules.Assign([Rule(months: 24, trigger: TriggerEvents.ContractEnd)], Facts(m: Open("ORCA")), Events)); // no contract end recorded
        Assert.Null(RetentionRules.Assign([Rule(trigger: TriggerEvents.ProgramClose)], Facts(m: Open()), Events)); // no program at all
    }

    [Fact]
    public void A_review_deferral_can_only_extend_the_due_date()
    {
        var rules = new[] { Rule(months: 12) };
        Assert.Equal(new DateOnly(2021, 1, 31), RetentionRules.Assign(rules, Facts(released: T0, deferred: new DateOnly(2020, 3, 1)), NoEvents)!.EligibleOn);
        Assert.Equal(new DateOnly(2030, 1, 1), RetentionRules.Assign(rules, Facts(released: T0, deferred: new DateOnly(2030, 1, 1)), NoEvents)!.EligibleOn);
    }

    [Fact]
    public void A_class_is_validated_before_it_can_be_saved()
    {
        ClassInput Ok() => new("SPEC-1", "Specifications", null, 100, ["document"], [], [], [], [], [], 60, TriggerEvents.Release, DispositionActions.Destroy, "FAR 4.703", null);
        Assert.Empty(RetentionRules.Validate(Ok()));
        Assert.NotEmpty(RetentionRules.Validate(Ok() with { Code = "x" }));
        Assert.NotEmpty(RetentionRules.Validate(Ok() with { Name = "" }));
        Assert.NotEmpty(RetentionRules.Validate(Ok() with { RetentionMonths = -1 }));
        Assert.NotEmpty(RetentionRules.Validate(Ok() with { RetentionMonths = 2000 }));
        Assert.NotEmpty(RetentionRules.Validate(Ok() with { TriggerEvent = "whenever" }));
        Assert.NotEmpty(RetentionRules.Validate(Ok() with { Disposition = "shred" }));
        Assert.NotEmpty(RetentionRules.Validate(Ok() with { Kinds = ["supplier"] }));
        Assert.NotEmpty(RetentionRules.Validate(Ok() with { Jurisdictions = ["Mars"] }));
        Assert.NotEmpty(RetentionRules.Validate(Ok() with { Statements = ["Z"] }));
        Assert.NotEmpty(RetentionRules.Validate(Ok() with { TriggerEvent = TriggerEvents.ProgramClose })); // needs a program to record the event against
        Assert.Empty(RetentionRules.Validate(Ok() with { TriggerEvent = TriggerEvents.ProgramClose, Programs = ["ORCA"] }));
    }

    [Fact]
    public void Hold_scopes_are_validated()
    {
        Assert.Empty(HoldService.ValidateScope(new HoldScope(HoldScopeKinds.Objects, [new HoldObjectRef("document", "D-000001")])));
        Assert.NotEmpty(HoldService.ValidateScope(new HoldScope(HoldScopeKinds.Objects, [])));
        Assert.NotEmpty(HoldService.ValidateScope(new HoldScope(HoldScopeKinds.Objects, [new HoldObjectRef("supplier", "SUP-1")])));
        Assert.NotEmpty(HoldService.ValidateScope(new HoldScope(HoldScopeKinds.Objects, [.. Enumerable.Range(0, 501).Select(i => new HoldObjectRef("part", $"P-{i:D6}"))])));
        Assert.Empty(HoldService.ValidateScope(new HoldScope(HoldScopeKinds.Program, Program: "ORCA")));
        Assert.NotEmpty(HoldService.ValidateScope(new HoldScope(HoldScopeKinds.Program, Program: " ")));
        Assert.NotEmpty(HoldService.ValidateScope(new HoldScope(HoldScopeKinds.Query, Query: new HoldQuery())));
        Assert.NotEmpty(HoldService.ValidateScope(new HoldScope(HoldScopeKinds.Query, Query: new HoldQuery(Jurisdiction: "Mars"))));
        Assert.Empty(HoldService.ValidateScope(new HoldScope(HoldScopeKinds.Query, Query: new HoldQuery(NumberPrefix: "D-0001"))));
        Assert.NotEmpty(HoldService.ValidateScope(new HoldScope("everything")));
    }

    [Fact]
    public void Options_are_validated()
    {
        Assert.Empty(new RecordsOptions().Validate());
        Assert.NotEmpty(new RecordsOptions { ObjectLock = "Sometimes" }.Validate());
        Assert.NotEmpty(new RecordsOptions { ObjectLockMode = "legal" }.Validate());
        Assert.NotEmpty(new RecordsOptions { ObjectLockDays = 0 }.Validate());
        Assert.NotEmpty(new RecordsOptions { BackgroundMinutes = -1 }.Validate());
    }

    [Fact]
    public void Records_roles_are_separate_and_only_the_records_manager_may_execute()
    {
        Subject S(params string[] roles) => new() { Id = "s", DisplayName = "S", Roles = [.. roles] };
        Assert.True(RecordsAccess.CanManage(S(Roles.RecordsManager)));
        Assert.True(RecordsAccess.CanManage(S(Roles.Admin)));
        Assert.False(RecordsAccess.CanManage(S(Roles.Author, Roles.Approver, Roles.Viewer)));
        Assert.True(RecordsAccess.CanView(S(Roles.Auditor)));
        Assert.False(RecordsAccess.CanManage(S(Roles.Auditor))); // read only
        Assert.True(RecordsAccess.CanExecute(S(Roles.RecordsManager)));
        Assert.False(RecordsAccess.CanExecute(S(Roles.Admin)));
        Assert.DoesNotContain(Roles.RecordsManager, Roles.All);
    }

    // ---------------------------------------------------------------- signatures

    private static (CheckpointSigner Signer, ECDsa Key, TrustedCheckpointKeys Trusted) Keys()
    {
        var key = CheckpointKeys.Generate();
        var signer = new CheckpointSigner(key, NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x"), TimeProvider.System);
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);
        return (signer, key, new TrustedCheckpointKeys([pub]));
    }

    [Fact]
    public void A_signature_is_bound_to_its_purpose_and_to_the_key()
    {
        var (signer, _, trusted) = Keys();
        var rs = new RecordsSigner(signer, trusted);
        var hash = RecordsSigner.Sha256Hex("body"u8);
        var sig = rs.Sign(RecordsSigner.CertificateDomain, hash);
        Assert.True(rs.Verify(RecordsSigner.CertificateDomain, hash, rs.KeyId, sig, out var known));
        Assert.True(known);
        Assert.False(rs.Verify(RecordsSigner.ArchiveDomain, hash, rs.KeyId, sig, out _)); // a certificate signature is not an archive signature
        Assert.False(rs.Verify(RecordsSigner.CertificateDomain, RecordsSigner.Sha256Hex("other"u8), rs.KeyId, sig, out _));
        Assert.False(rs.Verify(RecordsSigner.CertificateDomain, hash, rs.KeyId, [1, 2, 3], out _));
        Assert.False(rs.Verify(RecordsSigner.CertificateDomain, hash, "0000000000000000", sig, out var unknown));
        Assert.False(unknown);
        // The audit checkpoint message is a different domain again: a checkpoint signature cannot be replayed as a certificate.
        var cp = CheckpointKeys.Message(1, new byte[32], DateTimeOffset.UnixEpoch);
        Assert.False(rs.Verify(RecordsSigner.CertificateDomain, hash, rs.KeyId, signer.SignMessage(cp), out _));
    }

    [Fact]
    public void Without_a_signing_key_the_signer_reports_unavailable()
    {
        var rs = new RecordsSigner(null, new TrustedCheckpointKeys([]));
        Assert.False(rs.Available);
        Assert.Throws<InvalidOperationException>(() => rs.Sign(RecordsSigner.CertificateDomain, "00"));
    }

    // ---------------------------------------------------------------- archive packages

    private static byte[] BuildPackage(RecordsSigner signer, Action<List<ArchiveEntryInfo>, ArchiveWriter>? tamper = null, bool withEvents = true, bool signCheckpoint = false, CheckpointSigner? cpSigner = null, bool corruptEventHash = false)
    {
        using var writer = new ArchiveWriter(DateTimeOffset.UnixEpoch);
        var obj = new ArchivedObject("document", "D-000001", "Spec", "Specification", "UNCLASSIFIED",
            [new ArchivedRevision("A", "Released", T0, [new ArchivedIteration(1, "first", T0, "alice", [new ArchivedFile("a.txt", "text/plain", 5, RecordsSigner.Sha256Hex("hello"u8))])])]);
        writer.AddBytes(ArchiveFormat.ObjectPath("document", "D-000001"), JsonSerializer.SerializeToUtf8Bytes(obj, ArchiveFormat.Json));
        writer.AddBytes(ArchiveFormat.SignaturesPath, JsonSerializer.SerializeToUtf8Bytes(new List<ArchivedSignature>(), ArchiveFormat.Json));
        var events = new List<AuditExtractEvent>();
        if (withEvents)
        {
            var prev = new byte[32];
            for (long seq = 1; seq <= 3; seq++)
            {
                var payload = $"{{\"v\":1,\"n\":{seq}}}";
                var hash = AuditChain.ComputeHash(prev, seq, payload);
                events.Add(new AuditExtractEvent(seq, Guid.NewGuid().ToString("D"), "2020-01-31T00:00:00.000000Z", "alice", "document.create", "document", "D-000001", "Success", null, payload, Convert.ToHexStringLower(prev), Convert.ToHexStringLower(hash)));
                prev = hash;
            }
        }
        if (corruptEventHash && events.Count > 1) events[1] = events[1] with { Hash = new string('1', 64) };
        AuditExtractCheckpoint? cp = null;
        if (signCheckpoint && events.Count > 0 && cpSigner is not null)
        {
            var last = events[^1];
            var at = DateTimeOffset.Parse("2020-02-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            cp = new AuditExtractCheckpoint(last.Seq, last.Hash, AuditChain.FormatTimestamp(at), cpSigner.KeyId, Convert.ToBase64String(cpSigner.SignMessage(CheckpointKeys.Message(last.Seq, Convert.FromHexString(last.Hash), at))));
        }
        writer.AddBytes(ArchiveFormat.AuditPath, JsonSerializer.SerializeToUtf8Bytes(new AuditExtract("keel-audit-extract/1", events, cp), ArchiveFormat.Json));
        writer.AddBytes(ArchiveFormat.FilePath(RecordsSigner.Sha256Hex("hello"u8)), "hello"u8.ToArray());
        tamper?.Invoke(writer.Entries, writer);
        var manifest = new ArchiveManifest(ArchiveFormat.Format, "AR-000001", "Test", "2020-01-31T00:00:00.000000Z", "alice", signer.KeyId, "UNCLASSIFIED", [new ManifestObject("document", "D-000001", 1)], [.. writer.Entries.OrderBy(e => e.Path, StringComparer.Ordinal)]);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ArchiveFormat.Json);
        var sha = RecordsSigner.Sha256Hex(manifestBytes);
        writer.AddBytes(ArchiveFormat.ManifestPath, manifestBytes, record: false);
        writer.AddBytes(ArchiveFormat.SignaturePath, JsonSerializer.SerializeToUtf8Bytes(new ArchiveSignatureFile("keel-archive-signature/1", sha, signer.KeyId, "ECDSA-P256-SHA256", Convert.ToBase64String(signer.Sign(RecordsSigner.ArchiveDomain, sha))), ArchiveFormat.Json), record: false);
        using var file = writer.Finish();
        using var ms = new MemoryStream();
        file.CopyTo(ms);
        return ms.ToArray();
    }

    private static ArchiveRead Read(byte[] zip, TrustedCheckpointKeys trusted)
    {
        using var ms = new MemoryStream(zip);
        return ArchiveReader.Read(ms, trusted.Keys);
    }

    [Fact]
    public void A_sealed_archive_package_verifies_and_reads_back()
    {
        var (signer, _, trusted) = Keys();
        var read = Read(BuildPackage(new RecordsSigner(signer, trusted)), trusted);
        Assert.True(read.Verification.Valid, string.Join("; ", read.Verification.Problems));
        Assert.True(read.Verification.ManifestSigned);
        Assert.Equal(3, read.Verification.Events);
        Assert.Equal("AR-000001", read.Number);
        Assert.Equal("D-000001", read.Objects.Single().Number);
        Assert.NotNull(read.Verification.Note); // no checkpoint in this package: said plainly, not hidden
    }

    [Fact]
    public void A_checkpoint_in_the_package_is_verified_against_the_trusted_keys()
    {
        var (signer, _, trusted) = Keys();
        var rs = new RecordsSigner(signer, trusted);
        var good = Read(BuildPackage(rs, signCheckpoint: true, cpSigner: signer), trusted);
        Assert.True(good.Verification.Valid, string.Join("; ", good.Verification.Problems));
        Assert.True(good.Verification.CheckpointVerified);
        var (_, _, other) = Keys();
        var untrusted = Read(BuildPackage(rs, signCheckpoint: true, cpSigner: signer), other);
        Assert.False(untrusted.Verification.Valid);
    }

    [Fact]
    public void Any_change_to_a_package_is_detected()
    {
        var (signer, _, trusted) = Keys();
        var rs = new RecordsSigner(signer, trusted);
        var zip = BuildPackage(rs);

        // 1. a stored file altered in place (same name, new bytes) -- rewrite the ZIP with one entry changed
        byte[] Rewrite(Func<string, byte[], byte[]?> change, Action<System.IO.Compression.ZipArchive>? add = null)
        {
            using var input = new MemoryStream(zip);
            using var output = new MemoryStream();
            using (var src = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Read))
            using (var dst = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var e in src.Entries)
                {
                    using var s = e.Open();
                    using var buf = new MemoryStream();
                    s.CopyTo(buf);
                    var data = change(e.FullName, buf.ToArray());
                    if (data is null) continue;
                    using var w = dst.CreateEntry(e.FullName).Open();
                    w.Write(data);
                }
                add?.Invoke(dst);
            }
            return output.ToArray();
        }

        var file = ArchiveFormat.FilePath(RecordsSigner.Sha256Hex("hello"u8));
        var changedFile = Read(Rewrite((n, d) => n == file ? "HELLO"u8.ToArray() : d), trusted);
        Assert.False(changedFile.Verification.Valid);
        Assert.Contains(changedFile.Verification.Problems, p => p.Contains("does not match the manifest", StringComparison.Ordinal));

        var removed = Read(Rewrite((n, d) => n == file ? null : d), trusted);
        Assert.Contains(removed.Verification.Problems, p => p.Contains("listed but missing", StringComparison.Ordinal));

        var extra = Read(Rewrite((_, d) => d, z => { using var w = z.CreateEntry("files/extra").Open(); w.Write("x"u8); }), trusted);
        Assert.Contains(extra.Verification.Problems, p => p.Contains("not in the manifest", StringComparison.Ordinal));

        // 2. the manifest itself rewritten (a different title): the signature no longer matches the hash that was signed
        var manifestChanged = Read(Rewrite((n, d) => n == ArchiveFormat.ManifestPath ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(d).Replace("\"Test\"", "\"Other\"", StringComparison.Ordinal)) : d), trusted);
        Assert.False(manifestChanged.Verification.Valid);
        Assert.False(manifestChanged.Verification.ManifestSigned);

        // 3. an audit event altered: its stored hash no longer matches its payload (and the entry no longer matches the manifest)
        var auditChanged = Read(Rewrite((n, d) => n == ArchiveFormat.AuditPath ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(d).Replace("\\u0022n\\u0022:2", "\\u0022n\\u0022:9", StringComparison.Ordinal)) : d), trusted);
        Assert.False(auditChanged.Verification.Valid);

        // 4. not a ZIP at all, and an unsigned package
        Assert.False(Read("not a zip"u8.ToArray(), trusted).Verification.Valid);
        Assert.False(Read(Rewrite((n, d) => n == ArchiveFormat.SignaturePath ? null : d), trusted).Verification.Valid);
    }

    [Fact]
    public void An_audit_event_whose_hash_does_not_recompute_is_reported_even_when_the_package_is_otherwise_consistent()
    {
        var (signer, _, trusted) = Keys();
        var read = Read(BuildPackage(new RecordsSigner(signer, trusted), corruptEventHash: true), trusted);
        Assert.False(read.Verification.Valid);
        Assert.True(read.Verification.ManifestSigned); // the manifest is fine: only the chain check can catch this
        Assert.Contains(read.Verification.Problems, p => p.Contains("Audit event 2 does not match its hash", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_can_be_extracted_only_if_it_matches_its_name()
    {
        var (signer, _, trusted) = Keys();
        var zip = BuildPackage(new RecordsSigner(signer, trusted));
        using var ok = new MemoryStream();
        Assert.True(ArchiveReader.TryExtractFile(new MemoryStream(zip), RecordsSigner.Sha256Hex("hello"u8), ok));
        Assert.Equal("hello", Encoding.UTF8.GetString(ok.ToArray()));
        Assert.False(ArchiveReader.TryExtractFile(new MemoryStream(zip), new string('a', 64), new MemoryStream()));
    }

    [Fact]
    public void Entry_names_that_could_escape_a_folder_are_refused()
    {
        var (signer, _, trusted) = Keys();
        var zip = BuildPackage(new RecordsSigner(signer, trusted));
        using var input = new MemoryStream(zip);
        using var output = new MemoryStream();
        using (var src = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Read))
        using (var dst = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in src.Entries)
            {
                using var s = e.Open();
                using var w = dst.CreateEntry(e.FullName).Open();
                s.CopyTo(w);
            }
            using var evil = dst.CreateEntry("../evil").Open();
            evil.Write("x"u8);
        }
        var read = Read(output.ToArray(), trusted);
        Assert.False(read.Verification.Valid);
        Assert.Contains(read.Verification.Problems, p => p.Contains("unsafe name", StringComparison.Ordinal));
    }

    [Fact]
    public void The_text_of_a_hold_scope_names_no_member()
    {
        var text = HoldService.ScopeText(JsonSerializer.Serialize(new HoldScope(HoldScopeKinds.Objects, [new HoldObjectRef("document", "D-SECRET-1")]), ArchiveFormat.Json));
        Assert.DoesNotContain("D-SECRET-1", text, StringComparison.Ordinal);
        Assert.Contains("1 named object", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Object_lists_typed_by_people_are_parsed_strictly()
    {
        var (ok, errors) = Keel.Web.Components.Pages.Records.RecordsUi.ParseObjects("D-000001\np-000002, document D-000003;part P-000004");
        Assert.Empty(errors);
        Assert.Equal([("document", "D-000001"), ("part", "P-000002"), ("document", "D-000003"), ("part", "P-000004")], ok.Select(o => (o.Kind, o.Number)));
        var (_, bad) = Keel.Web.Components.Pages.Records.RecordsUi.ParseObjects("widget 12");
        Assert.Single(bad);
    }
}
