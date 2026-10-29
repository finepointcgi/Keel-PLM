using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Keel.Web.Services.Visuals;

/// <summary>The ids a diagram's content needs from its frame: patterns and the arrow head are defined once per diagram under this prefix.</summary>
public sealed record DiagramContext(string Uid)
{
    public string Pattern(string name) => $"url(#{Uid}-{name})";
    public string Arrow => $"url(#{Uid}-arrow)";
    public string NodeId(string id) => $"{Uid}-{id}";
}

/// <summary>Calls into wwwroot/js/visuals.js. Every call tolerates a circuit that has ended.</summary>
public sealed class VisualsJs(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    private async ValueTask<IJSObjectReference> ModuleAsync() => _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/Keel.Web/js/visuals.js");

    private async ValueTask CallAsync(string name, params object?[] args)
    {
        try { await (await ModuleAsync()).InvokeVoidAsync(name, args); }
        catch (JSDisconnectedException) { /* the circuit ended */ }
        catch (TaskCanceledException) { /* the circuit ended */ }
        catch (InvalidOperationException) { /* no JavaScript runtime (static rendering) */ }
    }

    public ValueTask AttachAsync(ElementReference root) => CallAsync("attach", root);
    public ValueTask DetachAsync(ElementReference root) => CallAsync("detach", root);
    public ValueTask RefitAsync(ElementReference root) => CallAsync("refit", root);
    public ValueTask FocusFirstAsync(ElementReference root) => CallAsync("focusFirst", root);
    public ValueTask FocusNodeAsync(ElementReference root, string id) => CallAsync("focusNode", root, id);

    /// <summary>True on a phone-width screen (under 600 px), where the table alternative is shown first.</summary>
    public async ValueTask<bool> IsNarrowAsync()
    {
        try { return await (await ModuleAsync()).InvokeAsync<bool>("isNarrow"); }
        catch (JSDisconnectedException) { return false; }
        catch (TaskCanceledException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}
