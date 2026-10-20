using BusPulse.Configuration;
using BusPulse.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace BusPulse.Services.Notifications;

/// <summary>Shows trip alerts. A new toast replaces the previous one (same tag), so alerts never stack.</summary>
public interface IAlertPresenter
{
    void ShowToast(AlertMessage message);

    void ShowFullScreen(AlertMessage message);

    /// <summary>Removes the trip toast and closes any full-screen alert.</summary>
    void DismissAll();
}

public sealed class WindowsAlertPresenter(
    AppConfig config,
    SoundService sound,
    IDispatcher dispatcher,
    ILogger<WindowsAlertPresenter> logger) : IAlertPresenter
{
    private const string TripTag = "buspulse-trip";
    private const string TripGroup = "buspulse";
    private AlertPopupHost? _popup;

    public void ShowToast(AlertMessage message)
    {
        if (config.NotificationsMuted)
        {
            return;
        }

        try
        {
            var builder = new ToastContentBuilder().AddText(message.Title).AddText(message.Message);
            if (message.Urgency == AlertUrgency.Red)
            {
                // Alarm scenario keeps the toast on screen and bypasses focus assist.
                builder.SetToastScenario(ToastScenario.Alarm);
            }
            else if (message.Urgency == AlertUrgency.Amber)
            {
                builder.SetToastScenario(ToastScenario.Reminder);
            }

            builder.Show(toast =>
            {
                toast.Tag = TripTag;
                toast.Group = TripGroup;
            });
            if (message.Urgency != AlertUrgency.Normal)
            {
                sound.PlayDelay();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Could not show the trip toast.");
        }
    }

    public void ShowFullScreen(AlertMessage message)
    {
        if (config.NotificationsMuted)
        {
            return;
        }

        dispatcher.Post(() =>
        {
            try
            {
                _popup?.Close();
                _popup = new AlertPopupHost(message);
                _popup.Show();
                sound.PlayArrival();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning(exception, "Could not show the full-screen alert.");
            }
        });
    }

    public void DismissAll()
    {
        try
        {
            ToastNotificationManagerCompat.History.Remove(TripTag, TripGroup);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogDebug(exception, "Could not clear the trip toast.");
        }

        dispatcher.Post(() =>
        {
            _popup?.Close();
            _popup = null;
        });
    }
}
