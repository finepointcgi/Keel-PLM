using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;
using NpgsqlTypes;

namespace Keel.Integration;

/// <summary>
/// Builds Technical Data Packages aligned with MIL-STD-31000 for a part revision or an established baseline (ADR-0025). A package is complete or
/// it is refused: everything it needs must be readable and downloadable by the exporter and releasable to the declared recipient profile, and
/// a refusal reports counts only. The archive is streamed (no whole-package buffering); the immutable record keeps the signed manifest and its
/// hash, never the bytes, so a package can be verified later.
/// </summary>
public sealed class TdpService(
    NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IntegrationOptions options, TdpSigner signer, IntegrationAdminService admin,
    ITdpPartSource parts, ITdpStructureSource structures, ITdpDocumentSource documents, IEnumerable<ILinkedDocumentProvider> linkedProviders)
{
    public const string Format = "keel-tdp/1";

    /// <summary>False until a TDP signing key is configured (Keel:Integration:TdpSigningKeyPath); packages are never exported unsigned.</summary>
    public bool CanExport => signer.CanSign;
    private readonly ILinkedDocumentProvider? _linked = linkedProviders.FirstOrDefault(p => p.ObjectType == "part");

    // ---------------------------------------------------------------- plan (preview and the basis of the export)

    public async Task<Outcome<TdpPlan>> PlanAsync(Subject subject, TdpRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Download))
        {
            await audit.AppendAsync(IntegrationAudit.Entry(subject, "integration.tdp.plan", AuditOutcome.Denied, request.Number, correlationId, ("reasons", AccessPolicy.RoleMissing)), ct);
            return Outcome<TdpPlan>.Forbidden([AccessPolicy.RoleMissing]);
        }
        if (request.ObjectType is not (TdpObjectTypes.Part or TdpObjectTypes.Baseline) || string.IsNullOrWhiteSpace(request.Number))
            return Outcome<TdpPlan>.Invalid(["Name a part or a baseline."]);
        var profile = (await admin.LoadProfilesAsync(ct)).FirstOrDefault(p => p.Name == request.RecipientProfile && p.Active);
        if (profile is null) return Outcome<TdpPlan>.Invalid([$"Recipient profile '{request.RecipientProfile}' does not exist or is not active."]);
        var clearance = profile.ToProfile();
        var rules = await TdpCategories.LoadAsync(dataSource, ct);

        var refusals = new Dictionary<string, (int Count, string Message)>(StringComparer.Ordinal);
        void Refuse(string reason, string message, int count = 1) =>
            refusals[reason] = refusals.TryGetValue(reason, out var e) ? (e.Count + count, e.Message) : (count, message);
        var allMarkings = new List<Markings>();
        void Account(Markings m)
        {
            allMarkings.Add(m);
            var decision = OutboundRelease.Evaluate(clearance, m);
            foreach (var reason in decision.Reasons) Refuse(reason, OutboundRelease.Describe(reason));
        }

        // Root object, structure and documents to include.
        TdpStructure structure;
        TdpPart root;
        var documentRefs = new List<TdpDocumentRef>();
        var unreadableDocuments = 0;
        Markings rootMarkings;
        string rootNumber, rootRevision;
        if (request.ObjectType == TdpObjectTypes.Part)
        {
            var p = await parts.GetAsync(subject, request.Number.Trim(), request.Revision, correlationId, ct);
            if (!p.IsOk) return Pass<TdpPart, TdpPlan>(p);
            root = p.Value!;
            var s = await structures.GetPartStructureAsync(subject, root.Number, root.Revision, correlationId, ct, request.Variant);
            if (!s.IsOk) return Pass<TdpStructure, TdpPlan>(s);
            structure = s.Value!;
            (rootMarkings, rootNumber, rootRevision) = (root.Markings, root.Number, root.Revision);
        }
        else
        {
            var b = await structures.GetBaselineAsync(subject, request.Number.Trim(), correlationId, ct);
            if (!b.IsOk) return Pass<TdpBaseline, TdpPlan>(b);
            var s = await structures.GetBaselineStructureAsync(subject, request.Number.Trim(), correlationId, ct);
            if (!s.IsOk) return Pass<TdpStructure, TdpPlan>(s);
            structure = s.Value!;
            var rp = await parts.GetAsync(subject, b.Value!.RootNumber, b.Value.RootRevision, correlationId, ct);
            if (!rp.IsOk) return Pass<TdpPart, TdpPlan>(rp);
            root = rp.Value!;
            (rootMarkings, rootNumber, rootRevision) = (b.Value.Markings, root.Number, root.Revision);
            if (b.Value.State != "Established" && b.Value.State != "Superseded")
                Refuse("baseline.not_established", $"Baseline {b.Value.Number} is {b.Value.State}; only an established baseline can be exported.");
            documentRefs.AddRange(b.Value.Documents);
            if (b.Value.RedactedDocuments > 0) { unreadableDocuments += b.Value.RedactedDocuments; }
        }
        Account(rootMarkings);
        if (request.ObjectType == TdpObjectTypes.Baseline) Account(root.Markings);

        if (structure.RedactedCount > 0) Refuse("unreadable.structure_lines", "Lines of the structure are not readable by the exporter; a package must be complete.", structure.RedactedCount);
        if (structure.UnresolvedCount > 0) Refuse("structure.unresolved_lines", "Lines of the structure have no released revision; a package must be complete.", structure.UnresolvedCount);
        foreach (var line in structure.Lines.Where(l => !l.Unresolved)) Account(line.Markings);

        var partRefs = new List<(string Number, string Revision)> { (root.Number, root.Revision) };
        if (request.ObjectType == TdpObjectTypes.Part)
            partRefs.AddRange(structure.Lines.Where(l => l.Revision is not null).Select(l => (l.Number, l.Revision!)).Distinct());
        if (partRefs.Count > options.TdpMaxParts)
            Refuse("tdp.too_many_parts", $"The package would contain {partRefs.Count} parts; the limit is {options.TdpMaxParts}. Split the export by sub-assembly.", partRefs.Count);

        if (request.ObjectType == TdpObjectTypes.Part && partRefs.Count <= options.TdpMaxParts)
        {
            if (_linked is null) throw new InvalidOperationException("No linked document provider is registered.");
            foreach (var (number, revision) in partRefs)
            {
                var links = await _linked.GetLinkedAsync(subject, number, revision, null, correlationId, ct);
                if (!links.IsOk) { unreadableDocuments++; continue; }
                foreach (var l in links.Value!)
                {
                    if (l.Redacted || l.Number is null || l.Revision is null) unreadableDocuments++;
                    else documentRefs.Add(new TdpDocumentRef(l.Number, l.Revision, l.LinkType, number));
                }
            }
        }

        var elements = new List<TdpElement>();
        var unmapped = 0;
        var documentCount = 0;
        var distinct = documentRefs.GroupBy(d => (d.Number, d.Revision)).ToList();
        if (distinct.Count > options.TdpMaxDocuments)
            Refuse("tdp.too_many_documents", $"The package would contain {distinct.Count} documents; the limit is {options.TdpMaxDocuments}.", distinct.Count);
        else
        {
            foreach (var g in distinct)
            {
                var d = await documents.GetAsync(subject, g.Key.Number, g.Key.Revision, correlationId, ct);
                if (!d.IsOk) { unreadableDocuments++; continue; }
                var doc = d.Value!;
                if (!AccessPolicy.Evaluate(subject, doc.Markings, PlmAction.Download).Permitted)
                {
                    Refuse("download.denied", "Documents the exporter may read but not download.");
                    continue;
                }
                Account(doc.Markings);
                var linkTypes = g.Select(x => x.LinkType).Distinct().OrderBy(x => x == "describes" ? 0 : 1).ToList();
                var category = linkTypes.Select(lt => TdpCategories.Resolve(rules, doc.DocumentType, lt)).FirstOrDefault(c => c is not null);
                var mapped = category is not null;
                if (!mapped) unmapped++;
                documentCount++;
                foreach (var f in doc.Files)
                    elements.Add(new TdpElement("document", category ?? TdpCategories.Other, $"documents/{category ?? TdpCategories.Other}/{doc.Number}-{doc.Revision}/{SafeName(f.FileName)}",
                        doc.Number, doc.Revision, doc.Name, f.FileId, f.FileName, f.SizeBytes, f.Sha256, doc.State, mapped));
                if (doc.Files.Count == 0)
                    elements.Add(new TdpElement("document-record", category ?? TdpCategories.Other, "", doc.Number, doc.Revision, doc.Name, null, null, null, null, doc.State, mapped));
            }
        }
        if (unreadableDocuments > 0) Refuse("unreadable.documents", "Linked documents are not readable by the exporter; a package must be complete.", unreadableDocuments);

        Markings? overall = null;
        if (allMarkings.Count > 0)
        {
            var cover = MarkingDominance.MinimalCover(allMarkings[0], allMarkings.Skip(1));
            if (cover.IsSuccess) overall = cover.Value;
            else Refuse("markings.cannot_combine", "The objects belong to different programs and cannot be marked as one package.");
        }

        var fixedElements = new List<TdpElement>
        {
            new("cover-sheet", TdpCategories.Other, "cover-sheet.txt", null, null, null, null, null, null, null, null, true),
            new("part-data", "ASSOCIATED_LISTS", "part.json", null, null, null, null, null, null, null, null, true),
            new("bom", "ASSOCIATED_LISTS", $"bom/{SafeName(rootNumber)}_{SafeName(rootRevision)}_bom.csv", null, null, null, null, null, null, null, null, true),
            new("bom", "ASSOCIATED_LISTS", $"bom/{SafeName(rootNumber)}_{SafeName(rootRevision)}_bom.json", null, null, null, null, null, null, null, null, true),
        };
        var versions = partRefs.Select(p => $"{p.Number} {p.Revision}").ToList();
        if (request.ObjectType == TdpObjectTypes.Baseline) versions.Insert(0, $"baseline {request.Number.Trim()}");
        var plan = new TdpPlan(request with { Number = request.Number.Trim() }, profile.Name, profile.RecipientCategory, rootNumber, rootRevision,
            overall?.BannerText() ?? "UNCLASSIFIED", overall, [.. fixedElements, .. elements.OrderBy(e => e.Category, StringComparer.Ordinal).ThenBy(e => e.Path, StringComparer.Ordinal)],
            [.. refusals.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new TdpRefusal(r.Key, r.Value.Count, r.Value.Message))],
            partRefs.Count, documentCount, structure.Lines.Count, unmapped, versions);
        return Outcome<TdpPlan>.Ok(plan);
    }

    private static Outcome<TOut> Pass<TIn, TOut>(Outcome<TIn> o) => new(o.Kind, default, o.Errors);

    private static string SafeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        var s = sb.ToString().TrimStart('.');
        return s.Length == 0 ? "file" : s.Length > 120 ? s[..120] : s;
    }

    // ---------------------------------------------------------------- export

    /// <summary>
    /// Plans, then (when nothing is refused) returns the streaming download. A refusal is audited with counts only. The returned function writes the
    /// archive; it can be run once.
    /// </summary>
    public async Task<Outcome<TdpDownload>> PrepareAsync(Subject subject, TdpRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (!signer.CanSign) return Outcome<TdpDownload>.Invalid(["TDP export is not configured: no signing key (Keel:Integration:TdpSigningKeyPath)."]);
        var planned = await PlanAsync(subject, request, correlationId, ct);
        if (!planned.IsOk) return Pass<TdpPlan, TdpDownload>(planned);
        var plan = planned.Value!;
        if (!plan.Permitted)
        {
            await audit.AppendAsync(IntegrationAudit.Entry(subject, "integration.tdp.export", AuditOutcome.Denied, request.Number, correlationId,
                ("recipient", plan.RecipientProfile), ("objectType", request.ObjectType),
                ("refusals", string.Join(";", plan.Refusals.Select(r => $"{r.Reason}={r.Count.ToString(CultureInfo.InvariantCulture)}")))), ct);
            return Outcome<TdpDownload>.Invalid([$"The package cannot be exported ({string.Join("; ", plan.Refusals.Select(r => $"{r.Message} [{r.Count.ToString(CultureInfo.InvariantCulture)}]"))})"]);
        }

        var id = Guid.CreateVersion7();
        var fileName = $"TDP-{SafeName(plan.RootNumber)}-{SafeName(plan.RootRevision)}{(request.ObjectType == TdpObjectTypes.Baseline ? "-" + SafeName(request.Number) : "")}.zip";
        return Outcome<TdpDownload>.Ok(new TdpDownload(id, fileName, "application/zip", plan.Banner, (stream, token) => WriteAsync(subject, plan, id, fileName, correlationId, stream, token)));
    }

    private async Task<TdpExportRecord> WriteAsync(Subject subject, TdpPlan plan, Guid exportId, string fileName, string? correlationId, Stream destination, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var folder = Path.GetFileNameWithoutExtension(fileName);
        var written = new List<JsonObject>();
        var overall = plan.OverallMarkings!;
        var root = await parts.GetAsync(subject, plan.RootNumber, plan.RootRevision, correlationId, ct);
        var structure = plan.Request.ObjectType == TdpObjectTypes.Part
            ? await structures.GetPartStructureAsync(subject, plan.RootNumber, plan.RootRevision, correlationId, ct, plan.Request.Variant)
            : await structures.GetBaselineStructureAsync(subject, plan.Request.Number, correlationId, ct);
        if (!root.IsOk || !structure.IsOk || structure.Value!.RedactedCount > 0 || structure.Value.Lines.Count != plan.StructureLines)
            throw new InvalidOperationException("The source data changed while the package was being prepared; export again.");

        var output = new SyncSafeStream(destination);
        await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8, ct);

        async Task AddAsync(TdpElement e, string path, Func<Stream, Task> write, CompressionLevel level)
        {
            var entry = zip.CreateEntry($"{folder}/{path}", level);
            entry.LastWriteTime = now;
            string sha;
            long size;
            await using (var es = await entry.OpenAsync(ct))
            await using (var hashing = new HashingStream(es))
            {
                await write(hashing);
                await hashing.FlushAsync(ct);
                (sha, size) = (hashing.Finish(), hashing.Written);
            }
            await output.FlushAsync(ct);
            var item = new JsonObject
            {
                ["path"] = path, ["kind"] = e.Kind, ["category"] = e.Category, ["sha256"] = sha, ["sizeBytes"] = size,
            };
            if (e.DocumentNumber is not null)
                item["source"] = new JsonObject { ["documentNumber"] = e.DocumentNumber, ["revision"] = e.DocumentRevision, ["name"] = e.DocumentName, ["fileId"] = e.FileId?.ToString("D"), ["fileName"] = e.FileName, ["sourceSha256"] = e.SourceSha256 };
            written.Add(item);
        }

        var coverText = Encoding.UTF8.GetBytes(CoverSheet(plan, exportId, subject, now));
        await AddAsync(plan.Elements[0], "cover-sheet.txt", s => s.WriteAsync(coverText, ct).AsTask(), CompressionLevel.Optimal);
        var partJson = Encoding.UTF8.GetBytes(PartJson(root.Value!, plan).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        await AddAsync(plan.Elements[1], "part.json", s => s.WriteAsync(partJson, ct).AsTask(), CompressionLevel.Optimal);
        var csv = Encoding.UTF8.GetBytes(BomCsv(plan.Banner, structure.Value!));
        var json = Encoding.UTF8.GetBytes(BomJson(structure.Value!, plan).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        await AddAsync(plan.Elements[2], plan.Elements[2].Path, s => s.WriteAsync(csv, ct).AsTask(), CompressionLevel.Optimal);
        await AddAsync(plan.Elements[3], plan.Elements[3].Path, s => s.WriteAsync(json, ct).AsTask(), CompressionLevel.Optimal);

        var fileCount = 0;
        foreach (var e in plan.Elements.Where(x => x.Kind == "document"))
        {
            var opened = await documents.OpenMarkedFileAsync(subject, e.DocumentNumber!, e.FileId!.Value, correlationId, ct);
            if (!opened.IsOk) throw new InvalidOperationException($"Document {e.DocumentNumber} could not be delivered ({opened.Kind}); the package is incomplete and was not recorded.");
            var marked = opened.Value!;
            // PDFs keep their name; anything else arrives in the marked ZIP wrapper under the wrapper's name.
            var path = $"documents/{e.Category}/{e.DocumentNumber}-{e.DocumentRevision}/{SafeName(marked.FileName)}";
            await AddAsync(e, path, s => marked.WriteToAsync(s, ct), CompressionLevel.NoCompression);
            fileCount++;
        }

        // Reconciliation: what was planned is what was written.
        var plannedFiles = plan.Elements.Count(x => x.Kind == "document");
        if (fileCount != plannedFiles) throw new InvalidOperationException($"Reconciliation failed: {fileCount} of {plannedFiles} document files were written.");

        var counts = new JsonObject
        {
            ["parts"] = plan.PartCount, ["documents"] = plan.DocumentCount, ["documentFiles"] = fileCount, ["structureLines"] = plan.StructureLines,
            ["unmappedDocuments"] = plan.UnmappedDocuments, ["entries"] = written.Count,
        };
        var manifest = new JsonObject
        {
            ["format"] = Format,
            ["standard"] = "MIL-STD-31000 (aligned; element categories per administrator mapping)",
            ["exportId"] = exportId.ToString("D"),
            ["createdAt"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["package"] = folder,
            ["object"] = new JsonObject { ["type"] = plan.Request.ObjectType, ["number"] = plan.Request.Number, ["rootPart"] = plan.RootNumber, ["rootRevision"] = plan.RootRevision },
            ["recipient"] = new JsonObject { ["profile"] = plan.RecipientProfile, ["category"] = plan.RecipientCategory },
            ["distribution"] = new JsonObject
            {
                ["banner"] = overall.BannerText(), ["statement"] = TdpTexts.Statement(overall), ["exportWarning"] = TdpTexts.ExportWarning, ["destructionNotice"] = TdpTexts.DestructionNotice,
                ["markings"] = Envelope.MarkingsSummary(overall),
            },
            ["provenance"] = new JsonObject
            {
                ["keelVersion"] = typeof(TdpService).Assembly.GetName().Version?.ToString() ?? "0",
                ["exporter"] = subject.Username ?? subject.Id,
                ["exportedAtUtc"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["sourceVersions"] = new JsonArray([.. plan.SourceVersions.Select(v => (JsonNode?)v)]),
                ["counts"] = counts.DeepClone(),
            },
            ["elements"] = new JsonArray([.. written.Select(w => (JsonNode?)w)]),
        };
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var manifestSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(manifestBytes));
        var signature = signer.Sign(manifestBytes);
        var keyId = signer.KeyId;

        // The record and its audit event commit together, before the manifest is released: no manifest leaves without a record.
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            Guid profileId;
            await using (var pc = new NpgsqlCommand("SELECT id FROM integration.clearance_profile WHERE name = @n", conn, tx))
            {
                pc.Parameters.AddWithValue("n", plan.RecipientProfile);
                profileId = (Guid)(await pc.ExecuteScalarAsync(ct))!;
            }
            await using (var ins = new NpgsqlCommand(
                $"""
                INSERT INTO integration.tdp_export (id, created_at, exporter_id, exporter_name, object_type, object_number, object_revision, recipient_profile_id, recipient_profile_name,
                    recipient_category, manifest, manifest_sha256, signature, signature_key_id, element_counts, correlation_id, {MarkingColumns.InsertColumns})
                VALUES (@id, @at, @eid, @ename, @otype, @onum, @orev, @pid, @pname, @pcat, @manifest, @sha, @sig, @kid, @counts, @corr, {MarkingColumns.InsertValues})
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("id", exportId);
                ins.Parameters.AddWithValue("at", now);
                ins.Parameters.AddWithValue("eid", subject.Id);
                ins.Parameters.AddWithValue("ename", subject.Username ?? subject.Id);
                ins.Parameters.AddWithValue("otype", plan.Request.ObjectType);
                ins.Parameters.AddWithValue("onum", plan.Request.Number);
                ins.Parameters.Add(new NpgsqlParameter("orev", NpgsqlDbType.Text) { Value = (object?)plan.RootRevision ?? DBNull.Value });
                ins.Parameters.AddWithValue("pid", profileId);
                ins.Parameters.AddWithValue("pname", plan.RecipientProfile);
                ins.Parameters.AddWithValue("pcat", plan.RecipientCategory);
                ins.Parameters.Add(new NpgsqlParameter("manifest", NpgsqlDbType.Text) { Value = Encoding.UTF8.GetString(manifestBytes) });
                ins.Parameters.AddWithValue("sha", manifestSha);
                ins.Parameters.AddWithValue("sig", signature);
                ins.Parameters.AddWithValue("kid", keyId);
                ins.Parameters.Add(new NpgsqlParameter("counts", NpgsqlDbType.Jsonb) { Value = counts.ToJsonString() });
                ins.Parameters.Add(new NpgsqlParameter("corr", NpgsqlDbType.Text) { Value = (object?)correlationId ?? DBNull.Value });
                foreach (var p in MarkingColumns.Parameters(overall)) ins.Parameters.Add(new NpgsqlParameter(p.Key, p.Value));
                await ins.ExecuteNonQueryAsync(ct);
            }
            await audit.AppendAsync(tx, IntegrationAudit.Entry(subject, "integration.tdp.export", AuditOutcome.Success, exportId.ToString("D"), correlationId,
                ("object", $"{plan.Request.ObjectType}:{plan.Request.Number}"), ("revision", plan.RootRevision), ("recipient", plan.RecipientProfile),
                ("parts", plan.PartCount.ToString(CultureInfo.InvariantCulture)), ("documents", plan.DocumentCount.ToString(CultureInfo.InvariantCulture)),
                ("documentFiles", fileCount.ToString(CultureInfo.InvariantCulture)), ("manifestSha256", manifestSha), ("signatureKeyId", keyId), ("markings", overall.BannerText())), ct);
            await tx.CommitAsync(ct);
        }

        await AddRawAsync(zip, $"{folder}/manifest.json", manifestBytes, now, ct);
        var sigJson = new JsonObject { ["algorithm"] = "ECDSA-P256-SHA256-P1363", ["keyId"] = keyId, ["manifestSha256"] = manifestSha, ["signature"] = signature };
        await AddRawAsync(zip, $"{folder}/manifest.sig", Encoding.UTF8.GetBytes(sigJson.ToJsonString()), now, ct);
        await AddRawAsync(zip, $"{folder}/public-key.pem", Encoding.ASCII.GetBytes(signer.PublicKeyPem), now, ct);
        await zip.DisposeAsync();
        await output.FlushAsync(ct);

        return new TdpExportRecord(exportId, now, subject.Username ?? subject.Id, plan.Request.ObjectType, plan.Request.Number, plan.RootRevision, plan.RecipientProfile, plan.RecipientCategory,
            manifestSha, keyId, signature, counts, overall.BannerText(), overall, Encoding.UTF8.GetString(manifestBytes));
    }

    private static async Task AddRawAsync(ZipArchive zip, string path, byte[] content, DateTimeOffset at, CancellationToken ct)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = at;
        await using var s = await entry.OpenAsync(ct);
        await s.WriteAsync(content, ct);
    }

    // ---------------------------------------------------------------- content

    private static string CoverSheet(TdpPlan plan, Guid id, Subject exporter, DateTimeOffset now)
    {
        var m = plan.OverallMarkings!;
        var sb = new StringBuilder();
        sb.AppendLine(plan.Banner).AppendLine();
        sb.AppendLine("TECHNICAL DATA PACKAGE (aligned with MIL-STD-31000)").AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Package:    {plan.Request.ObjectType} {plan.Request.Number} (root part {plan.RootNumber} revision {plan.RootRevision})"));
        sb.AppendLine($"Export id:  {id:D}");
        sb.AppendLine($"Exported:   {now.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z by {exporter.Username ?? exporter.Id}");
        sb.AppendLine($"Recipient:  {plan.RecipientProfile} ({plan.RecipientCategory})");
        sb.AppendLine($"Keel PLM:   {typeof(TdpService).Assembly.GetName().Version}").AppendLine();
        sb.AppendLine(TdpTexts.Statement(m)).AppendLine();
        sb.AppendLine(TdpTexts.ExportWarning).AppendLine();
        sb.AppendLine(TdpTexts.DestructionNotice).AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Contents: {plan.PartCount} part(s), {plan.StructureLines} structure line(s), {plan.DocumentCount} document(s) in {plan.Elements.Count(e => e.Kind == "document")} file(s)."));
        sb.AppendLine("Elements by category:");
        foreach (var g in plan.Elements.Where(e => e.Kind is "document" or "document-record").GroupBy(e => e.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"  {TdpCategories.Label(g.Key)}");
            foreach (var e in g) sb.AppendLine($"    {e.DocumentNumber} rev {e.DocumentRevision}  {e.FileName ?? "(no file)"}  source SHA-256 {e.SourceSha256 ?? "-"}");
        }
        sb.AppendLine("  Associated lists: bom/*.csv, bom/*.json, part.json");
        if (plan.UnmappedDocuments > 0)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Note: {plan.UnmappedDocuments} document(s) have no category mapping and are listed under Other technical data."));
        sb.AppendLine().AppendLine("manifest.json lists every file with its SHA-256; manifest.sig is a detached ECDSA P-256 signature over manifest.json; public-key.pem is the signer's public key.");
        sb.AppendLine().AppendLine(plan.Banner);
        return sb.ToString();
    }

    private static JsonObject PartJson(TdpPart p, TdpPlan plan) => new()
    {
        ["number"] = p.Number, ["name"] = p.Name, ["partType"] = p.PartType, ["source"] = p.Source, ["defaultUnit"] = p.DefaultUnit, ["revision"] = p.Revision, ["iteration"] = p.Iteration,
        ["state"] = p.State, ["description"] = p.Description, ["attributes"] = JsonNode.Parse(p.AttributesJson), ["markings"] = Envelope.MarkingsSummary(p.Markings), ["banner"] = plan.Banner,
    };

    private static JsonObject BomJson(TdpStructure s, TdpPlan plan) => new()
    {
        ["root"] = new JsonObject { ["number"] = s.RootNumber, ["revision"] = s.RootRevision }, ["configuration"] = s.Configuration, ["banner"] = plan.Banner,
        ["lines"] = new JsonArray([.. s.Lines.Select(l => (JsonNode?)new JsonObject
        {
            ["level"] = l.Level, ["path"] = l.Path, ["findNumber"] = l.FindNumber, ["number"] = l.Number, ["name"] = l.Name, ["partType"] = l.PartType, ["revision"] = l.Revision,
            ["iteration"] = l.Iteration, ["state"] = l.State, ["quantity"] = l.Quantity, ["extendedQuantity"] = l.ExtendedQuantity, ["unit"] = l.Unit,
            ["referenceDesignators"] = new JsonArray([.. l.ReferenceDesignators.Select(d => (JsonNode?)d)]), ["notes"] = l.Notes, ["effectivity"] = l.Effectivity,
        })]),
    };

    private static string BomCsv(string banner, TdpStructure s)
    {
        static string Cell(string? v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            var x = v[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + v : v;
            return x.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + x.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : x;
        }
        var sb = new StringBuilder();
        sb.Append(Cell(banner)).Append("\r\n");
        sb.Append("Level,Path,Find number,Part number,Name,Revision,State,Quantity,Unit,Extended quantity,Reference designators,Effectivity,Notes\r\n");
        foreach (var l in s.Lines)
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"{l.Level},{Cell(l.Path)},{l.FindNumber},{Cell(l.Number)},{Cell(l.Name)},{Cell(l.Revision is null ? null : $"{l.Revision}.{l.Iteration}")},{Cell(l.State)},{l.Quantity:0.######},{Cell(l.Unit)},{l.ExtendedQuantity:0.######},{Cell(string.Join(' ', l.ReferenceDesignators))},{Cell(l.Effectivity)},{Cell(l.Notes)}\r\n"));
        sb.Append(Cell(banner)).Append("\r\n");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- records and verification

    private const string RecordColumns =
        "e.id, e.created_at, e.exporter_name, e.object_type, e.object_number, e.object_revision, e.recipient_profile_name, e.recipient_category, e.manifest_sha256, " +
        "e.signature_key_id, e.signature, e.element_counts::text";

    private static TdpExportRecord ReadRecord(NpgsqlDataReader r, bool withManifest, int manifestIndex)
    {
        var markings = MarkingColumns.Read(r, 12);
        return new TdpExportRecord(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6),
            r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10), JsonNode.Parse(r.GetString(11))!.AsObject(), markings.BannerText(), markings,
            withManifest ? r.GetString(manifestIndex) : null);
    }

    public async Task<Outcome<IReadOnlyList<TdpExportRecord>>> ListAsync(Subject subject, string? number, int limit, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Read)) return Outcome<IReadOnlyList<TdpExportRecord>>.Forbidden([AccessPolicy.RoleMissing]);
        var filter = AccessPolicy.BuildReadFilter(subject, "e");
        await using var cmd = dataSource.CreateCommand(
            $"""
            SELECT {RecordColumns}, {MarkingColumns.Select("e")}
            FROM integration.tdp_export e
            WHERE {filter.Sql} AND (@num::text IS NULL OR e.object_number = @num)
            ORDER BY e.created_at DESC LIMIT @limit
            """);
        foreach (var p in filter.Parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);
        cmd.Parameters.Add(new NpgsqlParameter("num", NpgsqlDbType.Text) { Value = (object?)number ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        var list = new List<TdpExportRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadRecord(r, false, 0));
        return Outcome<IReadOnlyList<TdpExportRecord>>.Ok(list);
    }

    public async Task<Outcome<TdpExportRecord>> GetAsync(Subject subject, Guid id, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Read)) return Outcome<TdpExportRecord>.Forbidden([AccessPolicy.RoleMissing]);
        var filter = AccessPolicy.BuildReadFilter(subject, "e");
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {RecordColumns}, {MarkingColumns.Select("e")}, e.manifest FROM integration.tdp_export e WHERE e.id = @id AND {filter.Sql}");
        foreach (var p in filter.Parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            await r.CloseAsync();
            await audit.AppendAsync(IntegrationAudit.Entry(subject, "integration.tdp.read", AuditOutcome.Denied, id.ToString("D"), correlationId, ("reason", "not found or not readable")), ct);
            return Outcome<TdpExportRecord>.NotFound();
        }
        return Outcome<TdpExportRecord>.Ok(ReadRecord(r, true, 22));
    }

    /// <summary>The record's own integrity: the stored manifest hashes to the stored hash and the stored signature verifies under a trusted key.</summary>
    public async Task<Outcome<TdpVerification>> VerifyRecordAsync(Subject subject, Guid id, string? correlationId, CancellationToken ct = default)
    {
        var record = await GetAsync(subject, id, correlationId, ct);
        if (!record.IsOk) return Pass<TdpExportRecord, TdpVerification>(record);
        var rec = record.Value!;
        // The manifest is stored as the exact text that was signed, so the record can be checked without the package.
        var bytes = Encoding.UTF8.GetBytes(rec.ManifestText!);
        var checks = new List<TdpCheck>
        {
            new("record.manifest_sha256", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) == rec.ManifestSha256, "The stored manifest hashes to the recorded hash."),
            new("record.key_trusted", signer.IsTrusted(rec.SignatureKeyId), $"Signing key {rec.SignatureKeyId} is trusted by this installation."),
            new("record.signature", signer.Verify(bytes, rec.Signature, rec.SignatureKeyId), "The recorded signature verifies over the stored manifest."),
        };
        return Outcome<TdpVerification>.Ok(new TdpVerification(checks.All(c => c.Ok), id, checks));
    }

    /// <summary>
    /// Verifies a package against the record of its export: manifest hash and signature (under a key this installation trusts, not merely the key
    /// the package carries), every file's SHA-256 and size, no unlisted files, and the counts.
    /// </summary>
    public async Task<Outcome<TdpVerification>> VerifyPackageAsync(Subject subject, Stream package, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Read)) return Outcome<TdpVerification>.Forbidden([AccessPolicy.RoleMissing]);
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), "keel-tdp-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await using (temp)
        {
            await package.CopyToAsync(temp, ct);
            temp.Position = 0;
            var checks = new List<TdpCheck>();
            ZipArchive zip;
            try { zip = new ZipArchive(temp, ZipArchiveMode.Read, leaveOpen: true); }
            catch (InvalidDataException) { return Outcome<TdpVerification>.Invalid(["The file is not a ZIP archive."]); }
            using (zip)
            {
                var manifestEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/manifest.json", StringComparison.Ordinal) && e.FullName.Count(c => c == '/') == 1);
                var sigEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/manifest.sig", StringComparison.Ordinal) && e.FullName.Count(c => c == '/') == 1);
                if (manifestEntry is null || sigEntry is null)
                    return Outcome<TdpVerification>.Ok(new TdpVerification(false, null, [new TdpCheck("package.manifest", false, "manifest.json or manifest.sig is missing.")]));
                var prefix = manifestEntry.FullName[..^"manifest.json".Length];
                byte[] manifestBytes = await ReadAllAsync(manifestEntry, ct);
                var sig = JsonNode.Parse(Encoding.UTF8.GetString(await ReadAllAsync(sigEntry, ct)))!.AsObject();
                var manifest = JsonNode.Parse(manifestBytes)!.AsObject();
                var exportId = Guid.TryParse(manifest["exportId"]?.GetValue<string>(), out var g) ? g : (Guid?)null;
                var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(manifestBytes));

                checks.Add(new TdpCheck("signature.hash", sig["manifestSha256"]?.GetValue<string>() == sha, "The manifest hashes to the value in manifest.sig."));
                var keyId = sig["keyId"]?.GetValue<string>() ?? "";
                checks.Add(new TdpCheck("signature.key_trusted", signer.IsTrusted(keyId), $"Key {keyId} is a signing key this installation trusts."));
                checks.Add(new TdpCheck("signature.valid", signer.Verify(manifestBytes, sig["signature"]?.GetValue<string>() ?? "", keyId), "The ECDSA P-256 signature over manifest.json verifies."));

                var listed = manifest["elements"]!.AsArray().Select(e => e!.AsObject()).ToList();
                var bad = new List<string>();
                foreach (var el in listed)
                {
                    var path = el["path"]!.GetValue<string>();
                    var entry = zip.GetEntry(prefix + path);
                    if (entry is null) { bad.Add($"{path} (missing)"); continue; }
                    await using var es = await entry.OpenAsync(ct);
                    using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long size = 0;
                    int n;
                    while ((n = await es.ReadAsync(buffer, ct)) > 0) { h.AppendData(buffer, 0, n); size += n; }
                    if (Convert.ToHexStringLower(h.GetHashAndReset()) != el["sha256"]!.GetValue<string>() || size != el["sizeBytes"]!.GetValue<long>()) bad.Add($"{path} (hash or size differs)");
                }
                checks.Add(new TdpCheck("files.hashes", bad.Count == 0, bad.Count == 0 ? $"All {listed.Count} listed files match their SHA-256." : "Differs: " + string.Join("; ", bad)));
                var allowed = listed.Select(e => prefix + e["path"]!.GetValue<string>()).Concat([prefix + "manifest.json", prefix + "manifest.sig", prefix + "public-key.pem"]).ToHashSet(StringComparer.Ordinal);
                var extra = zip.Entries.Where(e => !e.FullName.EndsWith('/') && !allowed.Contains(e.FullName)).Select(e => e.FullName).ToList();
                checks.Add(new TdpCheck("files.no_extra", extra.Count == 0, extra.Count == 0 ? "No file outside the manifest." : "Unlisted: " + string.Join("; ", extra)));

                // Compare with the record of the export (needs read access to it; the record is the origin of trust for the manifest).
                if (exportId is not null)
                {
                    var rec = await GetAsync(subject, exportId.Value, correlationId, ct);
                    if (!rec.IsOk) checks.Add(new TdpCheck("record.found", false, "No readable export record exists for this package."));
                    else
                    {
                        checks.Add(new TdpCheck("record.found", true, $"Export {exportId}."));
                        checks.Add(new TdpCheck("record.manifest_sha256", rec.Value!.ManifestSha256 == sha, "The manifest hash equals the one recorded at export."));
                        checks.Add(new TdpCheck("record.signature", rec.Value.Signature == sig["signature"]?.GetValue<string>(), "The signature equals the one recorded at export."));
                    }
                }
                else checks.Add(new TdpCheck("record.found", false, "The manifest has no export id."));

                var result = new TdpVerification(checks.All(c => c.Ok), exportId, checks);
                await audit.AppendAsync(IntegrationAudit.Entry(subject, "integration.tdp.verify", result.Valid ? AuditOutcome.Success : AuditOutcome.Failure, exportId?.ToString("D"), correlationId,
                    ("valid", result.Valid.ToString()), ("failed", string.Join(",", checks.Where(c => !c.Ok).Select(c => c.Name)))), ct);
                return Outcome<TdpVerification>.Ok(result);
            }
        }
    }

    private static async Task<byte[]> ReadAllAsync(ZipArchiveEntry e, CancellationToken ct)
    {
        if (e.Length > 64 * 1024 * 1024) throw new InvalidDataException("Manifest entries are limited to 64 MiB.");
        await using var s = await e.OpenAsync(ct);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
