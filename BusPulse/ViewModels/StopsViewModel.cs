using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.Services.LiveData;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BusPulse.ViewModels;

public sealed partial class ArrivalRowViewModel : ObservableObject
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    private readonly ILocalizer _localizer;
    private Arrival _arrival;

    [ObservableProperty]
    private string _etaText = string.Empty;

    [ObservableProperty]
    private string _tooltip = string.Empty;

    [ObservableProperty]
    private bool _isStale;

    public ArrivalRowViewModel(Arrival arrival, ILocalizer localizer, DateTimeOffset now)
    {
        _arrival = arrival;
        _localizer = localizer;
        Refresh(now);
    }

    public string LineName => _arrival.LineName;

    public string Destination => _arrival.Destination ?? string.Empty;

    public string? OperatorName => _arrival.OperatorName;

    public bool IsLive => _arrival.IsLive;

    public string BadgeText => _localizer.Get(_arrival.IsLive ? "LiveBadge" : "ScheduledBadge");

    public void Update(Arrival arrival, DateTimeOffset now)
    {
        _arrival = arrival;
        OnPropertyChanged(nameof(LineName));
        OnPropertyChanged(nameof(Destination));
        OnPropertyChanged(nameof(OperatorName));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(BadgeText));
        Refresh(now);
    }

    /// <summary>Recomputes the countdown and freshness from the clock; called every second without hitting the network.</summary>
    public void Refresh(DateTimeOffset now)
    {
        var minutes = (int)Math.Ceiling((_arrival.Eta - now).TotalMinutes);
        EtaText = minutes <= 0
            ? _localizer.Get("EtaNow")
            : string.Format(CultureInfo.CurrentCulture, _localizer.Get("EtaMinutes"), minutes);
        var age = now - _arrival.ObservedAt;
        IsStale = _arrival.IsLive && age > StaleAfter;
        var seconds = Math.Max(0, (int)age.TotalSeconds);
        Tooltip = $"{_localizer.Get("SourceLabel")}: {_localizer.Get("Provider" + _arrival.Source)} · " +
            string.Format(CultureInfo.CurrentCulture, _localizer.Get("UpdatedAgo"), seconds);
    }
}

public sealed partial class StopItemViewModel : ObservableObject
{
    public StopItemViewModel(NearbyStop nearby, ILocalizer localizer)
    {
        Stop = nearby.Stop;
        DistanceMeters = nearby.DistanceMeters;
        DistanceText = FormatDistance(nearby.DistanceMeters, localizer);
    }

    public Stop Stop { get; }

    public string Name => Stop.Name;

    public string Code => Stop.Code;

    public double DistanceMeters { get; private set; }

    [ObservableProperty]
    private string _distanceText;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _arrivalsStatus = string.Empty;

    public ObservableCollection<ArrivalRowViewModel> Arrivals { get; } = [];

    public void UpdateDistance(double meters, ILocalizer localizer)
    {
        DistanceMeters = meters;
        DistanceText = FormatDistance(meters, localizer);
    }

    private static string FormatDistance(double meters, ILocalizer localizer) =>
        string.Format(CultureInfo.CurrentCulture, localizer.Get("DistanceMeters"), Math.Max(0, (int)Math.Round(meters)));
}

public sealed partial class StopsViewModel : ObservableObject
{
    private const int MaxStops = 15;
    private const int MaxArrivalRows = 40;
    private const double MaxDistanceMeters = 3000;

    private readonly ISettingsService _settings;
    private readonly ILocationService _location;
    private readonly IStopCatalog _catalog;
    private readonly ILiveDataAggregator _aggregator;
    private readonly SavedPlaceCollection _places;
    private readonly ILocalizer _localizer;
    private readonly IClock _clock;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<StopsViewModel> _logger;
    private CancellationTokenSource? _tickerCts;
    private bool _loadedOnce;
    private string _emptyText = string.Empty;
    private IReadOnlyList<NearbyStop> _lastNearby = [];

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _locationText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private SavedPlace? _selectedAnchor;

