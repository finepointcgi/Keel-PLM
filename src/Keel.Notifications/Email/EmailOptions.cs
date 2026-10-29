using System.Net;
using System.Text.RegularExpressions;

namespace Keel.Notifications;

/// <summary>How the connection to the mail server is protected.</summary>
public enum SmtpSecurity
{
    /// <summary>Plain connection upgraded with STARTTLS before anything sensitive is sent; a server that does not offer it is refused (no silent downgrade). Port 587.</summary>
    StartTls,
    /// <summary>TLS from the first byte (SMTPS). Port 465.</summary>
    ImplicitTls,
    /// <summary>No TLS at all. Refused unless <see cref="EmailOptions.AcknowledgeInsecureTransport"/> is set.</summary>
    None,
}

/// <summary>
/// Configuration section <c>Keel:Notifications:Email</c> (docs/ops/notifications.md, ADR-0032): the optional e-mail channel over an on-premises SMTP relay.
/// Off by default. A mail only ever says that there is a notification and links to the notifications page: it never carries names, numbers, markings or text.
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Keel:Notifications:Email";

    public bool Enabled { get; set; }
    /// <summary>The on-premises SMTP relay. Only private addresses are used unless the name is in <see cref="AllowedHosts"/> or the address in <see cref="AllowedNetworks"/>.</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
    /// <summary>Required to use <see cref="SmtpSecurity.None"/>: the operator states that the network between Keel and the relay is protected another way.</summary>
    public bool AcknowledgeInsecureTransport { get; set; }
    /// <summary>Optional SMTP authentication (PLAIN or LOGIN, only ever sent over TLS unless the insecure transport is acknowledged). Provide the password from the secrets directory or environment.</summary>
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>The sender address of every mail.</summary>
    public string FromAddress { get; set; } = "";
    /// <summary>The address users reach Keel at (for example <c>https://keel.corp.example</c>): the link in the mail is this plus <c>/notifications</c>.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>PEM file with the CA certificates the relay's certificate must chain to. Empty uses the system store.</summary>
    public string CaBundlePath { get; set; } = "";
    /// <summary>Relay host names beyond private addresses: exact names or <c>*.suffix</c>. Empty by default: no external host.</summary>
    public string[] AllowedHosts { get; set; } = [];
    /// <summary>CIDR ranges beyond the private ranges the relay may resolve into.</summary>
    public string[] AllowedNetworks { get; set; } = [];
    /// <summary>Domains a user may receive mail at (exact or <c>*.suffix</c>). Required: users type their own address, and mail must stay inside the organization.</summary>
    public string[] AllowedRecipientDomains { get; set; } = [];
    /// <summary>At most one mail per user in this many minutes, however many notifications arrive (the mail only says "you have a notification").</summary>
    public int MinIntervalMinutes { get; set; } = 10;
    public int MaxAttempts { get; set; } = 6;
    public int TimeoutSeconds { get; set; } = 20;
    public int PollSeconds { get; set; } = 10;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enabled) return errors;
        const string p = "Keel:Notifications:Email:";
        if (string.IsNullOrWhiteSpace(Host)) errors.Add(p + "Host is required when e-mail is enabled.");
        else if (!Regex.IsMatch(Host, "^[A-Za-z0-9.:\\-\\[\\]]{1,253}$", RegexOptions.None, TimeSpan.FromSeconds(1))) errors.Add(p + "Host is not a host name or address.");
        else if (new OnPremHostGuard(this).CheckConfiguredHost(Host) is { } hostProblem) errors.Add(p + "Host: " + hostProblem);
        if (Port is < 1 or > 65535) errors.Add(p + "Port must be between 1 and 65535.");
        if (!Enum.IsDefined(Security)) errors.Add(p + "Security must be StartTls, ImplicitTls or None.");
        else if (Security == SmtpSecurity.None && !AcknowledgeInsecureTransport)
            errors.Add(p + "Security is None: mail would cross the network in clear text. Use StartTls or ImplicitTls, or set AcknowledgeInsecureTransport=true to state that the link to the relay is protected another way.");
        if (string.IsNullOrEmpty(Username) != string.IsNullOrEmpty(Password)) errors.Add(p + "Username and Password must both be set or both be empty.");
        if (!EmailAddress.TryNormalize(FromAddress, out _)) errors.Add(p + "FromAddress must be a plain e-mail address such as keel@corp.example.");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUrl.Query) || !string.IsNullOrEmpty(baseUrl.Fragment) || !string.IsNullOrEmpty(baseUrl.UserInfo))
            errors.Add(p + "BaseUrl must be an absolute http or https address without credentials, query or fragment, for example https://keel.corp.example.");
        if (!string.IsNullOrEmpty(CaBundlePath) && !File.Exists(CaBundlePath)) errors.Add(p + "CaBundlePath does not exist.");
        foreach (var n in AllowedNetworks)
            if (!IPNetwork.TryParse(n, out _)) errors.Add($"{p}AllowedNetworks entry '{n}' is not a CIDR range.");
        if (AllowedRecipientDomains.Length == 0) errors.Add(p + "AllowedRecipientDomains is required: list the domains users may receive mail at (users type their own address).");
        foreach (var d in AllowedRecipientDomains.Concat(AllowedHosts))
            if (!Regex.IsMatch(d, "^(\\*\\.)?[A-Za-z0-9]([A-Za-z0-9.\\-]{0,251}[A-Za-z0-9])?$", RegexOptions.None, TimeSpan.FromSeconds(1))) errors.Add($"{p}'{d}' is not a domain name or *.suffix pattern.");
        if (MinIntervalMinutes is < 0 or > 1440) errors.Add(p + "MinIntervalMinutes must be between 0 and 1440.");
        if (MaxAttempts is < 1 or > 20) errors.Add(p + "MaxAttempts must be between 1 and 20.");
        if (TimeoutSeconds is < 5 or > 120) errors.Add(p + "TimeoutSeconds must be between 5 and 120.");
        if (PollSeconds is < 1 or > 300) errors.Add(p + "PollSeconds must be between 1 and 300.");
        return errors;
    }

    /// <summary>True when mail to the address stays inside the configured recipient domains.</summary>
    public bool IsRecipientAllowed(string normalizedAddress)
    {
        var at = normalizedAddress.LastIndexOf('@');
        return at > 0 && MatchesName(normalizedAddress[(at + 1)..], AllowedRecipientDomains);
    }

    internal static bool MatchesName(string name, IEnumerable<string> patterns) => patterns.Any(p =>
        p.StartsWith("*.", StringComparison.Ordinal)
            ? name.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase) && name.Length > p.Length - 1
            : string.Equals(name, p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Strict, ASCII-only e-mail address handling: no display names, no comments, no quoted forms, never a control character (no header injection).</summary>
public static partial class EmailAddress
{
    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(\\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\\.)+[A-Za-z]{2,63}$", RegexOptions.None, 1000)]
    private static partial Regex Pattern();

    /// <summary>Trims the address, checks it and lowercases the domain. False for anything that is not a plain address of at most 254 characters.</summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = "";
        var s = input?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length is < 6 or > 254 || !Pattern().IsMatch(s)) return false;
        var at = s.LastIndexOf('@');
        if (at is < 1 or > 64) return false;
        normalized = s[..at] + "@" + s[(at + 1)..].ToLowerInvariant();
        return true;
    }
}

