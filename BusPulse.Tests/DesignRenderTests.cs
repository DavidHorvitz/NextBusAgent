using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BusPulse.Infrastructure;
using BusPulse.Services.LiveData;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;
using BusPulse.Themes;
using BusPulse.ViewModels;
using BusPulse.Views;
using BusPulse.Views.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Xunit;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using FlowDirection = System.Windows.FlowDirection;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ListBox = System.Windows.Controls.ListBox;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using TextBlock = System.Windows.Controls.TextBlock;
using Path = System.Windows.Shapes.Path;
using TextBox = System.Windows.Controls.TextBox;

namespace BusPulse.Tests;

/// <summary>
/// One WPF Application on one STA thread for the tests that load views: views resolve their StaticResource styles through
/// Application.Current while InitializeComponent runs, as in the app. The theme is merged there; each test still puts the
/// light or dark alias brushes and the strings on its own host element, which take precedence.
/// </summary>
public sealed class WpfAppFixture : IDisposable
{
    private readonly Thread _thread;
    private System.Windows.Threading.Dispatcher? _dispatcher;

    public WpfAppFixture()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(Wpf.Theme());
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
        ready.Wait();
    }

    public void Run(Action action) => _dispatcher!.Invoke(action);

    public void Dispose()
    {
        _dispatcher?.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

[CollectionDefinition(Name)]
public sealed class WpfAppCollection : ICollectionFixture<WpfAppFixture>
{
    public const string Name = "WPF application";
}

/// <summary>
/// Renders the Stops reference screen and the component gallery offscreen with the real resources (light/dark, Hebrew/
/// English). Always checks that they load and lay out; writes PNGs only when BUSPULSE_SCREENSHOT_DIR is set
/// (docs/screens/session-0 was produced that way). Rendering has no Mica: the background is token step 1.
/// </summary>
[Collection(WpfAppCollection.Name)]
public sealed class DesignRenderTests(WpfAppFixture app)
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T17:42:00+02:00");

    [Theory]
    [InlineData("he", false)]
    [InlineData("he", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public void StopsScreen_Renders(string language, bool dark)
    {
        app.Run(() =>
        {
            var (host, _) = BuildStopsScreen(language, dark, expand: true);

            Assert.True(host.ActualHeight > 300);
            Save(host, $"stops-{language}-{(dark ? "dark" : "light")}.png");
        });
    }

    [Fact]
    public void StopsScreen_FirstLoad_ShowsSkeleton()
    {
        app.Run(() =>
        {
            var (host, view) = BuildStopsScreen("he", dark: false, expand: false, neverLoads: true);

            var skeletons = Descendants<Border>(view).Count(b => Motion.GetPulse(b) && Shown(b));
            Assert.True(skeletons >= 6, $"{skeletons} skeleton blocks visible");
            Save(host, "stops-he-light-loading.png");
        });
    }

    /// <summary>The brief: "9 דק׳" to "10 דק׳" must not move anything. Positions of everything but the number are compared.</summary>
    [Theory]
    [InlineData("he")]
    [InlineData("en")]
    public void Countdown_FromNineToTen_DoesNotMoveTheRow(string language)
    {
        app.Run(() =>
        {
            var (host, view) = BuildStopsScreen(language, dark: false, expand: true);
            var stops = (StopsViewModel)view.DataContext;
            var row = stops.Stops.SelectMany(s => s.Arrivals).First(a => a.LineName == "66");
            var clock = Now;
            row.Refresh(clock.AddMinutes(-1)); // 10 min
            Layout(host);
            Assert.Equal("10", row.EtaValue);
            var before = Positions(host, view);
            // Guard against comparing two empty lists: the expanded stops' badges, chips and units must be in there.
            Assert.Contains(before, p => p.StartsWith("LineBadge", StringComparison.Ordinal));
            Assert.Contains(before, p => p.StartsWith("StatusChip", StringComparison.Ordinal));
            Assert.Contains(before, p => p.Contains(language == "he" ? ":דק׳:" : ":min:", StringComparison.Ordinal));

            row.Refresh(clock); // 9 min
            Layout(host);
            Assert.Equal("9", row.EtaValue);
            Assert.Equal(before, Positions(host, view));

            row.Refresh(clock.AddMinutes(-90)); // 99 min: still inside the reserved number box
            Layout(host);
            Assert.Equal("99", row.EtaValue);
            Assert.Equal(before, Positions(host, view));

            // "Now" (line 1 is due now) is the widest value: every departure row keeps the reserved ETA column, and the
            // ETA fits in it, so the destination and status columns line up whatever the countdown says.
            Assert.Equal(language == "he" ? "עכשיו" : "Now", stops.Stops.SelectMany(s => s.Arrivals).First(a => a.LineName == "1").EtaValue);
            var reserved = ((GridLength)System.Windows.Application.Current.FindResource("Bp.EtaColumnWidth")).Value;
            var rows = Descendants<Grid>(view).Where(g => g.ColumnDefinitions.Count == 4 && Shown(g)).ToList();
            Assert.True(rows.Count >= 7, $"{rows.Count} departure rows");
            Assert.All(rows, grid =>
            {
                Assert.Equal(reserved, grid.ColumnDefinitions[3].ActualWidth, 3);
                var eta = grid.Children.OfType<StackPanel>().Single(child => Grid.GetColumn(child) == 3);
                Assert.True(eta.DesiredSize.Width <= reserved, $"ETA needs {eta.DesiredSize.Width}");
            });
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gallery_Renders(bool dark)
    {
        app.Run(() =>
        {
            var host = Host("he", dark, Gallery());
            Layout(host, 640);
            Save(host, $"components-he-{(dark ? "dark" : "light")}.png");
        });
    }

    /// <summary>RTL checklist evidence: font icons keep their drawing in RTL, Path geometry is mirrored.</summary>
    [Fact]
    public void Rtl_SymbolIconsAreNotMirrored_ButPathsAre()
    {
        app.Run(() =>
        {
            (byte[] Pixels, int Width) Draw(Func<UIElement> make, FlowDirection direction)
            {
                // Centred in a fixed box, so a mirror shows as a flipped drawing and not as a moved one.
                var box = new Grid { Width = 64, Height = 64 };
                box.Children.Add(make());
                var host = Host("en", false, box);
                host.Padding = new Thickness(0);
                host.Width = 64;
                host.FlowDirection = direction;
                Layout(host, 64);
                var bitmap = Render(host);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                return (pixels, bitmap.PixelWidth);
            }

            static byte[] Flip(byte[] pixels, int width)
            {
                var flipped = new byte[pixels.Length];
                var rows = pixels.Length / (width * 4);
                for (var y = 0; y < rows; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        Array.Copy(pixels, ((y * width) + x) * 4, flipped, ((y * width) + (width - 1 - x)) * 4, 4);
                    }
                }

                return flipped;
            }

            static long Distance(byte[] a, byte[] b) => a.Zip(b, (x, y) => (long)Math.Abs(x - y)).Sum();

            bool LooksMirrored(Func<UIElement> make)
            {
                var ltr = Draw(make, FlowDirection.LeftToRight);
                var rtl = Draw(make, FlowDirection.RightToLeft);
                return Distance(rtl.Pixels, Flip(ltr.Pixels, ltr.Width)) < Distance(rtl.Pixels, ltr.Pixels);
            }

            UIElement Icon() => new SymbolIcon
            {
                Symbol = SymbolRegular.ArrowRight24, FontSize = 40, Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            UIElement Arrow() => new Path
            {
                Data = Geometry.Parse("M12,12 L52,32 L12,52 Z"), Fill = Brushes.Black, Width = 64, Height = 64
            };

            Assert.False(LooksMirrored(Icon), "Fluent font icons should keep their drawing in RTL");
            Assert.True(LooksMirrored(Arrow), "Path geometry should mirror in RTL");
        });
    }

    // ------------------------------------------------------------------------------------------------ screen builders

    private static (Border Host, StopsView View) BuildStopsScreen(string language, bool dark, bool expand, bool neverLoads = false)
    {
        Motion.ForceReduced = true;
        var settings = new MemorySettings();
        settings.Config.FavoriteStopCodes.Add("21385");
        var strings = Strings(language);
        var localizer = new DictionaryLocalizer(strings);
        IStopCatalog catalog = neverLoads ? new PendingCatalog() : new FakeCatalog(SampleStops);
        var location = new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.0835, 34.8336, 18) };
        var vm = new StopsViewModel(
            settings, location, catalog, new SampleArrivals(), new SavedPlaceCollection(settings), localizer, new FixedClock(Now),
            new ImmediateDispatcher(), NullLogger<StopsViewModel>.Instance);

        var view = new StopsView { DataContext = vm };
        var host = Host(language, dark, view);
        _ = vm.LoadCommand.ExecuteAsync(null);
        if (expand)
        {
            // The nearest stop that is not pinned (the pinned one sorts first and stays collapsed).
            _ = vm.ToggleStopCommand.ExecuteAsync(vm.Stops[1]);
            _ = vm.ToggleStopCommand.ExecuteAsync(vm.Stops[2]);
        }

        Layout(host);
        return (host, view);
    }

    private static Border Host(string language, bool dark, UIElement content)
    {
        var host = new Border
        {
            Width = 600,
            Padding = new Thickness(16),
            FlowDirection = language == "he" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Child = content
        };
        var theme = Wpf.Theme();
        host.Resources.MergedDictionaries.Add(Strings(language));
        host.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light });
        host.Resources.MergedDictionaries.Add(new ControlsDictionary());
        host.Resources.MergedDictionaries.Add(theme);
        ThemeTokens.Apply(host.Resources, theme, dark);
        host.SetResourceReference(Border.BackgroundProperty, "Bp.Bg.App");
        host.SetValue(TextElement.FontFamilyProperty, theme["Bp.Font"]);
        host.SetResourceReference(TextElement.ForegroundProperty, "Bp.Text.Primary");
        return host;
    }

    internal static ResourceDictionary Strings(string language) => new() { Source = new Uri(Wpf.Pack + $"Resources/Strings.{language}.xaml") };

    private static void Layout(FrameworkElement host, double width = 600)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            host.Measure(new Size(width, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            Wpf.DoEvents();
        }
    }

    /// <summary>Position and width of every visible badge, chip and text, except the countdown number itself.</summary>
    private static List<string> Positions(FrameworkElement host, DependencyObject root) =>
        Descendants<FrameworkElement>(root)
            .Where(e => e is LineBadge or StatusChip or TextBlock && Shown(e))
            .Where(e => FindAncestor<TickerText>(e) is null)
            .Select(e => $"{e.GetType().Name}:{(e as TextBlock)?.Text}:{e.TranslatePoint(new Point(0, 0), host)}:{e.ActualWidth:0.##}")
            .ToList();

    private static UIElement Gallery()
    {
        var panel = new StackPanel();
        void Section(string title, params UIElement[] items)
        {
            var heading = new TextBlock { Text = title, Margin = new Thickness(0, 16, 0, 8) };
            heading.SetResourceReference(FrameworkElement.StyleProperty, "Bp.Type.Title");
            panel.Children.Add(heading);
            var row = new WrapPanel();
            foreach (var item in items)
            {
                if (item is FrameworkElement element)
                {
                    element.Margin = new Thickness(0, 0, 8, 8);
                }

                row.Children.Add(item);
            }

            panel.Children.Add(row);
        }

        Button Styled(string style, string text, bool enabled = true)
        {
            var button = new Button { Content = text, IsEnabled = enabled };
            button.SetResourceReference(FrameworkElement.StyleProperty, style);
            return button;
        }

        TextBlock Typed(string style, string text)
        {
            var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            block.SetResourceReference(FrameworkElement.StyleProperty, style);
            return block;
        }

        Section(
            "טיפוגרפיה",
            Typed("Bp.Type.Display", "9"),
            Typed("Bp.Type.Display", "10"),
            Typed("Bp.Type.Title", "תחנה מרכזית"),
            Typed("Bp.Type.Body", "גוף טקסט, English 123"),
            Typed("Bp.Type.Caption", "כיתוב משני"));
        Section(
            "כפתורים",
            Styled("Bp.Button.Primary", "חיפוש"),
            Styled("Bp.Button.Secondary", "החלפה"),
            Styled("Bp.Button.Quiet", "ניקוי"),
            Styled("Bp.Button.Destructive", "מחיקת מקום"),
            Styled("Bp.Button.Primary", "מושבת", enabled: false),
            Styled("Bp.Button.Secondary", "מושבת", enabled: false));
        Section(
            "סטטוס",
            new StatusChip { Status = DepartureStatus.Live, Text = "חי" },
            new StatusChip { Status = DepartureStatus.Scheduled, Text = "מתוכנן" },
            new StatusChip { Status = DepartureStatus.Delayed, Text = "מתעכב" },
            new StatusChip { Status = DepartureStatus.Cancelled, Text = "מבוטל" },
            new StatusChip { Status = DepartureStatus.Stale, Text = "לא עדכני" });
        // Fills are the four route_color values found in the Ministry feed; no route_text_color, as in the feed.
        Section(
            "קווים",
            new LineBadge { LineName = "1", RouteColor = "FF9933" },
            new LineBadge { LineName = "402", RouteColor = "33CC33" },
            new LineBadge { LineName = "18", RouteColor = "3399FF" },
            new LineBadge { LineName = "947", RouteColor = "9933FF" },
            new LineBadge { LineName = "66", OperatorName = "דן" },
            new LineBadge { LineName = "54א", OperatorName = "אגד" });
        var chips = new[] { "עכשיו", "יציאה ב-", "הגעה עד" }
            .Select((text, i) =>
            {
                var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = text, IsChecked = i == 0 };
                chip.SetResourceReference(FrameworkElement.StyleProperty, "Bp.ToggleChip");
                return (UIElement)chip;
            })
            .ToArray();
        Section("מצב זמן", chips);
        var box = new TextBox { Text = "דיזנגוף 50, תל אביב", Width = 260, VerticalAlignment = VerticalAlignment.Top };
        box.SetResourceReference(FrameworkElement.StyleProperty, "Bp.TextBox");
        var list = new ListBox { Width = 260, ItemsSource = new[] { "דיזנגוף סנטר", "דיזנגוף/פרישמן", "Dizengoff Square" }, SelectedIndex = 1 };
        list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "Bp.SuggestionRow");
        Section("קלט", box, list);
        Section(
            "הודעות",
            new Banner { Tone = BannerTone.Info, Text = "המיקום כבוי. בחרו מקום שמור כדי לראות תחנות קרובות.", Width = 520 },
            new Banner { Tone = BannerTone.Offline, Text = "אין חיבור. מוצגים זמנים מתוכננים בלבד.", Width = 520 },
            new Banner { Tone = BannerTone.Warning, Text = "אין שירות בתאריך הזה.", Width = 520 },
            new Banner { Tone = BannerTone.Danger, Text = "צאו עכשיו - קו 66 בעוד 7 דק׳", Width = 520 });
        var skeleton = new Border { Width = 220, Height = 14 };
        skeleton.SetResourceReference(FrameworkElement.StyleProperty, "Bp.Skeleton");
        Section("שלד טעינה", skeleton);
        return panel;
    }

    // ------------------------------------------------------------------------------------------------ helpers

    internal static void Save(FrameworkElement element, string name)
    {
        var directory = Environment.GetEnvironmentVariable("BUSPULSE_SCREENSHOT_DIR");
        var bitmap = Render(element);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(System.IO.Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static RenderTargetBitmap Render(FrameworkElement element)
    {
        const double Scale = 1.5; // 144 dpi, so text is legible in the docs
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * Scale), (int)Math.Ceiling(element.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
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

    /// <summary>Visible in layout terms. UIElement.IsVisible is false for anything not in a window, so it cannot be used here.</summary>
    private static bool Shown(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    private static T? FindAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------------------------------------ sample data

    internal static readonly Stop[] SampleStops =
    [
        new("21385", "ז'בוטינסקי/אבא הלל", "רמת גן", 32.0841, 34.8061),
        new("21240", "ז'בוטינסקי/רבי עקיבא", "בני ברק", 32.0838, 34.8341),
        new("21252", "ז'בוטינסקי/השומר", "בני ברק", 32.0846, 34.8312),
        new("20398", "תחנת רכבת בני ברק", "בני ברק", 32.0870, 34.8355),
        new("21533", "רבי עקיבא/חזון איש", "בני ברק", 32.0867, 34.8366),
    ];

    internal sealed class SampleArrivals : ILiveDataAggregator
    {
        public Task<AggregatedArrivals> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default)
        {
            Arrival Live(string line, string op, string to, int minutes, int ageSeconds = 20) =>
                new(stopCode, line, op, to, Now.AddMinutes(minutes).AddSeconds(-5), true, ArrivalSource.Curlbus, Now.AddSeconds(-ageSeconds));
            Arrival Planned(string line, string op, string to, int minutes) =>
                new(stopCode, line, op, to, Now.AddMinutes(minutes), false, ArrivalSource.Schedule, Now);

            IReadOnlyList<Arrival> arrivals = stopCode == "21240"
                ? [
                    Live("1", "דן", "תחנה מרכזית חדשה", 0),
                    Live("66", "דן", "ת. רכבת סבידור מרכז", 9),
                    Planned("402", "אגד", "ירושלים", 14),
                    Live("67", "דן", "קריית שלום", 22, ageSeconds: 260),
                    Planned("54א", "אגד", "פתח תקווה", 31),
                ]
                : [Planned("33", "דן", "בת ים", 6), Planned("44", "דן", "ראשון לציון", 18)];
            return Task.FromResult(new AggregatedArrivals(arrivals, new Dictionary<ArrivalSource, ProviderStatus>()));
        }
    }

    private sealed class PendingCatalog : IStopCatalog
    {
        public IReadOnlyList<Stop>? Loaded => null;

        public Task<IReadOnlyList<Stop>> GetStopsAsync(CancellationToken cancellationToken = default) =>
            new TaskCompletionSource<IReadOnlyList<Stop>>().Task;
    }

    internal sealed class DictionaryLocalizer(ResourceDictionary strings) : ILocalizer
    {
        public event EventHandler? LanguageChanged { add { } remove { } }

        public string Get(string key) => strings[key] as string ?? key;

        public void SetLanguage(string language)
        {
        }
    }
}
