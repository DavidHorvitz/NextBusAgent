using System.Globalization;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Trips;

public interface IDepartureReminder
{
    /// <summary>The route the user selected for alerts, refreshed with live data; null when none is selected.</summary>
    TripOption? Watched { get; }

    event EventHandler? Changed;

    void Watch(TripRequest request, TripOption option);

    void Cancel();

    /// <summary>Re-plans when the polling interval has passed and shows whatever alert is due now.</summary>
    Task<AlertMessage?> TickAsync(CancellationToken cancellationToken = default);
}

/// <summary>Drives the smart alerts for the selected route. Alerts run only while a route is selected.</summary>
public sealed class DepartureReminderService(
    ITripPlannerService planner,
    INotifier notifier,
    IAlertPresenter presenter,
    IClock clock,
    AppConfig config,
    ILocalizer localizer,
    ILogger<DepartureReminderService> logger) : IDepartureReminder
{
    private readonly object _gate = new();
    private readonly SmartAlertEngine _engine = new(localizer);
    private TripRequest? _request;
    private TripOption? _next;
    private DateTimeOffset _lastPlanAt = DateTimeOffset.MinValue;

    public TripOption? Watched { get; private set; }

    /// <summary>Longest a re-plan may run before it is abandoned.</summary>
    public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Longest a tick waits for a re-plan before alerting from the last known times (tests shorten it).</summary>
    public TimeSpan RefreshWait { get; set; } = TimeSpan.FromSeconds(3);

    private Task? _refresh;
    private CancellationTokenSource? _watchCts;

    public event EventHandler? Changed;

    public void Watch(TripRequest request, TripOption option)
    {
        lock (_gate)
        {
            // A re-plan for the previous ride must not block the new ride's refresh.
            StopRefreshLocked();
            _watchCts = new CancellationTokenSource();
            _request = request;
            Watched = option;
            _next = null;
            _lastPlanAt = clock.UtcNow;
            _engine.Reset();
        }

        presenter.DismissAll();
        notifier.Notify(
            string.Format(CultureInfo.CurrentCulture, localizer.Get("ToastAlertAdded"), option.Line, option.ToEndpoint ?? option.DestinationStop.Name),
            string.Format(CultureInfo.CurrentCulture, localizer.Get("ToastLeaveBy"), option.LeaveBy.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), option.OriginStop.Name),
            NotificationKind.Info);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            StopRefreshLocked();
            _request = null;
            Watched = null;
            _next = null;
            _engine.Reset();
        }

        presenter.DismissAll();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StopRefreshLocked()
    {
        var previous = _watchCts;
        _watchCts = null;
        _refresh = null;
        if (previous is null)
        {
            return;
        }

        try
        {
            previous.Cancel();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogDebug(exception, "Stopping the previous re-plan failed.");
        }

        previous.Dispose();
    }

    public async Task<AlertMessage?> TickAsync(CancellationToken cancellationToken = default)
    {
        TripRequest? request;
        TripOption? watched;
        CancellationToken watchToken;
        lock (_gate)
        {
            request = _request;
            watched = Watched;
            watchToken = _watchCts?.Token ?? new CancellationToken(true);
        }

        if (request is null || watched is null)
        {
            return null;
        }

        var now = clock.UtcNow;
        Task? pending;
        lock (_gate)
        {
            // Only start a re-plan for the ride still being watched.
            if (Watched?.RideId == watched.RideId &&
                now - _lastPlanAt >= TimeSpan.FromSeconds(Math.Clamp(config.PollingSeconds, 15, 60)) &&
                _refresh is null or { IsCompleted: true })
            {
                _lastPlanAt = now;
                _refresh = RefreshAsync(request, watched, now, watchToken, cancellationToken);
            }

            pending = _refresh;
        }

        // Use fresh times when they arrive quickly; otherwise alert from the last known times and let the
        // re-plan finish in the background (Stride can take tens of seconds).
        if (pending is { IsCompleted: false })
        {
            await Task.WhenAny(pending, Task.Delay(RefreshWait, cancellationToken));
        }

        lock (_gate)
        {
            watched = Watched;
        }

        if (watched is null)
        {
            return null;
        }

        AlertMessage? message;
        lock (_gate)
        {
            message = _engine.Evaluate(watched, _next, now, config.LeadMinutes);
        }

        if (message is null)
        {
            return null;
        }

        if (message.FullScreen)
        {
            presenter.ShowFullScreen(message);
        }
        else
        {
            presenter.ShowToast(message);
        }

        logger.LogDebug("Trip alert {Kind} shown for line {Line}.", message.Kind, watched.Line);
        if (message.Kind == AlertKind.Missed)
        {
            // The bus has gone; the user picks the next option from the planner.
            var cleared = false;
            lock (_gate)
            {
                if (Watched?.RideId == watched.RideId)
                {
                    _request = null;
                    Watched = null;
                    cleared = true;
                }
            }

            if (cleared)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        return message;
    }

    /// <summary>Runs unobserved in the background, so it handles every failure itself.</summary>
    private async Task RefreshAsync(TripRequest request, TripOption watched, DateTimeOffset now, CancellationToken watchToken, CancellationToken cancellationToken)
    {
        TripPlan plan;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchToken))
        {
            timeout.CancelAfter(RefreshTimeout);
            try
            {
                plan = await planner.PlanAsync(request with { WatchRideId = watched.RideId }, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Re-planning the watched trip timed out or was stopped; using the last known times.");
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning(exception, "Re-planning the watched trip failed; using the last known times.");
                return;
            }
        }

        var current = plan.Options.FirstOrDefault(option => option.RideId == watched.RideId);
        var next = plan.Options
            .Where(option => option.RideId != watched.RideId && option.BusDeparture > now)
            .OrderBy(option => option.BusDeparture)
            .FirstOrDefault();
        lock (_gate)
        {
            if (Watched?.RideId != watched.RideId)
            {
                return;
            }

            // Keep the last known times if the ride dropped out of the plan (for example after it departed).
            Watched = current ?? watched;
            _next = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Ticks the reminder every few seconds, independent of the window being visible.</summary>
public sealed class DepartureReminderWorker(IDepartureReminder reminder, ILogger<DepartureReminderWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                if (reminder.Watched is not null)
                {
                    await reminder.TickAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning(exception, "Trip alert tick failed; it will retry.");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
