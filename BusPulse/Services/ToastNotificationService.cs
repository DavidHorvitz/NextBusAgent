using Microsoft.Toolkit.Uwp.Notifications;

namespace BusPulse.Services;

public sealed class ToastNotificationService
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
}