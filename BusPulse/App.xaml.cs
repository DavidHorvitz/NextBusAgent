using System.IO;
using System.Windows;
using BusPulse.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Application = System.Windows.Application;

namespace BusPulse;

public partial class App : Application
{
    private IHost? _host;

    /// <summary>True once the user chose Exit (or Windows is ending the session); the main window then really closes.</summary>
    public static bool IsExiting { get; private set; }

    /// <summary>Ends the app from the tray menu; closing the window only hides it.</summary>
    public static void RequestExit()
    {
        IsExiting = true;
        Current.Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        base.OnSessionEnding(e);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterCrashLogging();

        try
        {
            var configStore = new AppConfigStore();
            var config = await configStore.LoadAsync();
            _host = Program.BuildHost(configStore, config);
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.ShowInTaskbar = true;
            window.WindowState = WindowState.Normal;
            window.Show();
            window.Visibility = Visibility.Visible;
            window.Activate();
            var themeService = _host.Services.GetRequiredService<Infrastructure.IThemeService>();
            themeService.Attach(window);
            themeService.Apply(LaunchOption(e.Args, "theme", ["system", "light", "dark"]) ?? config.Theme);
            if (LaunchOption(e.Args, "tab", ["0", "1", "2", "3"]) is { } tab)
            {
                _host.Services.GetRequiredService<ViewModels.ShellViewModel>().SelectTabCommand.Execute(tab);
            }

            _ = StartHostAsync(_host);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "BusPulse startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <summary>
    /// Reads a diagnostic launch flag such as <c>--theme=dark</c> or <c>--tab=2</c>. These affect only the current run and are
    /// never saved; values outside <paramref name="allowed"/> are ignored.
    /// </summary>
    private static string? LaunchOption(string[] args, string name, string[] allowed)
    {
        var prefix = $"--{name}=";
        var value = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
        return value is not null && allowed.Contains(value, StringComparer.OrdinalIgnoreCase) ? value.ToLowerInvariant() : null;
    }

    /// <summary>Writes unhandled exceptions to %LOCALAPPDATA%\BusPulse\crash.log so a crash leaves a trace.</summary>
    private void RegisterCrashLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("UI thread", args.Exception);
            System.Windows.MessageBox.Show(args.Exception.Message, "BusPulse", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("Task", args.Exception);
            args.SetObserved();
        };
    }

    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusPulse");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"{DateTimeOffset.Now:O} [{source}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done if the log itself cannot be written.
        }
    }

    private async Task StartHostAsync(IHost host)
    {
        try
        {
            await host.StartAsync();
        }
        catch (Exception exception)
        {
            Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                exception.Message,
                "BusPulse background services failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _ = _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}