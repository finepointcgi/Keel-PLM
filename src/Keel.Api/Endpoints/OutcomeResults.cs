using Keel.Core;

namespace Keel.Api.Endpoints;

public static class OutcomeResults
{
    /// <summary>400 with the allowed keys when a list request asks for a sort the module does not offer (GAP-A-1); nothing is queried.</summary>
    public static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem BadSort(string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["sort"] = [message] });

    /// <summary>Maps an application outcome to HTTP. Forbidden carries reason codes so the UI can explain the denial.</summary>
    public static IResult ToHttp<T>(this Outcome<T> o, Func<T, IResult>? ok = null) => o.Kind switch
    {
        OutcomeKind.Ok => ok is null ? Results.Ok(o.Value) : ok(o.Value!),
        OutcomeKind.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [.. o.Errors] }),
        OutcomeKind.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found"),
        OutcomeKind.Forbidden => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden",
            extensions: new Dictionary<string, object?> { ["reasons"] = o.Errors }),
        OutcomeKind.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: o.Errors[0]),
        OutcomeKind.Unprocessable => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: o.Errors[0],
            extensions: o.Errors.Count > 1 ? new Dictionary<string, object?> { ["signature"] = o.Errors[1] } : null),
        OutcomeKind.Unavailable => new UnavailableResult(o.Errors[0]),
        OutcomeKind.StepUpRequired => new StepUpRequiredResult(int.Parse(o.Errors[1], System.Globalization.CultureInfo.InvariantCulture)),
        _ => throw new InvalidOperationException($"Unhandled outcome {o.Kind}"),
    };
}

/// <summary>
/// 401 with the RFC 9470 challenge: the caller must present a token from a fresh authentication (<c>auth_time</c> within
/// <c>max_age</c> seconds) to sign (A9, ADR-0007).
/// </summary>
internal sealed class StepUpRequiredResult(int maxAgeSeconds) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.Headers.WWWAuthenticate =
            $"Bearer error=\"insufficient_user_authentication\", error_description=\"A fresh authentication is required to sign\", max_age=\"{maxAgeSeconds}\"";
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Step-up authentication required", type: "urn:keel:problem:step-up-required",
            extensions: new Dictionary<string, object?> { ["reason"] = "stepup.required", ["maxAgeSeconds"] = maxAgeSeconds }).ExecuteAsync(httpContext);
    }
}

/// <summary>503 with <c>Retry-After</c>: a dependency (the malware scanner) is down; nothing was changed and the request can be repeated.</summary>
internal sealed class UnavailableResult(string message) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.Headers.RetryAfter = "5";
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: message).ExecuteAsync(httpContext);
    }
}
