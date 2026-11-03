using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BusPulse.ViewModels;

/// <summary>A direction of a line that leaves the origin stop, identified by its ride.</summary>
public sealed record LineDirection(long RideId, string Destination, DateTimeOffset Departure);

/// <summary>One stop of the selected ride, in calling order.</summary>
public sealed record LineStopRow(int Sequence, string Name, string Code, DateTimeOffset Time, bool IsOrigin)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Stops of a line in one direction, from the Stride schedule. Live vehicle positions are not shown: no connected source
/// provides them, so the view says so and shows the schedule only.
/// </summary>
public sealed partial class LinesViewModel : ObservableObject
{
    private static readonly TimeSpan SearchWindow = TimeSpan.FromHours(3);
    private static readonly TimeSpan BeforeDeparture = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AfterDeparture = TimeSpan.FromHours(3);
    private const int MaxLineNameLength = 32;

    private readonly ISettingsService _settings;
    private readonly SavedPlaceCollection _places;
    private readonly ILocalizer _localizer;
    private readonly IStopCatalog _catalog;
    private readonly ILocationService _location;
    private readonly IRideScheduleSource _schedule;
    private readonly IClock _clock;
    private readonly ILogger<LinesViewModel> _logger;
    private CancellationTokenSource? _loadCts;
    private string _hintText;

    [ObservableProperty]
    private string _lineName = string.Empty;

    [ObservableProperty]
    private LocationChoice _selectedOrigin;

    [ObservableProperty]
    private LineDirection? _selectedDirection;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isFavoriteLine;

    public LinesViewModel(
        ISettingsService settings,
        SavedPlaceCollection places,
        ILocalizer localizer,
        IStopCatalog catalog,
        ILocationService location,
        IRideScheduleSource schedule,
        IClock clock,
        ILogger<LinesViewModel> logger)
    {
        _settings = settings;
        _places = places;
        _localizer = localizer;
        _catalog = catalog;
        _location = location;
        _schedule = schedule;
        _clock = clock;
        _logger = logger;
        MyLocation = new LocationChoice(localizer.Get("MyLocation"));
        _hintText = localizer.Get("LinesHint");
        localizer.LanguageChanged += (_, _) => RefreshLanguage();
        _selectedOrigin = MyLocation;
        _statusText = _hintText;
        FavoriteLines = new ObservableCollection<string>(settings.Config.FavoriteLines);
        RebuildOrigins();
        places.Items.CollectionChanged += (_, _) => RebuildOrigins();
    }

    private void RefreshLanguage()
    {
        MyLocation.DisplayName = _localizer.Get("MyLocation");
        var newHint = _localizer.Get("LinesHint");
        if (StatusText == _hintText)
        {
            StatusText = newHint;
        }

        _hintText = newHint;
        OnPropertyChanged(nameof(PinText));
        OnPropertyChanged(nameof(ScheduleNotice));
    }

    private void RebuildOrigins()
    {
        var previous = SelectedOrigin;
        Origins.Clear();
        Origins.Add(MyLocation);
        foreach (var place in _places.Items)
        {
            Origins.Add(new LocationChoice(place.Name, place));
        }

        // WPF pushes null into SelectedItem when the source is cleared; restore a valid origin.
        if (previous is null || previous.IsMyLocation)
        {
            SelectedOrigin = MyLocation;
        }
        else if (previous.Place is { } place)
        {
            SelectedOrigin = Origins.FirstOrDefault(c => c.Place?.Id == place.Id) ?? MyLocation;
        }
        else
        {
            Origins.Add(previous);
            SelectedOrigin = previous;
        }
    }

    public LocationChoice MyLocation { get; }

    /// <summary>Raised by the context menu on a stop row: start a trip from that stop.</summary>
    public event EventHandler<Stop>? PlanRouteFromRequested;

    /// <summary>Makes a stop the starting point (used by "show lines from here" in the Stops tab).</summary>
    public void SetOrigin(Stop stop)
    {
        var choice = new LocationChoice(stop.Name, stop: stop);
        Origins.Add(choice);
        SelectedOrigin = choice;
    }