/// <summary>
/// "No external hosts by default": the relay must be inside the organization. A literal or resolved address is accepted when it is loopback, private
/// (RFC 1918, unique-local, link-local) or inside an allowed network, or when the host name is on the allow-list; everything else is refused, at start-up for
/// literals and on every connection for resolved names (so a name that later points at the internet is refused too).
/// </summary>
public sealed class OnPremHostGuard(EmailOptions options)
{
    /// <summary>Start-up check of the configured host: only an address literal can be judged before resolving.</summary>
    public string? CheckConfiguredHost(string host)
    {
        var trimmed = host.Trim('[', ']');
        return IPAddress.TryParse(trimmed, out var literal) ? CheckAddress(host, literal) : null;
    }

    /// <summary>The problem with connecting to <paramref name="address"/> (resolved from <paramref name="host"/>), or null when it is allowed.</summary>
    public string? CheckAddress(string host, IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Parse("169.254.169.254"))) return "the cloud metadata address is never a mail relay.";
        if (EmailOptions.MatchesName(host.Trim('[', ']'), options.AllowedHosts)) return null;
        foreach (var n in options.AllowedNetworks)
            if (IPNetwork.TryParse(n, out var net) && net.Contains(address)) return null;
        if (IsOnPremises(address)) return null;
        return $"{address} is not a private address. Mail is only sent to relays inside the organization; list the relay in Keel:Notifications:Email:AllowedHosts or AllowedNetworks to allow it.";
    }

    internal static bool IsOnPremises(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        var b = a.GetAddressBytes();
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        return a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ((b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80));
    }
}
