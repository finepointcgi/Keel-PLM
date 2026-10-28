namespace Keel.Core;

/// <summary>Outcome of a validation or domain operation that can fail with user-facing messages.</summary>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T? value, IReadOnlyList<string> errors)
    {
        _value = value;
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
    public bool IsSuccess => Errors.Count == 0;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Result has errors: " + string.Join("; ", Errors));

    public static Result<T> Ok(T value) => new(value, []);
    public static Result<T> Fail(IEnumerable<string> errors)
    {
        var list = errors.ToList();
        if (list.Count == 0) throw new ArgumentException("A failed result needs at least one error.", nameof(errors));
        return new(default, list);
    }
}
