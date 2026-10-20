using System.Windows;
using BusPulse.Infrastructure;
using BusPulse.Services.Stops;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BusPulse.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    public const int TabCount = 4;

    private readonly ISettingsService _settings;
    private readonly ILocalizer _localizer;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteButtonText), nameof(TrayText))]
    private bool _isMuted;

    [ObservableProperty]
    private string _activeProfile;

    [ObservableProperty]
    private bool _isWindowActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageButtonText))]
    private System.Windows.FlowDirection _flowDirection;

    public ShellViewModel(
        ISettingsService settings,
        ILocalizer localizer,
        StopsViewModel stops,
        TripPlannerViewModel tripPlanner,
        LinesViewModel lines,
        SettingsViewModel settingsTab,
        IThemeService theme)
    {
        _settings = settings;
        _localizer = localizer;
        Stops = stops;
        TripPlanner = tripPlanner;
        Lines = lines;
        SettingsTab = settingsTab;

        _isMuted = settings.Config.NotificationsMuted;
        settingsTab.LanguageSelected += async (_, code) => await ApplyLanguageAsync(code);
        settingsTab.ThemeSelected += (_, code) => theme.Apply(code);
        settingsTab.FavoritesCleared += (_, _) =>
        {
            Stops.RefreshFavorites();
            Lines.ReloadFavorites();
        };
        stops.PlanRouteFromRequested += (_, stop) => PlanRouteFrom(stop);
        lines.PlanRouteFromRequested += (_, stop) => PlanRouteFrom(stop);
        stops.ShowLinesFromRequested += (_, stop) =>
        {
            Lines.SetOrigin(stop);
            SelectedTabIndex = 2;
        };
        settingsTab.NotificationsToggled += (_, enabled) =>
        {
            IsMuted = !enabled;
            OnPropertyChanged(nameof(TrayMenuMuteText));
        };
        _activeProfile = settings.Config.ActiveProfile;
        localizer.SetLanguage(settings.Config.Language);
        _flowDirection = DirectionFor(settings.Config.Language);
    }

    public StopsViewModel Stops { get; }

    public TripPlannerViewModel TripPlanner { get; }

    public LinesViewModel Lines { get; }

    public SettingsViewModel SettingsTab { get; }

    public event EventHandler? HideRequested;

    public string MuteButtonText => _localizer.Get(IsMuted ? "UnmuteNotifications" : "MuteNotifications");

    public string TrayMenuMuteText => _localizer.Get(IsMuted ? "TrayResume" : "TrayQuickMute");

    public string TrayText => _localizer.Get(IsMuted ? "TrayMuted" : "TrayMonitoring");

    public string LanguageButtonText => FlowDirection == System.Windows.FlowDirection.RightToLeft ? "English" : "עברית";

    partial void OnIsMutedChanged(bool value)
    {
        _settings.Config.NotificationsMuted = value;
        SettingsTab.SyncNotifications(!value);
    }

    partial void OnSelectedTabIndexChanged(int value) => UpdateActivity();

    partial void OnIsWindowActiveChanged(bool value) => UpdateActivity();

    // Live data is polled only while the stops tab is visible in a non-minimized window.
    private void UpdateActivity()
    {
        Stops.SetActive(IsWindowActive && SelectedTabIndex == 0);
        TripPlanner.SetActive(IsWindowActive && SelectedTabIndex == 1);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedTabIndex == 0)
        {
            await Stops.LoadCommand.ExecuteAsync(null);
            await Stops.RefreshExpandedAsync();
        }
        else if (SelectedTabIndex == 1)
        {
            await TripPlanner.RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleMuteAsync()
    {
        IsMuted = !IsMuted;
        OnPropertyChanged(nameof(TrayMenuMuteText));
        await _settings.SaveAsync();
    }

    [RelayCommand]
    private Task ToggleLanguageAsync() => ApplyLanguageAsync(_settings.Config.Language == "en" ? "he" : "en");

    private async Task ApplyLanguageAsync(string language)
    {
        if (language is not ("en" or "he"))
        {
            return;
        }

        SettingsTab.SyncLanguage(language);
        _settings.Config.Language = language;
        _localizer.SetLanguage(language);
        FlowDirection = DirectionFor(language);
        OnPropertyChanged(nameof(MuteButtonText));
        await _settings.SaveAsync();
    }

    [RelayCommand]
    private async Task SetProfileAsync(string profile)
    {
        _settings.Config.ActiveProfile = profile;
        ActiveProfile = profile;
        await _settings.SaveAsync();
    }

    /// <summary>
    /// Accepts the tab as an int or as text: a XAML <c>CommandParameter="1"</c> arrives as a string, and a strongly typed
    /// command would throw (and crash the app) on the first keyboard shortcut.
    /// </summary>
    [RelayCommand]
    private void SelectTab(object? tab)
    {
        var index = tab switch
        {
            int number => number,
            string text when int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => -1
        };
        if (index is >= 0 and < TabCount)
        {
            SelectedTabIndex = index;
        }
    }

    private void PlanRouteFrom(Services.Stops.Stop stop)
    {
        TripPlanner.SetOrigin(stop);
        SelectedTabIndex = 1;
    }

    /// <summary>Ctrl+F: opens the trip planner and puts the cursor in its destination search.</summary>
    [RelayCommand]
    private void FocusSearch()
    {
        SelectedTabIndex = 1;
        TripPlanner.RequestSearchFocus();
    }

    [RelayCommand]
    private void Hide() => HideRequested?.Invoke(this, EventArgs.Empty);

    private static System.Windows.FlowDirection DirectionFor(string language) =>
        language == "he" ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;
}
