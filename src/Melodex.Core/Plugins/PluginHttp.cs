using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

namespace Melodex.Core.Plugins;

/// <summary>Settings for the HTTP clients the core hands to plugins.</summary>
public class PluginHttpOptions
{
    /// <summary>Apply each plugin's <c>maxRequestsPerSecond</c>. Only tests replaying recorded responses turn this off.</summary>
    public bool RateLimit { get; set; } = true;

    /// <summary>Creates the innermost handler. Tests swap in a handler that replays recorded responses.</summary>
    public Func<HttpMessageHandler> PrimaryHandler { get; set; } = () => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    /// <summary>The first retry's wait after a 503, 429 or dropped connection; each later one doubles it, up to 30 seconds.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long the core leaves a plugin alone after it reports its source unavailable, before asking again.</summary>
    public TimeSpan UnavailablePause { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longer wait after <see cref="Library.SourceBackoff.InARowBeforeLongWait"/> unavailable answers in a row.</summary>
    public TimeSpan LongUnavailablePause { get; set; } = TimeSpan.FromMinutes(5);
}

public static class PluginHttp
{
    /// <summary>
    /// Set for requests someone is waiting on, such as a search: they take the next free slot under a plugin's rate
    /// limit ahead of background work like catalogue updates.
    /// </summary>
    public static readonly AsyncLocal<bool> Interactive = new();

    public static readonly ProductInfoHeaderValue[] UserAgent =
    [
        new("Melodex", typeof(PluginHttp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0"),
        new("(https://github.com/dextrd/Melodex)"),
    ];

    /// <summary>
    /// A client with the core's user agent, the plugin's rate limit, and retries on 503 and 429. Retries take their turn
    /// under the rate limit like any other request, so they never push the plugin over it.
    /// </summary>
    public static HttpClient CreateClient(PluginHttpOptions options, double? maxRequestsPerSecond)
    {
        var limiter = options.RateLimit && maxRequestsPerSecond is > 0 ? new RateLimiter(TimeSpan.FromSeconds(1 / maxRequestsPerSecond.Value)) : null;
        HttpMessageHandler handler = options.PrimaryHandler();
        if (limiter is not null)
            handler = new RateLimitHandler(limiter) { InnerHandler = handler };
        handler = new RetryHandler(options.RetryDelay, limiter) { InnerHandler = handler };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        foreach (var product in UserAgent)
            client.DefaultRequestHeaders.UserAgent.Add(product);
        return client;
    }

    /// <summary>
    /// Spaces requests at least <c>interval</c> apart, across every caller of the client. Background requests take a
    /// little less than the limit, leaving room for searches; while an interactive request waits, background ones hold
    /// back, so it goes next. When the source says it's busy, everyone waits out the same pause.
    /// </summary>
    private sealed class RateLimiter(TimeSpan interval)
    {
        /// <summary>The share of the limit background work uses: the rest is kept for someone searching.</summary>
        private const double BackgroundShare = 0.8;

        private static readonly TimeSpan Recheck = TimeSpan.FromMilliseconds(50);

        private readonly Lock _lock = new();
        private readonly TimeSpan _backgroundInterval = interval / BackgroundShare;
        private DateTime _next = DateTime.MinValue;
        private int _interactiveWaiting;

        public async Task WaitTurnAsync(CancellationToken cancellationToken)
        {
            var interactive = Interactive.Value;
            if (interactive)
                Interlocked.Increment(ref _interactiveWaiting);
            try
            {
                while (true)
                {
                    TimeSpan wait;
                    lock (_lock)
                    {
                        var now = DateTime.UtcNow;
                        if (_next <= now && (interactive || Volatile.Read(ref _interactiveWaiting) == 0))
                        {
                            _next = now + (interactive ? interval : _backgroundInterval);
                            return;
                        }
                        wait = _next > now ? _next - now : Recheck;
                    }
                    await Task.Delay(wait, cancellationToken);
                }
            }
            finally
            {
                if (interactive)
                    Interlocked.Decrement(ref _interactiveWaiting);
            }
        }

        /// <summary>No one sends anything for this long: the source asked for a rest.</summary>
        public void Pause(TimeSpan delay)
        {
            lock (_lock)
            {
                var until = DateTime.UtcNow + delay;
                if (until > _next)
                    _next = until;
            }
        }
    }

    private sealed class RateLimitHandler(RateLimiter limiter) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await limiter.WaitTurnAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Retries when the upstream says it's overloaded or rate-limiting, honouring Retry-After, and retries GETs whose
    /// connection failed. A busy answer pauses every request to the source, not only this one. Someone searching gets
    /// fewer tries, so they hear the source is busy in seconds rather than a minute.
    /// </summary>
    private sealed class RetryHandler(TimeSpan firstDelay, RateLimiter? limiter) : DelegatingHandler
    {
        private const int Attempts = 6;
        private const int InteractiveAttempts = 3;
        private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempts = Interactive.Value ? InteractiveAttempts : Attempts;
            for (var attempt = 1; ; attempt++)
            {
                HttpResponseMessage response;
                try
                {
                    response = await base.SendAsync(request, cancellationToken);
                }
                catch (HttpRequestException) when (request.Method == HttpMethod.Get && attempt < attempts)
                {
                    await Task.Delay(Backoff(attempt), cancellationToken);
                    continue;
                }
                if (response.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests) || attempt == attempts)
                    return response;
                var delay = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                if (delay > MaxDelay)
                    delay = MaxDelay;
                response.Dispose();
                // With a rate limit, the pause holds back everyone and the retry waits its turn behind it.
                if (limiter is not null)
                    limiter.Pause(delay);
                else
                    await Task.Delay(delay, cancellationToken);
            }
        }

        private TimeSpan Backoff(int attempt) =>
            TimeSpan.FromTicks(Math.Min(firstDelay.Ticks * (1L << (attempt - 1)), MaxDelay.Ticks));
    }
}
