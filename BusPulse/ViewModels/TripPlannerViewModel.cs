using System.Collections.ObjectModel;
using System.Globalization;
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

/// <summary>An origin or destination: the user's current location, a saved place, or a specific stop.</summary>
public sealed class LocationChoice
{
    public LocationChoice(string displayName, SavedPlace? place = null, Stop? stop = null)
    {
        DisplayName = displayName;
        Place = place;
        Stop = stop;
    }

    public string DisplayName { get; }

    public SavedPlace? Place { get; }

    public Stop? Stop { get; }

    public bool IsMyLocation => Place is null && Stop is null;
}

public sealed partial class TripOptionViewModel : ObservableObject
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);
    private readonly ILocalizer _localizer;

    [ObservableProperty]
    private TripOption _option;

    [ObservableProperty]
    private string _leaveText = string.Empty;

    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    private bool _isWatched;

    public TripOptionViewModel(TripOption option, double bearingDegrees, bool rtl, ILocalizer localizer, DateTimeOffset now)
    {
        _option = option;
        _localizer = localizer;
        BearingDegrees = bearingDegrees;
        Rtl = rtl;
        Refresh(now);
    }

    public double BearingDegrees { get; private set; }

    public bool Rtl { get; }

    public string Line => Option.Line;

    public string? Operator => Option.Operator;

    public bool IsLive => Option.IsLive;

    public string BadgeText => _localizer.Get(Option.IsLive ? "LiveBadge" : "ScheduledBadge");

    /// <summary>"Origin ◄ Destination" reading in the layout direction.</summary>
    public string RouteText
    {
        get
        {
            var from = Option.FromEndpoint ?? Option.OriginStop.Name;
            var to = Option.ToEndpoint ?? Option.DestinationStop.Name;
            return $"{from} {(Rtl ? "◄" : "►")} {to}";
        }
    }

    public string LeaveAtText => Option.LeaveBy.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    public string BusText => string.Format(CultureInfo.CurrentCulture, _localizer.Get("BusAt"), Option.BusDeparture.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));

    public string ArriveText => string.Format(CultureInfo.CurrentCulture, _localizer.Get("ArriveAt"), Option.ArriveAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));

    public string WalkText => string.Format(
        CultureInfo.CurrentCulture,
        _localizer.Get("WalkTo"),
        Math.Max(1, (int)Math.Round(Option.WalkToStop.TotalMinutes)),
        Option.OriginStop.Name);

    public string Tooltip { get; private set; } = string.Empty;

    public void Update(TripOption option, double bearingDegrees, DateTimeOffset now)
    {
        Option = option;
        BearingDegrees = bearingDegrees;
        OnPropertyChanged(nameof(BearingDegrees));
        foreach (var name in new[] { nameof(Line), nameof(Operator), nameof(IsLive), nameof(BadgeText), nameof(RouteText), nameof(LeaveAtText), nameof(BusText), nameof(ArriveText), nameof(WalkText) })
        {
            OnPropertyChanged(name);
        }

        Refresh(now);
    }

    /// <summary>Recomputes the "leave in" countdown and freshness from the clock without touching the network.</summary>
    public void Refresh(DateTimeOffset now)
    {
        var minutes = (int)Math.Ceiling((Option.LeaveBy - now).TotalMinutes);
        LeaveText = minutes <= 0
            ? _localizer.Get("LeaveNow")
            : string.Format(CultureInfo.CurrentCulture, _localizer.Get("LeaveInMinutes"), minutes);
        var age = now - Option.ObservedAt;
        IsStale = Option.IsLive && age > StaleAfter;
        Tooltip = $"{_localizer.Get("SourceLabel")}: {_localizer.Get("Provider" + Option.Source)} · " +
            string.Format(CultureInfo.CurrentCulture, _localizer.Get("UpdatedAgo"), Math.Max(0, (int)age.TotalSeconds));
        OnPropertyChanged(nameof(Tooltip));
    }
}

