using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;
using static Keel.Requirements.RequirementStore;

namespace Keel.Requirements;

public sealed partial class RequirementService
{
    public const int MaxEvidence = 20;

    private bool IsFresh(Subject subject, DateTimeOffset now) =>
        subject.AuthTime is { } t && t <= now + TimeSpan.FromSeconds(60) && now - t <= TimeSpan.FromSeconds(opts.StepUpMaxAgeSeconds);

    /// <summary>
    /// Records a verification of the requirement's CURRENT content (informal, or formal with an e-signature). A formal record needs the approver role and a recent sign-in
    /// (otherwise <see cref="OutcomeKind.StepUpRequired"/>), is signed over the requirement's content hash, the result, the method and the evidence, and is immutable like every record.
    /// Evidence documents must be readable by the person recording. Notes and the waiver reason are marked data: they are stored, never audited or sent in a notification.
    /// </summary>
    public async Task<Outcome<VerificationView>> RecordVerificationAsync(Subject subject, string number, string key, RecordVerificationCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        if (cmd.Formal)
        {
            var now0 = clock.GetUtcNow();
            if (!IsFresh(subject, now0) && AccessPolicy.HasRoleFor(subject, PlmAction.Sign))
            {
                await using var conn0 = await dataSource.OpenConnectionAsync(ct);
                await using var tx0 = await conn0.BeginTransactionAsync(ct);
                await audit.AppendAsync(tx0, Entry(subject, "requirement.verify", AuditOutcome.Denied, number, correlationId, ("key", key), ("reasons", "stepup.required"), ("authTime", subject.AuthTime?.ToString("O", CultureInfo.InvariantCulture))), ct);
                await tx0.CommitAsync(ct);
                return Outcome<VerificationView>.StepUpRequired("stepup.required", opts.StepUpMaxAgeSeconds);
            }
        }
        return await MutateAsync<VerificationView>(subject, number, "requirement.verify", correlationId, async (head, conn, tx) =>
        {
            var w = await FindReadableAsync(conn, tx, subject, head, key, ct);
            if (w is null) return Step<VerificationView>.Invalid($"Requirement {key} was not found.");
            var action = cmd.Formal ? PlmAction.Sign : PlmAction.Modify;
            var decision = AccessPolicy.Evaluate(subject, w.Markings, action);
            if (!decision.Permitted) return Step<VerificationView>.Hidden(cmd.Formal ? "A formal verification needs the approver role." : $"You are not authorized to change {key}.", string.Join(",", decision.Reasons));
            if (w.Status == ReqStatuses.Withdrawn) return Step<VerificationView>.Invalid($"{key} is withdrawn; it is not verified.");
            if (cmd.Result is null || !VerificationResults.All.Contains(cmd.Result)) return Step<VerificationView>.Invalid($"The result must be one of: {string.Join(", ", VerificationResults.All)}.");
            var method = cmd.Method ?? w.Method;
            if (!VerificationMethods.All.Contains(method)) return Step<VerificationView>.Invalid($"The method must be one of: {string.Join(", ", VerificationMethods.All)}.");
            var waiver = cmd.WaiverReason?.Trim();
            if (cmd.Result == VerificationResults.Waived && string.IsNullOrEmpty(waiver)) return Step<VerificationView>.Invalid("A waiver needs a reason.");
            if (cmd.Result != VerificationResults.Waived && !string.IsNullOrEmpty(waiver)) return Step<VerificationView>.Invalid("Only a waiver has a waiver reason.");
            if ((waiver?.Length ?? 0) > RequirementRules.MaxNotes || (cmd.Notes?.Length ?? 0) > RequirementRules.MaxNotes) return Step<VerificationView>.Invalid($"Notes and the waiver reason are at most {RequirementRules.MaxNotes} characters.");
            var meaning = cmd.SignatureMeaning?.Trim();
            if (cmd.Formal && (string.IsNullOrEmpty(meaning) || meaning.Length > 200)) return Step<VerificationView>.Invalid("A formal verification needs the meaning of the signature (at most 200 characters).");
            var docs = (cmd.EvidenceDocuments ?? []).Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (docs.Count > MaxEvidence) return Step<VerificationView>.Invalid($"At most {MaxEvidence} evidence documents.");
            if (cmd.Result == VerificationResults.Pass && docs.Count == 0 && cmd.Formal) return Step<VerificationView>.Invalid("A formal pass needs at least one evidence document.");
            var evidence = new List<(string Number, string Name, string? Revision, Markings Markings)>();
            foreach (var d in docs)
            {
                var t = await LoadTargetAsync(conn, tx, LinkTargetKinds.Document, d, ct);
                if (t is null || !AccessPolicy.Evaluate(subject, t.Markings, PlmAction.Read).Permitted) return Step<VerificationView>.Hidden($"The document '{d}' was not found, or you are not authorized to see it.", "evidence.not_readable");
                evidence.Add((t.Number, t.Name, t.Revision, t.Markings));
            }
            var now = clock.GetUtcNow();
            var id = Guid.CreateVersion7();
            var banner = w.Markings.BannerText();
            await using (var ins = new NpgsqlCommand(
                """
                INSERT INTO req.verification (id, requirement_id, spec_revision, requirement_sha256, method, result, formal, notes, waiver_reason, performed_at, performed_by, performed_by_sub)
                VALUES (@id, @q, @rev, @sha, @method, @result, @formal, @notes, @waiver, @at, @by, @sub)
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("q", w.Id);
                ins.Parameters.AddWithValue("rev", head.Revision);
                ins.Parameters.AddWithValue("sha", w.Sha);
                ins.Parameters.AddWithValue("method", method);
                ins.Parameters.AddWithValue("result", cmd.Result);
                ins.Parameters.AddWithValue("formal", cmd.Formal);
                ins.Parameters.AddWithValue("notes", cmd.Notes?.Trim() ?? "");
                ins.Parameters.Add(new NpgsqlParameter("waiver", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)waiver ?? DBNull.Value });
                ins.Parameters.AddWithValue("at", now);
                ins.Parameters.AddWithValue("by", Who(subject));
                ins.Parameters.AddWithValue("sub", subject.Id);
                await ins.ExecuteNonQueryAsync(ct);
            }
            foreach (var e in evidence)
            {
                await using var ev = new NpgsqlCommand("INSERT INTO req.verification_evidence (verification_id, document_number, document_revision) VALUES (@v, @d, @r)", conn, tx);
                ev.Parameters.AddWithValue("v", id);
                ev.Parameters.AddWithValue("d", e.Number);
                ev.Parameters.Add(new NpgsqlParameter("r", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)e.Revision ?? DBNull.Value });
                await ev.ExecuteNonQueryAsync(ct);
            }
            string? signatureHashHex = null;
            if (cmd.Formal)
            {
                var evidenceList = string.Join(',', evidence.Select(e => $"{e.Number}@{e.Revision}"));
                var content = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', "req-verification-content/1", w.Sha, cmd.Result, method, evidenceList)));
                var authTime = subject.AuthTime!.Value;
                var statement = string.Join('\n', "req-verification/1", id.ToString("D"), head.Number, w.Key, head.Revision, Convert.ToHexStringLower(content), subject.Id, Who(subject), meaning,
                    now.ToString("O", CultureInfo.InvariantCulture), authTime.ToString("O", CultureInfo.InvariantCulture), banner);
                var signature = SHA256.HashData(Encoding.UTF8.GetBytes(statement));
                signatureHashHex = Convert.ToHexStringLower(signature);
                await using var sig = new NpgsqlCommand(
                    """
                    INSERT INTO req.verification_signature (id, verification_id, signer_sub, signer_username, signer_display_name, meaning, signed_at, auth_time, spec_number, requirement_key, spec_revision,
                                                            marking_banner, content_hash, signature_hash)
                    VALUES (@id, @v, @sub, @user, @name, @meaning, @at, @auth, @spec, @key, @rev, @banner, @ch, @sh)
                    """, conn, tx);
                sig.Parameters.AddWithValue("id", Guid.CreateVersion7());
                sig.Parameters.AddWithValue("v", id);
                sig.Parameters.AddWithValue("sub", subject.Id);
                sig.Parameters.AddWithValue("user", Who(subject));
                sig.Parameters.AddWithValue("name", subject.DisplayName);
                sig.Parameters.AddWithValue("meaning", meaning!);
                sig.Parameters.AddWithValue("at", now);
                sig.Parameters.AddWithValue("auth", authTime);
                sig.Parameters.AddWithValue("spec", head.Number);
                sig.Parameters.AddWithValue("key", w.Key);
                sig.Parameters.AddWithValue("rev", head.Revision);
                sig.Parameters.AddWithValue("banner", banner);
                sig.Parameters.AddWithValue("ch", content);
                sig.Parameters.AddWithValue("sh", signature);
                await sig.ExecuteNonQueryAsync(ct);
            }
            if (notifications is not null)
            {
                await NotifyAsync(conn, tx, NotificationKindCodes.WatchedChanged, head, subject, ct);
                await notifications.AutoWatchAsync(conn, tx, subject.Id, AwarenessKinds.ReqSpec, head.Number, ct);
            }
            var view = new VerificationView(id, cmd.Result, method, cmd.Formal, head.Revision, true, cmd.Notes?.Trim(), waiver, now, Who(subject), cmd.Formal ? subject.DisplayName : null, cmd.Formal ? now : null, meaning,
                [.. evidence.Select(e => new EvidenceView(false, e.Number, e.Name, e.Revision, e.Markings.BannerText(), e.Markings))]);
            return Step<VerificationView>.Done(view, ("key", w.Key), ("result", cmd.Result), ("method", method), ("formal", cmd.Formal.ToString()), ("evidence", Fmt(evidence.Count)),
                ("requirementSha256", w.Sha), ("signatureSha256", signatureHashHex));
        }, ct, cmd.Formal ? PlmAction.Sign : PlmAction.Modify);
    }

    /// <summary>The verification records of a requirement, newest first. Evidence documents the caller cannot read are placeholders.</summary>
    public Task<Outcome<RequirementVerifications>> GetVerificationsAsync(Subject subject, string number, string key, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<RequirementVerifications>(subject, number, null, "requirement.verifications", correlationId, async (conn, tx, head) =>
        {
            var w = await FindReadableAsync(conn, tx, subject, head, key, ct);
            if (w is null) return (Outcome<RequirementVerifications>.NotFound(), []);
            var records = new List<(Guid Id, string Sha, VerificationView View)>();
            var dFilter = AccessPolicy.BuildReadFilter(subject, "dm");
            await using (var cmd = new NpgsqlCommand(
                """
                SELECT v.id, v.requirement_sha256, v.result, v.method, v.formal, v.spec_revision, v.notes, v.waiver_reason, v.performed_at, v.performed_by,
                       s.signer_display_name, s.signed_at, s.meaning
                FROM req.verification v LEFT JOIN req.verification_signature s ON s.verification_id = v.id
                WHERE v.requirement_id = @q
                ORDER BY v.performed_at DESC, v.id DESC
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("q", w.Id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    records.Add((r.GetGuid(0), r.GetString(1), new VerificationView(r.GetGuid(0), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetString(5), false, r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                        r.GetFieldValue<DateTimeOffset>(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), r.IsDBNull(12) ? null : r.GetString(12), [])));
            }
            var finished = new List<VerificationView>();
            foreach (var (id, sha, view) in records)
            {
                var evidence = new List<EvidenceView>();
                await using (var cmd = new NpgsqlCommand(
                    $"""
                    SELECT e.document_number, e.document_revision, dm.number, dm.name, {MarkingColumns.Select("dm")}
                    FROM req.verification_evidence e
                    LEFT JOIN plm.document_master dm ON dm.number = e.document_number AND {dFilter.Sql}
                    WHERE e.verification_id = @v ORDER BY e.document_number
                    """, conn, tx))
                {
                    cmd.Parameters.AddWithValue("v", id);
                    foreach (var p in dFilter.Parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        evidence.Add(r.IsDBNull(2) ? new EvidenceView(true, null, null, null, null) : new EvidenceView(false, r.GetString(2), r.GetString(3), r.IsDBNull(1) ? null : r.GetString(1), MarkingColumns.Read(r, 4).BannerText(), MarkingColumns.Read(r, 4)));
                }
                finished.Add(view with { Evidence = evidence, Current = sha == w.Sha });
            }
            var state = RequirementRules.VerificationState(w.Sha, [.. records.Select(r => (r.Sha, r.View.Result))]);
            var shown = finished.SelectMany(f => f.Evidence).Where(e => e.Markings is not null).Select(e => e.Markings!).Append(w.Markings).Append(head.Markings);
            var set = new RequirementVerifications(head.Number, key, state, finished, Markings.AggregateBanner(shown));
            return (Outcome<RequirementVerifications>.Ok(set), [("key", key), ("records", Fmt(finished.Count)), ("state", state)]);
        }, ct);
}
