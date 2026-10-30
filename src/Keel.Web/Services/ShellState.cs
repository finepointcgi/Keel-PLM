using Keel.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Keel.Web.Services;

/// <summary>
/// Lazy access to the shell's browser module (wwwroot/js/shell.js). Every call tolerates a circuit that is going away. Scoped: one module reference per circuit.
/// </summary>
public sealed class ShellJs(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    private async ValueTask<IJSObjectReference> ModuleAsync() => _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/Keel.Web/js/shell.js");

    public async ValueTask InvokeVoidAsync(string name, params object?[] args)
    {
        try { await (await ModuleAsync()).InvokeVoidAsync(name, args); }
        catch (JSDisconnectedException) { /* the circuit ended */ }
        catch (TaskCanceledException) { /* the circuit ended */ }
    }

    public async ValueTask<T?> InvokeAsync<T>(string name, params object?[] args)
    {
        try { return await (await ModuleAsync()).InvokeAsync<T>(name, args); }
        catch (JSDisconnectedException) { return default; }
        catch (TaskCanceledException) { return default; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}

/// <summary>
/// The display preferences of the signed-in user for this circuit (theme, density, navigation collapsed), loaded once from the preference store
/// and written back when the user changes them. Failing to load or save never breaks the shell: the defaults apply and the page keeps working.
/// </summary>
public sealed class ShellState(CurrentUser users, IPreferenceStore workspace, ShellJs js, ILogger<ShellState> logger)
{
    private bool _loaded;

    public UserPreferences Preferences { get; private set; } = UserPreferences.Default;

    public event Action? Changed;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try { Preferences = await workspace.GetPreferencesAsync(await users.GetAsync()); }
        catch (Exception e) when (e is Npgsql.NpgsqlException or InvalidOperationException)
        {
            logger.LogWarning(e, "Display preferences could not be loaded; defaults apply.");
        }
    }

    public Task SetThemeAsync(string theme) => UpdateAsync(Preferences with { Theme = theme });

    public Task SetDensityAsync(string density) => UpdateAsync(Preferences with { Density = density });

    public Task SetNavCollapsedAsync(bool collapsed) => UpdateAsync(Preferences with { NavCollapsed = collapsed }, apply: false);

    /// <summary>Simple view (ADR 0061): the navigation shows the everyday groups only. Display only; access never depends on it.</summary>
    public Task SetSimpleViewAsync(bool simple) => UpdateAsync(Preferences with { SimpleView = simple }, apply: false);

    /// <summary>
    /// The interface language (ADR 0057). Saved for the person; the caller then reloads the page so the new circuit starts in the new culture.
    /// Display only: it changes the words on the screen, never access, markings, banners or the audit log.
    /// </summary>
    public Task SetLanguageAsync(string language) => UpdateAsync(Preferences with { Language = language }, apply: false);

    /// <summary>Cycles light, dark, high contrast (the palette's "Toggle theme"). "System" counts as the theme the OS currently shows, so it moves to light.</summary>
    public Task ToggleThemeAsync() => SetThemeAsync(Preferences.Theme switch
    {
        ThemeNames.Light => ThemeNames.Dark,
        ThemeNames.Dark => ThemeNames.HighContrast,
        _ => ThemeNames.Light,
    });

    private async Task UpdateAsync(UserPreferences next, bool apply = true)
    {
        next = UserPreferences.Normalize(next.Theme, next.Density, next.NavCollapsed, next.SimpleView, next.Language);
        Preferences = next;
        Changed?.Invoke();
        if (apply) await js.InvokeVoidAsync("applyPreferences", next.Theme, next.Density);
        try { await workspace.SetPreferencesAsync(await users.GetAsync(), next); }
        catch (Exception e) when (e is Npgsql.NpgsqlException or InvalidOperationException)
        {
            logger.LogWarning(e, "Display preferences could not be saved.");
        }
    }
}
