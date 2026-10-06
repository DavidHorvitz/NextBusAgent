using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using BusPulse.Configuration;
using BusPulse.Services;
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
                services.AddSingleton<OpenBusService>();
                services.AddSingleton<ToastNotificationService>();
                services.AddSingleton<MainWindow>();
                services.AddHttpClient("TransitApi", client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(12);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("BusPulseApp/1.0");
                }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        EnabledSslProtocols = SslProtocols.Tls13
                    }
                });
                services.AddHostedService<SchedulePollingService>();
            })
            .Build();
        return host;
    }
}