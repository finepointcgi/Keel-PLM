using System.Collections.Concurrent;
using Keel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Workflow;

public sealed record SignDraft(string? ReasonCode, string? Comment);

/// <summary>
/// Keeps what a signer had typed while they are sent to the IdP for step-up, keyed by task and user. In process memory,
/// one-time, and short-lived: it never travels in a URL (ADR-0014 requires sticky sessions behind a load balancer anyway).
/// </summary>
public sealed class SignDraftStore(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<(Guid Task, string User), (SignDraft Draft, DateTimeOffset At)> _drafts = new();

    public void Put(Guid taskId, string userSub, SignDraft draft)
    {
        var now = clock.GetUtcNow();
        foreach (var stale in _drafts.Where(d => now - d.Value.At > Lifetime).Select(d => d.Key)) _drafts.TryRemove(stale, out _);
        _drafts[(taskId, userSub)] = (draft, now);
    }

    /// <summary>Returns and removes the draft, or null when there is none or it has expired.</summary>
    public SignDraft? Take(Guid taskId, string userSub) =>
        _drafts.TryRemove((taskId, userSub), out var entry) && clock.GetUtcNow() - entry.At <= Lifetime ? entry.Draft : null;
}

public static class WorkflowRegistration
{
    /// <summary>Registers the workflow module. The host must also register one <c>IWorkflowObjectGateway</c> per reviewable object type.</summary>
    public static IServiceCollection AddKeelWorkflow(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(WorkflowOptions.Section).Get<WorkflowOptions>() ?? new WorkflowOptions();
        if (options.StepUpMaxAgeSeconds is < 30 or > 900)
            throw new InvalidOperationException("Keel:Workflow:StepUpMaxAgeSeconds must be between 30 and 900 (A9 default 300).");
        // One setting for the whole site (ADR-0058): Keel:Auth:StepUpAcr, unless Keel:Workflow overrides it.
        if (string.IsNullOrWhiteSpace(options.StepUpRequiredAcr)) options.StepUpRequiredAcr = configuration["Keel:Auth:StepUpAcr"];
        services.AddSingleton(options);
        services.AddSingleton<UserDirectory>();
        services.AddSingleton<WorkflowService>();
        services.AddSingleton<SignDraftStore>();
        services.AddSingleton<Keel.Core.IWorkflowTemplateAdmin, WorkflowTemplateAdmin>(); // 2.2 admin UI
        return services;
    }

    /// <summary>
    /// Refreshes the user directory from authenticated requests (throttled, best effort). Add after authentication.
    /// </summary>
    public static IApplicationBuilder UseKeelUserDirectory(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.User.Identity?.IsAuthenticated == true && SubjectClaims.FromPrincipal(ctx.User) is { } subject)
            await ctx.RequestServices.GetRequiredService<UserDirectory>().TouchAsync(subject, ctx.RequestAborted);
        await next(ctx);
    });
}
