using Keel.Core;

namespace Keel.Api.Endpoints;

public static class OutcomeResults
{
    /// <summary>Maps an application outcome to HTTP. Forbidden carries reason codes so the UI can explain the denial.</summary>
    public static IResult ToHttp<T>(this Outcome<T> o, Func<T, IResult>? ok = null) => o.Kind switch
    {
        OutcomeKind.Ok => ok is null ? Results.Ok(o.Value) : ok(o.Value!),
        OutcomeKind.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [.. o.Errors] }),
        OutcomeKind.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found"),
        OutcomeKind.Forbidden => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden",
            extensions: new Dictionary<string, object?> { ["reasons"] = o.Errors }),
        OutcomeKind.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: o.Errors[0]),
        _ => throw new InvalidOperationException($"Unhandled outcome {o.Kind}"),
    };
}
