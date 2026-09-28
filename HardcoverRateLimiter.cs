using System.Net;
using System.Text.RegularExpressions;
using Serilog;

namespace Chronicle.Plugin.Hardcover;

/// <summary>
/// Client-side limiter that keeps Chronicle at ~90% of Hardcover's published API limits
/// (docs.hardcover.app/api/getting-started, "Rate Limits"): per-minute 60 (Free and Supporter),
/// burst 10 (a token bucket that refills at the per-minute rate), daily 5,000 (Free) / 50,000
/// (Supporter) resetting at midnight UTC. Each top-level query in a request counts as one request
/// against every limit.
///
/// Root-caused live (2026-09-28): the previous fixed 250ms spacing allowed ~240 requests/minute
/// -- four times the per-minute limit -- which produced ~40 429s a minute, and together with a
/// full-library enrichment pass plus a series sweep it exhausted the DAILY cap, after which
/// Hardcover answered every request with a Retry-After of many hours until 00:00 UTC.
///
/// Three layers: (1) a token bucket (capacity 8 vs. the published burst of 10, refilled at 0.9/s =
/// 54/min vs. 60), (2) the server's own RateLimit headers as the authority, clamping the bucket and
/// noticing an exhausted daily quota, (3) a circuit breaker so every caller fails fast, with a 429
/// <see cref="HttpRequestException"/> the host already treats as "provider unavailable, stop this
/// pass, leave items Pending", instead of waiting out hours.
/// </summary>
internal sealed class HardcoverRateLimiter
{
    public const double BucketCapacity  = 8;
    public const double RefillPerSecond = 0.9;

    /// <summary>A 429 asking for at most this long is waited out inline (the per-minute bucket
    /// refilling); anything longer is a daily-cap block.</summary>
    public static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(90);

    private static readonly ILogger Log = Serilog.Log.ForContext<HardcoverRateLimiter>();

