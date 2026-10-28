namespace Keel.Api.Infrastructure;

/// <summary>
/// Reports whether the OS cryptographic module runs in FIPS mode (ADR-0008). .NET on Linux uses the system
/// OpenSSL, which on RHEL in FIPS mode loads only the validated provider; the kernel flag is the authoritative signal.
/// </summary>
public sealed record FipsStatus(bool Enabled, string Detail)
{
    public static FipsStatus Detect()
    {
        if (!OperatingSystem.IsLinux())
            return new(false, $"{Environment.OSVersion.Platform} is not a FIPS-validated deployment platform (dev only).");
        const string flag = "/proc/sys/crypto/fips_enabled";
        try
        {
            var enabled = File.Exists(flag) && File.ReadAllText(flag).Trim() == "1";
            return new(enabled, enabled ? "Kernel FIPS mode enabled." : "Kernel FIPS mode disabled.");
        }
        catch (IOException ex)
        {
            return new(false, $"Could not read {flag}: {ex.Message}");
        }
    }
}
