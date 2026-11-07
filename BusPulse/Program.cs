using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.Services.Calendar;
using BusPulse.Services.Geocoding;
using BusPulse.Services.LiveData;
using BusPulse.Services.Notifications;
using BusPulse.Services.Location;
using BusPulse.Services.Secrets;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using BusPulse.Services.Trips.Gtfs;
using BusPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BusPulse;

internal static class Program
{
    public static IHost BuildHost(AppConfigStore configStore, AppConfig config)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(config);
                services.AddSingleton(configStore);
                services.AddSingleton<ToastNotificationService>();
                services.AddSingleton<INotifier>(provider => provider.GetRequiredService<ToastNotificationService>());
                services.AddSingleton<SoundService>();
                services.AddSingleton<IClock, SystemClock>();
                services.AddSingleton<ILiveArrivalsProvider, CurlbusArrivalsProvider>();
                services.AddSingleton<ILiveArrivalsProvider, RailProvider>();
                services.AddSingleton<IScheduleProvider, StrideScheduleProvider>();
                services.AddSingleton<ILiveDataAggregator, LiveDataAggregator>();
                services.AddSingleton<ILocationService, WindowsLocationService>();
                services.AddSingleton<IStopCatalog, StrideStopCatalog>();
                services.AddSingleton<IRideScheduleSource, StrideRideScheduleSource>();
                services.AddSingleton<GtfsFeedScheduleSource>();
                services.AddSingleton<IFutureScheduleSource>(provider => provider.GetRequiredService<GtfsFeedScheduleSource>());
                // The Ministry of Transport feed is about 150 MB; HTTPS from one fixed host, no redirects.
                services.AddHttpClient(GtfsFeedStore.ClientName, client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(15);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("BusPulseApp/1.0");
                }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
                services.AddSingleton<ITripPlannerService, TripPlannerService>();
                services.AddSingleton<IHistoryService, HistoryService>();
                services.AddSingleton<IAlertPresenter, WindowsAlertPresenter>();
                services.AddSingleton<DepartureReminderService>();
                services.AddSingleton<IDepartureReminder>(provider => provider.GetRequiredService<DepartureReminderService>());
                services.AddHostedService<DepartureReminderWorker>();
                services.AddHttpClient(CurlbusArrivalsProvider.ClientName, client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "BusPulse/0.1 (+https://github.com/DavidHorvitz/NextBusAgent)");
                }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false
                });
                services.AddSingleton<IGeocoder, NominatimGeocoder>();
                services.AddHttpClient(NominatimGeocoder.ClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
                    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
                services.AddSingleton<IDispatcher, WpfDispatcher>();
                services.AddSingleton(InfisicalOptions.FromEnvironment());
                services.AddHttpClient(InfisicalSecretProvider.ClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
                    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
                services.AddSingleton<InfisicalSecretProvider>();
                services.AddSingleton<EnvironmentSecretProvider>();
                services.AddSingleton<ISecretProvider, ChainedSecretProvider>();
                services.AddSingleton<CalendarTokenStore>();
                services.AddSingleton<ICalendarService, GoogleCalendarService>();
                services.AddHttpClient("Google", client => client.Timeout = TimeSpan.FromSeconds(15));
                services.AddSingleton<IThemeService, WpfUiThemeService>();
                services.AddSingleton<ILocalizer, ResourceLocalizer>();
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<SavedPlaceCollection>();
                services.AddSingleton<StopsViewModel>();
                services.AddSingleton<TripPlannerViewModel>();
                services.AddSingleton<LinesViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<TrayController>();
                services.AddSingleton<MainWindow>();
                // Stride answers small queries in 10-25 s on slow days; 12 s made every request fail.
                services.AddHttpClient(StrideStopCatalog.BulkClientName, client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(120);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("BusPulseApp/1.0");
                }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
                services.AddHttpClient("TransitApi", client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(45);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("BusPulseApp/1.0");
                }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        EnabledSslProtocols = SslProtocols.None
                    }
                });
                services.AddHostedService<SchedulePollingService>();
            })
            .Build();
        return host;
    }
}