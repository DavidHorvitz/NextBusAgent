using System.Collections.ObjectModel;
using System.IO;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services.LiveData;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BusPulse.ViewModels;

public sealed record ProviderStatusItem(string Name, string Status);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly SavedPlaceCollection _places;
    private readonly ILocalizer _localizer;
    private readonly IReadOnlyList<ILiveArrivalsProvider> _providers;
    private readonly IScheduleProvider _schedule;

    [ObservableProperty]
    private string _newPlaceName;

    [ObservableProperty]
    private string _newPlaceStopCode = string.Empty;

    [ObservableProperty]
    private string _placeStatus = string.Empty;

    [ObservableProperty]
    private bool _isChecking;

    public SettingsViewModel(
        ISettingsService settings,
        SavedPlaceCollection places,
        ILocalizer localizer,
        IEnumerable<ILiveArrivalsProvider> providers,
        IScheduleProvider schedule)
    {
        _settings = settings;
        _places = places;
        _localizer = localizer;
        _providers = providers.OrderBy(provider => provider.Source).ToArray();
        _schedule = schedule;
        _newPlaceName = localizer.Get("HomeName");
    }

    public ObservableCollection<SavedPlace> Places => _places.Items;

    public ObservableCollection<ProviderStatusItem> ProviderStatuses { get; } = [];

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
