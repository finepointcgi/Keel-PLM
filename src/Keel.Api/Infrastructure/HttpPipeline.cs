using Keel.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Keel.Api.Infrastructure;

public static class CorrelationId
{
    public const string Header = "X-Correlation-Id";
    private const string ItemKey = "keel.correlation";

    public static string Get(HttpContext context) => (string)context.Items[ItemKey]!;

    /// <summary>Accepts a caller-supplied GUID, otherwise generates one; echoes it and adds it to every log scope.</summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var incoming = context.Request.Headers[Header].ToString();
        var id = Guid.TryParse(incoming, out var g) ? g.ToString("D") : Guid.CreateVersion7().ToString("D");
        context.Items[ItemKey] = id;
        context.Response.Headers[Header] = id;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Keel.Http");
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
            await next(context);
    });

    /// <summary>OWASP-recommended response headers. CSP allows only same-origin resources (air-gapped, no CDNs).</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var h = context.Response.Headers;
        h["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cache-Control"] = "no-store";
        await next(context);
    });
}

/// <summary>Writes an <c>access.denied</c> audit event whenever role-based authorization forbids a request.</summary>
public sealed class AuditingAuthorizationResultHandler(IAuditLog audit, ILogger<AuditingAuthorizationResultHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _inner = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && SubjectFactory.FromPrincipal(context.User) is { } subject)
        {
            await audit.AppendAsync(new AuditEntry
            {
                Actor = ActorSnapshot.From(subject),
                Action = "access.denied",
                Outcome = AuditOutcome.Denied,
                ObjectType = "endpoint",
                ObjectId = $"{context.Request.Method} {context.Request.Path}",
                CorrelationId = CorrelationId.Get(context),
                Details = AuditDetails.Of(("reason", "role.missing")),
            }, context.RequestAborted);
        }
        else if (authorizeResult.Challenged)
        {
            // Unauthenticated requests carry no verifiable identity; they are logged, not chained, to avoid audit flooding.
            logger.LogWarning("Unauthenticated request to {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        await _inner.HandleAsync(next, context, policy, authorizeResult);
    }
}
