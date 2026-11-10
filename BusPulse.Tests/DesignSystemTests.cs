using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BusPulse.Themes;
using Xunit;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace BusPulse.Tests;

/// <summary>Runs WPF code on its own STA thread (each test gets a fresh dispatcher) and loads the app's resources.</summary>
internal static class Wpf
{
    public const string Pack = "pack://application:,,,/BusPulse;component/";

    static Wpf()
    {
        // Registers the pack:// scheme and the application package without creating an Application.
        RuntimeHelpers.RunClassConstructor(typeof(System.Windows.Application).TypeHandle);
    }

    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public static ResourceDictionary Theme() => new() { Source = new Uri(Pack + "Themes/Theme.xaml") };

    /// <summary>Lets bindings, templates and layout settle.</summary>
    public static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    public static string RepoFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(parts)} above {AppContext.BaseDirectory}.");
    }
}

/// <summary>Design tokens, measured contrast, line badge colours and the embedded font (docs/DESIGN.md).</summary>
public sealed class DesignSystemTests
{
    private static readonly string[] Scales = ["Neutral", "Accent", "Success", "Warning", "Danger"];

    [Fact]
    public void LightAndDarkSets_DefineExactlyTheSameTokens()
    {
        Wpf.Run(() =>
        {
            var theme = Wpf.Theme();
            var light = ThemeTokens.Read(theme, dark: false);
            var dark = ThemeTokens.Read(theme, dark: true);

            Assert.Equal(light.Keys, dark.Keys);
            foreach (var scale in Scales)
            {
                Assert.All(Enumerable.Range(1, 12), step => Assert.Contains($"{scale}.{step}", light.Keys));
            }

            // Role aliases (StaticResource entries) must resolve to colours, or ThemeTokens would silently skip them.
            Assert.Contains("Text.Primary", light.Keys);
            Assert.Contains("Primary.Hover", dark.Keys);
        });
    }

    [Fact]
    public void Apply_WritesOneBrushPerToken_AndSwitchesWithTheTheme()
    {
        Wpf.Run(() =>
        {
            var theme = Wpf.Theme();
            var target = new ResourceDictionary();

            ThemeTokens.Apply(target, theme, dark: false);
            var light = ((SolidColorBrush)target["Bp.Neutral.1"]).Color;
            ThemeTokens.Apply(target, theme, dark: true);
            var dark = ((SolidColorBrush)target["Bp.Neutral.1"]).Color;

            Assert.Equal(ThemeTokens.Read(theme, false).Count, target.Count);
            Assert.Equal(Color.FromRgb(0xFC, 0xFC, 0xFD), light);
            Assert.Equal(Color.FromRgb(0x11, 0x11, 0x13), dark);
            Assert.True(((SolidColorBrush)target["Bp.Text.Primary"]).IsFrozen);
        });
    }

