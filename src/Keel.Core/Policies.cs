namespace Keel.Core;

/// <summary>ASP.NET authorization policy names shared by the REST API and the Blazor UI.</summary>
public static class Policies
{
    public const string AnyPlmRole = "plm.any";
    public const string Auditor = "plm.auditor";
    public const string Admin = "plm.admin";
    /// <summary>The integration pull API (service accounts with the plm-integration role).</summary>
    public const string Integration = "plm.integration";
    /// <summary>The supplier portal (users with the plm-supplier role).</summary>
    public const string Supplier = "plm.supplier";
    /// <summary>Records management (8.6): the records manager, administrators and auditors (who only read).</summary>
    public const string Records = "plm.records";
}
