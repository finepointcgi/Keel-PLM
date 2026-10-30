using Keel.Sourcing;

namespace Keel.Web.Components.Pages.Sourcing;

/// <summary>Wording, classes and links for the sourcing pages.</summary>
public static class SourcingUi
{
    public static string SupplierHref(string number) => $"suppliers/{Uri.EscapeDataString(number)}";
    public static string PartHref(string number) => $"parts/{Uri.EscapeDataString(number)}?tab=sourcing";
    public static string EntryHref(string number) => $"sourcing/entries/{Uri.EscapeDataString(number)}";
    public static string RequestHref(string number) => $"requests/{Uri.EscapeDataString(number)}";
    public static string PortalRequestHref(string number) => $"portal/requests/{Uri.EscapeDataString(number)}";
    public static string FileUrl(string request, Guid file) => $"downloads/requests/{Uri.EscapeDataString(request)}/files/{file:D}";
    public static string PortalFileUrl(string request, Guid file) => $"downloads/portal/requests/{Uri.EscapeDataString(request)}/files/{file:D}";
    public static string PortalDocumentUrl(string request, Guid file) => $"downloads/portal/requests/{Uri.EscapeDataString(request)}/documents/{file:D}";
    public static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes / 1024d / 1024:0.#} MiB";

    public static string StatusClass(string status) => status switch
    {
        SupplierStatuses.Approved => "state-approved",
        SupplierStatuses.Conditional => "state-underreview",
        SupplierStatuses.Prospective => "state-inwork",
        _ => "state-obsolete",
    };

    public static string EntryClass(string approvalState) => approvalState switch
    {
        EntryApprovalStates.Active => "state-approved",
        EntryApprovalStates.UnderReview => "state-underreview",
        EntryApprovalStates.Draft => "state-inwork",
        _ => "state-obsolete",
    };

    public static string BucketClass(string bucket) => bucket switch
    {
        CoverageBuckets.Covered => "state-approved",
        CoverageBuckets.Conditional => "state-underreview",
        _ => "state-obsolete",
    };

    public static string StandingClass(string standing) => standing switch
    {
        CertStandings.Valid => "state-approved",
        CertStandings.Expiring => "state-underreview",
        _ => "state-obsolete",
    };

    public static string RequestClass(string state) => state switch
    {
        RequestStates.Issued => "state-underreview",
        RequestStates.Draft => "state-inwork",
        _ => "state-obsolete",
    };

    public static string Date(DateOnly? d) => d is { } v ? v.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "";

    public static string Countdown(CertificationView c) => c.Standing switch
    {
        CertStandings.Revoked => "revoked",
        CertStandings.Expired => $"expired {-c.DaysLeft} day{(c.DaysLeft == -1 ? "" : "s")} ago",
        _ => $"{c.DaysLeft} day{(c.DaysLeft == 1 ? "" : "s")} left",
    };

    /// <summary>Why a recipient does not get the request, in words.</summary>
    public static string Withheld(string? code) => code switch
    {
        null => "",
        SourcingRules.NoClearanceProfile => "The supplier has no clearance profile.",
        SourcingRules.ForeignSupplierItarPart => "A foreign supplier never receives ITAR-marked data.",
        SourcingRules.SupplierNotActive => "The supplier is disapproved or obsolete.",
        "portal.no_releasable_part" => "None of the parts may be released to this supplier.",
        _ => "The supplier's clearance profile does not allow the request's markings.",
    };
}
