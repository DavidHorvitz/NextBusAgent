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