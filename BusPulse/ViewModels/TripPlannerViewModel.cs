using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.Services.Calendar;
using BusPulse.Services.Geocoding;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BusPulse.ViewModels;

public enum LocationChoiceKind
{
    MyLocation,
    Place,
    Stop,

    /// <summary>An ad-hoc address found by the address search: coordinates and a label, nothing else.</summary>
    Address
}

/// <summary>An origin or destination: the user's current location, a saved place, a specific stop or a searched address.</summary>
public sealed partial class LocationChoice : ObservableObject
{
    public LocationChoice(string displayName, SavedPlace? place = null, Stop? stop = null, GeocodedPlace? address = null)
    {
        _displayName = displayName;
        Place = place;
        Stop = stop;
        Address = address;
    }

    [ObservableProperty]
    private string _displayName;

    public SavedPlace? Place { get; }

    public Stop? Stop { get; }

    public GeocodedPlace? Address { get; }

    public bool IsMyLocation => Place is null && Stop is null && Address is null;

    public LocationChoiceKind Kind => Place is not null ? LocationChoiceKind.Place
        : Stop is not null ? LocationChoiceKind.Stop
        : Address is not null ? LocationChoiceKind.Address
        : LocationChoiceKind.MyLocation;

    /// <summary>Stops and addresses can be kept as a saved place; the rest already are, or have no fixed point.</summary>
    public bool CanSave => Kind is LocationChoiceKind.Stop or LocationChoiceKind.Address;

    /// <summary>The list boxes show <see cref="DisplayName"/>, and a screen reader names an item by its text.</summary>
    public override string ToString() => DisplayName;
}

/// <summary>A calendar event that matched a saved place, ready to show as a one-click trip.</summary>
public sealed class CalendarSuggestionItem(CalendarSuggestion suggestion)
{
    public CalendarSuggestion Suggestion { get; } = suggestion;

    public string Title => Suggestion.Event.Title;

    public string PlaceName => Suggestion.Place.Name;

    public string StartText => TimeZoneInfo.ConvertTime(Suggestion.Event.Start, TripTime.IsraelZone)
        .ToString("HH:mm", CultureInfo.InvariantCulture);
}

