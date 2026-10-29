using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Keel.Core;
using Keel.Security;

namespace Keel.Sourcing;

/// <summary>
/// The pure rules of sourcing (ADR-0037): validation, status changes, certification standing, the export-control rule for foreign suppliers, the release
/// check for the supplier portal and the coverage classification. No database, no clock: the services pass what the rules need.
/// </summary>
public static partial class SourcingRules
{
    // Reason codes are stable identifiers: they are shown in messages and written to the audit log.
    public const string ForeignSupplierItarPart = "sourcing.foreign_supplier_itar_part";
    public const string NoClearanceProfile = "portal.no_clearance_profile";
    public const string SupplierNotActive = "portal.supplier_not_active";

    public const int CertificationWarnDays = 60;

    // ---------------------------------------------------------------- export control

    /// <summary>
    /// The export-control rule of the supplier master: a supplier flagged foreign is never selected for an ITAR-marked part. There is no override and no export
    /// authorization exception here (the portal clearance profile has its own rule). Returns the reason code, or null when the pairing is allowed.
    /// </summary>
    public static string? CheckSupplierForPart(bool supplierIsForeign, Markings partMarkings) =>
        supplierIsForeign && partMarkings.ExportJurisdiction == ExportJurisdiction.Itar ? ForeignSupplierItarPart : null;

    /// <summary>
    /// May data with these markings go to this supplier through the portal? Needs the supplier's clearance profile (none = nothing may be released), the hard
    /// foreign-supplier rule for ITAR data, and the outbound release check of the integration module against the profile.
    /// </summary>
    public static OutboundDecision ReleaseToSupplier(bool supplierIsForeign, ClearanceProfile? profile, Markings markings)
    {
        if (profile is null) return new OutboundDecision(false, [NoClearanceProfile]);
        if (CheckSupplierForPart(supplierIsForeign, markings) is { } code) return new OutboundDecision(false, [code]);
        return OutboundRelease.Evaluate(profile, markings);
    }

    // ---------------------------------------------------------------- suppliers

