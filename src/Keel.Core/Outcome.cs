namespace Keel.Core;

public enum OutcomeKind
{
    Ok,
    Invalid,
    NotFound,
    Forbidden,
    Conflict,
    /// <summary>The action needs a fresh authentication (e-signature step-up, A9). Errors[0] = reason code, Errors[1] = max age in seconds.</summary>
    StepUpRequired,
    /// <summary>The request was understood but its content cannot be accepted (an upload the malware scanner flagged, ADR-0026). Errors[0] = message, Errors[1] = signature when there is one.</summary>
    Unprocessable,
    /// <summary>A dependency the request needs is unavailable right now (the malware scanner in Required mode); retry later. Errors[0] = message.</summary>
    Unavailable,
}

/// <summary>Result of an application command or query, mapped by the API to HTTP status codes.</summary>
public sealed record Outcome<T>(OutcomeKind Kind, T? Value, IReadOnlyList<string> Errors)
{
    public bool IsOk => Kind == OutcomeKind.Ok;

    public static Outcome<T> Ok(T value) => new(OutcomeKind.Ok, value, []);
    public static Outcome<T> Invalid(IEnumerable<string> errors) => new(OutcomeKind.Invalid, default, errors.ToList());
    /// <summary>Also used when the caller may not read the object, so existence is not disclosed (ADR-0004).</summary>
    public static Outcome<T> NotFound() => new(OutcomeKind.NotFound, default, []);
    public static Outcome<T> Forbidden(IEnumerable<string> reasons) => new(OutcomeKind.Forbidden, default, reasons.ToList());
    public static Outcome<T> Conflict(string message) => new(OutcomeKind.Conflict, default, [message]);
    public static Outcome<T> Unprocessable(string message, string? signature = null) => new(OutcomeKind.Unprocessable, default, signature is null ? [message] : [message, signature]);
    public static Outcome<T> Unavailable(string message) => new(OutcomeKind.Unavailable, default, [message]);
    public static Outcome<T> StepUpRequired(string reasonCode, int maxAgeSeconds) =>
        new(OutcomeKind.StepUpRequired, default, [reasonCode, maxAgeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
}
