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
    Address,

    /// <summary>Text only (a calendar event's location): it has no point until the user picks it and it is looked up.</summary>
    Text
}

/// <summary>An origin or destination: the user's current location, a saved place, a specific stop or a searched address.</summary>
public sealed partial class LocationChoice : ObservableObject
{
    public LocationChoice(string displayName, SavedPlace? place = null, Stop? stop = null, GeocodedPlace? address = null, bool isText = false)
    {
        _displayName = displayName;
        Place = place;
        Stop = stop;
        Address = address;
        IsText = isText;
    }

    [ObservableProperty]
    private string _displayName;

    /// <summary>The kind of suggestion in words (saved, recent, calendar, stop, address); empty where the name says it all.</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    public bool IsText { get; }

    public SavedPlace? Place { get; }

    public Stop? Stop { get; }

    public GeocodedPlace? Address { get; }

    public bool IsMyLocation => Place is null && Stop is null && Address is null && !IsText;

    public LocationChoiceKind Kind => Place is not null ? LocationChoiceKind.Place
        : Stop is not null ? LocationChoiceKind.Stop
        : Address is not null ? LocationChoiceKind.Address
        : IsText ? LocationChoiceKind.Text
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
    // Index 0 is the destination box, 1 the origin box.
    private readonly CancellationTokenSource?[] _typingTokens = new CancellationTokenSource?[2];
    private readonly CancellationTokenSource?[] _lookupTokens = new CancellationTokenSource?[2];
    private readonly string[] _lastSearched = ["", ""];
    private List<string> _calendarLocations = [];
    private bool _suppressText;
    private DateTimeOffset _lastPromptAt = DateTimeOffset.MinValue;
    private readonly IClock _clock;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<TripPlannerViewModel> _logger;
    private CancellationTokenSource? _tickerCts;
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

    /// <summary>The suggestion the arrow keys are on (Enter takes it); null means nothing is highlighted and Enter looks the text up.</summary>
    [ObservableProperty]
    private LocationChoice? _highlightedOrigin;

    [ObservableProperty]
    private LocationChoice? _highlightedDestination;

    /// <summary>True only while a real network call from that box is in flight and has been for <see cref="SpinnerDelay"/>.</summary>
    [ObservableProperty]
    private bool _isOriginSearching;

    [ObservableProperty]
    private bool _isDestinationSearching;

    /// <summary>The last lookup from that box found nothing: the list shows one "no results" line.</summary>
    [ObservableProperty]
    private bool _originNoResults;

    [ObservableProperty]
    private bool _destinationNoResults;

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
        _originQuery = MyLocation.DisplayName;
        _destinationQuery = _selectedDestination?.DisplayName ?? string.Empty;
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
    public TimeSpan DebounceDelay { get; set; } = TimeSpan.Zero;