    [GeneratedRegex("^[0-9A-Z]{5}$")] private static partial Regex CagePattern();
    [GeneratedRegex("^[0-9]{9}$")] private static partial Regex DunsPattern();
    [GeneratedRegex("^[A-Z]{3}$")] private static partial Regex CountryPattern();
    [GeneratedRegex("^[A-Z0-9][A-Z0-9 ._-]{1,29}$")] private static partial Regex CertTypePattern();
    [GeneratedRegex("^[A-Z]{3}$")] private static partial Regex CurrencyPattern();

    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static IReadOnlyList<string> ValidateSupplier(string name, string country, IReadOnlyList<string> roles, bool isForeign, string? cage, string? duns)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) errors.Add("The name is required and at most 200 characters.");
        if (!CountryPattern().IsMatch(country ?? "")) errors.Add("The country is an ISO 3166 alpha-3 code such as USA or DEU.");
        else if (country != "USA" && !isForeign) errors.Add("A supplier outside the USA must be flagged foreign.");
        if (roles.Count == 0 || roles.Any(r => !SupplierRoles.All.Contains(r))) errors.Add("Choose at least one role: vendor, manufacturer or both.");
        if (cage is not null && !CagePattern().IsMatch(cage)) errors.Add("The CAGE code is five letters or digits.");
        if (duns is not null && !DunsPattern().IsMatch(duns)) errors.Add("The company identifier is nine digits.");
        return errors;
    }

    public static IReadOnlyList<string> ValidateCertification(AddCertificationCommand c)
    {
        var errors = new List<string>();
        if (!CertTypePattern().IsMatch(c.Type)) errors.Add("The certification type is 2 to 30 capital letters, digits, blanks or . _ - (for example AS9100 or NADCAP).");
        if (string.IsNullOrWhiteSpace(c.Number) || c.Number.Length > 80) errors.Add("The certificate number is required (at most 80 characters).");
        if (string.IsNullOrWhiteSpace(c.Issuer) || c.Issuer.Length > 120) errors.Add("The issuer is required (at most 120 characters).");
        if (c.Scope is { Length: > 500 }) errors.Add("The scope is at most 500 characters.");
        if (c.ExpiresOn < c.IssuedOn) errors.Add("The certificate cannot expire before it was issued.");
        return errors;
    }

    public static IReadOnlyList<string> ValidateContact(ContactInput c)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 120) errors.Add("The contact name is required (at most 120 characters).");
        if (c.Email is { } e && (e.Length > 200 || !EmailPattern().IsMatch(e))) errors.Add("The e-mail address is not valid.");
        if (c.Phone is { Length: > 40 }) errors.Add("The phone number is at most 40 characters.");
        if (c.Title is { Length: > 120 }) errors.Add("The title is at most 120 characters.");
        return errors;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+$")] private static partial Regex EmailPattern();

    /// <summary>
    /// A status that a review may grant: approved or conditional, from prospective, disapproved or the other granted status. Approval needs a certification
    /// in good standing; conditional does not (that is what makes it conditional).
    /// </summary>
    public static string? CheckProposal(string current, string proposed, int validCertifications)
    {
        if (proposed is not (SupplierStatuses.Approved or SupplierStatuses.Conditional)) return "Only approved or conditional can be granted by a review.";
        if (current == SupplierStatuses.Obsolete) return "An obsolete supplier cannot be approved again.";
        if (current == proposed) return $"The supplier is already {SupplierStatuses.Label(current).ToLowerInvariant()}.";
        if (proposed == SupplierStatuses.Approved && validCertifications == 0) return "A supplier needs at least one valid certification to be approved. Add a certification or propose conditional.";
        return null;
    }

    /// <summary>Withdrawing standing needs no signature (it is always the safe direction): disapprove or make obsolete, with a reason.</summary>
    public static string? CheckDirectStatus(string current, string target, string? reason)
    {
        if (target is not (SupplierStatuses.Disapproved or SupplierStatuses.Obsolete)) return "Only disapproved or obsolete can be set directly; approval needs a review.";
        if (current == SupplierStatuses.Obsolete) return "An obsolete supplier cannot change status.";
        if (current == target) return $"The supplier is already {SupplierStatuses.Label(current).ToLowerInvariant()}.";
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000) return "A reason is required (at most 1000 characters).";
        return null;
    }

    // ---------------------------------------------------------------- certifications

    public static string CertStanding(DateOnly expiresOn, bool revoked, DateOnly today) =>
        revoked ? CertStandings.Revoked
        : expiresOn < today ? CertStandings.Expired
        : expiresOn.DayNumber - today.DayNumber <= CertificationWarnDays ? CertStandings.Expiring
        : CertStandings.Valid;

    /// <summary>The notice stage a certification has reached: none more than 60 days ahead, then d60, d30, d7 and expired.</summary>
    public static string? NoticeStage(DateOnly expiresOn, DateOnly today)
    {
        var left = expiresOn.DayNumber - today.DayNumber;
        return left < 0 ? "expired" : left <= 7 ? "d7" : left <= 30 ? "d30" : left <= CertificationWarnDays ? "d60" : null;
    }

    // ---------------------------------------------------------------- list entries

    public static IReadOnlyList<string> ValidateEntry(
        string kind, string status, DateOnly? from, DateOnly? to, string? vendorPartNumber, int? leadTimeDays, decimal? moq, string? currency, IReadOnlyList<PriceBreak>? breaks, string? note,
        string? revisionScope)
    {
        var errors = new List<string>();
        if (kind is not (SourceKinds.Aml or SourceKinds.Avl)) errors.Add("The list is aml or avl.");
        if (!EntryStatuses.All.Contains(status)) errors.Add("The status is preferred, approved, alternate or disapproved.");
        if (from is { } f && to is { } t && t < f) errors.Add("The end date cannot be before the start date.");
        if (vendorPartNumber is { Length: > 60 }) errors.Add("The vendor part number is at most 60 characters.");
        if (leadTimeDays is < 0 or > 3650) errors.Add("The lead time is 0 to 3650 days.");
        if (moq is <= 0) errors.Add("The minimum order quantity must be positive.");
        if (currency is not null && !CurrencyPattern().IsMatch(currency)) errors.Add("The currency is an ISO 4217 code such as USD.");
        if ((breaks?.Count ?? 0) > 0 && currency is null) errors.Add("Price breaks need a currency.");
        if (note is { Length: > 500 }) errors.Add("The note is at most 500 characters.");
        if (revisionScope is not null && !RevisionPattern().IsMatch(revisionScope)) errors.Add("The revision is one to three capital letters.");
        errors.AddRange(ValidatePriceBreaks(breaks));
        return errors;
    }

    [GeneratedRegex("^[A-Z]{1,3}$")] private static partial Regex RevisionPattern();

    public static IReadOnlyList<string> ValidatePriceBreaks(IReadOnlyList<PriceBreak>? breaks)
    {
        var errors = new List<string>();
        if (breaks is null) return errors;
        if (breaks.Count > 20) errors.Add("At most 20 price breaks.");
        for (var i = 0; i < breaks.Count; i++)
        {
            if (breaks[i].Quantity <= 0) errors.Add($"Price break {i + 1}: the quantity must be positive.");
            if (breaks[i].UnitPrice < 0) errors.Add($"Price break {i + 1}: the price cannot be negative.");
            if (i > 0 && breaks[i].Quantity <= breaks[i - 1].Quantity) errors.Add($"Price break {i + 1}: quantities must increase.");
        }
        return errors;
    }

    /// <summary>An entry counts for sourcing only while Active and inside its dates.</summary>
    public static bool IsEffective(string approvalState, DateOnly? from, DateOnly? to, DateOnly today) =>
        approvalState == EntryApprovalStates.Active && (from is null || from <= today) && (to is null || to >= today);

    /// <summary>What the coverage classification needs to know about one entry. Entries that the viewer cannot read must not be passed in.</summary>
    public sealed record EntryFacts(
        string Status, bool Effective, string SupplierStatus, bool SupplierHasExpiredCertification, bool SupplierForeign, string? RevisionScope);

    /// <summary>
    /// The sourcing bucket of one part (MBOM, ADR-0037): covered by an effective preferred or approved entry of an approved supplier in good standing; else
    /// conditional when something effective remains (alternates, conditional suppliers, expired certification); else not covered. A foreign supplier never
    /// counts for an ITAR part, whatever the data says.
    /// </summary>
    public static string Classify(IEnumerable<EntryFacts> entries, Markings partMarkings, string? revision)
    {
        var covered = false;
        var conditional = false;
        foreach (var e in entries)
        {
            if (!e.Effective || e.Status == EntryStatuses.Disapproved) continue;
            if (e.RevisionScope is not null && e.RevisionScope != revision) continue;
            if (CheckSupplierForPart(e.SupplierForeign, partMarkings) is not null) continue;
            if (e.SupplierStatus is not (SupplierStatuses.Approved or SupplierStatuses.Conditional)) continue;
            var strong = e.Status is EntryStatuses.Preferred or EntryStatuses.Approved && e.SupplierStatus == SupplierStatuses.Approved && !e.SupplierHasExpiredCertification;
            if (strong) covered = true; else conditional = true;
        }
        return covered ? CoverageBuckets.Covered : conditional ? CoverageBuckets.Conditional : CoverageBuckets.NotCovered;
    }

    // ---------------------------------------------------------------- requests

    public static IReadOnlyList<string> ValidateRequest(CreateRequestCommand c)
    {
        var errors = new List<string>();
        if (c.Kind is not (RequestKinds.Rfi or RequestKinds.Rfq)) errors.Add("The request is a request for information (rfi) or quotation (rfq).");
        if (string.IsNullOrWhiteSpace(c.Title) || c.Title.Trim().Length > 200) errors.Add("The title is required (at most 200 characters).");
        if ((c.Description?.Length ?? 0) > 4000) errors.Add("The description is at most 4000 characters.");
        if (c.Parts.Count == 0) errors.Add("Choose at least one part.");
        if (c.Parts.Count > 200) errors.Add("At most 200 parts per request.");
        if (c.Parts.Select(p => p.PartNumber.Trim()).Distinct(StringComparer.Ordinal).Count() != c.Parts.Count) errors.Add("A part can be listed once.");
        if (c.Parts.Any(p => p.Quantity <= 0)) errors.Add("Quantities must be positive.");
        if (c.SupplierNumbers.Count == 0) errors.Add("Choose at least one supplier.");
        if (c.SupplierNumbers.Distinct(StringComparer.Ordinal).Count() != c.SupplierNumbers.Count) errors.Add("A supplier can be listed once.");
        return errors;
    }

    public static IReadOnlyList<string> ValidateResponse(PortalResponseInput input, IReadOnlyCollection<string> requestedParts)
    {
        var errors = new List<string>();
        if (input.Message.Length > 4000) errors.Add("The message is at most 4000 characters.");
        if (input.Declined && input.Quote.Count > 0) errors.Add("A declined response carries no quote.");
        if (!input.Declined && input.Message.Trim().Length == 0 && input.Quote.Count == 0) errors.Add("Write a message or enter a quote.");
        foreach (var q in input.Quote)
        {
            if (!requestedParts.Contains(q.Part)) errors.Add($"{q.Part} is not part of this request.");
            if (q.UnitPrice is < 0) errors.Add($"{q.Part}: the price cannot be negative.");
            if (q.Currency is not null && !CurrencyPattern().IsMatch(q.Currency)) errors.Add($"{q.Part}: the currency is an ISO 4217 code.");
            if (q.LeadTimeDays is < 0 or > 3650) errors.Add($"{q.Part}: the lead time is 0 to 3650 days.");
            if (q.Moq is <= 0) errors.Add($"{q.Part}: the minimum order quantity must be positive.");
        }
        if (input.Quote.Select(q => q.Part).Distinct(StringComparer.Ordinal).Count() != input.Quote.Count) errors.Add("A part can be quoted once.");
        return errors;
    }

    /// <summary>Builds a clearance profile from stored columns (the integration module owns the table; sourcing only reads it).</summary>
    public static ClearanceProfile Profile(
        string name, string category, bool usOnly, IEnumerable<string> jurisdictions, IEnumerable<string> eccns, IEnumerable<string> usml, IEnumerable<string> cui,
        IEnumerable<string> programs, IEnumerable<string> statements, IEnumerable<string> authorizations) =>
        new(name, Enum.Parse<RecipientCategory>(category), usOnly, jurisdictions.ToImmutableSortedSet(StringComparer.Ordinal), eccns.ToImmutableSortedSet(StringComparer.Ordinal),
            usml.ToImmutableSortedSet(StringComparer.Ordinal), cui.ToImmutableSortedSet(StringComparer.Ordinal), programs.ToImmutableSortedSet(StringComparer.Ordinal),
            statements.Select(Enum.Parse<DistributionStatement>).ToImmutableSortedSet(), authorizations.ToImmutableSortedSet(StringComparer.Ordinal));
}
