using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Forms;
using BusPulse.Configuration;
using BusPulse.Services;
using Microsoft.Extensions.Logging;
using Application = System.Windows.Application;

namespace BusPulse;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly AppConfigStore _configStore;
    private readonly OpenBusService _openBusService;
    private readonly ObservableCollection<SavedPlace> _savedPlaces;
    private readonly ToastNotificationService _toast;
    private readonly System.Drawing.Icon _busIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _muteMenuItem;
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(
        AppConfig config,
        AppConfigStore configStore,
        OpenBusService openBusService,
        ToastNotificationService toast,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _config = config;
        _configStore = configStore;
        _openBusService = openBusService;
        _toast = toast;
        _logger = logger;
        _savedPlaces = new ObservableCollection<SavedPlace>(_config.SavedPlaces);
        ProfileText.Text = _config.ActiveProfile;
        LineNameInput.Text = _config.LineName;
        StartPlaceCombo.ItemsSource = _savedPlaces;
        DestinationPlaceCombo.ItemsSource = _savedPlaces;
        StartPlaceCombo.SelectedValue = _config.StartPlaceId;
        DestinationPlaceCombo.SelectedValue = _config.DestinationPlaceId;

        var menu = new ContextMenuStrip();
        _muteMenuItem = new ToolStripMenuItem("Quick mute", null, async (_, _) => await ToggleMuteAsync());
        var profiles = new ToolStripMenuItem("Profiles");
        profiles.DropDownItems.Add("Default", null, (_, _) => SetProfile("Default"));
        profiles.DropDownItems.Add("Work", null, (_, _) => SetProfile("Work"));
        profiles.DropDownItems.Add("Weekend", null, (_, _) => SetProfile("Weekend"));
        menu.Items.Add(_muteMenuItem);
        menu.Items.Add(profiles);
        menu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(Show));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());
        ApplyLanguage(_config.Language);
        PlaceNameInput.Text = (string)FindResource("HomeName");
        RouteStatusText.Text = (string)FindResource("SelectRouteMessage");
        RefreshSelectedRouteText();

        _busIcon = CreateBusIcon();
        _trayIcon = new NotifyIcon
        {
            Icon = _busIcon,
            Text = "BusPulse",
            ContextMenuStrip = menu,
            Visible = true
        };
        Icon = Imaging.CreateBitmapSourceFromHIcon(
            _busIcon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        Closed += (_, _) =>
        {
            _trayIcon.Dispose();
            _busIcon.Dispose();
        };
    }

    private async void MuteButton_Click(object sender, RoutedEventArgs e) => await ToggleMuteAsync();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyLanguage(_config.Language == "en" ? "he" : "en");
        _ = SaveConfigAsync();
    }

    private async void AddRouteButton_Click(object sender, RoutedEventArgs e)
    {
        var startPlace = StartPlaceCombo.SelectedItem as SavedPlace;
        var destinationPlace = DestinationPlaceCombo.SelectedItem as SavedPlace;
        var lineName = LineNameInput.Text.Trim();
        if (startPlace is null || destinationPlace is null || string.IsNullOrWhiteSpace(lineName))
        {
            RouteStatusText.Text = (string)FindResource("SelectRouteMessage");
            return;
        }

        _config.StartPlaceId = startPlace.Id;
        _config.DestinationPlaceId = destinationPlace.Id;
        _config.LineName = lineName;

        try
        {
            _config.Validate();
            await _configStore.SaveAsync(_config);
            RouteStatusText.Text = (string)FindResource("CheckingOpenBus");
            RefreshSelectedRouteText();
            var arrivals = await _openBusService.GetBusArrivalsAsync(startPlace.StopCode, lineName);
            RouteStatusText.Text = $"{FindResource("MatchesFoundAtStart")}: {arrivals.Count}";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            _logger.LogInformation(exception, "The selected route is invalid.");
            RouteStatusText.Text = (string)FindResource("SelectRouteMessage");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException)
        {
            _logger.LogWarning(exception, "Could not check the OpenBus route.");
            RouteStatusText.Text = (string)FindResource("OpenBusUnavailable");
        }
    }

    private async void AddPlaceButton_Click(object sender, RoutedEventArgs e)
    {
        var place = new SavedPlace
        {
            Name = PlaceNameInput.Text.Trim(),
            StopCode = PlaceStopCodeInput.Text.Trim()
        };

        try
        {
            place.Validate();
            if (_savedPlaces.Any(existing => string.Equals(existing.Name, place.Name, StringComparison.OrdinalIgnoreCase)))
            {
                PlaceStatusText.Text = (string)FindResource("PlaceAlreadyExists");
                return;
            }

            _savedPlaces.Add(place);
            _config.SavedPlaces = _savedPlaces.ToList();
            StartPlaceCombo.SelectedItem ??= place;
            DestinationPlaceCombo.SelectedItem ??= place;
            _config.StartPlaceId = (StartPlaceCombo.SelectedItem as SavedPlace)?.Id;
            _config.DestinationPlaceId = (DestinationPlaceCombo.SelectedItem as SavedPlace)?.Id;
            await _configStore.SaveAsync(_config);
            PlaceNameInput.Clear();
            PlaceStopCodeInput.Clear();
            PlaceNameInput.Text = (string)FindResource("HomeName");
            PlaceStatusText.Text = (string)FindResource("PlaceSaved");
            RefreshSelectedRouteText();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            _logger.LogInformation(exception, "A saved place failed validation.");
            PlaceStatusText.Text = (string)FindResource("InvalidPlaceMessage");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogError(exception, "Could not save the place.");
            PlaceStatusText.Text = (string)FindResource("PlaceSaveFailed");
        }
    }

    private void RouteSelection_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RefreshSelectedRouteText();
    }

    private void RefreshSelectedRouteText()
    {
        if (SelectedRouteText is null)
        {
            return;
        }

        var start = StartPlaceCombo.SelectedItem as SavedPlace;
        var destination = DestinationPlaceCombo.SelectedItem as SavedPlace;
        SelectedRouteText.Text = start is not null && destination is not null
            ? $"{start.Name} → {destination.Name}"
            : (string)FindResource("WaitingForRoute");
    }

    private async Task ToggleMuteAsync()
    {
        _config.NotificationsMuted = !_config.NotificationsMuted;
        UpdateMuteLabels();
        await SaveConfigAsync();
    }

    private void SetProfile(string profile)
    {
        _config.ActiveProfile = profile;
        ProfileText.Text = profile;
        _ = SaveConfigAsync();
    }

    private void ApplyLanguage(string language)
    {
        _config.Language = language;
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{language}.xaml", UriKind.Relative)
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries.RemoveAt(0);
        dictionaries.Insert(0, dictionary);
        FlowDirection = language == "he"
            ? System.Windows.FlowDirection.RightToLeft
            : System.Windows.FlowDirection.LeftToRight;
        UpdateMuteLabels();
    }

    private void UpdateMuteLabels()
    {
        MuteButton.Content = FindResource(_config.NotificationsMuted ? "UnmuteNotifications" : "MuteNotifications");
        _muteMenuItem.Text = _config.NotificationsMuted ? "Resume notifications" : "Quick mute";
    }

    private async Task SaveConfigAsync()
    {
        try
        {
            await _configStore.SaveAsync(_config);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogError(exception, "Could not save BusPulse settings.");
        }
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private static System.Drawing.Icon CreateBusIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var busBrush = new SolidBrush(Color.FromArgb(20, 125, 112)))
        using (var windowBrush = new SolidBrush(Color.White))
        using (var wheelBrush = new SolidBrush(Color.FromArgb(35, 50, 48)))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.FillRectangle(busBrush, 3, 6, 26, 19);
            graphics.FillRectangle(windowBrush, 6, 9, 8, 7);
            graphics.FillRectangle(windowBrush, 17, 9, 8, 7);
            graphics.FillRectangle(windowBrush, 6, 18, 19, 2);
            graphics.FillEllipse(wheelBrush, 6, 22, 7, 7);
            graphics.FillEllipse(wheelBrush, 20, 22, 7, 7);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}