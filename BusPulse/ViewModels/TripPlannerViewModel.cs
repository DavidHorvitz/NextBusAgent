using System.Collections.ObjectModel;
using System.IO;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services.LiveData;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BusPulse.ViewModels;

public sealed partial class TripPlannerViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ILocalizer _localizer;
    private readonly ILiveDataAggregator _aggregator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RouteSummary))]
    private SavedPlace? _selectedStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RouteSummary))]
    private SavedPlace? _selectedDestination;

    [ObservableProperty]
    private string _lineName;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private bool _isBusy;

    public TripPlannerViewModel(
        ISettingsService settings,
        SavedPlaceCollection places,
        ILocalizer localizer,
        ILiveDataAggregator aggregator)
    {
        _settings = settings;
        _localizer = localizer;
        _aggregator = aggregator;
        Places = places.Items;
        _lineName = settings.Config.LineName ?? string.Empty;
        _selectedStart = Places.FirstOrDefault(place => place.Id == settings.Config.StartPlaceId);
        _selectedDestination = Places.FirstOrDefault(place => place.Id == settings.Config.DestinationPlaceId);
        _statusText = localizer.Get("SelectRouteMessage");
    }

    public ObservableCollection<SavedPlace> Places { get; }

    public string RouteSummary => SelectedStart is not null && SelectedDestination is not null
        ? $"{SelectedStart.Name} → {SelectedDestination.Name}"
        : _localizer.Get("WaitingForRoute");

    [RelayCommand]
    private async Task CheckRouteAsync(CancellationToken cancellationToken)
    {
        var start = SelectedStart;
        var destination = SelectedDestination;
        var line = LineName.Trim();
        if (start is null || destination is null || string.IsNullOrWhiteSpace(line))
        {
            StatusText = _localizer.Get("SelectRouteMessage");
            return;
        }

        IsBusy = true;
        try
        {
            var config = _settings.Config;
            config.StartPlaceId = start.Id;
            config.DestinationPlaceId = destination.Id;
            config.LineName = line;
            config.Validate();
            if (!await _settings.SaveAsync())
            {
                StatusText = _localizer.Get("PlaceSaveFailed");
                return;
            }

            StatusText = _localizer.Get("CheckingOpenBus");
            var result = await _aggregator.GetArrivalsAsync(start.StopCode, cancellationToken);
            var matches = result.Arrivals
                .Where(arrival => string.Equals(arrival.LineName, line, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var live = matches.Count(arrival => arrival.IsLive);
            StatusText = $"{_localizer.Get("MatchesFoundAtStart")}: {matches.Length} " +
                $"({live} {_localizer.Get("LiveBadge")}, {matches.Length - live} {_localizer.Get("ScheduledBadge")})";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            StatusText = _localizer.Get("SelectRouteMessage");
        }
        catch (OperationCanceledException)
        {
            StatusText = _localizer.Get("OpenBusUnavailable");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
