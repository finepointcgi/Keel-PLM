using System.Globalization;
using Keel.Api.Infrastructure;
using Keel.Audit;
using Keel.Security;
using Npgsql;

namespace Keel.Api.Endpoints;

public static class Policies
{
    public const string AnyPlmRole = "plm.any";
    public const string Auditor = "plm.auditor";
    public const string Admin = "plm.admin";
}

public static class ApiEndpoints
{
    private static string? ActorName(string payload)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("actor").TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() : null;
    }

    public static void MapKeelApi(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        // Anonymous: the SPA needs the IdP coordinates before it can sign in. Contains no secrets.
        v1.MapGet("/config", (KeelOptions o) => new ClientConfig(o.Auth.Authority, o.Auth.WebClientId, o.Auth.Audience)).AllowAnonymous();

        var authed = v1.MapGroup("").RequireAuthorization(Policies.AnyPlmRole);

        authed.MapGet("/me", (HttpContext ctx) =>
        {
            var s = SubjectFactory.Require(ctx);
            return new MeResponse(s.Id, s.DisplayName, s.UsPerson, s.Citizenship, s.Org, s.Programs, s.ExportAuthorizations, s.CuiCategories, s.Roles);
        });

        // "What-if" access check: validates markings and explains the decision for the caller. Reads no object data,
        // so it is not an auditable access; the UI uses it to show users why something would be hidden.
        authed.MapPost("/access/evaluate", (EvaluateRequest req, HttpContext ctx) =>
        {
            var markings = req.Markings.ToMarkings();
            if (!markings.IsSuccess)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["markings"] = [.. markings.Errors] });
            var decision = AccessPolicy.Evaluate(SubjectFactory.Require(ctx), markings.Value, req.Action);
            return Results.Ok(new EvaluateResponse(decision.Permitted, decision.Reasons, markings.Value.BannerText()));
        });

        var audit = v1.MapGroup("/audit");

        audit.MapGet("/events", async (long? after, string? objectType, string? objectId, string? actor, int? limit,
            HttpContext ctx, AuditQueries queries, IAuditLog log, CancellationToken ct) =>
        {
            var q = new AuditQuery(after, objectType, objectId, actor, limit ?? 100);
            var events = await queries.ListAsync(q, ct);
            await log.AppendAsync(new AuditEntry
            {
                Actor = ActorSnapshot.From(SubjectFactory.Require(ctx)),
                Action = "audit.read",
                Outcome = AuditOutcome.Success,
                ObjectType = "audit_log",
                CorrelationId = CorrelationId.Get(ctx),
                Details = AuditDetails.Of(("after", q.AfterSeq?.ToString(CultureInfo.InvariantCulture)), ("objectType", q.ObjectType),
                    ("objectId", q.ObjectId), ("actor", q.Actor), ("returned", events.Count.ToString(CultureInfo.InvariantCulture))),
            }, ct);
            return events.Select(e => new AuditEventDto(e.Seq, e.Id, e.OccurredAt, e.Actor, e.Action, e.ObjectType, e.ObjectId,
                e.Outcome.ToString(), e.CorrelationId, ActorName(e.Payload), e.Payload, Convert.ToHexStringLower(e.Hash)));
        }).RequireAuthorization(Policies.Auditor);

        audit.MapPost("/verify", async (HttpContext ctx, NpgsqlDataSource db, TrustedCheckpointKeys keys, IAuditLog log, CancellationToken ct) =>
        {
            VerificationResult result;
            await using (var conn = await db.OpenConnectionAsync(ct))
                result = await AuditVerifier.VerifyAsync(conn, keys.Keys, ct);
            await log.AppendAsync(new AuditEntry
            {
                Actor = ActorSnapshot.From(SubjectFactory.Require(ctx)),
                Action = "audit.verify",
                Outcome = result.Valid ? AuditOutcome.Success : AuditOutcome.Failure,
                ObjectType = "audit_log",
                CorrelationId = CorrelationId.Get(ctx),
                Details = AuditDetails.Of(("eventsChecked", result.EventsChecked.ToString(CultureInfo.InvariantCulture)),
                    ("checkpointsChecked", result.CheckpointsChecked.ToString(CultureInfo.InvariantCulture)),
                    ("failures", result.Failures.Count.ToString(CultureInfo.InvariantCulture))),
            }, ct);
            return result;
        }).RequireAuthorization(Policies.Auditor);

        audit.MapPost("/checkpoints", async (HttpContext ctx, IServiceProvider sp, IAuditLog log, CancellationToken ct) =>
        {
            if (sp.GetService<CheckpointSigner>() is not { } signer)
                return Results.Problem("Checkpoint signing key is not configured (Keel:Audit:CheckpointPrivateKeyPath).", statusCode: 409);
            var cp = await signer.CreateAsync(ct);
            await log.AppendAsync(new AuditEntry
            {
                Actor = ActorSnapshot.From(SubjectFactory.Require(ctx)),
                Action = "audit.checkpoint",
                Outcome = AuditOutcome.Success,
                ObjectType = "audit_log",
                CorrelationId = CorrelationId.Get(ctx),
                Details = AuditDetails.Of(("seq", cp?.Seq.ToString(CultureInfo.InvariantCulture)), ("keyId", signer.KeyId)),
            }, ct);
            return cp is null
                ? Results.Ok(new { created = false })
                : Results.Ok(new { created = true, cp.Seq, hash = Convert.ToHexStringLower(cp.Hash), cp.CreatedAt, cp.KeyId });
        }).RequireAuthorization(Policies.Admin);
    }
}
