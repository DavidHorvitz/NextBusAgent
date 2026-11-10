using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BusPulse.Services.Location;
using BusPulse.Themes;
using BusPulse.ViewModels;
using BusPulse.Views;
using BusPulse.Views.Controls;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Xunit;
using Expander = System.Windows.Controls.Expander;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;
using Window = System.Windows.Window;

namespace BusPulse.Tests;

/// <summary>
/// Renders the shell (navigation, the three tab views, the settings side panel) in a real but off-screen window, with made-up
/// sample data, in Hebrew and English, light and dark. The title bar (with the gear) belongs to MainWindow and is not part of
/// this render. PNGs are written only when BUSPULSE_SCREENSHOT_DIR is set (docs/screens/session-1).
/// </summary>
[Collection(WpfAppCollection.Name)]
public sealed class ShellRenderTests(WpfAppFixture app)
{
    [Theory]
    [InlineData("he", false, 0)]
    [InlineData("he", true, 0)]
    [InlineData("en", false, 0)]
    [InlineData("en", true, 0)]
    [InlineData("he", false, 1)]
    [InlineData("he", true, 1)]
    [InlineData("en", false, 1)]
    [InlineData("en", true, 1)]
    [InlineData("he", false, 2)]
    [InlineData("he", true, 2)]
    [InlineData("en", false, 2)]
    [InlineData("en", true, 2)]
    public void Shell_ShowsEachTab(string language, bool dark, int tab)
    {
        app.Run(() =>
        {
            var (window, shell, view) = Build(language, dark);
            try
            {
                shell.SelectTabCommand.Execute(tab);
                Pump();

                Assert.False(shell.IsSettingsOpen);
                Assert.Equal(Visibility.Collapsed, Panel(view).Visibility);
                var expected = new[] { typeof(StopsView), typeof(TripPlannerView), typeof(LinesView) }[tab];
                Assert.Contains(Descendants<FrameworkElement>(view), e => e.GetType() == expected && e.IsVisible);
                Save(window, $"shell-{language}-{(dark ? "dark" : "light")}-tab{tab + 1}.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("he")]
    [InlineData("en")]
    public void Navigation_SitsAtTheStartEdge_AndSettingsPanelAtTheEnd(string language)
    {
        app.Run(() =>
        {
            var (window, shell, view) = Build(language, dark: false);
            try
            {
                var items = Descendants<NavigationViewItem>(view).ToList();
                Assert.Equal(3, items.Count);
                var navX = items[0].TranslatePoint(new Point(items[0].ActualWidth / 2, 0), window).X;

                shell.OpenSettingsCommand.Execute(null);
                Pump();
                var panel = Panel(view);
                var panelX = panel.TranslatePoint(new Point(panel.ActualWidth / 2, 0), window).X;

                if (language == "he")
                {
                    Assert.True(navX > window.ActualWidth / 2, $"nav at {navX} should be on the right in Hebrew");
                    Assert.True(panelX < window.ActualWidth / 2, $"panel at {panelX} should be on the left in Hebrew");
                }
                else
                {
                    Assert.True(navX < window.ActualWidth / 2, $"nav at {navX} should be on the left in English");
                    Assert.True(panelX > window.ActualWidth / 2, $"panel at {panelX} should be on the right in English");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("he", false)]
    [InlineData("he", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public void SettingsPanel_OpensOverTheStopsTab(string language, bool dark)
    {
        app.Run(() =>
        {
            var (window, shell, view) = Build(language, dark);
            try
            {
                shell.OpenSettingsCommand.Execute(null);
                Pump();

                Assert.True(shell.IsSettingsOpen);
                Assert.Equal(Visibility.Visible, Panel(view).Visibility);
                Save(window, $"settings-{language}-{(dark ? "dark" : "light")}.png");

                shell.CloseSettingsCommand.Execute(null);
                Pump();
                Assert.Equal(Visibility.Collapsed, Panel(view).Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SettingsPanel_AdvancedExpander_IsClosedByDefault_AndOpens()
    {
        app.Run(() =>
        {
            var (window, shell, view) = Build("he", dark: false);
            try
            {
                shell.OpenSettingsCommand.Execute(null);
                Pump();
                var advanced = Descendants<Expander>(Panel(view)).Single();
                Assert.False(advanced.IsExpanded);

                advanced.IsExpanded = true;
                Pump();
                Descendants<ScrollViewer>(Panel(view)).First().ScrollToEnd();
                Pump();
                Save(window, "settings-he-light-advanced.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Shell_StartsAtTheStopsTab_WithSettingsClosed()
    {
        var f = new ShellAndSettingsTests.Fixture();

        Assert.Equal(0, f.Shell.SelectedTabIndex);
        Assert.False(f.Shell.IsSettingsOpen);
    }

    [Fact]
    public void Shell_SettingsShortcuts_OpenAndCloseThePanel()
    {
        var f = new ShellAndSettingsTests.Fixture();

        f.Shell.SelectTabCommand.Execute("3");
        Assert.True(f.Shell.IsSettingsOpen);
        Assert.Equal(0, f.Shell.SelectedTabIndex);

        f.Shell.CloseSettingsCommand.Execute(null);
        Assert.False(f.Shell.IsSettingsOpen);

        f.Shell.ToggleSettingsCommand.Execute(null);
        Assert.True(f.Shell.IsSettingsOpen);
        f.Shell.ToggleSettingsCommand.Execute(null);
        Assert.False(f.Shell.IsSettingsOpen);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static (Window Window, ShellViewModel Shell, ShellView View) Build(string language, bool dark)
    {
        Motion.ForceReduced = true;
        var strings = DesignRenderTests.Strings(language);
        var location = new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.0835, 34.8336, 18) };
        var f = new ShellAndSettingsTests.Fixture(
            config => config.Language = language,
            new DesignRenderTests.DictionaryLocalizer(strings),
            new FakeCatalog(DesignRenderTests.SampleStops),
            new DesignRenderTests.SampleArrivals(),
            location,
            new FixedClock(DesignRenderTests.Now));
        f.AddPlaces("בית", "21385", "עבודה", "21533");
        _ = f.Shell.Stops.LoadCommand.ExecuteAsync(null);

        var theme = Wpf.Theme();
        var view = new ShellView { DataContext = f.Shell };
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Left = -20000,
            Top = 0,
            Width = 980,
            Height = 700,
            FlowDirection = language == "he" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Content = view
        };
        // Like App.xaml: strings, WPF-UI themes and controls and our tokens at application level (views inside the
        // navigation Frame resolve through the Application).
        var appResources = System.Windows.Application.Current.Resources;
        foreach (var old in appResources.MergedDictionaries.Where(d => d is ThemesDictionary or ControlsDictionary || d.Source?.OriginalString.Contains("Strings.", StringComparison.Ordinal) == true).ToList())
        {
            appResources.MergedDictionaries.Remove(old);
        }

        appResources.MergedDictionaries.Add(DesignRenderTests.Strings(language));
        appResources.MergedDictionaries.Add(new ThemesDictionary { Theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light });
        appResources.MergedDictionaries.Add(new ControlsDictionary());
        ThemeTokens.Apply(appResources, ThemeTokens.FindTokenDictionary(appResources)!, dark);
        window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Bp.Bg.App");
        window.SetValue(TextElement.FontFamilyProperty, theme["Bp.Font"]);
        window.SetResourceReference(TextElement.ForegroundProperty, "Bp.Text.Primary");
        window.Show();
        Pump();
        return (window, f.Shell, view);
    }

    private static FrameworkElement Panel(ShellView view) => (FrameworkElement)view.FindName("SettingsPanel");

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Wpf.DoEvents();
        }
    }

    private static void Save(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("BUSPULSE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        const double Scale = 1.5;
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth * Scale), (int)Math.Ceiling(window.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        System.IO.Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(System.IO.Path.Combine(directory, name));
        encoder.Save(stream);
        _ = content;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