    // "Free";r=8;t=42, "daily";r=4231;t=51234  (r = remaining, t = seconds until that bucket resets)
    private static readonly Regex EntryRe =
        new(@"""(?<name>[^""]+)""\s*;\s*r=(?<r>\d+)\s*;\s*t=(?<t>\d+)", RegexOptions.Compiled);

    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();

    private double   _tokens = BucketCapacity;
    private DateTime _lastRefill;
    private DateTime _blockedUntil = DateTime.MinValue;
    private DateTime _pausedUntil  = DateTime.MinValue;
    private string?  _blockReason;
    private int      _dailyRemaining = -1;

    public HardcoverRateLimiter(Func<DateTime>? utcNow = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delay  = delay  ?? ((t, ct) => Task.Delay(t, ct));
        _lastRefill = _utcNow();
    }

    /// <summary>The process-wide instance shared by every HardcoverClient (one API token, one quota).</summary>
    public static HardcoverRateLimiter Shared { get; } = new();

    public double Tokens { get { lock (_lock) return _tokens; } }
    public int DailyRemaining { get { lock (_lock) return _dailyRemaining; } }
    public bool IsBlocked { get { lock (_lock) return _utcNow() < _blockedUntil; } }

    /// <summary>The limiter's own (injectable) delay, for callers that back off on the same clock.</summary>
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => _delay(delay, ct);

    /// <summary>Takes one token, waiting for a refill when empty. Throws at once (no network call)
    /// while the breaker is open.</summary>
    public async Task AcquireAsync(CancellationToken ct)
    {
        ThrowIfBlocked();
        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfBlocked();
            var wait = TimeSpan.Zero;
            lock (_lock)
            {
                Refill();
                if (_tokens < 1) wait = TimeSpan.FromSeconds((1 - _tokens) / RefillPerSecond);
                // A 429 pauses EVERY caller, not just the one that received it -- otherwise the
                // others keep sending ~1s later, take their own 429s and burn their retries.
                var pause = _pausedUntil - _utcNow();
                if (pause > wait) wait = pause;
            }
            if (wait > TimeSpan.Zero)
            {
                await _delay(wait, ct);
                lock (_lock) { Refill(); _tokens = Math.Max(_tokens, 1); }
                ThrowIfBlocked(); // a breaker may have opened while we waited
            }
            lock (_lock) _tokens -= 1;
        }
        finally { _gate.Release(); }
    }

    private void Refill()
    {
        var now = _utcNow();
        _tokens = Math.Min(BucketCapacity, _tokens + (now - _lastRefill).TotalSeconds * RefillPerSecond);
        _lastRefill = now;
    }

    public void ThrowIfBlocked()
    {
        DateTime until; string? reason;
        lock (_lock) { until = _blockedUntil; reason = _blockReason; }
        if (_utcNow() < until)
            throw new HttpRequestException(
                $"Hardcover API is rate-limited until {until:u} ({reason}); not calling it until then.",
                null, HttpStatusCode.TooManyRequests);
    }

    public void OpenBreaker(TimeSpan duration, string reason)
    {
        lock (_lock)
        {
            var until = _utcNow() + duration;
            // Never shorten an active block: a caller that ran out of short retries must not
            // replace the daily-quota breaker another caller just opened with a 1-minute one.
            if (until > _blockedUntil) { _blockedUntil = until; _blockReason = reason; }
        }
        Log.Warning("Hardcover circuit breaker OPEN for {Duration}: {Reason}", duration, reason);
    }

    /// <summary>Forget any block/pause -- used when the API token changes (a new token, or an upgrade
    /// to Supporter's larger daily quota, must not stay locked out by the old token's penalty).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _blockedUntil = DateTime.MinValue;
            _pausedUntil = DateTime.MinValue;
            _blockReason = null;
            _dailyRemaining = -1;
        }
    }

    /// <summary>A short 429 just arrived: drain the bucket and pause every caller for
    /// <paramref name="pause"/> (the server's Retry-After plus a margin).</summary>
    public void NoteThrottled(TimeSpan pause)
    {
        lock (_lock)
        {
            _tokens = 0;
            _lastRefill = _utcNow();
            var until = _utcNow() + pause;
            if (until > _pausedUntil) _pausedUntil = until;
        }
    }

    /// <summary>Applies the RateLimit header: the per-minute bucket is clamped down to what the
    /// server says is left, and an exhausted daily bucket opens the breaker until it resets.</summary>
    public void Observe(IEnumerable<string> rateLimitHeaderValues)
    {
        // Runs on every response before status handling: it must never throw and mask the response.
        try
        {
            foreach (Match m in EntryRe.Matches(string.Join(", ", rateLimitHeaderValues)))
            {
                if (!int.TryParse(m.Groups["r"].Value, out var remaining) ||
                    !int.TryParse(m.Groups["t"].Value, out var resetSecs))
                    continue;
                var name = m.Groups["name"].Value;

                if (string.Equals(name, "daily", StringComparison.OrdinalIgnoreCase))
                {
                    int previous;
                    lock (_lock) { previous = _dailyRemaining; _dailyRemaining = remaining; }
                    if (remaining <= 0)
                        OpenBreaker(TimeSpan.FromSeconds(resetSecs + 5.0), "daily request quota exhausted");
                    else if (previous < 0 || remaining / 500 != previous / 500)
                        Log.Information("Hardcover daily quota: {Remaining} requests remaining (resets in {Hours:F1}h)",
                            remaining, resetSecs / 3600.0);
                }
                else
                {
                    // Per-minute bucket: never assume more tokens than the server says are left.
                    lock (_lock)
                    {
                        Refill(); // credit elapsed time first, so the clamp isn't credited again later
                        _tokens = Math.Min(_tokens, remaining);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Hardcover RateLimit header could not be parsed; ignoring it");
        }
    }
}
