using Keel.Core;

namespace Keel.Sourcing;

public static class SupplierStatuses
{
    public const string Prospective = "prospective";
    public const string Approved = "approved";
    public const string Conditional = "conditional";
    public const string Disapproved = "disapproved";
    public const string Obsolete = "obsolete";
    public static readonly IReadOnlyList<string> All = [Prospective, Approved, Conditional, Disapproved, Obsolete];

    public static string Label(string status) => status switch
    {
        Prospective => "Prospective", Approved => "Approved", Conditional => "Conditional", Disapproved => "Disapproved", Obsolete => "Obsolete", _ => status,
    };
}

public static class SupplierRoles
{
    public const string Vendor = "vendor";
    public const string Manufacturer = "manufacturer";
    public static readonly IReadOnlyList<string> All = [Vendor, Manufacturer];
}

public static class EntryStatuses
{
    public const string Preferred = "preferred";
    public const string Approved = "approved";
    public const string Alternate = "alternate";
    public const string Disapproved = "disapproved";
    public static readonly IReadOnlyList<string> All = [Preferred, Approved, Alternate, Disapproved];
    public static string Label(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

public static class EntryApprovalStates
{
    public const string Draft = "Draft";
    public const string UnderReview = "UnderReview";
    public const string Active = "Active";
    public const string Withdrawn = "Withdrawn";
    public static string Label(string s) => s == UnderReview ? "Under review" : s;
}

public static class SourceKinds
{
    public const string Aml = "aml";
    public const string Avl = "avl";
    public static string Label(string kind) => kind == Aml ? "Approved manufacturer" : "Approved vendor";
}

public static class RequestKinds
{
    public const string Rfi = "rfi";
    public const string Rfq = "rfq";
    public static string Label(string kind) => kind == Rfi ? "Request for information" : "Request for quotation";
}

public static class RequestStates
{
    public const string Draft = "Draft";
    public const string Issued = "Issued";
    public const string Closed = "Closed";
    public const string Cancelled = "Cancelled";
}

public static class RecipientStates
{
    public const string Sent = "Sent";
    public const string Responded = "Responded";
    public const string Declined = "Declined";
}

public static class PartRelations
{
    public const string Alternate = "alternate";
    public const string Replaces = "replaces";
}

/// <summary>How well a part is sourced (BOM coverage report).</summary>
public static class CoverageBuckets
{
    /// <summary>At least one effective preferred or approved entry whose supplier is approved, in good standing and allowed for the part's markings.</summary>
    public const string Covered = "covered";
    /// <summary>Only alternates, or suppliers that are conditional or have an expired certification.</summary>
    public const string Conditional = "conditional";
    public const string NotCovered = "notcovered";
    public static string Label(string b) => b switch { Covered => "Approved source", Conditional => "Conditional source only", _ => "No approved source" };
}

// ---------------------------------------------------------------- suppliers

public sealed record SupplierSummary(
    Guid Id, string Number, string Name, string? CageCode, string Country, IReadOnlyList<string> Roles, string Status, bool InReview, bool IsForeign,
    string Banner, Markings Markings)
{
    public string StatusLabel => SupplierStatuses.Label(Status);
}

public sealed record CertificationView(
    Guid Id, string Type, string Number, string Issuer, string? Scope, DateOnly IssuedOn, DateOnly ExpiresOn, bool Revoked, string Standing, int DaysLeft);

public sealed record ContactView(Guid Id, string Name, string? Email, string? Phone, string? Title, bool IsPrimary);

public sealed record QualityRefView(Guid Id, string Kind, string RefId, string? Label, string? Href, string? StateLabel);

public sealed record PortalUserView(string Sub, string Username, string DisplayName, bool Active);

public sealed record SupplierPermissions(bool CanModify, bool CanAdminister);

public sealed record SupplierDetail(
    SupplierSummary Summary, string? DunsId, string? ProposedStatus, string? StatusReason, string? ClearanceProfile, int Revision,
    DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? UpdatedAt,
    IReadOnlyList<CertificationView> Certifications, IReadOnlyList<ContactView> Contacts, IReadOnlyList<QualityRefView> QualityRefs,
    IReadOnlyList<PortalUserView> PortalUsers, SupplierPermissions Permissions)
{
    public int ExpiredCertifications => Certifications.Count(c => c.Standing == CertStandings.Expired);
}

public static class CertStandings
{
    public const string Valid = "valid";
    public const string Expiring = "expiring";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
    public static string Label(string s) => s switch { Valid => "Valid", Expiring => "Expiring soon", Expired => "Expired", Revoked => "Revoked", _ => s };
}

public sealed record SupplierSearch(string? Text = null, string? Status = null, string? Role = null, string? Country = null, bool ForeignOnly = false, int Limit = 50, int Offset = 0, SortSpec? Sort = null);

public sealed record SupplierPage(IReadOnlyList<SupplierSummary> Items, int Total, string Banner);

public sealed record CreateSupplierCommand(
    string Name, string Country, IReadOnlyList<string> Roles, bool IsForeign, Markings Markings, string? CageCode = null, string? DunsId = null, string? ClearanceProfile = null);

public sealed record UpdateSupplierCommand(
    string Name, string Country, IReadOnlyList<string> Roles, bool IsForeign, string? CageCode, string? DunsId, string? ClearanceProfile);

public sealed record AddCertificationCommand(string Type, string Number, string Issuer, string? Scope, DateOnly IssuedOn, DateOnly ExpiresOn);

public sealed record ContactInput(string Name, string? Email, string? Phone, string? Title, bool IsPrimary);

// ---------------------------------------------------------------- manufacturer parts and list entries

public sealed record PriceBreak(decimal Quantity, decimal UnitPrice);

/// <summary>One AML or AVL entry as the part's Sourcing tab shows it. <see cref="SupplierName"/> is the manufacturer for an AML entry and the vendor for an AVL entry.</summary>
public sealed record EntryView(
    Guid Id, string Number, string Kind, string PartNumber, string? RevisionScope, string SupplierNumber, string SupplierName, string SupplierStatus, bool SupplierForeign,
    string? Mpn, string? ManufacturerNumber, string? ManufacturerName, string Status, string ApprovalState, DateOnly? EffectiveFrom, DateOnly? EffectiveTo,
    string? VendorPartNumber, int? LeadTimeDays, decimal? Moq, string? Currency, IReadOnlyList<PriceBreak> PriceBreaks, string? Note, string? Reason, bool Effective,
    string CreatedBy, DateTimeOffset CreatedAt, int Revision)
{
    public string StatusLabel => EntryStatuses.Label(Status);
    public string ApprovalLabel => EntryApprovalStates.Label(ApprovalState);
}

public sealed record RelationView(Guid Id, string Relation, string PartNumber, string PartName, string Direction, string? Note, string Banner, Markings Markings);

public sealed record PartSourcing(
    string PartNumber, string PartName, string PartState, string PartBanner, string Banner, IReadOnlyList<EntryView> Entries, IReadOnlyList<RelationView> Relations,
    bool CanModify, string Coverage);

public sealed record CreateEntryCommand(
    string Kind, string PartNumber, string? RevisionScope, string SupplierNumber, string? Mpn, string? ManufacturerNumber, string Status,
    DateOnly? EffectiveFrom = null, DateOnly? EffectiveTo = null, string? VendorPartNumber = null, int? LeadTimeDays = null, decimal? Moq = null,
    string? Currency = null, IReadOnlyList<PriceBreak>? PriceBreaks = null, string? Note = null);

public sealed record UpdateEntryCommand(
    string Status, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, string? VendorPartNumber, int? LeadTimeDays, decimal? Moq, string? Currency,
    IReadOnlyList<PriceBreak>? PriceBreaks, string? Note);

/// <summary>A row of where-sourced: a readable part and the entry that ties it to the supplier or manufacturer part asked about.</summary>
public sealed record WhereSourcedRow(
    string PartNumber, string PartName, string PartState, string EntryNumber, string Kind, string Status, string ApprovalState, string? Mpn,
    string SupplierNumber, string SupplierName, bool Effective, string Banner, Markings Markings);

public sealed record WhereSourcedQuery(string? SupplierNumber = null, string? Mpn = null, string? Text = null, int Limit = 100, int Offset = 0);

public sealed record WhereSourcedResult(IReadOnlyList<WhereSourcedRow> Rows, int Total, string Banner);

/// <summary>The sourcing standing of one part for the coverage report; <see cref="Sources"/> are the effective approved entries (readable ones only).</summary>
public sealed record PartCoverage(string PartNumber, string Source, string Bucket, int Sources, bool Preferred);

// ---------------------------------------------------------------- requests (RFQ-lite)

public sealed record RequestSummary(
    Guid Id, string Number, string Kind, string Title, string State, DateOnly? DueOn, int Parts, int Recipients, int Responded, string Banner, Markings Markings, DateTimeOffset CreatedAt, string CreatedBy);

public sealed record RequestPartView(string PartNumber, string PartName, decimal Quantity, string Unit, string? Note, string Banner);

public sealed record RequestRecipientView(string SupplierNumber, string SupplierName, string State, DateTimeOffset? FirstViewedAt, bool Releasable, string? WithheldReason);

public sealed record ResponseFileView(Guid Id, string Kind, string FileName, string Sha256, long SizeBytes);

public sealed record QuoteLine(string Part, decimal? UnitPrice, string? Currency, int? LeadTimeDays, decimal? Moq);

public sealed record ResponseView(
    Guid Id, string SupplierNumber, string SupplierName, int Seq, bool Declined, string Message, IReadOnlyList<QuoteLine> Quote, DateTimeOffset SubmittedAt, string SubmittedBy,
    IReadOnlyList<ResponseFileView> Files);

public sealed record RequestDetail(
    RequestSummary Summary, string Description, IReadOnlyList<RequestPartView> Parts, IReadOnlyList<RequestRecipientView> Recipients, IReadOnlyList<ResponseView> Responses, bool CanModify);

public sealed record RequestSearch(string? Text = null, string? State = null, int Limit = 50, int Offset = 0, SortSpec? Sort = null);

public sealed record RequestPage(IReadOnlyList<RequestSummary> Items, int Total, string Banner);

public sealed record RequestPartInput(string PartNumber, decimal Quantity, string? Unit = null, string? Note = null);

public sealed record CreateRequestCommand(
    string Kind, string Title, string Description, DateOnly? DueOn, Markings Markings, IReadOnlyList<RequestPartInput> Parts, IReadOnlyList<string> SupplierNumbers);

// ---------------------------------------------------------------- portal (what a supplier user sees)

public sealed record PortalProfile(string SupplierNumber, string SupplierName, string UserName);

public sealed record PortalRequestSummary(string Number, string Kind, string Title, string State, DateOnly? DueOn, string MyState, string Banner);

/// <summary>A request as ONE supplier may see it: only the parts its clearance profile allows, only its own responses.</summary>
public sealed record PortalRequestDetail(
    PortalRequestSummary Summary, string Description, IReadOnlyList<PortalPart> Parts, IReadOnlyList<PortalResponse> Responses, bool CanRespond);

public sealed record PortalPart(string PartNumber, string PartName, decimal Quantity, string Unit, string? Note);

public sealed record PortalResponse(Guid Id, int Seq, bool Declined, string Message, IReadOnlyList<QuoteLine> Quote, DateTimeOffset SubmittedAt, string SubmittedBy, IReadOnlyList<ResponseFileView> Files);

public sealed record PortalResponseInput(string Message, bool Declined, IReadOnlyList<QuoteLine> Quote);

/// <summary>One file of a response. <see cref="Content"/> is read once and streamed to the vault (which scans it).</summary>
public sealed record UploadedFile(string FileName, string Kind, Stream Content);
