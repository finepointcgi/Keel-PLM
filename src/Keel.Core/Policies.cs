namespace Keel.Core;

/// <summary>ASP.NET authorization policy names shared by the REST API and the Blazor UI.</summary>
public static class Policies
{
    public const string AnyPlmRole = "plm.any";
    public const string Auditor = "plm.auditor";
    public const string Admin = "plm.admin";
}
