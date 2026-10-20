using BusPulse.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace BusPulse.Services;

public enum NotificationKind
{
    Departure,
    Delay
}

public interface INotifier
{
    /// <summary>Shows a desktop notification; never throws and does nothing while notifications are muted.</summary>
    void Notify(string title, string message, NotificationKind kind);
}

/// <summary>Toast notifications with the matching sound. Content is limited to line, stop and time, never addresses.</summary>
public sealed class ToastNotificationService(AppConfig config, SoundService sound, ILogger<ToastNotificationService> logger) : INotifier
{
    public string? CreateToastXml(string title, string message)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return new ToastContentBuilder()
            .AddText(title)
            .AddText(message)
            .GetToastContent()
            .GetContent();
    }

    public void Notify(string title, string message, NotificationKind kind)
    {
        if (config.NotificationsMuted || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
            if (kind == NotificationKind.Delay)
            {
                sound.PlayDelay();
            }
            else
            {
                sound.PlayArrival();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Toast delivery depends on shell registration; a failure must never take the app down.
            logger.LogWarning(exception, "Could not show a toast notification.");
        }
    }
}
