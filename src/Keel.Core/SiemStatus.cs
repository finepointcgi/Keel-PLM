namespace Keel.Core;

/// <summary>Delivery state of one SIEM destination (ADR-0026). Lag is how far behind the audit log the destination is.</summary>
public sealed record SiemDestinationStatus(
    string Name,
    string Type,
    long LastForwardedSeq,
    long HeadSeq,
    long LagEvents,
    double LagSeconds,
    int QueuedEvents,
    bool Connected,
    long ConsecutiveFailures,
    string? LastError,
    bool ChainBroken,
    string? ChainBreakNote);

/// <summary>Whether forwarding is configured and how each destination is doing; read by the health check, the metrics and the admin page.</summary>
public sealed record SiemStatus(bool Enabled, IReadOnlyList<SiemDestinationStatus> Destinations);

/// <summary>Implemented by Keel.Siem. When forwarding is not configured the status says so (<see cref="SiemStatus.Enabled"/> false).</summary>
public interface ISiemStatus
{
    SiemStatus Current();
}