    [Fact]
    public void FindTokenDictionary_ReturnsTheMergedThemeDictionary_NotTheAppDictionary()
    {
        // Same nesting as App.xaml. ResourceDictionary.Contains searches merged dictionaries, which once made this return the
        // root and the app fail at startup ("The given key 'Accent.9' was not present").
        Wpf.Run(() =>
        {
            var theme = Wpf.Theme();
            var root = new ResourceDictionary();
            root.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(Wpf.Pack + "Resources/Strings.en.xaml") });
            root.MergedDictionaries.Add(theme);

            Assert.Same(theme, ThemeTokens.FindTokenDictionary(root));
            ThemeTokens.Apply(root, ThemeTokens.FindTokenDictionary(root)!, dark: false);
            Assert.IsType<SolidColorBrush>(root["Bp.Accent.9"]);
        });
    }

    /// <summary>
    /// Every text/background pair the styles use, with the WCAG minimum it must meet in both themes. The measured values are
    /// written to docs/DESIGN.md; if a token changes, this test says which pair broke.
    /// </summary>
    public static TheoryData<string, string, double> Pairs => new()
    {
        // Text on the app and subtle backgrounds (the brief's requirement: steps 11 and 12 readable on step 2).
        { "Neutral.11", "Neutral.2", 4.5 },
        { "Neutral.12", "Neutral.2", 7.0 },
        { "Neutral.11", "Neutral.1", 4.5 },
        { "Neutral.12", "Neutral.1", 7.0 },
        // Secondary text on list-row hover and in the expanded departures panel.
        { "Neutral.11", "Neutral.3", 4.5 },
        { "Neutral.12", "Neutral.4", 4.5 },
        // Buttons.
        { "Text.OnSolid", "Primary.Rest", 4.5 },
        { "Text.OnSolid", "Primary.Hover", 4.5 },
        { "Text.OnSolid", "Primary.Pressed", 4.5 },
        { "Neutral.12", "Neutral.5", 4.5 },
        { "Accent.11", "Neutral.3", 4.5 },
        { "Accent.11", "Neutral.4", 4.5 },
        { "Danger.11", "Neutral.3", 4.5 },
        { "Danger.11", "Danger.3", 4.5 },
        { "Danger.12", "Danger.4", 4.5 },
        // Selected suggestion row and checked chip.
        { "Accent.12", "Accent.4", 4.5 },
        { "Accent.12", "Accent.5", 4.5 },
        // Status chips: word on step 3 (text), icon on step 3 (non-text, 3:1).
        { "Success.12", "Success.3", 4.5 },
        { "Warning.12", "Warning.3", 4.5 },
        { "Danger.12", "Danger.3", 4.5 },
        { "Neutral.12", "Neutral.3", 4.5 },
        { "Success.11", "Success.3", 3.0 },
        { "Warning.11", "Warning.3", 3.0 },
        { "Danger.11", "Danger.3", 3.0 },
        // Banners.
        { "Accent.12", "Accent.3", 4.5 },
        // Legacy text brushes on the subtle background (other views until they are rebuilt).
        { "Accent.11", "Neutral.2", 4.5 },
        { "Warning.12", "Neutral.2", 4.5 },
        { "Danger.11", "Neutral.2", 4.5 },
        // Focus ring against the backgrounds it is drawn on (non-text, 3:1).
        { "Focus", "Neutral.1", 3.0 },
        { "Focus", "Neutral.2", 3.0 },
        // White header text on both ends of the header gradient.
        { "Text.OnSolid", "Accent.9", 4.5 },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void TokenPairs_MeetTheirContrastMinimum_InBothThemes(string foreground, string background, double minimum)
    {
        Wpf.Run(() =>
        {
            var theme = Wpf.Theme();
            foreach (var dark in new[] { false, true })
            {
                var tokens = ThemeTokens.Read(theme, dark);
                var ratio = ThemeTokens.Contrast(tokens[foreground], tokens[background]);
                Assert.True(ratio >= minimum, $"{foreground} on {background} ({(dark ? "dark" : "light")}): {ratio:0.00} < {minimum}");
            }
        });
    }

    [Fact]
    public void HeaderGradient_KeepsWhiteTextReadable()
    {
        Wpf.Run(() =>
        {
            var theme = Wpf.Theme();
            var light = ThemeTokens.Read(theme, false);
            var dark = ThemeTokens.Read(theme, true);
            foreach (var color in new[] { light["Accent.12"], light["Accent.9"], dark["Accent.3"], dark["Accent.6"] })
            {
                Assert.True(ThemeTokens.Contrast(Colors.White, color) >= 5.2, color.ToString());
            }
        });
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("12345", "FFFFFF")]
    [InlineData("GGGGGG", null)]
    public void LineBadge_WithoutAUsableRouteColor_IsNeutral(string? routeColor, string? textColor) =>
        Assert.Null(LineBadgeColors.Resolve(routeColor, textColor));

    [Theory]
    // The four colours in the Ministry of Transport feed of 2026-11-10 (it has no route_text_color column, so GTFS black applies).
    [InlineData("FF9933", null, "#FF000000")]
    [InlineData("33CC33", null, "#FF000000")]
    [InlineData("3399FF", null, "#FF000000")]
    [InlineData("9933FF", null, "#FFFFFFFF")] // black on this purple measures 4.27:1, so white (4.92:1)
    // The feed's text colour is kept when it passes, replaced when it does not.
    [InlineData("#003366", "FFFFFF", "#FFFFFFFF")]
    [InlineData("FFFF00", "FFFFFF", "#FF000000")]
    public void LineBadge_UsesFeedColours_AndFallsBackByLuminance(string routeColor, string? textColor, string expectedText)
    {
        var colors = LineBadgeColors.Resolve(routeColor, textColor);

        Assert.NotNull(colors);
        Assert.Equal(expectedText, colors.Value.Text.ToString());
        Assert.True(ThemeTokens.Contrast(colors.Value.Fill, colors.Value.Text) >= LineBadgeColors.MinimumContrast);
    }

    [Fact]
    public void BlackOnFeedPurple_IsBelowTheMinimum()
    {
        var purple = ThemeTokens.ParseHex("9933FF")!.Value;
        Assert.InRange(ThemeTokens.Contrast(purple, Colors.Black), 4.26, 4.28);
    }

    [Fact]
    public void Heebo_IsEmbedded_WithTabularDigits_AndHebrew()
    {
        Wpf.Run(() =>
        {
            var family = (FontFamily)Wpf.Theme()["Bp.Font"];
            foreach (var weight in new[] { FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold, FontWeights.Bold })
            {
                var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
                Assert.True(typeface.TryGetGlyphTypeface(out var glyphs), $"no glyph typeface for {weight}");
                Assert.Contains("Heebo", glyphs.FamilyNames.Values);
                // A real cut, not a synthesized bold.
                Assert.Equal(weight, glyphs.Weight);

                var digitWidths = "0123456789".Select(c => glyphs.AdvanceWidths[glyphs.CharacterToGlyphMap[c]]).Distinct().ToArray();
                Assert.Single(digitWidths);

                foreach (var c in "אבגדהוזחטיכלמנסעפצקרשתךםןףץ")
                {
                    Assert.True(glyphs.CharacterToGlyphMap.ContainsKey(c), $"missing {c}");
                }

                // Geresh (U+05F3) as in "דק׳".
                Assert.True(glyphs.CharacterToGlyphMap.ContainsKey('׳'));
            }
        });
    }

    [Fact]
    public void FontLicence_IsTheOfl_AndShipsWithTheApp()
    {
        var licence = File.ReadAllText(Wpf.RepoFile("BusPulse", "Assets", "Fonts", "OFL.txt"));
        Assert.Contains("Copyright 2014 The Heebo Project Authors", licence);
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", licence);
        var project = File.ReadAllText(Wpf.RepoFile("BusPulse", "BusPulse.csproj"));
        Assert.Contains(@"Assets\Fonts\OFL.txt", project);
    }

    /// <summary>DESIGN.md "do not" list: views on the design system use tokens only.</summary>
    [Theory]
    [InlineData("StopsView.xaml")]
    [InlineData("ShellView.xaml")]
    [InlineData("SettingsView.xaml")]
    [InlineData("TripPlannerView.xaml")]
    public void RebuiltViews_UseNoRawColours(string view)
    {
        var xaml = File.ReadAllText(Wpf.RepoFile("BusPulse", "Views", view));

        Assert.DoesNotMatch(new Regex("#[0-9A-Fa-f]{3,8}\\b"), xaml);
        Assert.DoesNotMatch(new Regex("(Foreground|Background|BorderBrush|Fill|Stroke)=\"(?!\\{|Transparent\")"), xaml);
        Assert.DoesNotContain("SystemColors", xaml);
    }

    [Fact]
    public void Theme_IsMergedAfterTheStringsDictionary()
    {
        // ResourceLocalizer swaps MergedDictionaries[0]; the theme must never take that slot.
        var app = File.ReadAllText(Wpf.RepoFile("BusPulse", "App.xaml"));
        var strings = app.IndexOf("Resources/Strings.en.xaml", StringComparison.Ordinal);
        var theme = app.IndexOf("Themes/Theme.xaml", StringComparison.Ordinal);
        Assert.InRange(strings, 0, theme - 1);
    }
}