    public StopsViewModel(
        ISettingsService settings,
        ILocationService location,
        IStopCatalog catalog,
        ILiveDataAggregator aggregator,
        SavedPlaceCollection places,
        ILocalizer localizer,
        IClock clock,
        IDispatcher dispatcher,
        ILogger<StopsViewModel> logger)
    {
        _settings = settings;
        _location = location;
        _catalog = catalog;
        _aggregator = aggregator;
        _places = places;
        _localizer = localizer;
        _clock = clock;
        _dispatcher = dispatcher;
        _logger = logger;
        _statusText = localizer.Get("StopsEmpty");
        _emptyText = _statusText;
        localizer.LanguageChanged += (_, _) =>
        {
            var current = localizer.Get("StopsEmpty");
            if (StatusText == _emptyText)
            {
                StatusText = current;
            }

            _emptyText = current;
            OnPropertyChanged(nameof(EmptyMessage));
        };
    }

    public ObservableCollection<StopItemViewModel> Stops { get; } = [];

    public ObservableCollection<SavedPlace> Places => _places.Items;

    public string EmptyMessage => _localizer.Get("StopsEmpty");

    partial void OnSelectedAnchorChanged(SavedPlace? value) => _ = LoadCommand.ExecuteAsync(null);

    /// <summary>Called by the shell when this tab is visible in a non-minimized window; loads once and keeps data fresh.</summary>
    public void SetActive(bool active)
    {
        if (!active)
        {
            _tickerCts?.Cancel();
            _tickerCts = null;
            return;
        }

        if (_tickerCts is not null)
        {
            return;
        }

        _tickerCts = new CancellationTokenSource();
        var token = _tickerCts.Token;
        if (!_loadedOnce)
        {
            _ = LoadCommand.ExecuteAsync(null);
        }

        _ = RunTickerAsync(token);
    }

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        _loadedOnce = true;
        try
        {
            StatusText = _localizer.Get("LoadingStops");
            var stops = await _catalog.GetStopsAsync(cancellationToken);
            if (!TryResolveOrigin(stops, out var latitude, out var longitude, out var failure, out var accuracy, out var originText))
            {
                var result = await ResolveLocationAsync(cancellationToken);
                if (result.Status != LocationStatus.Ok)
                {
                    Stops.Clear();
                    LocationText = string.Empty;
                    StatusText = _localizer.Get("Location" + result.Status);
                    return;
                }

                latitude = result.Latitude;
                longitude = result.Longitude;
                accuracy = result.AccuracyMeters;
                originText = string.Format(CultureInfo.CurrentCulture, _localizer.Get("LocationAccuracy"), (int)Math.Round(accuracy));
            }
            else if (failure is not null)
            {
                Stops.Clear();
                StatusText = failure;
                return;
            }

            LocationText = originText;
            var nearby = Geo.FindNearby(stops, latitude, longitude, MaxStops, MaxDistanceMeters);
            _lastNearby = nearby;
            Reconcile(nearby);
            StatusText = nearby.Count == 0 ? _localizer.Get("NoStopsNearby") : string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(exception, "Could not load nearby stops.");
            StatusText = _localizer.Get("StopsLoadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleStopAsync(StopItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsExpanded = !item.IsExpanded;
        if (item.IsExpanded)
        {
            await LoadArrivalsAsync(item, CancellationToken.None);
        }
    }

    /// <summary>Re-queries only expanded stops (the only ones visible), reusing rows so the list does not flicker.</summary>
    public async Task RefreshExpandedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var item in Stops.Where(stop => stop.IsExpanded).ToArray())
        {
            await LoadArrivalsAsync(item, cancellationToken);
        }
    }

    public void Tick()
    {
        var now = _clock.UtcNow;
        foreach (var row in Stops.SelectMany(stop => stop.Arrivals))
        {
            row.Refresh(now);
        }
    }

    /// <summary>
    /// Every line that is due soon gets a row: each line's next arrival, plus its second one, soonest first. A plain "first N
    /// arrivals" would let a frequent line push the others off the list.
    /// </summary>
    public static IReadOnlyList<Arrival> PickRows(IEnumerable<Arrival> arrivals)
    {
        var lines = arrivals
            .GroupBy(arrival => arrival.LineName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(arrival => arrival.Eta).ToList())
            .ToList();
        return lines.Select(line => line[0])
            .Concat(lines.Where(line => line.Count > 1).Select(line => line[1]))
            .OrderBy(arrival => arrival.Eta)
            .Take(MaxArrivalRows)
            .ToArray();
    }

    private async Task LoadArrivalsAsync(StopItemViewModel item, CancellationToken cancellationToken)
    {
        item.IsLoading = item.Arrivals.Count == 0;
        try
        {
            var result = await _aggregator.GetArrivalsAsync(item.Code, cancellationToken);
            var now = _clock.UtcNow;
            var arrivals = PickRows(result.Arrivals);
            for (var i = 0; i < arrivals.Count; i++)
            {
                if (i < item.Arrivals.Count)
                {
                    item.Arrivals[i].Update(arrivals[i], now);
                }
                else
                {
                    item.Arrivals.Add(new ArrivalRowViewModel(arrivals[i], _localizer, now));
                }
            }

            while (item.Arrivals.Count > arrivals.Count)
            {
                item.Arrivals.RemoveAt(item.Arrivals.Count - 1);
            }

            item.ArrivalsStatus = arrivals.Count == 0
                ? _localizer.Get("NoArrivals")
                : arrivals.Any(arrival => arrival.IsLive) ? string.Empty : _localizer.Get("NoLiveData");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            item.IsLoading = false;
        }
    }

    /// <summary>Pinned stops first, then nearest first. Pinning is stored in the config, so it survives restarts.</summary>
    [RelayCommand]
    private async Task TogglePinStopAsync(StopItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var favorites = _settings.Config.FavoriteStopCodes;
        var code = item.Code;
        if (favorites.Contains(code))
        {
            favorites.Remove(code);
        }
        else
        {
            favorites.Add(code);
        }

        await _settings.SaveAsync();
        Reconcile(_lastNearby);
    }

    private void Reconcile(IReadOnlyList<NearbyStop> nearby)
    {
        var favorites = _settings.Config.FavoriteStopCodes.ToHashSet(StringComparer.Ordinal);
        nearby = nearby
            .OrderByDescending(stop => favorites.Contains(stop.Stop.Code))
            .ThenBy(stop => stop.DistanceMeters)
            .ToArray();

        var sameOrder = nearby.Count == Stops.Count && nearby.Select(n => n.Stop.Code).SequenceEqual(Stops.Select(s => s.Code));
        if (sameOrder)
        {
            for (var i = 0; i < nearby.Count; i++)
            {
                Stops[i].UpdateDistance(nearby[i].DistanceMeters, _localizer);
                Stops[i].IsFavorite = favorites.Contains(nearby[i].Stop.Code);
            }

            return;
        }

        var expanded = Stops.Where(stop => stop.IsExpanded).Select(stop => stop.Code).ToHashSet();
        Stops.Clear();
        foreach (var stop in nearby)
        {
            Stops.Add(new StopItemViewModel(stop, _localizer) { IsExpanded = false, IsFavorite = favorites.Contains(stop.Stop.Code) });
        }

        foreach (var stop in Stops.Where(stop => expanded.Contains(stop.Code)))
        {
            stop.IsExpanded = true;
            _ = LoadArrivalsAsync(stop, CancellationToken.None);
        }
    }

    private bool TryResolveOrigin(
        IReadOnlyList<Stop> stops,
        out double latitude,
        out double longitude,
        out string? failure,
        out double accuracy,
        out string originText)
    {
        latitude = longitude = accuracy = 0;
        failure = null;
        originText = string.Empty;
        if (SelectedAnchor is null)
        {
            return false;
        }

        var stop = stops.FirstOrDefault(candidate => candidate.Code == SelectedAnchor.StopCode);
        if (stop is null)
        {
            failure = _localizer.Get("PlaceNotFound");
            return true;
        }

        latitude = stop.Latitude;
        longitude = stop.Longitude;
        originText = string.Format(CultureInfo.CurrentCulture, _localizer.Get("LocationFromPlace"), SelectedAnchor.Name);
        return true;
    }

    private Task<LocationResult> ResolveLocationAsync(CancellationToken cancellationToken) =>
        _location.GetCurrentAsync(cancellationToken);

    private async Task RunTickerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            var sinceRefresh = TimeSpan.Zero;
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                sinceRefresh += TimeSpan.FromSeconds(1);
                _dispatcher.Post(Tick);
                if (sinceRefresh >= TimeSpan.FromSeconds(Math.Clamp(_settings.Config.PollingSeconds, 15, 60)))
                {
                    sinceRefresh = TimeSpan.Zero;
                    await RefreshExpandedAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