public sealed partial class TripPlannerViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly SavedPlaceCollection _places;
    private readonly ILocalizer _localizer;
    private readonly ITripPlannerService _planner;
    private readonly IStopCatalog _catalog;
    private readonly ILocationService _location;
    private readonly IHistoryService _history;
    private readonly IDepartureReminder _reminder;
    private readonly INotifier _notifier;
    private DateTimeOffset _lastPromptAt = DateTimeOffset.MinValue;
    private readonly IClock _clock;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<TripPlannerViewModel> _logger;
    private CancellationTokenSource? _tickerCts;
    private CancellationTokenSource? _suggestCts;
    private TripRequest? _lastRequest;

    [ObservableProperty]
    private LocationChoice _selectedOrigin;

    [ObservableProperty]
    private LocationChoice? _selectedDestination;

    [ObservableProperty]
    private string _lineName;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _selectedModeIndex;

    [ObservableProperty]
    private TimeSpan _targetTime;

    [ObservableProperty]
    private string _destinationQuery = string.Empty;

    public TripPlannerViewModel(
        ISettingsService settings,
        SavedPlaceCollection places,
        ILocalizer localizer,
        ITripPlannerService planner,
        IStopCatalog catalog,
        ILocationService location,
        IHistoryService history,
        IDepartureReminder reminder,
        INotifier notifier,
        IClock clock,
        IDispatcher dispatcher,
        ILogger<TripPlannerViewModel> logger)
    {
        _settings = settings;
        _places = places;
        _localizer = localizer;
        _planner = planner;
        _catalog = catalog;
        _location = location;
        _history = history;
        _reminder = reminder;
        _notifier = notifier;
        _clock = clock;
        _dispatcher = dispatcher;
        _logger = logger;
        MyLocation = new LocationChoice(localizer.Get("MyLocation"));
        RebuildChoices();
        places.Items.CollectionChanged += (_, _) => RebuildChoices();
        // The planner always starts from the user's current location.
        _selectedOrigin = MyLocation;
        _selectedDestination = settings.Config.DestinationPlaceId is { } destinationId
            ? Choices.FirstOrDefault(c => c.Place?.Id == destinationId)
            : null;
        _lineName = settings.Config.LineName ?? string.Empty;
        _statusText = localizer.Get("PlanHint");
        _targetTime = TimeSpan.FromHours(DateTime.Now.Hour).Add(TimeSpan.FromMinutes(DateTime.Now.Minute));
        reminder.Changed += (_, _) => _dispatcher.Post(SyncWatched);
    }

    public LocationChoice MyLocation { get; }

    public ObservableCollection<LocationChoice> Choices { get; } = [];

    public ObservableCollection<LocationChoice> Suggestions { get; } = [];

    public ObservableCollection<TripOptionViewModel> Options { get; } = [];

    /// <summary>Delay before suggestions are recomputed after typing; zero in tests.</summary>
    public TimeSpan DebounceDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public bool HasHome => FindPlace(_settings.Config.HomePlaceId) is not null;

    public bool HasWork => FindPlace(_settings.Config.WorkPlaceId) is not null;

    public TripTimeMode Mode => SelectedModeIndex switch
    {
        1 => TripTimeMode.DepartAt,
        2 => TripTimeMode.ArriveBy,
        _ => TripTimeMode.Now
    };

    public bool IsTimeSelectable => Mode != TripTimeMode.Now;

    public string RouteSummary => SelectedDestination is null
        ? _localizer.Get("WaitingForRoute")
        : $"{SelectedOrigin.DisplayName} → {SelectedDestination.DisplayName}";

    partial void OnSelectedOriginChanged(LocationChoice value) => OnPropertyChanged(nameof(RouteSummary));

    partial void OnSelectedDestinationChanged(LocationChoice? value) => OnPropertyChanged(nameof(RouteSummary));

    partial void OnSelectedModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsTimeSelectable));
        if (Mode != TripTimeMode.Now)
        {
            PromptForRoute("PromptSelectRoute");
        }
    }

    partial void OnTargetTimeChanged(TimeSpan value)
    {
        if (Mode != TripTimeMode.Now)
        {
            PromptForRoute("PromptSelectRoute");
        }
    }

    /// <summary>Reminds the user that alerts need a selected route; at most once a minute and never while one is selected.</summary>
    private void PromptForRoute(string key)
    {
        var now = _clock.UtcNow;
        if (_reminder.Watched is not null || now - _lastPromptAt < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastPromptAt = now;
        _notifier.Notify("BusPulse", _localizer.Get(key), NotificationKind.Info);
    }

    partial void OnDestinationQueryChanged(string value) => _ = UpdateSuggestionsAsync();

    /// <summary>Called by the shell while this tab is visible: keeps the plan fresh and counts down.</summary>
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
        _ = RunTickerAsync(_tickerCts.Token);
    }

    public void SetDestination(LocationChoice choice)
    {
        SelectedDestination = choice;
        DestinationQuery = string.Empty;
        Suggestions.Clear();
    }

    public void SetOrigin(Stop stop)
    {
        SelectedOrigin = new LocationChoice(stop.Name, stop: stop);
    }

    [RelayCommand]
    private void Swap()
    {
        if (SelectedDestination is null)
        {
            return;
        }

        var origin = SelectedOrigin;
        SelectedOrigin = SelectedDestination;
        SelectedDestination = origin;
    }

    [RelayCommand]
    private void GoHome()
    {
        var place = FindPlace(_settings.Config.HomePlaceId);
        if (place is not null)
        {
            SetDestination(Choices.First(choice => choice.Place?.Id == place.Id));
        }
    }

    [RelayCommand]
    private void GoWork()
    {
        var place = FindPlace(_settings.Config.WorkPlaceId);
        if (place is not null)
        {
            SetDestination(Choices.First(choice => choice.Place?.Id == place.Id));
        }
    }

    [RelayCommand]
    private void ChooseSuggestion(LocationChoice? choice)
    {
        if (choice is not null)
        {
            SetDestination(choice);
        }
    }

    [RelayCommand]
    private void ClearSuggestions()
    {
        DestinationQuery = string.Empty;
        Suggestions.Clear();
    }

    /// <summary>Filters as you type (debounced): recent destinations first, then matching saved places.</summary>
    public async Task UpdateSuggestionsAsync()
    {
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        try
        {
            if (DebounceDelay > TimeSpan.Zero)
            {
                await Task.Delay(DebounceDelay, cts.Token);
            }

            var query = DestinationQuery.Trim();
            Suggestions.Clear();
            if (query.Length == 0)
            {
                return;
            }

            var recentIds = _history.Recent.Select(entry => entry.PlaceId).ToList();
            var matches = Choices
                .Where(choice => choice.Place is not null &&
                    choice.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(choice => recentIds.IndexOf(choice.Place!.Id) is var index && index < 0 ? int.MaxValue : index)
                .ThenBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Take(6);
            foreach (var match in matches)
            {
                Suggestions.Add(match);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task PlanAsync(CancellationToken cancellationToken)
    {
        if (SelectedDestination is null)
        {
            StatusText = _localizer.Get("SetDestinationFirst");
            return;
        }

        if (SelectedDestination == SelectedOrigin)
        {
            StatusText = _localizer.Get("SameOriginDestination");
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = _localizer.Get("Planning");
            var stops = await _catalog.GetStopsAsync(cancellationToken);
            var origin = await ResolveAsync(SelectedOrigin, stops, cancellationToken);
            var destination = await ResolveAsync(SelectedDestination, stops, cancellationToken);
            if (origin.Failure is not null || destination.Failure is not null)
            {
                Options.Clear();
                StatusText = origin.Failure ?? destination.Failure!;
                return;
            }

            _lastRequest = new TripRequest(
                origin.Latitude,
                origin.Longitude,
                destination.Latitude,
                destination.Longitude,
                Mode,
                Mode == TripTimeMode.Now ? null : ResolveTarget(_clock.UtcNow),
                string.IsNullOrWhiteSpace(LineName) ? null : LineName.Trim(),
                _settings.Config.BufferMinutes);
            await RunPlanAsync(_lastRequest, cancellationToken);
            await RememberSelectionAsync();
            if (Mode != TripTimeMode.Now && Options.Count > 0)
            {
                PromptForRoute("PromptEnableAlerts");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(exception, "Trip planning failed.");
            StatusText = _localizer.Get("PlanFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleReminder(TripOptionViewModel? item)
    {
        if (item is null || _lastRequest is null)
        {
            return;
        }

        if (_reminder.Watched?.RideId == item.Option.RideId)
        {
            _reminder.Cancel();
        }
        else
        {
            _reminder.Watch(_lastRequest, item.Option);
        }
    }

    /// <summary>Re-plans with the last request, e.g. on the polling timer, updating rows in place.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_lastRequest is null)
        {
            return;
        }

        try
        {
            await RunPlanAsync(_lastRequest, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(exception, "Trip refresh failed.");
        }
    }

    public void Tick()
    {
        var now = _clock.UtcNow;
        foreach (var option in Options)
        {
            option.Refresh(now);
        }
    }

    private async Task RunPlanAsync(TripRequest request, CancellationToken cancellationToken)
    {
        // "Now" requests are re-based on every refresh so the search window moves with the clock.
        var plan = await _planner.PlanAsync(request, cancellationToken);
        Reconcile(plan.Options, request);
        StatusText = plan.Status switch
        {
            TripPlanStatus.Ok => string.Empty,
            TripPlanStatus.NoStopsNearOrigin => _localizer.Get("PlanNoStopsNearOrigin"),
            TripPlanStatus.NoStopsNearDestination => _localizer.Get("PlanNoStopsNearDestination"),
            _ => _localizer.Get("PlanNoOptions")
        };
    }

    private void Reconcile(IReadOnlyList<TripOption> options, TripRequest request)
    {
        var now = _clock.UtcNow;
        var rtl = _settings.Config.Language == "he";
        var bearing = Geo.BearingDegrees(request.OriginLatitude, request.OriginLongitude, request.DestinationLatitude, request.DestinationLongitude);
        for (var i = 0; i < options.Count; i++)
        {
            if (i < Options.Count)
            {
                Options[i].Update(options[i], bearing, now);
            }
            else
            {
                Options.Add(new TripOptionViewModel(options[i], bearing, rtl, _localizer, now));
            }
        }

        while (Options.Count > options.Count)
        {
            Options.RemoveAt(Options.Count - 1);
        }

        SyncWatched();
    }

    private void SyncWatched()
    {
        var watched = _reminder.Watched?.RideId;
        foreach (var option in Options)
        {
            option.IsWatched = watched is not null && option.Option.RideId == watched;
        }
    }

    private async Task RememberSelectionAsync()
    {
        var config = _settings.Config;
        config.DestinationPlaceId = SelectedDestination?.Place?.Id;
        config.LineName = string.IsNullOrWhiteSpace(LineName) ? null : LineName.Trim();
        if (SelectedDestination?.Place is { } place)
        {
            _history.Record(place.Id, place.Name);
        }

        await _settings.SaveAsync();
    }

    /// <summary>Time-of-day targets mean today, or tomorrow when that time has already passed.</summary>
    private DateTimeOffset ResolveTarget(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var target = new DateTimeOffset(local.Date + TargetTime, local.Offset);
        if (target < local - TimeSpan.FromMinutes(1))
        {
            target = target.AddDays(1);
        }

        return target.ToUniversalTime();
    }

    private async Task<(double Latitude, double Longitude, string? Failure)> ResolveAsync(
        LocationChoice choice,
        IReadOnlyList<Stop> stops,
        CancellationToken cancellationToken)
    {
        if (choice.Stop is { } stop)
        {
            return (stop.Latitude, stop.Longitude, null);
        }

        if (choice.Place is { } place)
        {
            var found = stops.FirstOrDefault(candidate => candidate.Code == place.StopCode);
            return found is null
                ? (0, 0, _localizer.Get("PlaceNotFound"))
                : (found.Latitude, found.Longitude, null);
        }

        var result = await _location.GetCurrentAsync(cancellationToken);
        return result.Status == LocationStatus.Ok
            ? (result.Latitude, result.Longitude, null)
            : (0, 0, _localizer.Get("Location" + result.Status));
    }

    private SavedPlace? FindPlace(string? id) => id is null ? null : _places.Items.FirstOrDefault(place => place.Id == id);

    private void RebuildChoices()
    {
        Choices.Clear();
        Choices.Add(MyLocation);
        foreach (var place in _places.Items)
        {
            Choices.Add(new LocationChoice(place.Name, place));
        }

        OnPropertyChanged(nameof(HasHome));
        OnPropertyChanged(nameof(HasWork));
    }

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
                    await RefreshAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
