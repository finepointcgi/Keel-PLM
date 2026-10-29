using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Keel.Web.Services.Demo;

/// <summary>
/// The guarded demo mode (ADR-0045): a local sign-in page replaces the identity provider so one self-contained container can be
/// shown without Keycloak. It exists only when BOTH <c>ASPNETCORE_ENVIRONMENT=Demo</c> and <c>Keel:Demo:Enabled=true</c> are set;
/// <c>Enabled=true</c> in any other environment stops the host from starting. Nothing in this folder is reachable otherwise.
/// </summary>
public static class DemoMode
{
    public const string EnvironmentName = "Demo";
    public const string EnabledKey = "Keel:Demo:Enabled";
    public const string BannerText = "DEMO: synthetic data only, anyone can sign in as any demo user, resets when the instance restarts";
    public const string SignInPath = "/account/demo-signin";

    /// <summary>True when demo mode is on. Throws when it is requested outside the Demo environment, so a stray setting can never switch it on in production.</summary>
    public static bool IsEnabled(IConfiguration configuration, string environmentName)
    {
        var requested = configuration.GetValue<bool>(EnabledKey);
        var demoEnvironment = string.Equals(environmentName, EnvironmentName, StringComparison.Ordinal);
        if (requested && !demoEnvironment)
            throw new InvalidOperationException(
                $"{EnabledKey} is true but the environment is '{environmentName}'. Demo sign-in (no passwords, anyone can be any user) requires ASPNETCORE_ENVIRONMENT=Demo as well; refusing to start.");
        return requested && demoEnvironment;
    }
}

/// <summary>Registered only when demo mode is on; its presence is what the layout and the endpoints check.</summary>
public sealed class DemoModeInfo(string banner)
{
    public string Banner { get; } = banner;
}

/// <summary>
/// Refuses to start against a database that holds anything other than demo data: a directory entry or an audit actor that is not one of the
/// synthetic users means a real system was pointed at the demo build.
/// </summary>
public sealed class DemoDatabaseGuard(NpgsqlDataSource dataSource) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var foreign = await ForeignIdentitiesAsync(dataSource, cancellationToken);
        if (foreign.Count > 0)
            throw new InvalidOperationException(
                "Demo mode refuses to run against a database that already has non-demo data: found identities that are not demo users ("
                + string.Join(", ", foreign.Take(5)) + "). Use an empty database; the demo seeds its own.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Subjects in the user directory or the audit log that are neither a demo user nor the system.</summary>
    public static async Task<IReadOnlyList<string>> ForeignIdentitiesAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        var known = DemoUsers.KnownSubjects;
        var foreign = new List<string>();
        foreach (var sql in new[] { "SELECT sub FROM identity.user_directory", "SELECT DISTINCT actor FROM audit.event" })
        {
            await using var cmd = dataSource.CreateCommand(sql);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                if (!known.Contains(id) && !id.StartsWith("system:", StringComparison.Ordinal) && !foreign.Contains(id)) foreign.Add(id);
            }
        }
        return foreign;
    }
}
