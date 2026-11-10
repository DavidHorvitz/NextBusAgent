using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services.Calendar;
using BusPulse.Services.Geocoding;
using BusPulse.Services.Location;
using BusPulse.Themes;
using BusPulse.ViewModels;
using BusPulse.Views;
using BusPulse.Views.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Xunit;
using FlowDirection = System.Windows.FlowDirection;
using ListBox = System.Windows.Controls.ListBox;
using Size = System.Windows.Size;
using TextBlock = System.Windows.Controls.TextBlock;

namespace BusPulse.Tests;

/// <summary>
/// Renders the free-text origin and destination of the trip planner off-screen with made-up sample data (no real places,
/// stops near anyone or calendar content). PNGs are written only when BUSPULSE_SCREENSHOT_DIR is set (docs/screens/session-2).
/// </summary>
[Collection(WpfAppCollection.Name)]
public sealed class TripPlannerRenderTests(WpfAppFixture app)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T08:00:00+00:00");

    private sealed record Scene(TripPlannerViewModel Vm, FakeGeocoder Geocoder, FakeCalendar Calendar, Border Host, TripPlannerView View);

    private static Scene Build(string language, bool dark)
    {
        Motion.ForceReduced = true;
        var settings = new MemorySettings();
        settings.Config.Language = language;
        var places = new SavedPlaceCollection(settings);
        places.Items.Add(new SavedPlace { Name = language == "he" ? "בית" : "Home", StopCode = "21240" });
        places.Items.Add(new SavedPlace { Name = language == "he" ? "משרד הרצל" : "Herzl office", StopCode = "21533" });
        places.SyncToConfig();
        var geocoder = new FakeGeocoder();
        var calendar = new FakeCalendar { IsConnected = true };
        calendar.Events = [new CalendarEvent("1", "Sample meeting", Now.AddHours(4), language == "he" ? "הרצל 12, תל אביב-יפו" : "Herzl 12, Tel Aviv")];
        var vm = new TripPlannerViewModel(
            settings, places, new DesignRenderTests.DictionaryLocalizer(DesignRenderTests.Strings(language)), new FakeTripPlanner(),
            new FakeCatalog(DesignRenderTests.SampleStops), new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.0835, 34.8336, 18) },
            new FakeHistory(), new FakeReminder(), new RecordingNotifier(), new FixedClock(Now), new ImmediateDispatcher(),
            NullLogger<TripPlannerViewModel>.Instance, calendar, geocoder)
        {
            SpinnerDelay = TimeSpan.Zero,
            AutocompleteDelay = TimeSpan.Zero
        };
        vm.LoadCalendarSuggestionsAsync().GetAwaiter().GetResult();

        var view = new TripPlannerView { DataContext = vm };
        var host = new Border
        {
            Width = 600,
            Padding = new Thickness(16),
            FlowDirection = language == "he" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Child = view
        };
        var theme = Wpf.Theme();
        host.Resources.MergedDictionaries.Add(DesignRenderTests.Strings(language));
        host.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light });
        host.Resources.MergedDictionaries.Add(new ControlsDictionary());
        host.Resources.MergedDictionaries.Add(theme);
        ThemeTokens.Apply(host.Resources, theme, dark);
        host.SetResourceReference(Border.BackgroundProperty, "Bp.Bg.App");
        host.SetValue(TextElement.FontFamilyProperty, theme["Bp.Font"]);
        host.SetResourceReference(TextElement.ForegroundProperty, "Bp.Text.Primary");
        return new Scene(vm, geocoder, calendar, host, view);
    }

    private static void Layout(FrameworkElement host)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            host.Measure(new Size(600, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            Wpf.DoEvents();
        }
    }

    // The host is not shown in a window, so IsVisible is always false; check the Visibility of the element and its parents.
    private static bool Visible(FrameworkElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return element.ActualWidth > 0;
    }

    private static ListBox List(Scene scene, string name) => (ListBox)scene.View.FindName(name);

    private static IEnumerable<TextBlock> Texts(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text)
            {
                yield return text;
            }

            foreach (var nested in Texts(child))
            {
                yield return nested;
            }
        }
    }

    private static bool Shown(Scene scene, string text) =>
        Texts(scene.View).Any(block => block.Text == text && Visible(block) && block.ActualWidth > 0);

    [Theory]
    [InlineData("he", false)]
    [InlineData("he", true)]
    [InlineData("en", false)]
    public void Typing_ShowsLocalSuggestions_InOrder_WithTheirKind(string language, bool dark)
    {
        app.Run(() =>
        {
            var scene = Build(language, dark);
            scene.Vm.OriginQuery = string.Empty;
            scene.Vm.ShowDefaultSuggestions(origin: true);
            scene.Vm.DestinationQuery = language == "he" ? "הרצ" : "Herzl";
            Layout(scene.Host);

            Assert.True(scene.Vm.OriginSuggestions[0].IsMyLocation);
            Assert.Empty(scene.Geocoder.Queries);
            Assert.Empty(scene.Geocoder.SuggestQueries);
            Assert.True(Visible(List(scene, "OriginSuggestionList")));
            Assert.True(Visible(List(scene, "SuggestionList")));
            Assert.True(Shown(scene, scene.Vm.MyLocation.DisplayName));
            Assert.Equal([LocationChoiceKind.Place, LocationChoiceKind.Text], scene.Vm.Suggestions.Select(c => c.Kind).ToArray());
            DesignRenderTests.Save(scene.Host, $"freetext-typing-{language}-{(dark ? "dark" : "light")}.png");
        });
    }

    [Theory]
    [InlineData("he", false)]
    [InlineData("he", true)]
    public void Enter_ShowsGeocoderResults_WithTheFirstHighlighted(string language, bool dark)
    {
        app.Run(() =>
        {
            var scene = Build(language, dark);
            scene.Geocoder.Result = new GeocodeResult(GeocodeStatus.Ok,
            [
                new GeocodedPlace("הרצל 12, תל אביב-יפו", 32.0600, 34.7700),
                new GeocodedPlace("הרצל 14, תל אביב-יפו", 32.0610, 34.7700),
                new GeocodedPlace("הרצל 3, חיפה", 32.8000, 34.9900)
            ]);
            scene.Vm.DestinationQuery = "הרצל";

            scene.Vm.CommitDestinationCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Layout(scene.Host);

            Assert.Equal(["הרצל"], scene.Geocoder.Queries);
            Assert.Equal("הרצל 12, תל אביב-יפו", scene.Vm.HighlightedDestination?.DisplayName);
            Assert.Equal(3, scene.Vm.Suggestions.Count(c => c.Kind == LocationChoiceKind.Address));
            Assert.Same(scene.Vm.HighlightedDestination, List(scene, "SuggestionList").SelectedItem);
            DesignRenderTests.Save(scene.Host, $"freetext-geocoder-{language}-{(dark ? "dark" : "light")}.png");
        });
    }

    [Theory]
    [InlineData("he", false)]
    [InlineData("he", true)]
    public void NoResults_ShowsOneLine_AndNoSpinner(string language, bool dark)
    {
        app.Run(() =>
        {
            var scene = Build(language, dark);
            scene.Geocoder.Result = GeocodeResult.Of(GeocodeStatus.NoResults);
            scene.Vm.DestinationQuery = "xqzvw";

            scene.Vm.CommitDestinationCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Layout(scene.Host);

            Assert.True(scene.Vm.DestinationNoResults);
            Assert.True(Shown(scene, "לא נמצאו תוצאות"));
            Assert.False(scene.Vm.IsDestinationSearching);
            Assert.False(Visible(List(scene, "SuggestionList")));
            DesignRenderTests.Save(scene.Host, $"freetext-empty-{language}-{(dark ? "dark" : "light")}.png");
        });
    }

    [Fact]
    public void Spinner_IsVisibleOnlyWhileACallIsInFlight()
    {
        app.Run(() =>
        {
            var scene = Build("he", false);
            var gate = new TaskCompletionSource();
            scene.Geocoder.Gate = gate.Task;
            scene.Geocoder.Result = GeocodeResult.Of(GeocodeStatus.NoResults);
            scene.Vm.DestinationQuery = "הרצל";
            // A real (off-screen) window: the ring animates only inside a presentation source.
            var window = new Window { WindowStyle = WindowStyle.None, ShowInTaskbar = false, Left = -20000, Top = 0, Width = 640, Height = 900, Content = scene.Host };
            window.Show();
            Layout(scene.Host);
            Assert.Empty(Spinners(scene));

            var call = scene.Vm.CommitDestinationCommand.ExecuteAsync(null);
            Layout(scene.Host);
            Assert.Single(Spinners(scene));

            gate.SetResult();
            // The continuation runs on this dispatcher: pump it instead of blocking (blocking deadlocks).
            for (var i = 0; i < 200 && !call.IsCompleted; i++)
            {
                Wpf.DoEvents();
            }

            Assert.True(call.IsCompleted);
            Layout(scene.Host);
            Assert.Empty(Spinners(scene));
            window.Close();
        });
    }

    private static List<System.Windows.Controls.ProgressBar> Spinners(Scene scene)
    {
        var found = new List<System.Windows.Controls.ProgressBar>();
        void Walk(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is System.Windows.Controls.ProgressBar { Height: 2 } ring && Visible(ring))
                {
                    found.Add(ring);
                }

                Walk(child);
            }
        }

        Walk(scene.View);
        return found;
    }
}
