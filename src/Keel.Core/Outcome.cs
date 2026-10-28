namespace Keel.Core;

public enum OutcomeKind
{
    Ok,
    Invalid,
    NotFound,
    Forbidden,
    Conflict,
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
}
