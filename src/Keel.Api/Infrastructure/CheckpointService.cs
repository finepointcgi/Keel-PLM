using Keel.Audit;

namespace Keel.Api.Infrastructure;

/// <summary>Signs the audit chain head every <see cref="KeelOptions.AuditOptions.CheckpointInterval"/> (ADR-0005).</summary>
public sealed class CheckpointService(CheckpointSigner signer, KeelOptions options, ILogger<CheckpointService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Audit.CheckpointInterval);
        do
        {
            try
            {
                var cp = await signer.CreateAsync(stoppingToken);
                if (cp is not null) logger.LogInformation("Audit checkpoint signed at seq {Seq} with key {KeyId}", cp.Seq, cp.KeyId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Audit checkpoint failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
