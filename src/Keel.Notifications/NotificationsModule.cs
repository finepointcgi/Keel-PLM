using Keel.Audit;
using Keel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Notifications;

public static class NotificationsModule
{
    /// <summary>
    /// Registers notifications, watch lists, comments and the activity feed (ADR-0030): the sink the owning modules write to (<see cref="INotificationSink"/>), the
    /// in-process hub the bell listens to, the outbox fan-out and retention services (hosted) and the services the UI reads through. The host must also register an
    /// <see cref="IObjectAccess"/> per object kind (parts, documents, changes, baselines, MBOMs) from the modules that own them.
    /// </summary>
    public static IServiceCollection AddKeelNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(NotificationOptions.Section).Get<NotificationOptions>() ?? new NotificationOptions();
        var errors = options.Validate();
        if (errors.Count > 0) throw new InvalidOperationException("Invalid notification configuration: " + string.Join(" ", errors));
        return services.AddKeelNotifications(options);
    }

    public static IServiceCollection AddKeelNotifications(this IServiceCollection services, NotificationOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<NotificationHub>();
        services.AddSingleton<INotificationHub>(sp => sp.GetRequiredService<NotificationHub>());
        services.AddSingleton<NotificationWriter>();
        services.AddSingleton<INotificationSink>(sp => new NotificationSink(sp.GetRequiredService<NotificationWriter>(), sp.GetRequiredService<ILogger<NotificationSink>>()));
        services.AddSingleton<ObjectAccessResolver>();
        services.AddSingleton<NotificationCenter>();
        services.AddSingleton<INotificationCenter>(sp => sp.GetRequiredService<NotificationCenter>());
        services.AddSingleton<WatchService>();
        services.AddSingleton<IWatchService>(sp => sp.GetRequiredService<WatchService>());
        services.AddSingleton<CommentService>();
        services.AddSingleton<ICommentService>(sp => sp.GetRequiredService<CommentService>());
        services.AddSingleton<ActivityService>();
        services.AddSingleton<IActivityFeed>(sp => sp.GetRequiredService<ActivityService>());
        services.AddSingleton<FanOutService>();
        services.AddHostedService(sp => sp.GetRequiredService<FanOutService>());
        services.AddSingleton<RetentionService>();
        services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());
        return services;
    }
}

/// <summary>
/// A delivery channel for notifications. In-app is the only channel built (the bell and the notifications page read the table directly). The e-mail channel is
/// Phase 5: it throws, naming the phase, so nothing can silently pretend to send mail (an on-prem SMTP relay is part of the production operations work).
/// </summary>
public interface INotificationChannel
{
    string Name { get; }

    Task DeliverAsync(string recipientSub, NotificationKind kind, CancellationToken ct);
}

public sealed class EmailNotificationChannel : INotificationChannel
{
    public string Name => "email";

    public Task DeliverAsync(string recipientSub, NotificationKind kind, CancellationToken ct) =>
        throw new NotSupportedException("E-mail delivery of notifications needs an on-prem SMTP relay and is delivered in Phase 5. Notifications are in-app only until then.");
}
