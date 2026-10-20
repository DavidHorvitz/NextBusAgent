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
            themeService.Apply(config.Theme);

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