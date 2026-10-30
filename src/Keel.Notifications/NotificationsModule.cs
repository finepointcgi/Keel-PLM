using Keel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        // E-mail channel (ADR-0032): registered always, active only with Keel:Notifications:Email:Enabled.
        services.AddSingleton(new OnPremHostGuard(options.Email));
        services.AddSingleton<SmtpSender>(sp => new SmtpSender(options.Email, sp.GetRequiredService<OnPremHostGuard>()));
        services.AddSingleton<EmailNotificationChannel>();
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<EmailNotificationChannel>());
        services.AddSingleton<EmailBounceService>();
        services.AddSingleton<EmailVerificationService>();
        services.AddSingleton<IEmailVerification>(sp => sp.GetRequiredService<EmailVerificationService>());
        services.AddSingleton<BounceMailboxReader>();
        services.AddHostedService(sp => sp.GetRequiredService<BounceMailboxReader>());
        services.AddSingleton<EmailDispatchService>();
        services.AddHostedService(sp => sp.GetRequiredService<EmailDispatchService>());
        services.AddSingleton<EmailPreferenceService>();
        services.AddSingleton<IEmailPreferences>(sp => sp.GetRequiredService<EmailPreferenceService>());
        return services;
    }
}
