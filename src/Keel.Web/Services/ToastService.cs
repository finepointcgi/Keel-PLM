namespace Keel.Web.Services;

public enum ToastSeverity { Success, Info, Warning, Error }

/// <summary>One message shown in the toast container. <see cref="Message"/> is plain text (rendered encoded) and must not contain marked data beyond what the page shows.</summary>
public sealed record Toast(long Id, ToastSeverity Severity, string Message, string? Title, DateTimeOffset At);

/// <summary>
/// Per-circuit queue of transient feedback messages (ADR-0028). Components call <c>Success</c>, <c>Error</c> etc.; <c>ToastContainer</c> renders them in an
/// aria-live region without moving focus. The queue holds at most <see cref="Limit"/> messages: a new one displaces the oldest.
/// </summary>
public sealed class ToastService(TimeProvider clock)
{
    public const int Limit = 4;
    private readonly List<Toast> _toasts = [];
    private long _next;

    public event Action? Changed;

    public IReadOnlyList<Toast> Toasts
    {
        get { lock (_toasts) return _toasts.ToList(); }
    }

    public Toast Show(ToastSeverity severity, string message, string? title = null)
    {
        Toast toast;
        lock (_toasts)
        {
            toast = new Toast(++_next, severity, message, title, clock.GetUtcNow());
            _toasts.Add(toast);
            while (_toasts.Count > Limit) _toasts.RemoveAt(0);
        }
        Changed?.Invoke();
        return toast;
    }

    public Toast Success(string message, string? title = null) => Show(ToastSeverity.Success, message, title);
    public Toast Info(string message, string? title = null) => Show(ToastSeverity.Info, message, title);
    public Toast Warning(string message, string? title = null) => Show(ToastSeverity.Warning, message, title);
    public Toast Error(string message, string? title = null) => Show(ToastSeverity.Error, message, title);

    public void Dismiss(long id)
    {
        bool removed;
        lock (_toasts) removed = _toasts.RemoveAll(t => t.Id == id) > 0;
        if (removed) Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_toasts) _toasts.Clear();
        Changed?.Invoke();
    }

    /// <summary>How long a toast stays: errors and warnings stay longer than confirmations.</summary>
    public static TimeSpan Lifetime(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Error => TimeSpan.FromSeconds(12),
        ToastSeverity.Warning => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(6),
    };
}
