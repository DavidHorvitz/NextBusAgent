using System.Collections.ObjectModel;
using System.IO;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.Services.LiveData;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BusPulse.ViewModels;

public sealed record ProviderStatusItem(string Name, string Status);

public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>Choices offered by the dropdowns; the config accepts any value in its valid range.</summary>
    public static readonly int[] LeadMinutesOptions = [0, 2, 5, 10, 15];

    public static readonly int[] PollingSecondsOptions = [15, 20, 30];

    private readonly ISettingsService _settings;
    private readonly SavedPlaceCollection _places;
    private readonly ILocalizer _localizer;
    private readonly IReadOnlyList<ILiveArrivalsProvider> _providers;
    private readonly IScheduleProvider _schedule;
    private readonly IHistoryService _history;
    private bool _loading = true;

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

    public SettingsViewModel(
        ISettingsService settings,
        SavedPlaceCollection places,
        ILocalizer localizer,
        IEnumerable<ILiveArrivalsProvider> providers,
        IScheduleProvider schedule,
        IHistoryService history)
    {
        _settings = settings;
        _places = places;
        _localizer = localizer;
        _providers = providers.OrderBy(provider => provider.Source).ToArray();
        _schedule = schedule;
        _history = history;
        _newPlaceName = localizer.Get("HomeName");
        var config = settings.Config;
        _notificationsEnabled = !config.NotificationsMuted;
        _selectedLeadMinutes = Nearest(LeadMinutesOptions, config.LeadMinutes);
        _selectedPollingSeconds = Nearest(PollingSecondsOptions, config.PollingSeconds);
        _homePlace = places.Items.FirstOrDefault(place => place.Id == config.HomePlaceId);
        _workPlace = places.Items.FirstOrDefault(place => place.Id == config.WorkPlaceId);
        _loading = false;
    }

    public ObservableCollection<SavedPlace> Places => _places.Items;

    public ObservableCollection<ProviderStatusItem> ProviderStatuses { get; } = [];

    public IReadOnlyList<int> LeadOptions => LeadMinutesOptions;

    public IReadOnlyList<int> PollingOptions => PollingSecondsOptions;

    /// <summary>Raised when the user flips the notifications switch so the shell can mirror it on the tray.</summary>
    public event EventHandler<bool>? NotificationsToggled;

    /// <summary>Applies a change made elsewhere (the tray menu) without echoing it back.</summary>
    public void SyncNotifications(bool enabled)
    {
        _loading = true;
        NotificationsEnabled = enabled;
        _loading = false;
    }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _settings.Config.NotificationsMuted = !value;
        NotificationsToggled?.Invoke(this, value);
        _ = _settings.SaveAsync();
    }

    partial void OnSelectedLeadMinutesChanged(int value)
    {
        if (!_loading)
        {
            _settings.Config.LeadMinutes = value;
            _ = _settings.SaveAsync();
        }
    }

    partial void OnSelectedPollingSecondsChanged(int value)
    {
        if (!_loading)
        {
            _settings.Config.PollingSeconds = value;
            _ = _settings.SaveAsync();
        }
    }

    partial void OnHomePlaceChanged(SavedPlace? value)
    {
        if (!_loading)
        {
            _settings.Config.HomePlaceId = value?.Id;
            _ = _settings.SaveAsync();
        }
    }

    partial void OnWorkPlaceChanged(SavedPlace? value)
    {
        if (!_loading)
        {
            _settings.Config.WorkPlaceId = value?.Id;
            _ = _settings.SaveAsync();
        }
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

    [RelayCommand]
    private async Task AddPlaceAsync()
    {
        var place = new SavedPlace { Name = NewPlaceName.Trim(), StopCode = NewPlaceStopCode.Trim() };
        try
        {
            place.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            PlaceStatus = _localizer.Get("InvalidPlaceMessage");
            return;
        }

        if (Places.Any(existing => string.Equals(existing.Name, place.Name, StringComparison.OrdinalIgnoreCase)))
        {
            PlaceStatus = _localizer.Get("PlaceAlreadyExists");
            return;
        }

        Places.Add(place);
        _places.SyncToConfig();
        PlaceStatus = await _settings.SaveAsync() ? _localizer.Get("PlaceSaved") : _localizer.Get("PlaceSaveFailed");
        NewPlaceStopCode = string.Empty;
        NewPlaceName = _localizer.Get("HomeName");
    }

    [RelayCommand]
    private async Task CheckConnectionAsync(CancellationToken cancellationToken)
    {
        var stopCode = Places.FirstOrDefault()?.StopCode;
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
