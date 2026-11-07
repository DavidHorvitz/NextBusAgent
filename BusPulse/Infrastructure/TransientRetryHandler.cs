using System.Net;
using System.Net.Http;

namespace BusPulse.Infrastructure;

/// <summary>
/// Retries a GET that failed for a transient reason: a server error (500, 502, 503, 504), 408, or a dropped connection.
/// Stride answers with sporadic 5xx under load and a second try a moment later usually succeeds. Other methods and
/// client errors are never retried. A timeout or a cancelled caller is not retried either: the client timeout covers
/// all attempts, so a retry never extends the wait beyond it.
/// </summary>
public sealed class TransientRetryHandler(params TimeSpan[] delays) : DelegatingHandler
{
    private static readonly TimeSpan[] DefaultDelays = [TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1500)];

    private readonly TimeSpan[] _delays = delays.Length == 0 ? DefaultDelays : delays;

    /// <summary>Replaceable so tests do not really wait.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (span, token) => Task.Delay(span, token);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        for (var attempt = 0; ; attempt++)
        {
            var isLast = attempt >= _delays.Length;
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(attempt == 0 ? request : Clone(request), cancellationToken);
            }
            catch (HttpRequestException) when (!isLast && !cancellationToken.IsCancellationRequested)
            {
                await Delay(_delays[attempt], cancellationToken);
                continue;
            }

            if (isLast || !IsTransient(response.StatusCode))
            {
                return response;
            }

            response.Dispose();
            await Delay(_delays[attempt], cancellationToken);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout;

    /// <summary>A request message cannot be sent twice, so each retry sends a copy (GET requests carry no body).</summary>
    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return copy;
    }
}
