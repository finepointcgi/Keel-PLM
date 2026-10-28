using System.Security.Claims;
using Keel.Core;

namespace Keel.Workflow;

public sealed class WorkflowOptions
{
    public const string Section = "Keel:Workflow";

    /// <summary>How recently the signer must have authenticated (<c>auth_time</c>) for a signature to be accepted (A9).</summary>
    public int StepUpMaxAgeSeconds { get; set; } = 300;

    /// <summary>Minimum interval between directory writes for an unchanged user seen on API calls.</summary>
    public int DirectoryRefreshSeconds { get; set; } = 300;

    public TimeSpan StepUpMaxAge => TimeSpan.FromSeconds(StepUpMaxAgeSeconds);
}

/// <summary>E-signature step-up rules (A9, ADR-0007), shared by the service, the REST API and the UI sign-in.</summary>
public static class StepUp
{
    /// <summary>Allowed difference between IdP and application clocks.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>True when the subject authenticated within <paramref name="maxAge"/>. A missing <c>auth_time</c> is never fresh.</summary>
    public static bool IsFresh(Subject subject, DateTimeOffset now, TimeSpan maxAge) =>
        subject.AuthTime is { } t && t <= now + ClockSkew && now - t <= maxAge;

    public enum Verdict
    {
        Accepted,
        /// <summary>There was no prior session, so there is nobody to compare against; it is an ordinary sign-in.</summary>
        NoPriorSession,
        SubjectMismatch,
        MissingAuthTime,
    }

    /// <summary>
    /// Checks the principal produced by a step-up re-authentication against the session it replaces: the same <c>sub</c>
    /// is required (a different person re-authenticating must never take over the session), and the IdP must have
    /// released <c>auth_time</c> (it is required whenever <c>max_age</c> is requested, OIDC Core 3.1.2.1).
    /// </summary>
    public static Verdict Check(ClaimsPrincipal? existing, ClaimsPrincipal fresh)
    {
        var freshSub = fresh.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(freshSub)) return Verdict.SubjectMismatch;
        if (existing?.Identity?.IsAuthenticated == true)
        {
            var oldSub = existing.FindFirst("sub")?.Value;
            if (!string.Equals(oldSub, freshSub, StringComparison.Ordinal)) return Verdict.SubjectMismatch;
        }
        else return fresh.FindFirst("auth_time") is null ? Verdict.MissingAuthTime : Verdict.NoPriorSession;
        return fresh.FindFirst("auth_time") is null ? Verdict.MissingAuthTime : Verdict.Accepted;
    }
}