public sealed partial class TripOptionViewModel : ObservableObject
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);

    /// <summary>From this many minutes away a departure is shown by its clock time rather than a countdown.</summary>
    private const int DistantDepartureMinutes = 180;
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

    public bool Rtl { get; private set; }

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

    public string WalkText => Option.WalkToStop < TimeSpan.FromSeconds(30)
        ? string.Format(CultureInfo.CurrentCulture, _localizer.Get("WalkUnderMinute"), Option.OriginStop.Name)
        : string.Format(
            CultureInfo.CurrentCulture,
            _localizer.Get("WalkTo"),
            (int)Math.Round(Option.WalkToStop.TotalMinutes),
            Option.OriginStop.Name);

    public string Tooltip { get; private set; } = string.Empty;

    /// <summary>"Timetable for Mon 9 Nov" when the bus leaves on a day after today; empty for today. Never live.</summary>
    public string DateText { get; private set; } = string.Empty;

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

    /// <summary>Re-reads every localized text and the reading direction after a language change.</summary>
    public void RefreshLanguage(bool rtl, DateTimeOffset now)
    {
        Rtl = rtl;
        foreach (var name in new[] { nameof(Rtl), nameof(BadgeText), nameof(RouteText), nameof(BusText), nameof(ArriveText), nameof(WalkText) })
        {
            OnPropertyChanged(name);
        }

        Refresh(now);
    }

    /// <summary>Recomputes the "leave in" countdown and freshness from the clock without touching the network.</summary>
    public void Refresh(DateTimeOffset now)
    {
        var departureDay = TripTime.TodayIn(Option.BusDeparture, TripTime.IsraelZone);
        DateText = departureDay > TripTime.TodayIn(now, TripTime.IsraelZone)
            ? string.Format(CultureInfo.CurrentCulture, _localizer.Get("PlannedForDate"), TripPlannerViewModel.FormatDate(departureDay))
            : string.Empty;
        OnPropertyChanged(nameof(DateText));
        var minutes = (int)Math.Ceiling((Option.LeaveBy - now).TotalMinutes);
        // "Leave in 1441 minutes" is unreadable: a distant departure (tomorrow, or hours away) names its clock time instead.
        LeaveText = minutes <= 0
            ? _localizer.Get("LeaveNow")
            : minutes >= DistantDepartureMinutes
                ? string.Format(CultureInfo.CurrentCulture, _localizer.Get("LeaveAtClock"), LeaveAtText)
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
    private readonly ICalendarService _calendar;
    private readonly IGeocoder? _geocoder;
    private CancellationTokenSource? _originSuggestCts;
    private DateTimeOffset _lastPromptAt = DateTimeOffset.MinValue;
    private readonly IClock _clock;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<TripPlannerViewModel> _logger;
    private CancellationTokenSource? _tickerCts;
    private CancellationTokenSource? _suggestCts;
    private TripRequest? _lastRequest;
    private string _hintText;

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

    [ObservableProperty]
    private string _originQuery = string.Empty;

    /// <summary>The Israel-local calendar day the trip is planned for (time of day comes from <see cref="TargetTime"/>).</summary>
    [ObservableProperty]
    private DateTime? _targetDate;

    /// <summary>Outcome of the last address search started from the destination box (empty when none).</summary>
    [ObservableProperty]
    private string _destinationSearchMessage = string.Empty;

    /// <summary>Outcome of the last address search started from the origin box (empty when none).</summary>
    [ObservableProperty]
    private string _originSearchMessage = string.Empty;

    /// <summary>Shown when the chosen time has already passed today, so the jump to tomorrow is never silent.</summary>
    [ObservableProperty]
    private string _dateNotice = string.Empty;

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
        ILogger<TripPlannerViewModel> logger,
        ICalendarService calendar,
        IGeocoder? geocoder = null)
    {
        _calendar = calendar;
        _geocoder = geocoder;
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
        _hintText = localizer.Get("PlanHint");
        localizer.LanguageChanged += (_, _) => RefreshLanguage();
        RebuildChoices();
        places.Items.CollectionChanged += (_, _) => RebuildChoices();
        // The planner always starts from the user's current location.
        _selectedOrigin = MyLocation;
        _selectedDestination = settings.Config.DestinationPlaceId is { } destinationId
            ? Choices.FirstOrDefault(c => c.Place?.Id == destinationId)
            : null;
        _lineName = settings.Config.LineName ?? string.Empty;
        _statusText = _hintText;
        _targetTime = TimeSpan.FromHours(DateTime.Now.Hour).Add(TimeSpan.FromMinutes(DateTime.Now.Minute));
        _targetDate = Today.ToDateTime(TimeOnly.MinValue);
        reminder.Changed += (_, _) => _dispatcher.Post(SyncWatched);
    }

    public LocationChoice MyLocation { get; }

    public ObservableCollection<LocationChoice> Choices { get; } = [];

    public ObservableCollection<LocationChoice> Suggestions { get; } = [];

    public ObservableCollection<LocationChoice> OriginSuggestions { get; } = [];

    public ObservableCollection<TripOptionViewModel> Options { get; } = [];

    public ObservableCollection<CalendarSuggestionItem> CalendarSuggestions { get; } = [];

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

    /// <summary>The date selector is shown with the time selector ("Now" is always today, with live data).</summary>
    public bool IsDateSelectable => IsTimeSelectable;

    /// <summary>Today's date in Israel, from the injected clock.</summary>
    public DateOnly Today => TripTime.TodayIn(_clock.UtcNow, TripTime.IsraelZone);

    /// <summary>First selectable day for the date picker.</summary>
    public DateTime MinDate => Today.ToDateTime(TimeOnly.MinValue);

    /// <summary>Last selectable day for the date picker (today plus <see cref="TripTime.MaxDaysAhead"/> days).</summary>
    public DateTime MaxDate => Today.AddDays(TripTime.MaxDaysAhead).ToDateTime(TimeOnly.MinValue);

    public bool HasDateNotice => DateNotice.Length > 0;

    public bool IsAddressSearchEnabled => _settings.Config.AddressSearchEnabled;

    public bool CanSaveOrigin => SelectedOrigin.CanSave;

    public bool CanSaveDestination => SelectedDestination?.CanSave == true;

    public string RouteSummary => SelectedDestination is null
        ? _localizer.Get("WaitingForRoute")
        : $"{SelectedOrigin.DisplayName} → {SelectedDestination.DisplayName}";

    partial void OnSelectedOriginChanged(LocationChoice value)
    {
        OnPropertyChanged(nameof(RouteSummary));
        OnPropertyChanged(nameof(CanSaveOrigin));
    }

    partial void OnSelectedDestinationChanged(LocationChoice? value)
    {
        OnPropertyChanged(nameof(RouteSummary));
        OnPropertyChanged(nameof(CanSaveDestination));
    }

    partial void OnSelectedModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsTimeSelectable));
        OnPropertyChanged(nameof(IsDateSelectable));
        UpdateDateNotice();
        if (Mode != TripTimeMode.Now)
        {
            PromptForRoute("PromptSelectRoute");
        }
    }

    partial void OnTargetTimeChanged(TimeSpan value)
    {
        UpdateDateNotice();
        if (Mode != TripTimeMode.Now)
        {
            PromptForRoute("PromptSelectRoute");
        }
    }

    partial void OnTargetDateChanged(DateTime? value) => UpdateDateNotice();

    partial void OnDateNoticeChanged(string value) => OnPropertyChanged(nameof(HasDateNotice));

    [RelayCommand]
    private void SelectToday() => TargetDate = Today.ToDateTime(TimeOnly.MinValue);

    [RelayCommand]
    private void SelectTomorrow() => TargetDate = Today.AddDays(1).ToDateTime(TimeOnly.MinValue);

    /// <summary>The chosen day, kept within today..today+60 days.</summary>
    private DateOnly SelectedDay()
    {
        var today = Today;
        var day = TargetDate is { } picked ? DateOnly.FromDateTime(picked) : today;
        return day < today ? today : day > today.AddDays(TripTime.MaxDaysAhead) ? today.AddDays(TripTime.MaxDaysAhead) : day;
    }

    /// <summary>Tells the user, in words and with the date, when today's chosen time has already gone by.</summary>
    private void UpdateDateNotice()
    {
        var now = _clock.UtcNow;
        DateNotice = Mode != TripTimeMode.Now && SelectedDay() == Today && TripTime.HasPassed(now, Today, TargetTime, TripTime.IsraelZone)
            ? string.Format(CultureInfo.CurrentCulture, _localizer.Get("TimePassedTomorrow"), FormatDate(Today.AddDays(1)))
            : string.Empty;
    }

    internal static string FormatDate(DateOnly date) =>
        date.ToDateTime(TimeOnly.MinValue).ToString("ddd d MMM", CultureInfo.CurrentCulture);

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

    /// <summary>Re-reads text that was captured while the previous language was active.</summary>
    private void RefreshLanguage()
    {
        MyLocation.DisplayName = _localizer.Get("MyLocation");
        var newHint = _localizer.Get("PlanHint");
        if (StatusText == _hintText)
        {
            StatusText = newHint;
        }

        _hintText = newHint;
        OnPropertyChanged(nameof(RouteSummary));
        UpdateDateNotice();
        var rtl = _settings.Config.Language == "he";
        var now = _clock.UtcNow;
        foreach (var option in Options)
        {
            option.RefreshLanguage(rtl, now);
        }
    }

    /// <summary>Raised when the destination search should take keyboard focus (Ctrl+F).</summary>
    public event EventHandler? SearchFocusRequested;

    public void RequestSearchFocus() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Called by the shell while this tab is visible: keeps the plan fresh and counts down.</summary>
    public void SetActive(bool active)
    {
        if (!active)
        {
            _tickerCts?.Cancel();
            _tickerCts = null;
            return;
        }
        // The Settings switch may have changed while another tab was showing.
        OnPropertyChanged(nameof(IsAddressSearchEnabled));

        if (_tickerCts is not null)
        {
            return;
        }

        _tickerCts = new CancellationTokenSource();
        _ = RunTickerAsync(_tickerCts.Token);
        _ = LoadCalendarSuggestionsAsync(_tickerCts.Token);
    }

    /// <summary>Refreshes trip suggestions from the connected calendar; any failure just leaves the list empty.</summary>
    public async Task LoadCalendarSuggestionsAsync(CancellationToken ct = default)
    {
        try
        {
            if (!_calendar.IsConnected)
            {
                CalendarSuggestions.Clear();
                return;
            }

            var now = _clock.UtcNow;
            var events = await _calendar.GetUpcomingAsync(now, ct);
            var suggestions = CalendarTripSuggester.Suggest(events, _places.Items, now);
            CalendarSuggestions.Clear();
            foreach (var suggestion in suggestions)
            {
                CalendarSuggestions.Add(new CalendarSuggestionItem(suggestion));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning(exception, "Loading calendar suggestions failed.");
            CalendarSuggestions.Clear();
        }
    }

    [RelayCommand]
    private async Task UseCalendarSuggestionAsync(CalendarSuggestionItem? item)
    {
        if (item is null)
        {
            return;
        }

        var choice = Choices.FirstOrDefault(c => c.Place?.Id == item.Suggestion.Place.Id);
        if (choice is null)
        {
            return;
        }

        SetDestination(choice);
        SelectedModeIndex = 2;
        var local = TimeZoneInfo.ConvertTime(item.Suggestion.Event.Start, TripTime.IsraelZone);
        // The event's own day and time: the date selector shows it and the request carries its full instant.
        TargetDate = local.Date;
        TargetTime = local.TimeOfDay;
        await PlanCommand.ExecuteAsync(null);
    }

    public void SetDestination(LocationChoice choice)
    {
        // A stop picked from the search is not one of the saved choices; add it so the destination box can show it.
        if (!Choices.Contains(choice))
        {
            Choices.Add(choice);
        }

        SelectedDestination = choice;
        DestinationQuery = string.Empty;
        Suggestions.Clear();
        DestinationSearchMessage = string.Empty;
    }

    public void SetOrigin(Stop stop)
    {
        var choice = Choices.FirstOrDefault(c => c.Stop?.Code == stop.Code);
        if (choice is null)
        {
            choice = new LocationChoice(stop.Name, stop: stop);
            Choices.Add(choice);
        }

        SelectedOrigin = choice;
    }

    /// <summary>Picks any choice (saved place, stop or searched address) as the origin and closes the origin suggestions.</summary>
    public void SetOrigin(LocationChoice choice)
    {
        if (!Choices.Contains(choice))
        {
            Choices.Add(choice);
        }

        SelectedOrigin = choice;
        OriginQuery = string.Empty;
        OriginSuggestions.Clear();
        OriginSearchMessage = string.Empty;
    }

    /// <summary>Finds the rebuilt choice for a previous selection: the same saved place, my location, or a kept stop.</summary>
    private LocationChoice? Reselect(LocationChoice? previous)
    {
        if (previous is null)
        {
            return null;
        }

        if (previous.IsMyLocation)
        {
            return MyLocation;
        }

        if (previous.Place is { } place)
        {
            return Choices.FirstOrDefault(c => c.Place?.Id == place.Id);
        }

        Choices.Add(previous);
        return previous;
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
        DestinationSearchMessage = string.Empty;
    }

    private const int MaxSuggestions = 10;

    private Task UpdateSuggestionsNowAsync()
    {
        var delay = DebounceDelay;
        DebounceDelay = TimeSpan.Zero;
        try
        {
            return UpdateSuggestionsAsync();
        }
        finally
        {
            DebounceDelay = delay;
        }
    }

    /// <summary>Stops whose name contains the text, names that start with it first; one entry per name and city.</summary>
    private IEnumerable<Stop> FindStopsByName(string query, int count)
    {
        if (count <= 0 || query.Length < 2 || _catalog.Loaded is not { } stops)
        {
            return [];
        }

        return stops
            .Where(stop => stop.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(stop => (stop.Name, stop.City))
            .Select(group => group.First())
            .OrderBy(stop => stop.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(stop => stop.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(count);
    }

    private static string StopLabel(Stop stop) => string.IsNullOrWhiteSpace(stop.City) ? stop.Name : $"{stop.Name}, {stop.City}";

    /// <summary>Filters as you type (debounced): recent destinations first, then matching saved places, then stops by name.</summary>
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

            foreach (var choice in LocalMatches(query))
            {
                Suggestions.Add(choice);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Saved places first (recent ones leading), then stops by name. Searched locally: nothing is sent anywhere.</summary>
    private List<LocationChoice> LocalMatches(string query)
    {
        var recentIds = _history.Recent.Select(entry => entry.PlaceId).ToList();
        var matches = Choices
            .Where(choice => choice.Place is not null &&
                choice.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(choice => recentIds.IndexOf(choice.Place!.Id) is var index && index < 0 ? int.MaxValue : index)
            .ThenBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(6)
            .ToList();
        var result = new List<LocationChoice>(matches);

        // Any stop by name (for example "אסף הרופא"), searched in the local stop list.
        foreach (var stop in FindStopsByName(query, MaxSuggestions - matches.Count))
        {
            result.Add(new LocationChoice(StopLabel(stop), stop: stop));
        }

        return result;
    }

    partial void OnOriginQueryChanged(string value) => _ = UpdateOriginSuggestionsAsync();

    /// <summary>The origin box filters the same way as the destination box (debounced, local only).</summary>
    public async Task UpdateOriginSuggestionsAsync()
    {
        _originSuggestCts?.Cancel();
        var cts = _originSuggestCts = new CancellationTokenSource();
        try
        {
            if (DebounceDelay > TimeSpan.Zero)
            {
                await Task.Delay(DebounceDelay, cts.Token);
            }

            var query = OriginQuery.Trim();
            OriginSuggestions.Clear();
            OriginSearchMessage = string.Empty;
            if (query.Length == 0)
            {
                return;
            }

            foreach (var choice in LocalMatches(query))
            {
                OriginSuggestions.Add(choice);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private void ChooseOriginSuggestion(LocationChoice? choice)
    {
        if (choice is not null)
        {
            SetOrigin(choice);
        }
    }

    [RelayCommand]
    private void ClearOriginSuggestions()
    {
        OriginQuery = string.Empty;
        OriginSuggestions.Clear();
        OriginSearchMessage = string.Empty;
    }

    /// <summary>
    /// Looks the typed text up as an address. Runs only from this command (button or Enter), never while typing and never on
    /// a timer. Results replace the suggestion list; nothing is written to the history.
    /// </summary>
    [RelayCommand]
    private Task SearchDestinationAddressAsync(CancellationToken cancellationToken) =>
        SearchAddressAsync(DestinationQuery, Suggestions, message => DestinationSearchMessage = message, LocalMatches, cancellationToken);

    [RelayCommand]
    private Task SearchOriginAddressAsync(CancellationToken cancellationToken) =>
        SearchAddressAsync(OriginQuery, OriginSuggestions, message => OriginSearchMessage = message, LocalMatches, cancellationToken);

    private async Task SearchAddressAsync(
        string text,
        ObservableCollection<LocationChoice> target,
        Action<string> setMessage,
        Func<string, List<LocationChoice>> localMatches,
        CancellationToken cancellationToken)
    {
        var query = text.Trim();
        setMessage(string.Empty);
        if (_geocoder is null)
        {
            setMessage(_localizer.Get("GeocodeUnavailable"));
            return;
        }

        GeocodeResult result;
        try
        {
            result = await _geocoder.SearchAsync(query, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (result.Status != GeocodeStatus.Ok || result.Places.Count == 0)
        {
            setMessage(_localizer.Get(result.Status switch
            {
                GeocodeStatus.Disabled => "GeocodeDisabled",
                GeocodeStatus.Unavailable => "GeocodeUnavailable",
                GeocodeStatus.TooShort => "GeocodeTooShort",
                _ => "GeocodeNoResults"
            }));
            return;
        }

        target.Clear();
        if (query.Length > 0)
        {
            foreach (var local in localMatches(query).Take(MaxSuggestions - MaxAddressResults))
            {
                target.Add(local);
            }
        }

        foreach (var place in result.Places.Take(MaxAddressResults))
        {
            target.Add(new LocationChoice(place.Label, address: place));
        }

        setMessage(_localizer.Get("GeocodeResults"));
    }

    private const int MaxAddressResults = 5;

    /// <summary>
    /// Keeps a chosen stop or address as a saved place. <paramref name="slot"/> is "home", "work" or null for a plain place.
    /// The place lives in the encrypted config only (never in the history), within the 30-place limit and with a unique name.
    /// </summary>
    public async Task SavePlaceAsync(LocationChoice? choice, string? slot)
    {
        if (choice is not { CanSave: true })
        {
            return;
        }

        if (_places.Items.Count >= MaxSavedPlaces)
        {
            StatusText = _localizer.Get("PlaceLimitReached");
            return;
        }

        var baseName = slot switch
        {
            "home" => _localizer.Get("HomeName"),
            "work" => _localizer.Get("PlaceNameWork"),
            _ => SuggestName(choice)
        };
        var place = new SavedPlace { Name = UniqueName(baseName) };
        if (choice.Address is { } address)
        {
            place.Latitude = address.Latitude;
            place.Longitude = address.Longitude;
            place.Address = Truncate(address.Label, 200);
        }
        else if (choice.Stop is { } stop)
        {
            place.StopCode = stop.Code;
        }

        var config = _settings.Config;
        var previousHome = config.HomePlaceId;
        var previousWork = config.WorkPlaceId;
        if (slot == "home")
        {
            config.HomePlaceId = place.Id;
        }
        else if (slot == "work")
        {
            config.WorkPlaceId = place.Id;
        }

        _places.Items.Add(place);
        _places.SyncToConfig();
        if (!await _settings.SaveAsync())
        {
            // Keep memory and disk in step: undo the in-memory change when the encrypted config could not be written.
            _places.Items.Remove(place);
            _places.SyncToConfig();
            config.HomePlaceId = previousHome;
            config.WorkPlaceId = previousWork;
            OnPropertyChanged(nameof(HasHome));
            OnPropertyChanged(nameof(HasWork));
            StatusText = _localizer.Get("PlaceSaveFailed");
            return;
        }

        // Show the saved place (not the ad-hoc choice) from now on.
        var saved = Choices.FirstOrDefault(c => c.Place?.Id == place.Id);
        if (saved is not null)
        {
            if (ReferenceEquals(choice, SelectedOrigin))
            {
                SelectedOrigin = saved;
            }

            if (ReferenceEquals(choice, SelectedDestination))
            {
                SelectedDestination = saved;
            }
        }

        StatusText = string.Format(CultureInfo.CurrentCulture, _localizer.Get("PlaceSavedNamed"), place.Name);
    }

    private const int MaxSavedPlaces = 30;

    [RelayCommand]
    private Task SaveOriginAsync(string? slot) => SavePlaceAsync(SelectedOrigin, slot);

    [RelayCommand]
    private Task SaveDestinationAsync(string? slot) => SavePlaceAsync(SelectedDestination, slot);

    /// <summary>A short name from the label: the part before the first comma, trimmed to 40 characters.</summary>
    private static string SuggestName(LocationChoice choice)
    {
        var label = choice.Address?.Label ?? choice.Stop?.Name ?? choice.DisplayName;
        var first = label.Split(',')[0].Trim();
        return Truncate(first.Length == 0 ? label.Trim() : first, 40);
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var suffix = 2; _places.Items.Any(p => string.Equals(p.Name, candidate, StringComparison.CurrentCultureIgnoreCase)); suffix++)
        {
            candidate = $"{name} {suffix}";
        }

        return candidate;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    [RelayCommand]
    private async Task PlanAsync(CancellationToken cancellationToken)
    {
        // Typed a name and pressed "Find trips" without picking from the list: take the best match.
        if (SelectedDestination is null && DestinationQuery.Trim().Length > 0)
        {
            // Always recompute: the list may be stale when the text changed within the debounce window.
            await UpdateSuggestionsNowAsync();

            if (Suggestions.FirstOrDefault() is { } best)
            {
                SetDestination(best);
            }
        }

        // The same for a typed start: use the best local match instead of silently planning from "my location".
        if (OriginQuery.Trim().Length > 0)
        {
            var origins = LocalMatches(OriginQuery.Trim());
            if (origins.Count == 0)
            {
                StatusText = _localizer.Get("GeocodeNoResults");
                return;
            }

            SetOrigin(origins[0]);
        }

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
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
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
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
        if (!ReferenceEquals(request, _lastRequest))
        {
            // A newer plan replaced this request while it was in flight; its results must not overwrite the newer ones.
            return;
        }

        Reconcile(plan.Options, request);
        StatusText = plan.Status switch
        {
            TripPlanStatus.Ok => string.Empty,
            TripPlanStatus.NoStopsNearOrigin => _localizer.Get("PlanNoStopsNearOrigin"),
            TripPlanStatus.NoStopsNearDestination => _localizer.Get("PlanNoStopsNearDestination"),
            TripPlanStatus.NoScheduleForDate => _localizer.Get("PlanNoScheduleForDate"),
            TripPlanStatus.ScheduleUnavailable => _localizer.Get("PlanScheduleUnavailable"),
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
        // user_history.json is not encrypted: places defined by an address stay out of it (they live in the encrypted config only).
        if (SelectedDestination?.Place is { } place && string.IsNullOrWhiteSpace(place.Address))
        {
            _history.Record(place.Id, place.Name);
        }

        await _settings.SaveAsync();
    }

    /// <summary>
    /// The absolute instant of the chosen date and time. When the time has already passed today the plan moves to tomorrow
    /// and the date selector and notice show it, so the jump is never silent.
    /// </summary>
    private DateTimeOffset ResolveTarget(DateTimeOffset now)
    {
        var zone = TripTime.IsraelZone;
        var day = SelectedDay();
        if (day == Today && TripTime.HasPassed(now, day, TargetTime, zone))
        {
            day = day.AddDays(1);
            TargetDate = day.ToDateTime(TimeOnly.MinValue);
            DateNotice = string.Format(CultureInfo.CurrentCulture, _localizer.Get("TimePassedTomorrow"), FormatDate(day));
        }
        else if (TargetDate is null || DateOnly.FromDateTime(TargetDate.Value) != day)
        {
            TargetDate = day.ToDateTime(TimeOnly.MinValue);
        }

        return TripTime.Resolve(day, TargetTime, zone);
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

        if (choice.Address is { } address)
        {
            return (address.Latitude, address.Longitude, null);
        }

        if (choice.Place is { } place)
        {
            return PlacePoint.Resolve(place, stops) is { } point
                ? (point.Latitude, point.Longitude, null)
                : (0, 0, _localizer.Get("PlaceNotFound"));
        }

        var result = await _location.GetCurrentAsync(cancellationToken);
        return result.Status == LocationStatus.Ok
            ? (result.Latitude, result.Longitude, null)
            : (0, 0, _localizer.Get("Location" + result.Status));
    }

    private SavedPlace? FindPlace(string? id) => id is null ? null : _places.Items.FirstOrDefault(place => place.Id == id);

    private void RebuildChoices()
    {
        var origin = SelectedOrigin;
        var destination = SelectedDestination;
        Choices.Clear();
        Choices.Add(MyLocation);
        foreach (var place in _places.Items)
        {
            Choices.Add(new LocationChoice(place.Name, place));
        }

        SelectedOrigin = Reselect(origin) ?? MyLocation;
        SelectedDestination = Reselect(destination);

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
                    try
                    {
                        await RefreshAsync(cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // One failed refresh must not stop the countdown; the next interval retries.
                        _logger.LogWarning(exception, "Trip refresh failed; will retry.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
