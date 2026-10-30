using System.Net;
using System.Net.Sockets;

namespace Keel.Integration;

/// <summary>
/// SSRF protection for administrator-defined sink URLs (ADR-0025). Default deny: a URL is accepted only when its host name matches the
/// configured allow-list or its address lies in an allowed network. Loopback, link-local, cloud-metadata and unspecified addresses are
/// refused whatever the lists say, unless the development switch is on. The address check runs again on the resolved address when the
/// connection is opened, so a name that resolves somewhere else later (DNS rebinding) is still refused.
/// </summary>
public sealed class SsrfGuard(IntegrationOptions options)
{
    private readonly IPNetwork[] _networks = [.. options.AllowedNetworks.Select(n => IPNetwork.Parse(n))];

    public static bool IsDangerous(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6Multicast
                || address.Equals(IPAddress.Parse("fd00:ec2::254")); // AWS IPv6 instance metadata
        var b = address.GetAddressBytes();
        return b[0] == 0 // "this network"
            || b[0] == 169 && b[1] == 254 // link-local, includes the 169.254.169.254 metadata endpoint
            || b[0] >= 224; // multicast, reserved, broadcast
    }

    public bool HostMatches(string host) => options.AllowedHosts.Any(p =>
        p.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase) && host.Length > p.Length - 1
            : string.Equals(p, host, StringComparison.OrdinalIgnoreCase));

    private bool InAllowedNetwork(IPAddress address) => _networks.Any(n => n.Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));

    /// <summary>Why an address that a host name resolved to may not be connected to; null when it may.</summary>
    public string? CheckAddress(string host, IPAddress address)
    {
        if (!options.AllowDangerousAddresses && IsDangerous(address))
            return $"{address} is a loopback, link-local, metadata or otherwise reserved address; webhooks never connect to those.";
        if (HostMatches(host) || InAllowedNetwork(address)) return null;
        return $"{host} ({address}) is neither an allowed host nor inside an allowed network (Keel:Integration:AllowedHosts / AllowedNetworks).";
    }

    /// <summary>Configuration-time check of a sink URL; returns the messages shown to the administrator (empty when acceptable).</summary>
    public IReadOnlyList<string> CheckUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return ["The URL must be absolute."];
        var errors = new List<string>();
        if (uri.Scheme != Uri.UriSchemeHttps) errors.Add("Only https:// endpoints are allowed.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) errors.Add("The URL must not contain credentials; use a secret name.");
        if (!string.IsNullOrEmpty(uri.Fragment)) errors.Add("The URL must not contain a fragment.");
        if (errors.Count > 0) return errors;

        return CheckHost(uri.Host, "webhooks");
    }

    /// <summary>
    /// Configuration-time check of a host name or address (the target of a message broker sink, or the host of a URL): the same default-deny rules as for URLs.
    /// <paramref name="what"/> names the thing that is refused, for the messages ("webhooks", "broker sinks").
    /// </summary>
    public IReadOnlyList<string> CheckHost(string host, string what)
    {
        var errors = new List<string>();
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            if (CheckAddress(host, literal) is { } problem) errors.Add(problem);
            return errors;
        }
        if (HostMatches(host))
        {
            // The name is allowed; dangerous addresses are still refused when the connection is made.
            return errors;
        }
        if (_networks.Length == 0)
        {
            errors.Add($"The host '{host}' is not in Keel:Integration:AllowedHosts, and no allowed networks are configured: {what} are refused by default.");
            return errors;
        }
        try
        {
            var addresses = Dns.GetHostAddresses(host);
            foreach (var a in addresses)
                if (CheckAddress(host, a) is { } problem) errors.Add(problem);
        }
        catch (SocketException)
        {
            errors.Add($"The host '{host}' is not in Keel:Integration:AllowedHosts and could not be resolved to check it against the allowed networks.");
        }
        return errors;
    }
}
