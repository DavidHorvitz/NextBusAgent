using System.Windows;
using BusPulse.Infrastructure;
using BusPulse.ViewModels;

namespace BusPulse;

public partial class MainWindow : Window
{
    private readonly TrayController _tray;

    public MainWindow(ShellViewModel shell, TrayController tray)
    {
        InitializeComponent();
        DataContext = shell;
        _tray = tray;
        shell.HideRequested += (_, _) => Hide();
        tray.ShowRequested += (_, _) => ShowWindow();
        Closed += (_, _) => _tray.Dispose();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