    /// <summary>The pause after the last keystroke before a provider with autocomplete is asked (400 ms; zero in tests).</summary>
    public TimeSpan AutocompleteDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>A network call must run this long before the inline spinner appears (300 ms; zero in tests).</summary>
    public TimeSpan SpinnerDelay { get; set; } = TimeSpan.FromMilliseconds(300);

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
        SetTextSilently(origin: true, value.DisplayName);
        OnPropertyChanged(nameof(RouteSummary));
        OnPropertyChanged(nameof(CanSaveOrigin));
    }

    partial void OnSelectedDestinationChanged(LocationChoice? value)
    {
        if (value is not null)
        {
            SetTextSilently(origin: false, value.DisplayName);
        }

        OnPropertyChanged(nameof(RouteSummary));
        OnPropertyChanged(nameof(CanSaveDestination));
    }

    /// <summary>Puts a chosen name into a box without treating it as typing (no suggestions, no lookup).</summary>
    private void SetTextSilently(bool origin, string text)
    {
        var previous = _suppressText;
        _suppressText = true;
        try
        {
            if (origin)
            {
                OriginQuery = text;
            }
            else
            {
                DestinationQuery = text;
            }
        }
        finally
        {
            _suppressText = previous;
        }
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
        // The date was only moved to tomorrow automatically and the new time is still ahead today: go back to today.
        if (_dateRolledAutomatically && Mode != TripTimeMode.Now && Today.AddDays(1) == SelectedDay() &&
            !TripTime.HasPassed(_clock.UtcNow, Today, value, TripTime.IsraelZone))
        {
            SetTargetDateAutomatically(Today);
        }

        UpdateDateNotice();
        if (Mode != TripTimeMode.Now)
        {
            PromptForRoute("PromptSelectRoute");
        }
    }

    partial void OnTargetDateChanged(DateTime? value)
    {
        if (!_settingDateAutomatically)
        {
            // A date picked by the user (buttons, date picker, calendar suggestion) is never changed automatically.
            _dateRolledAutomatically = false;
        }

        UpdateDateNotice();
    }

    private bool _dateRolledAutomatically;
    private bool _settingDateAutomatically;

    private void SetTargetDateAutomatically(DateOnly day)
    {
        _settingDateAutomatically = true;
        try
        {
            TargetDate = day.ToDateTime(TimeOnly.MinValue);
        }
        finally
        {
            _settingDateAutomatically = false;
        }

        _dateRolledAutomatically = day != Today;
    }

    partial void OnDateNoticeChanged(string value) => OnPropertyChanged(nameof(HasDateNotice));

    [RelayCommand]
    private void SelectToday() => PickDate(Today);

    [RelayCommand]
    private void SelectTomorrow() => PickDate(Today.AddDays(1));

    private void PickDate(DateOnly day)
    {
        // An explicit choice, even of the date that is already set, is never undone automatically.
        _dateRolledAutomatically = false;
        TargetDate = day.ToDateTime(TimeOnly.MinValue);
    }

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

    partial void OnDestinationQueryChanged(string value)
    {
        if (_suppressText)
        {
            return;
        }

        // Editing the text makes the previous pick stale: the box is free text again until something is chosen.
        if (SelectedDestination is { } selected && !string.Equals(value.Trim(), selected.DisplayName, StringComparison.CurrentCulture))
        {
            SelectedDestination = null;
        }

        _ = UpdateSuggestionsAsync();
    }

    /// <summary>Re-reads text that was captured while the previous language was active.</summary>
    private void RefreshLanguage()
    {
        var previousName = MyLocation.DisplayName;
        MyLocation.DisplayName = _localizer.Get("MyLocation");
        if (string.Equals(OriginQuery, previousName, StringComparison.CurrentCulture))
        {
            SetTextSilently(origin: true, MyLocation.DisplayName);
        }

        // Suggestion captions were built in the previous language.
        Suggestions.Clear();
        OriginSuggestions.Clear();
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
                _calendarLocations = [];
                CalendarSuggestions.Clear();
                return;
            }

            var now = _clock.UtcNow;
            var events = await _calendar.GetUpcomingAsync(now, ct);
            // Event locations feed the suggestion list (in memory only; they are looked up only if the user picks one).
            _calendarLocations = events
                .Select(item => item.Location?.Trim())
                .Where(location => !string.IsNullOrEmpty(location) && location.Length <= 120 && !location.Any(char.IsControl))
                .Select(location => location!)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Take(10)
                .ToList();
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
        SetTextSilently(origin: false, choice.DisplayName);
        CloseSuggestions(origin: false);
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
        SetTextSilently(origin: true, choice.DisplayName);
        CloseSuggestions(origin: true);
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
    private Task ChooseSuggestionAsync(LocationChoice? choice, CancellationToken cancellationToken) =>
        choice is null ? Task.CompletedTask : ChooseAsync(origin: false, choice, cancellationToken);

    [RelayCommand]
    private Task ChooseOriginSuggestionAsync(LocationChoice? choice, CancellationToken cancellationToken) =>
        choice is null ? Task.CompletedTask : ChooseAsync(origin: true, choice, cancellationToken);

    /// <summary>Esc: closes the suggestions and keeps the text.</summary>
    [RelayCommand]
    private void ClearSuggestions() => CloseSuggestions(origin: false);

    [RelayCommand]
    private void ClearOriginSuggestions() => CloseSuggestions(origin: true);

    private void CloseSuggestions(bool origin)
    {
        CancelLookup(origin);
        ListFor(origin).Clear();
        Highlight(origin, null);
        SetNoResults(origin, false);
        SetMessage(origin, string.Empty);
    }

    /// <summary>Enter in the box: takes the highlighted suggestion, or, with none highlighted, looks the text up as an address.</summary>
    [RelayCommand]
    private Task CommitDestinationAsync(CancellationToken cancellationToken) => CommitAsync(origin: false, cancellationToken);

    [RelayCommand]
    private Task CommitOriginAsync(CancellationToken cancellationToken) => CommitAsync(origin: true, cancellationToken);

    private Task CommitAsync(bool origin, CancellationToken cancellationToken) =>
        (origin ? HighlightedOrigin : HighlightedDestination) is { } highlighted
            ? ChooseAsync(origin, highlighted, cancellationToken)
            : SearchAddressAsync(origin, highlightFirstResult: true, cancellationToken);

    /// <summary>
    /// The box lost focus. Looks the text up once, unless a provider with autocomplete already did, the text is a pick,
    /// a local suggestion has exactly this name, or this exact text was already searched.
    /// </summary>
    [RelayCommand]
    private Task LeaveDestinationAsync(CancellationToken cancellationToken) => LeaveAsync(origin: false, cancellationToken);

    [RelayCommand]
    private Task LeaveOriginAsync(CancellationToken cancellationToken) => LeaveAsync(origin: true, cancellationToken);

    private async Task LeaveAsync(bool origin, CancellationToken cancellationToken)
    {
        var query = EffectiveQuery(origin);
        if (query.Length < MinLookupLength || _geocoder is null || _geocoder.SupportsAutocomplete ||
            string.Equals(_lastSearched[Index(origin)], query, StringComparison.Ordinal) ||
            BuildLocal(query, origin).Any(choice => string.Equals(choice.DisplayName, query, StringComparison.CurrentCultureIgnoreCase)))
        {
            return;
        }

        await SearchAddressAsync(origin, highlightFirstResult: false, cancellationToken);
    }

    /// <summary>The box got keyboard focus: with no text of its own it offers the defaults (my location, saved places, ...).</summary>
    public void ShowDefaultSuggestions(bool origin)
    {
        if (EffectiveQuery(origin).Length == 0)
        {
            _ = RefreshAsync(origin);
        }
    }

    private const int MaxSuggestions = 10;
    private const int MaxLocal = 12;
    private const int MinLookupLength = 3;

    private static int Index(bool origin) => origin ? 1 : 0;

    private ObservableCollection<LocationChoice> ListFor(bool origin) => origin ? OriginSuggestions : Suggestions;

    private string QueryFor(bool origin) => origin ? OriginQuery : DestinationQuery;

    private LocationChoice? SelectedFor(bool origin) => origin ? SelectedOrigin : SelectedDestination;

    private void Highlight(bool origin, LocationChoice? choice)
    {
        if (origin)
        {
            HighlightedOrigin = choice;
        }
        else
        {
            HighlightedDestination = choice;
        }
    }

    private void SetNoResults(bool origin, bool value)
    {
        if (origin)
        {
            OriginNoResults = value;
        }
        else
        {
            DestinationNoResults = value;
        }
    }

    private void SetMessage(bool origin, string text)
    {
        if (origin)
        {
            OriginSearchMessage = text;
        }
        else
        {
            DestinationSearchMessage = text;
        }
    }

    private void SetSearching(bool origin, bool value)
    {
        if (origin)
        {
            IsOriginSearching = value;
        }
        else
        {
            IsDestinationSearching = value;
        }
    }

    /// <summary>What the user typed, or nothing when the box only shows the name of the current pick.</summary>
    private string EffectiveQuery(bool origin)
    {
        var text = QueryFor(origin).Trim();
        return SelectedFor(origin) is { } selected && string.Equals(text, selected.DisplayName, StringComparison.CurrentCulture)
            ? string.Empty
            : text;
    }

    private void CancelLookup(bool origin)
    {
        var index = Index(origin);
        _lookupTokens[index]?.Cancel();
        _lookupTokens[index] = null;
        SetSearching(origin, false);
    }

    private Task UpdateSuggestionsNowAsync() => RefreshAsync(origin: false, immediate: true);

    /// <summary>Filters the local sources as you type (no network) and, for a provider with autocomplete, asks it after a pause.</summary>
    public Task UpdateSuggestionsAsync() => RefreshAsync(origin: false);

    public Task UpdateOriginSuggestionsAsync() => RefreshAsync(origin: true);

    partial void OnOriginQueryChanged(string value)
    {
        if (!_suppressText)
        {
            _ = RefreshAsync(origin: true);
        }
    }

    private async Task RefreshAsync(bool origin, bool immediate = false)
    {
        var index = Index(origin);
        _typingTokens[index]?.Cancel();
        CancelLookup(origin);
        var cts = _typingTokens[index] = new CancellationTokenSource();
        try
        {
            if (!immediate && DebounceDelay > TimeSpan.Zero)
            {
                await Task.Delay(DebounceDelay, cts.Token);
            }

            var query = EffectiveQuery(origin);
            SetMessage(origin, string.Empty);
            SetNoResults(origin, false);
            Highlight(origin, null);
            Replace(ListFor(origin), BuildLocal(query, origin));

            if (query.Length >= MinLookupLength && _geocoder is { SupportsAutocomplete: true } && _settings.Config.AddressSearchEnabled)
            {
                await AutocompleteAsync(origin, query, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>As-you-type suggestions from a provider that allows them (Google Places): after a 400 ms pause, one call in flight.</summary>
    private async Task AutocompleteAsync(bool origin, string query, CancellationToken typingToken)
    {
        if (AutocompleteDelay > TimeSpan.Zero)
        {
            await Task.Delay(AutocompleteDelay, typingToken);
        }

        var result = await RunLookupAsync(origin, token => _geocoder!.SuggestAsync(query, token), typingToken);
        if (typingToken.IsCancellationRequested || !string.Equals(EffectiveQuery(origin), query, StringComparison.Ordinal))
        {
            return;
        }

        ApplyGeocode(origin, query, result, explicitSearch: false, highlightFirstResult: false);
    }

    /// <summary>
    /// Runs one real network call for a box. The inline spinner appears only after <see cref="SpinnerDelay"/> and only while
    /// the call is in flight; a newer call or a text change cancels this one.
    /// </summary>
    private async Task<T> RunLookupAsync<T>(bool origin, Func<CancellationToken, Task<T>> call, CancellationToken outer)
    {
        var index = Index(origin);
        _lookupTokens[index]?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _lookupTokens[index] = cts;
        var finished = false;

        async Task ShowSpinnerLater()
        {
            try
            {
                if (SpinnerDelay > TimeSpan.Zero)
                {
                    await Task.Delay(SpinnerDelay, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!finished && ReferenceEquals(_lookupTokens[index], cts))
            {
                SetSearching(origin, true);
            }
        }

        var spinner = ShowSpinnerLater();
        try
        {
            return await call(cts.Token);
        }
        finally
        {
            finished = true;
            if (ReferenceEquals(_lookupTokens[index], cts))
            {
                _lookupTokens[index] = null;
                SetSearching(origin, false);
            }

            // Stop a spinner timer that has not fired yet; a call that finished quickly never shows it.
            await cts.CancelAsync();
            await spinner;
        }
    }

    private static void Replace(ObservableCollection<LocationChoice> list, IReadOnlyList<LocationChoice> items)
    {
        list.Clear();
        foreach (var item in items)
        {
            list.Add(item);
        }
    }

    /// <summary>
    /// The local sources, in order, without a network call: my location (origin only, always first), saved places (recent
    /// ones leading), locations of upcoming calendar events, then stops by name. Duplicates by name or coordinates are dropped.
    /// </summary>
    private List<LocationChoice> BuildLocal(string query, bool origin)
    {
        var result = new List<LocationChoice>();

        void Add(LocationChoice choice, string detail)
        {
            if (result.Count < MaxLocal && !result.Any(existing => IsDuplicate(existing, choice)))
            {
                choice.Detail = detail;
                result.Add(choice);
            }
        }

        if (origin)
        {
            Add(MyLocation, string.Empty);
        }

        var recentIds = _history.Recent.Take(MaxRecent).Select(entry => entry.PlaceId).ToList();
        foreach (var choice in Choices
                     .Where(choice => choice.Place is { } place &&
                         (query.Length == 0 ||
                          choice.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                          place.Address?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true))
                     .OrderBy(choice => recentIds.IndexOf(choice.Place!.Id) is var index && index < 0 ? int.MaxValue : index)
                     .ThenBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Add(choice, _localizer.Get(recentIds.Contains(choice.Place!.Id) ? "SuggestRecent" : "SuggestSaved"));
        }

        foreach (var location in _calendarLocations.Where(location => query.Length == 0 ||
                     location.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
        {
            Add(new LocationChoice(location, isText: true), _localizer.Get("SuggestCalendar"));
        }

        foreach (var stop in FindStopsByName(query, MaxSuggestions))
        {
            Add(new LocationChoice(StopLabel(stop), stop: stop), _localizer.Get("SuggestStop"));
        }

        return result;
    }

    private const int MaxRecent = 20;

    private static string NormalizeName(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static bool TryGetPoint(LocationChoice choice, out double latitude, out double longitude)
    {
        if (choice.Address is { IsUnresolved: false } address)
        {
            (latitude, longitude) = (address.Latitude, address.Longitude);
            return true;
        }

        if (choice.Stop is { } stop)
        {
            (latitude, longitude) = (stop.Latitude, stop.Longitude);
            return true;
        }

        if (choice.Place is { Latitude: { } lat, Longitude: { } lon })
        {
            (latitude, longitude) = (lat, lon);
            return true;
        }

        (latitude, longitude) = (0, 0);
        return false;
    }

    /// <summary>The same name, or two known points less than 50 m apart.</summary>
    private static bool IsDuplicate(LocationChoice a, LocationChoice b) =>
        ReferenceEquals(a, b) ||
        string.Equals(NormalizeName(a.DisplayName), NormalizeName(b.DisplayName), StringComparison.Ordinal) ||
        (TryGetPoint(a, out var latA, out var lonA) && TryGetPoint(b, out var latB, out var lonB) &&
         Geo.DistanceMeters(latA, lonA, latB, lonB) < 50);

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

    /// <summary>
    /// Looks the typed text up as an address. Runs only from Enter or when the box loses focus (or the Search commands),
    /// never while typing on the public Nominatim server and never on a timer. Nothing is written to the history.
    /// </summary>
    [RelayCommand]
    private Task SearchDestinationAddressAsync(CancellationToken cancellationToken) =>
        SearchAddressAsync(origin: false, highlightFirstResult: true, cancellationToken);

    [RelayCommand]
    private Task SearchOriginAddressAsync(CancellationToken cancellationToken) =>
        SearchAddressAsync(origin: true, highlightFirstResult: true, cancellationToken);

    private async Task SearchAddressAsync(bool origin, bool highlightFirstResult, CancellationToken cancellationToken)
    {
        var query = EffectiveQuery(origin);
        SetMessage(origin, string.Empty);
        SetNoResults(origin, false);
        if (query.Length == 0)
        {
            return;
        }

        if (_geocoder is null)
        {
            SetMessage(origin, _localizer.Get("GeocodeUnavailable"));
            return;
        }

        // A lookup replaces whatever typing or an earlier lookup was doing for this box.
        _typingTokens[Index(origin)]?.Cancel();
        GeocodeResult result;
        try
        {
            result = await RunLookupAsync(origin, token => _geocoder.SearchAsync(query, token), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!string.Equals(EffectiveQuery(origin), query, StringComparison.Ordinal))
        {
            // The text changed while the call was in flight: this answer no longer belongs to the box.
            return;
        }

        if (result.Status is GeocodeStatus.Ok or GeocodeStatus.NoResults)
        {
            _lastSearched[Index(origin)] = query;
        }

        ApplyGeocode(origin, query, result, explicitSearch: true, highlightFirstResult);
    }

    private void ApplyGeocode(bool origin, string query, GeocodeResult result, bool explicitSearch, bool highlightFirstResult)
    {
        SetMessage(origin, string.Empty);
        SetNoResults(origin, false);
        if (result.Status == GeocodeStatus.Ok && result.Places.Count > 0)
        {
            var items = BuildLocal(query, origin);
            LocationChoice? firstAddress = null;
            foreach (var place in result.Places.Take(MaxAddressResults))
            {
                var choice = new LocationChoice(place.Label, address: place) { Detail = _localizer.Get("SuggestAddress") };
                // A calendar location that was just looked up is replaced by its resolved address.
                items.RemoveAll(existing => existing.IsText && IsDuplicate(existing, choice));
                if (items.Any(existing => IsDuplicate(existing, choice)))
                {
                    continue;
                }

                items.Add(choice);
                firstAddress ??= choice;
                if (origin)
                {
                    _originAddressQuery = query;
                }
                else
                {
                    _destinationAddressQuery = query;
                }
            }

            Replace(ListFor(origin), items);
            Highlight(origin, highlightFirstResult ? firstAddress : null);
            return;
        }

        switch (result.Status)
        {
            case GeocodeStatus.NoResults:
                SetNoResults(origin, explicitSearch || ListFor(origin).Count == 0);
                break;
            case GeocodeStatus.Disabled when !explicitSearch:
            case GeocodeStatus.TooShort when !explicitSearch:
            case GeocodeStatus.Ok:
                break;
            default:
                SetMessage(origin, _localizer.Get(result.Status switch
                {
                    GeocodeStatus.Disabled => "GeocodeDisabled",
                    GeocodeStatus.TooShort => "GeocodeTooShort",
                    GeocodeStatus.RateLimited => "GeocodeRateLimited",
                    GeocodeStatus.DailyCapReached => "GeocodeDailyCap",
                    _ => "GeocodeUnavailable"
                }));
                break;
        }
    }

    /// <summary>Takes a suggestion: a Google suggestion first gets its coordinates, a calendar text is looked up as an address.</summary>
    private async Task ChooseAsync(bool origin, LocationChoice choice, CancellationToken cancellationToken)
    {
        if (choice.IsText)
        {
            // Picking a calendar location is an explicit request to find it: put it in the box and look it up.
            SetTextSilently(origin, choice.DisplayName);
            if (!origin && SelectedDestination is not null)
            {
                SelectedDestination = null;
                SetTextSilently(origin, choice.DisplayName);
            }

            await SearchAddressAsync(origin, highlightFirstResult: true, cancellationToken);
            return;
        }

        if (choice.Address is { IsUnresolved: true } unresolved)
        {
            if (_geocoder is null)
            {
                return;
            }

            GeocodedPlace? resolved;
            try
            {
                resolved = await RunLookupAsync(origin, token => _geocoder.ResolveAsync(unresolved, token), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (resolved is null || resolved.IsUnresolved)
            {
                SetMessage(origin, _localizer.Get("GeocodeUnavailable"));
                return;
            }

            choice = new LocationChoice(choice.DisplayName, address: resolved) { Detail = choice.Detail };
        }

        if (origin)
        {
            SetOrigin(choice);
        }
        else
        {
            SetDestination(choice);
        }
    }

    private const int MaxAddressResults = 5;

    private string _destinationAddressQuery = string.Empty;
    private string _originAddressQuery = string.Empty;

    /// <summary>The first address result on screen, provided it was searched for exactly the text that is in the box now.</summary>
    private static LocationChoice? AddressResultFor(string currentText, ObservableCollection<LocationChoice> list, string searchedText) =>
        searchedText.Length > 0 && string.Equals(currentText.Trim(), searchedText, StringComparison.Ordinal)
            ? list.FirstOrDefault(choice => choice.Address is not null)
            : null;

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
            if (AddressResultFor(DestinationQuery, Suggestions, _destinationAddressQuery) is { } searched)
            {
                // The user just searched this text as an address and sees the results: use the first of them.
                SetDestination(searched);
            }
            else
            {
                // Always recompute: the list may be stale when the text changed within the debounce window.
                await UpdateSuggestionsNowAsync();

                if (Suggestions.FirstOrDefault() is { } best)
                {
                    SetDestination(best);
                }
            }
        }

        // The same for a typed start: use the best local match instead of silently planning from "my location".
        if (OriginQuery.Trim().Length == 0)
        {
            // An emptied box means the current location again.
            if (!SelectedOrigin.IsMyLocation)
            {
                SelectedOrigin = MyLocation;
            }
        }
        else if (EffectiveQuery(origin: true).Length > 0)
        {
            var origins = AddressResultFor(OriginQuery, OriginSuggestions, _originAddressQuery) is { } searchedOrigin
                ? [searchedOrigin]
                : BuildLocal(OriginQuery.Trim(), origin: true).Where(choice => !choice.IsMyLocation && !choice.IsText).ToList();
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
            SetTargetDateAutomatically(day);
            DateNotice = string.Format(CultureInfo.CurrentCulture, _localizer.Get("TimePassedTomorrow"), FormatDate(day));
        }
        else if (TargetDate is null || DateOnly.FromDateTime(TargetDate.Value) != day)
        {
            var rolled = _dateRolledAutomatically;
            SetTargetDateAutomatically(day);
            _dateRolledAutomatically = rolled && day != Today;
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
