using System.ComponentModel;
using System.IO;
using System.Windows.Forms;
using BusPulse.ViewModels;
using Application = System.Windows.Application;

namespace BusPulse.Infrastructure;

/// <summary>Owns the notification-area icon; its state and menu are driven by <see cref="ShellViewModel"/>.</summary>
public sealed class TrayController : IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly IDispatcher _dispatcher;
    private readonly System.Drawing.Icon _activeIcon = LoadIcon("bus_active.ico");
    private readonly System.Drawing.Icon _mutedIcon = LoadIcon("bus_muted.ico");
    private readonly System.Drawing.Icon _alertIcon = LoadIcon("bus_alert.ico");
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _muteItem;

    public TrayController(ShellViewModel shell, IDispatcher dispatcher)
    {
        _shell = shell;
        _dispatcher = dispatcher;

        var profiles = new ToolStripMenuItem("Profiles");
        foreach (var profile in new[] { "Default", "Work", "Weekend" })
        {
            profiles.DropDownItems.Add(profile, null, (_, _) => _dispatcher.Post(() => _shell.SetProfileCommand.Execute(profile)));
        }

        _muteItem = new ToolStripMenuItem(_shell.TrayMenuMuteText, null, (_, _) => _dispatcher.Post(() => _shell.ToggleMuteCommand.Execute(null)));
        var menu = new ContextMenuStrip();
        menu.Items.Add(_muteItem);
        menu.Items.Add(profiles);
        menu.Items.Add("Settings", null, (_, _) => _dispatcher.Post(() => ShowRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _dispatcher.Post(() => Application.Current.Shutdown()));

        _trayIcon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _trayIcon.DoubleClick += (_, _) => _dispatcher.Post(() => ShowRequested?.Invoke(this, EventArgs.Empty));
        Refresh();
        _shell.PropertyChanged += OnShellPropertyChanged;
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
        _shell.PropertyChanged -= OnShellPropertyChanged;
        _trayIcon.Dispose();
        _activeIcon.Dispose();
        _mutedIcon.Dispose();
        _alertIcon.Dispose();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsMuted) or nameof(ShellViewModel.TrayText))
        {
            _dispatcher.Post(Refresh);
        }
    }

    private void Refresh()
    {
        _trayIcon.Icon = _shell.IsMuted ? _mutedIcon : _activeIcon;
        _trayIcon.Text = Truncate(_shell.TrayText);
        _muteItem.Text = _shell.TrayMenuMuteText;
    }

    // NotifyIcon.Text is limited to 63 characters.
    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];

    private static System.Drawing.Icon LoadIcon(string fileName) =>
        new(Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", fileName));
}
