using System.Collections.ObjectModel;
using System.IO;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.Services.Calendar;
using BusPulse.Services.Geocoding;
using BusPulse.Services.LiveData;
using BusPulse.Services.Secrets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BusPulse.ViewModels;

public sealed record ProviderStatusItem(string Name, string Status);

public sealed record LanguageOption(string Code, string Name);

/// <summary>A theme choice; the name is re-read when the language changes.</summary>
public sealed partial class ThemeOption(string code, string name) : ObservableObject
{
    public string Code { get; } = code;

    [ObservableProperty]
    private string _name = name;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>Choices offered by the dropdowns; the config accepts any value in its valid range.</summary>
    public static readonly int[] LeadMinutesOptions = [0, 2, 5, 10, 15];

    public static readonly int[] PollingSecondsOptions = [15, 20, 30];

    /// <summary>Same cap as the config validation.</summary>
    public const int MaxSavedPlaces = 30;

    private readonly ISettingsService _settings;
    private readonly SavedPlaceCollection _places;
    private readonly ILocalizer _localizer;
    private readonly IReadOnlyList<ILiveArrivalsProvider> _providers;
    private readonly IScheduleProvider _schedule;
    private readonly IHistoryService _history;
    private readonly INotifier _notifier;
    private readonly ICalendarService _calendar;
    private readonly IGeocoder? _geocoder;
    private readonly ISecretProvider? _secrets;
    private string _suggestedAddressName = string.Empty;
    private string? _calendarStatusKey;
    private bool _loading = true;
    private string _defaultPlaceName = string.Empty;

    [ObservableProperty]
    private string _newPlaceName;

    [ObservableProperty]
    private string _newPlaceStopCode = string.Empty;

    [ObservableProperty]
    private string _placeStatus = string.Empty;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _notificationsEnabled;

    [ObservableProperty]
    private int _selectedLeadMinutes;

    [ObservableProperty]
    private int _selectedPollingSeconds;

    [ObservableProperty]
    private SavedPlace? _homePlace;

    [ObservableProperty]
    private SavedPlace? _workPlace;

    [ObservableProperty]
    private string _historyStatus = string.Empty;

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    [ObservableProperty]
    private string _testStatus = string.Empty;

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    [ObservableProperty]
    private string _favoritesStatus = string.Empty;

    [ObservableProperty]
    private bool _isCalendarConnected;

    [ObservableProperty]
    private bool _isConnectingCalendar;

    [ObservableProperty]
    private string _calendarStatus = string.Empty;

    public bool IsCalendarConfigured => _calendar.IsConfigured;

