using Keel.Core;
using Keel.Core.Reports;

namespace Keel.Web.Services;

/// <summary>Wording of the output pages (release packages, BOM exchange, mass change, reports).</summary>
public static class OutputText
{
    public static string Message<T>(Outcome<T> o)
    {
        if (o.Kind == OutcomeKind.Forbidden && o.Errors.Any(e => e.StartsWith("package.unreadable_objects:", StringComparison.Ordinal)))
        {
            var n = o.Errors.First(e => e.StartsWith("package.unreadable_objects:", StringComparison.Ordinal))[27..];
            return $"The package cannot be made: {n} of the objects it needs are not readable by you. It is never made with parts left out.";
        }
        if (o.Kind == OutcomeKind.Forbidden && o.Errors.Contains("signing.dev_certificate_disabled")) return "This host does not offer development certificates. Register the certificate from your own PKI.";
        if (o.Kind == OutcomeKind.Forbidden && o.Errors.Contains("signing.admin_only")) return "Only an administrator manages the signing identity.";
        if (o.Kind == OutcomeKind.StepUpRequired) return "Sign in again to continue. Part of this change needs a fresh sign-in; the objects already changed stay changed.";
        return UiText.Message(o);
    }

    public static string ResultLabel(string result) => result switch
    {
        "pending" => "Waiting", "applied" => "Changed", "failed" => "Failed", "undone" => "Undone", "undo_failed" => "Undo failed", "skipped" => "Skipped", _ => result,
    };

    public static string OperationLabel(string op) => op switch
    {
        MassOperations.SetAttribute => "Change an attribute",
        MassOperations.ChangeMarkings => "Change markings",
        MassOperations.BaselineProposal => "Add to a baseline proposal",
        MassOperations.SubmitReview => "Submit for review",
        MassOperations.Watch => "Watch",
        MassOperations.Favorite => "Add to favorites",
        _ => op,
    };
}
