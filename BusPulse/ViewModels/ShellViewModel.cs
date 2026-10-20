using System.Windows;
using BusPulse.Infrastructure;
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
        SettingsViewModel settingsTab)
    {
        _settings = settings;
        _localizer = localizer;
        Stops = stops;
        TripPlanner = tripPlanner;
        Lines = lines;
        SettingsTab = settingsTab;

        _isMuted = settings.Config.NotificationsMuted;
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

    public string TrayMenuMuteText => IsMuted ? "Resume notifications" : "Quick mute";

    public string TrayText => IsMuted ? "BusPulse - muted" : "BusPulse - monitoring";

    public string LanguageButtonText => FlowDirection == System.Windows.FlowDirection.RightToLeft ? "English" : "עברית";

    partial void OnIsMutedChanged(bool value) => _settings.Config.NotificationsMuted = value;

    partial void OnSelectedTabIndexChanged(int value) => UpdateActivity();

    partial void OnIsWindowActiveChanged(bool value) => UpdateActivity();

    // Live data is polled only while the stops tab is visible in a non-minimized window.
    private void UpdateActivity() => Stops.SetActive(IsWindowActive && SelectedTabIndex == 0);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedTabIndex == 0)
        {
            await Stops.LoadCommand.ExecuteAsync(null);
            await Stops.RefreshExpandedAsync();
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
    private async Task ToggleLanguageAsync()
    {
        var language = _settings.Config.Language == "en" ? "he" : "en";
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

    [RelayCommand]
    private void SelectTab(int index)
    {
        if (index is >= 0 and < TabCount)
        {
            SelectedTabIndex = index;
        }
    }

    [RelayCommand]
    private void Hide() => HideRequested?.Invoke(this, EventArgs.Empty);

    private static System.Windows.FlowDirection DirectionFor(string language) =>
        language == "he" ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;
}