    /// <summary>Looks the Google client id and secret up (Infisical, then environment) and updates the calendar card.</summary>
    public async Task RefreshCalendarConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await _calendar.RefreshConfigurationAsync(cancellationToken);
        OnPropertyChanged(nameof(IsCalendarConfigured));
        if (!_calendar.IsConfigured)
        {
            SetCalendarStatus("CalendarNotConfigured");
        }
        else if (_calendarStatusKey == "CalendarNotConfigured")
        {
            SetCalendarStatus(_calendar.IsConnected ? "CalendarConnected" : "CalendarReady");
        }
    }

    [ObservableProperty]
    private string _saveStatus = string.Empty;

    [ObservableProperty]
    private string _addressQuery = string.Empty;

    [ObservableProperty]
    private GeocodedPlace? _selectedAddress;

    [ObservableProperty]
    private string _newAddressPlaceName = string.Empty;

    [ObservableProperty]
    private string _addressStatus = string.Empty;

    [ObservableProperty]
    private bool _isSearchingAddress;

    [ObservableProperty]
    private bool _addressSearchEnabled;

    /// <summary>Up to <see cref="NominatimGeocoder.MaxResults"/> labels from the last address search.</summary>
    public ObservableCollection<GeocodedPlace> AddressResults { get; } = [];

    public string AddressAttribution => NominatimGeocoder.Attribution;

    /// <summary>Where the Google client id and secret come from, in words.</summary>
    public string SecretsSourceStatus
    {
        get
        {
            var describe = _secrets?.Describe ?? string.Empty;
            var key = describe.Length == 0 || describe.Contains("not configured", StringComparison.OrdinalIgnoreCase)
                ? "SecretsSourceNone"
                : describe.StartsWith("Infisical", StringComparison.OrdinalIgnoreCase) ? "SecretsSourceInfisical" : "SecretsSourceEnvironment";
            return _localizer.Get(key);
        }
    }

    public SettingsViewModel(
        ISettingsService settings,
        SavedPlaceCollection places,
        ILocalizer localizer,
        IEnumerable<ILiveArrivalsProvider> providers,
        IScheduleProvider schedule,
        IHistoryService history,
        INotifier notifier,
        ICalendarService calendar,
        IGeocoder? geocoder = null,
        ISecretProvider? secrets = null)
    {
        _settings = settings;
        _geocoder = geocoder;
        _secrets = secrets;
        _calendar = calendar;
        _isCalendarConnected = calendar.IsConnected;
        _places = places;
        _localizer = localizer;
        _providers = providers.OrderBy(provider => provider.Source).ToArray();
        _schedule = schedule;
        _history = history;
        _notifier = notifier;
        _newPlaceName = localizer.Get("HomeName");
        _defaultPlaceName = _newPlaceName;
        localizer.LanguageChanged += (_, _) =>
        {
            var current = localizer.Get("HomeName");
            if (NewPlaceName == _defaultPlaceName)
            {
                NewPlaceName = current;
            }

            _defaultPlaceName = current;
        };
        var config = settings.Config;
        _notificationsEnabled = !config.NotificationsMuted;
        _addressSearchEnabled = config.AddressSearchEnabled;
        localizer.LanguageChanged += (_, _) => OnPropertyChanged(nameof(SecretsSourceStatus));
        _selectedLeadMinutes = Nearest(LeadMinutesOptions, config.LeadMinutes);
        _selectedPollingSeconds = Nearest(PollingSecondsOptions, config.PollingSeconds);
        _homePlace = places.Items.FirstOrDefault(place => place.Id == config.HomePlaceId);
        _workPlace = places.Items.FirstOrDefault(place => place.Id == config.WorkPlaceId);
        _selectedLanguage = Languages.First(option => option.Code == config.Language);
        Themes = [new("system", localizer.Get("ThemeSystem")), new("light", localizer.Get("ThemeLight")), new("dark", localizer.Get("ThemeDark"))];
        _selectedTheme = Themes.FirstOrDefault(option => option.Code == config.Theme) ?? Themes[0];
        localizer.LanguageChanged += (_, _) =>
        {
            foreach (var theme in Themes)
            {
                theme.Name = localizer.Get("Theme" + char.ToUpperInvariant(theme.Code[0]) + theme.Code[1..]);
            }
        };
        SetCalendarStatus(calendar.IsConfigured ? (calendar.IsConnected ? "CalendarConnected" : "CalendarReady") : "CalendarNotConfigured");
        localizer.LanguageChanged += (_, _) => { if (_calendarStatusKey is not null) CalendarStatus = localizer.Get(_calendarStatusKey); };
        _ = RefreshCalendarConfigurationAsync();
        _loading = false;
    }

    public ObservableCollection<SavedPlace> Places => _places.Items;

    public ObservableCollection<ProviderStatusItem> ProviderStatuses { get; } = [];

    public IReadOnlyList<ThemeOption> Themes { get; }

    public IReadOnlyList<int> LeadOptions => LeadMinutesOptions;

    /// <summary>Language names are shown in their own language so either can be found.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } = [new("en", "English"), new("he", "עברית")];

    public IReadOnlyList<int> PollingOptions => PollingSecondsOptions;

    /// <summary>Raised when the user flips the notifications switch so the shell can mirror it on the tray.</summary>
    public event EventHandler<bool>? NotificationsToggled;

    /// <summary>Raised when the user picks a language; the shell applies it to the UI and toasts.</summary>
    public event EventHandler<string>? LanguageSelected;

    /// <summary>Raised when the user picks a theme ("system", "light" or "dark").</summary>
    public event EventHandler<string>? ThemeSelected;

    /// <summary>Raised after favorites were cleared so the stop and line lists can refresh.</summary>
    public event EventHandler? FavoritesCleared;

    /// <summary>Applies a change made elsewhere (the tray menu) without echoing it back.</summary>
    public void SyncNotifications(bool enabled)
    {
        _loading = true;
        NotificationsEnabled = enabled;
        _loading = false;
    }

    /// <summary>Applies a language change made elsewhere (the header button) without echoing it back.</summary>
    public void SyncLanguage(string code)
    {
        _loading = true;
        SelectedLanguage = Languages.FirstOrDefault(option => option.Code == code) ?? Languages[0];
        _loading = false;
    }

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (_loading || value is null)
        {
            return;
        }

        _settings.Config.Theme = value.Code;
        ThemeSelected?.Invoke(this, value.Code);
        _ = PersistAsync();
    }

    /// <summary>Opens Google's consent page in the browser and waits for the answer (or a three-minute timeout).</summary>
    [RelayCommand]
    private async Task ConnectCalendarAsync(CancellationToken cancellationToken)
    {
        await RefreshCalendarConfigurationAsync(cancellationToken);
        if (!_calendar.IsConfigured)
        {
            return;
        }

        IsConnectingCalendar = true;
        SetCalendarStatus("CalendarWaiting");
        try
        {
            var result = await _calendar.ConnectAsync(cancellationToken);
            IsCalendarConnected = result.Status == CalendarConnectStatus.Connected;
            SetCalendarStatus(result.Status switch
            {
                CalendarConnectStatus.Connected => "CalendarConnected",
                CalendarConnectStatus.NotConfigured => "CalendarNotConfigured",
                CalendarConnectStatus.Cancelled => "CalendarCancelled",
                _ => "CalendarFailed"
            });
        }
        finally
        {
            IsConnectingCalendar = false;
        }
    }

    private void SetCalendarStatus(string key)
    {
        _calendarStatusKey = key;
        CalendarStatus = _localizer.Get(key);
    }

    [RelayCommand]
    private void DisconnectCalendar()
    {
        _calendar.Disconnect();
        IsCalendarConnected = false;
        SetCalendarStatus("CalendarDisconnected");
    }

    [RelayCommand]
    private async Task ClearFavoritesAsync()
    {
        _settings.Config.FavoriteStopCodes = [];
        _settings.Config.FavoriteLines = [];
        FavoritesStatus = await _settings.SaveAsync() ? _localizer.Get("FavoritesCleared") : _localizer.Get("SettingsSaveFailed");
        FavoritesCleared?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (!_loading && value is not null)
        {
            LanguageSelected?.Invoke(this, value.Code);
        }
    }

    [RelayCommand]
    private void SendTestNotification()
    {
        if (_settings.Config.NotificationsMuted)
        {
            TestStatus = _localizer.Get("NotificationsOffStatus");
            return;
        }

        _notifier.Notify(_localizer.Get("TestNotificationTitle"), _localizer.Get("TestNotificationBody"), NotificationKind.Info);
        TestStatus = _localizer.Get("TestNotificationSent");
    }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _settings.Config.NotificationsMuted = !value;
        NotificationsToggled?.Invoke(this, value);
        _ = PersistAsync();
    }

    partial void OnSelectedLeadMinutesChanged(int value)
    {
        if (!_loading)
        {
            _settings.Config.LeadMinutes = value;
            _ = PersistAsync();
        }
    }

    partial void OnSelectedPollingSecondsChanged(int value)
    {
        if (!_loading)
        {
            _settings.Config.PollingSeconds = value;
            _ = PersistAsync();
        }
    }

    partial void OnHomePlaceChanged(SavedPlace? value)
    {
        if (!_loading)
        {
            _settings.Config.HomePlaceId = value?.Id;
            _ = PersistAsync();
        }
    }

    partial void OnWorkPlaceChanged(SavedPlace? value)
    {
        if (!_loading)
        {
            _settings.Config.WorkPlaceId = value?.Id;
            _ = PersistAsync();
        }
    }

    /// <summary>Saves a changed setting and reports a failure, instead of dropping the result of an un-awaited save.</summary>
    private async Task PersistAsync()
    {
        var saved = await _settings.SaveAsync();
        SaveStatus = saved ? string.Empty : _localizer.Get("SettingsSaveFailed");
    }

    [RelayCommand]
    private void ClearHome() => HomePlace = null;

    [RelayCommand]
    private void ClearWork() => WorkPlace = null;

    [RelayCommand]
    private void ClearHistory()
    {
        _history.Clear();
        HistoryStatus = _localizer.Get("HistoryCleared");
    }

    partial void OnAddressSearchEnabledChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _settings.Config.AddressSearchEnabled = value;
        _ = PersistAsync();
    }

    /// <summary>Runs one address lookup. Only the button and Enter call this; typing never does.</summary>
    [RelayCommand]
    private async Task SearchAddressAsync(CancellationToken cancellationToken)
    {
        var query = AddressQuery.Trim();
        if (!_settings.Config.AddressSearchEnabled)
        {
            AddressStatus = _localizer.Get("AddressSearchOff");
            return;
        }

        if (_geocoder is null)
        {
            AddressStatus = _localizer.Get("AddressUnavailable");
            return;
        }

        if (query.Length < NominatimGeocoder.MinQueryLength)
        {
            AddressStatus = _localizer.Get("AddressTooShort");
            return;
        }

        IsSearchingAddress = true;
        AddressStatus = _localizer.Get("AddressSearching");
        try
        {
            var result = await _geocoder.SearchAsync(query, cancellationToken);
            AddressResults.Clear();
            SelectedAddress = null;
            if (result.Status == GeocodeStatus.Ok && result.Places.Count > 0)
            {
                foreach (var found in result.Places.Take(NominatimGeocoder.MaxResults))
                {
                    AddressResults.Add(found);
                }

                SelectedAddress = AddressResults[0];
                var suggestion = Truncate(AddressResults[0].Label, 64);
                if (NewAddressPlaceName.Length == 0 || NewAddressPlaceName == _suggestedAddressName)
                {
                    NewAddressPlaceName = suggestion;
                }

                _suggestedAddressName = suggestion;
                AddressStatus = string.Empty;
                return;
            }

            AddressStatus = _localizer.Get(result.Status switch
            {
                GeocodeStatus.Disabled => "AddressSearchOff",
                GeocodeStatus.TooShort => "AddressTooShort",
                GeocodeStatus.NoResults or GeocodeStatus.Ok => "AddressNoResults",
                _ => "AddressUnavailable"
            });
        }
        finally
        {
            IsSearchingAddress = false;
        }
    }

    [RelayCommand]
    private async Task AddAddressPlaceAsync()
    {
        if (SelectedAddress is not { } address)
        {
            AddressStatus = _localizer.Get("AddressPickFirst");
            return;
        }

        var place = new SavedPlace
        {
            Name = NewAddressPlaceName.Trim(),
            Latitude = address.Latitude,
            Longitude = address.Longitude,
            Address = Truncate(address.Label, 200)
        };
        var (message, stored) = await TryAddPlaceAsync(place);
        AddressStatus = message;
        if (stored)
        {
            AddressResults.Clear();
            SelectedAddress = null;
            AddressQuery = string.Empty;
            NewAddressPlaceName = string.Empty;
            _suggestedAddressName = string.Empty;
        }
    }

    [RelayCommand]
    private async Task AddPlaceAsync()
    {
        var place = new SavedPlace { Name = NewPlaceName.Trim(), StopCode = NewPlaceStopCode.Trim() };
        var (message, stored) = await TryAddPlaceAsync(place);
        PlaceStatus = message;
        if (stored)
        {
            NewPlaceStopCode = string.Empty;
            NewPlaceName = _localizer.Get("HomeName");
        }
    }

    /// <summary>Validates and stores a place; returns the localized status message and whether the place was added.</summary>
    private async Task<(string Message, bool Stored)> TryAddPlaceAsync(SavedPlace place)
    {
        try
        {
            place.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return (_localizer.Get("InvalidPlaceMessage"), false);
        }

        if (Places.Any(existing => string.Equals(existing.Name, place.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return (_localizer.Get("PlaceAlreadyExists"), false);
        }

        if (Places.Count >= MaxSavedPlaces)
        {
            return (_localizer.Get("PlaceLimitReached"), false);
        }

        Places.Add(place);
        _places.SyncToConfig();
        if (!await _settings.SaveAsync())
        {
            // Not persisted: take it back so a later unrelated save cannot write it, and keep the form for a retry.
            Places.Remove(place);
            _places.SyncToConfig();
            return (_localizer.Get("PlaceSaveFailed"), false);
        }

        return (_localizer.Get("PlaceSaved"), true);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    [RelayCommand]
    private async Task CheckConnectionAsync(CancellationToken cancellationToken)
    {
        var stopCode = Places.FirstOrDefault(place => place.StopCode.Length > 0)?.StopCode;
        ProviderStatuses.Clear();
        if (stopCode is null)
        {
            ProviderStatuses.Add(new ProviderStatusItem("-", _localizer.Get("AddPlaceFirst")));
            return;
        }

        IsChecking = true;
        try
        {
            foreach (var provider in _providers)
            {
                var result = await ProbeAsync(() => provider.GetArrivalsAsync(stopCode, cancellationToken), cancellationToken);
                ProviderStatuses.Add(new ProviderStatusItem(_localizer.Get("Provider" + provider.Source), Describe(result)));
            }

            var schedule = await ProbeAsync(() => _schedule.GetScheduleAsync(stopCode, cancellationToken), cancellationToken);
            ProviderStatuses.Add(new ProviderStatusItem(_localizer.Get("ProviderSchedule"), Describe(schedule)));
        }
        finally
        {
            IsChecking = false;
        }
    }

    private static int Nearest(int[] options, int value) => options.OrderBy(option => Math.Abs(option - value)).First();

    private static async Task<ProviderResult> ProbeAsync(Func<Task<ProviderResult>> probe, CancellationToken cancellationToken)
    {
        try
        {
            return await probe();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ProviderResult.Failed(ProviderStatus.Unavailable, "error");
        }
    }

    private string Describe(ProviderResult result) =>
        result.Status == ProviderStatus.Ok
            ? $"{_localizer.Get("StatusOk")} ({result.Arrivals.Count})"
            : _localizer.Get("Status" + result.Status);
}
