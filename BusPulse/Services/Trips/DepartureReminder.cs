using System.Globalization;
using BusPulse.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Trips;

public interface IDepartureReminder
{
    TripOption? Watched { get; }

    event EventHandler? Changed;

    void Watch(TripRequest request, TripOption option);

    void Cancel();

    /// <summary>Re-plans the watched trip and notifies when it is time to leave or when the bus is delayed.</summary>
    Task<bool> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed class DepartureReminderService(
    ITripPlannerService planner,
    INotifier notifier,
    IClock clock,
    AppConfig config,
    ILogger<DepartureReminderService> logger) : IDepartureReminder
{
    private static readonly TimeSpan DelayThreshold = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MissedGrace = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private TripRequest? _request;
    private DateTimeOffset _notifiedBusDeparture;
    private bool _leaveNotified;

    public TripOption? Watched { get; private set; }

    public event EventHandler? Changed;

    public void Watch(TripRequest request, TripOption option)
    {
        lock (_gate)
        {
            _request = request;
            Watched = option;
            _leaveNotified = false;
            _notifiedBusDeparture = option.BusDeparture;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _request = null;
            Watched = null;
            _leaveNotified = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        TripRequest? request;
        TripOption? watched;
        lock (_gate)
        {
            request = _request;
            watched = Watched;
        }

        if (request is null || watched is null)
        {
            return false;
        }

        var now = clock.UtcNow;
        var plan = await planner.PlanAsync(request, cancellationToken);
        var current = plan.Options.FirstOrDefault(option => option.RideId == watched.RideId);
        if (current is null)
        {
            // The ride dropped out of the plan; if it is clearly in the past, stop watching.
            if (watched.LeaveBy < now - MissedGrace)
            {
                Cancel();
            }

            return false;
        }

        lock (_gate)
        {
            Watched = current;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        if (current.LeaveBy < now - MissedGrace)
        {
            Cancel();
            return false;
        }

        var notified = false;
        var minutesToLeave = (int)Math.Ceiling((current.LeaveBy - now).TotalMinutes);
        if (!_leaveNotified && minutesToLeave <= Math.Clamp(config.LeadMinutes, 1, 60))
        {
            _leaveNotified = true;
            _notifiedBusDeparture = current.BusDeparture;
            notifier.Notify(
                $"{current.Line} · {current.OriginStop.Name}",
                string.Format(CultureInfo.CurrentCulture, "Leave in {0} min · bus at {1:HH:mm}", Math.Max(0, minutesToLeave), current.BusDeparture.ToLocalTime()),
                NotificationKind.Departure);
            notified = true;
        }
        else if (_leaveNotified && current.BusDeparture - _notifiedBusDeparture >= DelayThreshold)
        {
            var delay = (int)Math.Round((current.BusDeparture - _notifiedBusDeparture).TotalMinutes);
            _notifiedBusDeparture = current.BusDeparture;
            notifier.Notify(
                $"{current.Line} · {current.OriginStop.Name}",
                string.Format(CultureInfo.CurrentCulture, "Delayed by {0} min · now at {1:HH:mm}", delay, current.BusDeparture.ToLocalTime()),
                NotificationKind.Delay);
            notified = true;
        }

        logger.LogDebug("Reminder check: {Minutes} min to leave, notified={Notified}.", minutesToLeave, notified);
        return notified;
    }
}

/// <summary>Runs the reminder check on the configured polling interval, independent of the window being visible.</summary>
public sealed class DepartureReminderWorker(IDepartureReminder reminder, AppConfig config, ILogger<DepartureReminderWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(config.PollingSeconds, 15, 60)), stoppingToken);
                if (reminder.Watched is not null)
                {
                    await reminder.CheckAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning(exception, "Departure reminder check failed; it will retry.");
            }
        }
    }
}
