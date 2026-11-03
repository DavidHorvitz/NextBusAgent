using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace BusPulse.Infrastructure;

/// <summary>Applies the light, dark or system theme to the whole app.</summary>
public interface IThemeService
{
    /// <summary>Theme is "system", "light" or "dark"; anything else is treated as "system".</summary>
    void Apply(string theme);

    /// <summary>Starts following the operating system theme for this window when the theme is "system".</summary>
    void Attach(Window window);
}

public sealed class WpfUiThemeService : IThemeService
{
    private static readonly Color LightAccent = Color.FromRgb(0x14, 0x7D, 0x70);
    private static readonly Color DarkAccent = Color.FromRgb(0x3F, 0xC8, 0xB4);

    private Window? _window;

    public WpfUiThemeService()
    {
        ApplicationThemeManager.Changed += (theme, _) => ApplyBrushes(theme == ApplicationTheme.Dark);
    }
    private string _theme = "system";

    public void Attach(Window window)
    {
        _window = window;
        Apply(_theme);
    }

    public void Apply(string theme)
    {
        _theme = theme is "light" or "dark" ? theme : "system";
        if (_window is not null)
        {
            SystemThemeWatcher.UnWatch(_window);
        }

        switch (_theme)
        {
            case "light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case "dark":
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                if (_window is not null)
                {
                    SystemThemeWatcher.Watch(_window);
                }

                break;
        }

        ApplyBrushes(ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark);
    }

    /// <summary>
    /// The app's own brushes (accent, badges, warnings, header) for the current theme. Also runs when Windows switches theme
    /// while "system" is selected, because WPF-UI then changes only its own resources.
    /// </summary>
    private static void ApplyBrushes(bool dark)
    {
        var accent = dark ? DarkAccent : LightAccent;
        ApplicationAccentColorManager.Apply(accent, dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
        var resources = Application.Current.Resources;
        resources["AccentBrush"] = new SolidColorBrush(accent);
        resources["HeaderGradientBrush"] = Gradient(dark ? 0x072E2A : 0x0B4F47, dark ? 0x0E5249 : 0x137A6D);
        resources["LiveBadgeBrush"] = Brush(dark ? 0x1F4A44 : 0xD8F1EC);
        resources["ScheduledBadgeBrush"] = Brush(dark ? 0x4A3A14 : 0xFFF1D6);
        resources["WarningTextBrush"] = Brush(dark ? 0xFFC766 : 0x8A5A00);
        resources["ErrorTextBrush"] = Brush(dark ? 0xFF8A8A : 0xB01E1E);
    }

    private static LinearGradientBrush Gradient(int from, int to) => new(
        Color.FromRgb((byte)(from >> 16), (byte)(from >> 8), (byte)from),
        Color.FromRgb((byte)(to >> 16), (byte)(to >> 8), (byte)to),
        new System.Windows.Point(0, 0),
        new System.Windows.Point(1, 1));

    private static SolidColorBrush Brush(int rgb) =>
        new(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
}
