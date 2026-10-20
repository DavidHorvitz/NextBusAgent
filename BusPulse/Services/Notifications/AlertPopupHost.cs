using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace BusPulse.Services.Notifications;

/// <summary>A topmost red banner covering the primary screen; it stays until the user dismisses it.</summary>
internal sealed class AlertPopupHost : Window
{
    public AlertPopupHost(AlertMessage message)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Background = new SolidColorBrush(Color.FromRgb(0xB0, 0x1E, 0x1E));
        Title = message.Title;
        FlowDirection = System.Windows.Application.Current?.MainWindow?.FlowDirection ?? System.Windows.FlowDirection.LeftToRight;

        var dismiss = new Button
        {
            Content = (System.Windows.Application.Current?.TryFindResource("DismissAlert") as string) ?? "Dismiss",
            FontSize = 22,
            Padding = new Thickness(36, 12, 36, 12),
            Margin = new Thickness(0, 36, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsDefault = true,
            IsCancel = true
        };
        dismiss.Click += (_, _) => Close();
        System.Windows.Automation.AutomationProperties.SetName(dismiss, "Dismiss");

        var panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Orientation = Orientation.Vertical,
            Margin = new Thickness(48)
        };
        panel.Children.Add(new TextBlock
        {
            Text = message.Title,
            Foreground = Brushes.White,
            FontSize = 56,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Segoe UI")
        });
        panel.Children.Add(new TextBlock
        {
            Text = message.Message,
            Foreground = Brushes.White,
            FontSize = 28,
            Margin = new Thickness(0, 24, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        });
        panel.Children.Add(dismiss);
        Content = panel;
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Space)
            {
                Close();
            }
        };
        Loaded += (_, _) => dismiss.Focus();
    }
}