    /// <summary>Re-reads the pinned lines after they changed elsewhere.</summary>
    public void ReloadFavorites()
    {
        FavoriteLines.Clear();
        foreach (var line in _settings.Config.FavoriteLines)
        {
            FavoriteLines.Add(line);
        }

        IsFavoriteLine = FavoriteLines.Contains(LineName.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task PlanRouteFromLineStopAsync(LineStopRow? row)
    {
        if (row is null)
        {
            return;
        }

        var stops = await _catalog.GetStopsAsync();
        var stop = stops.FirstOrDefault(candidate => candidate.Code == row.Code);
        if (stop is not null)
        {
            PlanRouteFromRequested?.Invoke(this, stop);
        }
    }

    public ObservableCollection<LocationChoice> Origins { get; } = [];

    public ObservableCollection<LineDirection> Directions { get; } = [];

    public ObservableCollection<LineStopRow> Stops { get; } = [];

    public ObservableCollection<string> FavoriteLines { get; }

    public string ScheduleNotice => _localizer.Get("LinesScheduleOnly");

    partial void OnSelectedDirectionChanged(LineDirection? value)
    {
        if (value is not null)
        {
            _ = LoadStopsAsync(value);
        }
        else
        {
            Stops.Clear();
        }
    }

    partial void OnLineNameChanged(string value) => IsFavoriteLine = FavoriteLines.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    partial void OnIsFavoriteLineChanged(bool value) => OnPropertyChanged(nameof(PinText));

    public string PinText => _localizer.Get(IsFavoriteLine ? "LineUnpin" : "LinePin");

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var line = LineName.Trim();
        if (line.Length == 0 || line.Length > MaxLineNameLength)
        {
            StatusText = _localizer.Get("LinesEnterLine");
            return;
        }

        IsBusy = true;
        try
        {
            var stop = await ResolveOriginStopAsync(cancellationToken);
            if (stop is null)
            {
                return;
            }

            var now = _clock.UtcNow;
            var departures = await _schedule.GetStopDeparturesAsync(stop.Id, now, now + SearchWindow, cancellationToken);
            var directions = departures
                .Where(row => string.Equals(row.Line, line, StringComparison.OrdinalIgnoreCase))
                .GroupBy(row => row.RideId)
                .Select(group => group.OrderBy(row => row.Time).First())
                .Select(row => new LineDirection(row.RideId, RouteEndpoints.Destination(row.RouteLongName) ?? line, row.Time))
                .GroupBy(direction => direction.Destination, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => group.OrderBy(direction => direction.Departure).First())
                .OrderBy(direction => direction.Departure)
                .ToArray();

            Directions.Clear();
            Stops.Clear();
            foreach (var direction in directions)
            {
                Directions.Add(direction);
            }

            StatusText = directions.Length == 0 ? _localizer.Get("LinesNoService") : string.Empty;
            SelectedDirection = directions.FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not load the line schedule.");
            StatusText = _localizer.Get("LinesLoadFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void TogglePinLine()
    {
        var line = LineName.Trim();
        if (line.Length == 0)
        {
            return;
        }

        var existing = FavoriteLines.FirstOrDefault(name => string.Equals(name, line, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            FavoriteLines.Add(line);
        }
        else
        {
            FavoriteLines.Remove(existing);
        }

        _settings.Config.FavoriteLines = FavoriteLines.ToList();
        IsFavoriteLine = existing is null;
        _ = _settings.SaveAsync();
    }

    [RelayCommand]
    private void SelectFavoriteLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            LineName = line;
        }
    }

    private async Task<Stop?> ResolveOriginStopAsync(CancellationToken cancellationToken)
    {
        var stops = await _catalog.GetStopsAsync(cancellationToken);
        if (SelectedOrigin.Stop is { } stop)
        {
            return stop;
        }

        if (SelectedOrigin.Place is { } place)
        {
            var found = stops.FirstOrDefault(candidate => candidate.Code == place.StopCode);
            if (found is null)
            {
                StatusText = _localizer.Get("PlaceNotFound");
            }

            return found;
        }

        var result = await _location.GetCurrentAsync(cancellationToken);
        if (result.Status != LocationStatus.Ok)
        {
            StatusText = _localizer.Get("Location" + result.Status);
            return null;
        }

        var nearest = Geo.FindNearby(stops.Where(candidate => candidate.Id > 0), result.Latitude, result.Longitude, 1, TripPlannerService.MaxWalkMeters);
        if (nearest.Count == 0)
        {
            StatusText = _localizer.Get("NoStopsNearby");
            return null;
        }

        return nearest[0].Stop;
    }

    private async Task LoadStopsAsync(LineDirection direction)
    {
        try
        {
            var from = direction.Departure - BeforeDeparture;
            var rows = await _schedule.GetRideStopsAsync(direction.RideId, from, direction.Departure + AfterDeparture);
            var origin = SelectedOrigin.Stop?.Code ?? SelectedOrigin.Place?.StopCode;
            Stops.Clear();
            foreach (var row in rows.OrderBy(row => row.Sequence))
            {
                Stops.Add(new LineStopRow(
                    row.Sequence,
                    row.StopName ?? string.Empty,
                    row.StopCode ?? string.Empty,
                    row.Time,
                    row.StopCode is not null && row.StopCode == origin));
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not load the stops of the selected direction.");
            StatusText = _localizer.Get("LinesLoadFailed");
        }
    }
}
