using System.Windows;
using System.Windows.Media;
using BusPulse.Themes;
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
    /// Older brush keys still used by the views that were not rebuilt yet, mapped onto design tokens (DESIGN.md, "Legacy
    /// brushes"). AccentBrush is used as text, so it is accent step 11, which reads on the backgrounds in both themes.
    /// </summary>
    private static readonly (string Legacy, string Token)[] LegacyBrushes =
    [
        ("AccentBrush", "Accent.11"),
        ("LiveBadgeBrush", "Success.3"),
        ("ScheduledBadgeBrush", "Neutral.3"),
        ("WarningTextBrush", "Warning.12"),
        ("ErrorTextBrush", "Danger.11"),
    ];

    /// <summary>
    /// The app's own brushes (design tokens, legacy keys, header) for the current theme. Also runs when Windows switches
    /// theme while "system" is selected, because WPF-UI then changes only its own resources.
    /// </summary>
    private static void ApplyBrushes(bool dark)
    {
        var resources = Application.Current.Resources;
        if (ThemeTokens.FindTokenDictionary(resources) is not { } tokenDictionary)
        {
            return;
        }

        var tokens = ThemeTokens.Read(tokenDictionary, dark);
        ThemeTokens.Apply(resources, tokenDictionary, dark);
        ApplicationAccentColorManager.Apply(tokens["Accent.9"], dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
        foreach (var (legacy, token) in LegacyBrushes)
        {
            resources[legacy] = resources[ThemeTokens.Prefix + token];
        }

        // White header text: accent 12 -> 9 in light, accent 3 -> 6 in dark (white measured at least 5.2:1 on all four).
        resources["HeaderGradientBrush"] = Gradient(tokens[dark ? "Accent.3" : "Accent.12"], tokens[dark ? "Accent.6" : "Accent.9"]);
    }

    private static LinearGradientBrush Gradient(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
        brush.Freeze();
        return brush;
    }
}
