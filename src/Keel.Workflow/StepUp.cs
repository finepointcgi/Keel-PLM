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

    /// <summary>
    /// The <c>acr</c> a signing sign-in must carry (<c>Keel:Auth:StepUpAcr</c>, ADR-0058; "2" is the certificate-only level of the shipped CAC/PIV
    /// Keycloak flow). Empty = any fresh authentication is enough (A9).
    /// </summary>
    public string? StepUpRequiredAcr { get; set; }
}

/// <summary>E-signature step-up rules (A9, ADR-0007), shared by the service, the REST API and the UI sign-in.</summary>
public static class StepUp
{
    /// <summary>Allowed difference between IdP and application clocks.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>True when the subject authenticated within <paramref name="maxAge"/>. A missing <c>auth_time</c> is never fresh.</summary>
    public static bool IsFresh(Subject subject, DateTimeOffset now, TimeSpan maxAge) =>
        subject.AuthTime is { } t && t <= now + ClockSkew && now - t <= maxAge;

    /// <summary>
    /// <see cref="IsFresh(Subject, DateTimeOffset, TimeSpan)"/> and, when the site requires an authentication level for signatures
    /// (<c>Keel:Auth:StepUpAcr</c>, ADR-0058), a sign-in that carries exactly that <c>acr</c>. A password sign-in made seconds ago is
    /// fresh but does not satisfy a required certificate level.
    /// </summary>
    public static bool IsFresh(Subject subject, DateTimeOffset now, TimeSpan maxAge, string? requiredAcr) =>
        IsFresh(subject, now, maxAge) && AcrSatisfied(subject.Acr, requiredAcr);

    /// <summary>True when no level is required, or the released <c>acr</c> equals the required one (ordinal: the IdP's value is an opaque string).</summary>
    public static bool AcrSatisfied(string? releasedAcr, string? requiredAcr) =>
        string.IsNullOrWhiteSpace(requiredAcr) || string.Equals(releasedAcr, requiredAcr.Trim(), StringComparison.Ordinal);

    public enum Verdict
    {
        Accepted,
        /// <summary>There was no prior session, so there is nobody to compare against; it is an ordinary sign-in.</summary>
        NoPriorSession,
        SubjectMismatch,
        MissingAuthTime,
        /// <summary>A level was required (<c>Keel:Auth:StepUpAcr</c>) and the IdP returned another one or none.</summary>
        AcrNotSatisfied,
    }

    /// <summary>
    /// Checks the principal produced by a step-up re-authentication against the session it replaces: the same <c>sub</c>
    /// is required (a different person re-authenticating must never take over the session), and the IdP must have
    /// released <c>auth_time</c> (it is required whenever <c>max_age</c> is requested, OIDC Core 3.1.2.1).
    /// </summary>
    public static Verdict Check(ClaimsPrincipal? existing, ClaimsPrincipal fresh, string? requiredAcr = null)
    {
        var verdict = CheckCore(existing, fresh);
        return verdict is Verdict.Accepted or Verdict.NoPriorSession && !AcrSatisfied(fresh.FindFirst("acr")?.Value, requiredAcr) ? Verdict.AcrNotSatisfied : verdict;
    }

    private static Verdict CheckCore(ClaimsPrincipal? existing, ClaimsPrincipal fresh)
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
