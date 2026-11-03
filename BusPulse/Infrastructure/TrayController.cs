using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using System.Windows.Threading;
using BusPulse.Services;
using BusPulse.Services.Trips;
using BusPulse.ViewModels;
using Application = System.Windows.Application;

namespace BusPulse.Infrastructure;

/// <summary>
/// Owns the notification-area icon. Its state, menu and tooltip follow <see cref="ShellViewModel"/> and the selected route,
/// and every string comes from the localizer so the tray follows the UI language.
/// </summary>
public sealed class TrayController : IDisposable
{
    private static readonly TimeSpan TooltipInterval = TimeSpan.FromSeconds(10);

    private readonly ShellViewModel _shell;
    private readonly IDispatcher _dispatcher;
    private readonly ILocalizer _localizer;
    private readonly IDepartureReminder _reminder;
    private readonly IClock _clock;
    private readonly System.Drawing.Icon _activeIcon = LoadIcon("bus_active.ico");
    private readonly System.Drawing.Icon _mutedIcon = LoadIcon("bus_muted.ico");
    private readonly System.Drawing.Icon _alertIcon = LoadIcon("bus_alert.ico");
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _muteItem = new();
    private readonly ToolStripMenuItem _profilesItem = new();
    private readonly ToolStripMenuItem _settingsItem = new();
    private readonly ToolStripMenuItem _exitItem = new();
    private readonly DispatcherTimer _tooltipTimer;

    public TrayController(ShellViewModel shell, IDispatcher dispatcher, ILocalizer localizer, IDepartureReminder reminder, IClock clock)
    {
        _shell = shell;
        _dispatcher = dispatcher;
        _localizer = localizer;
        _reminder = reminder;
        _clock = clock;

        foreach (var profile in new[] { "Default", "Work", "Weekend" })
        {
            var name = profile;
            _profilesItem.DropDownItems.Add(name, null, (_, _) => _dispatcher.Post(() => _shell.SetProfileCommand.Execute(name)));
        }

        _muteItem.Click += (_, _) => _dispatcher.Post(() => _shell.ToggleMuteCommand.Execute(null));
        _settingsItem.Click += (_, _) => _dispatcher.Post(() => ShowRequested?.Invoke(this, EventArgs.Empty));
        _exitItem.Click += (_, _) => _dispatcher.Post(App.RequestExit);
        _menu.Items.Add(_muteItem);
        _menu.Items.Add(_profilesItem);
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _trayIcon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _trayIcon.DoubleClick += (_, _) => _dispatcher.Post(() => ShowRequested?.Invoke(this, EventArgs.Empty));

        _tooltipTimer = new DispatcherTimer { Interval = TooltipInterval };
        _tooltipTimer.Tick += (_, _) => RefreshTooltip();
        _tooltipTimer.Start();

        Refresh();
        _shell.PropertyChanged += OnShellPropertyChanged;
        _reminder.Changed += (_, _) => _dispatcher.Post(RefreshTooltip);
    }

    public event EventHandler? ShowRequested;

    /// <summary>Alert icon for arrival or delay; the tooltip carries the same state so colour is never the only cue.</summary>
    public void ShowAlert(string text)
    {
        _trayIcon.Icon = _alertIcon;
        _trayIcon.Text = Truncate(text);
    }

    public void Dispose()
    {
        _tooltipTimer.Stop();
        _shell.PropertyChanged -= OnShellPropertyChanged;
        _trayIcon.Dispose();
        _menu.Dispose();
        _activeIcon.Dispose();
        _mutedIcon.Dispose();
        _alertIcon.Dispose();
    }

    /// <summary>Re-applies every label and the icon, e.g. after a language change.</summary>
    public void Refresh()
    {
        _trayIcon.Icon = _shell.IsMuted ? _mutedIcon : _activeIcon;
        _muteItem.Text = _shell.TrayMenuMuteText;
        _profilesItem.Text = _localizer.Get("TrayProfiles");
        _settingsItem.Text = _localizer.Get("TraySettings");
        _exitItem.Text = _localizer.Get("TrayExit");
        RefreshTooltip();
    }

    private void RefreshTooltip()
    {
        var watched = _reminder.Watched;
        if (watched is null)
        {
            _trayIcon.Text = Truncate(_shell.IsMuted ? _shell.TrayText : _localizer.Get("TrayNoRoute"));
            return;
        }

        var minutes = Math.Max(0, (int)Math.Ceiling((watched.BusDeparture - _clock.UtcNow).TotalMinutes));
        _trayIcon.Text = Truncate(string.Format(CultureInfo.CurrentCulture, _localizer.Get("TrayNextDeparture"), watched.Line, minutes));
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsMuted) or nameof(ShellViewModel.TrayText) or nameof(ShellViewModel.TrayMenuMuteText) or nameof(ShellViewModel.FlowDirection))
        {
            _dispatcher.Post(Refresh);
        }
    }

    // NotifyIcon.Text is limited to 63 characters.
    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];

    private static System.Drawing.Icon LoadIcon(string fileName) =>
        new(Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", fileName));
}
