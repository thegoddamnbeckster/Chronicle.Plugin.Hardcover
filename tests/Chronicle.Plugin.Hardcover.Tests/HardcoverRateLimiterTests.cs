using System.Net;

namespace Chronicle.Plugin.Hardcover.Tests;

/// <summary>
/// Hardcover's published limits: 60/min sustained, burst 10, 5,000/day (Free) resetting at 00:00
/// UTC. The limiter must stay under all three and must fail fast, not wait, once the daily quota
/// is gone (root-caused live 2026-09-28).
/// </summary>
public class HardcoverRateLimiterTests
{
    /// <summary>A limiter on a fake clock whose Delay just advances that clock.</summary>
    internal sealed class Fake
    {
        public DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        public TimeSpan Waited = TimeSpan.Zero;
        public HardcoverRateLimiter Limiter { get; }
        public Fake() => Limiter = new HardcoverRateLimiter(() => Now, (t, _) => { Now += t; Waited += t; return Task.CompletedTask; });
    }

    [Fact]
    public async Task ABurstUpToTheBucketCapacity_IsNotDelayed()
    {
        var f = new Fake();
        for (var i = 0; i < (int)HardcoverRateLimiter.BucketCapacity; i++)
            await f.Limiter.AcquireAsync(default);

        Assert.Equal(TimeSpan.Zero, f.Waited);
    }

    [Fact]
    public async Task InAnySlidingWindow_StaysUnderHardcoversTokenBucket()
    {
        var f = new Fake();
        var sent = new List<DateTime>();
        for (var i = 0; i < 400; i++)
        {
            await f.Limiter.AcquireAsync(default);
            sent.Add(f.Now);
        }

        // Hardcover enforces a token bucket (burst 10, refilling at 60/min), so a window of length L may
        // legitimately hold burst + L*1/s requests: 70 in 60s. We must stay under that with margin --
        // our own bucket allows 8 + 0.9/s = ~62 in 60s.
        for (var i = 0; i < sent.Count; i++)
        {
            var window = sent.Skip(i).TakeWhile(t => t - sent[i] <= TimeSpan.FromSeconds(60)).Count();
            Assert.True(window <= 64, $"{window} requests inside 60s starting at #{i}");
            Assert.True(window < 10 + 60, "must stay under Hardcover's own bucket");
        }
        for (var i = 0; i < sent.Count; i++)
        {
            var shortWindow = sent.Skip(i).TakeWhile(t => t - sent[i] <= TimeSpan.FromSeconds(1)).Count();
            Assert.True(shortWindow <= 10, $"{shortWindow} requests inside 1s starting at #{i}");
        }
        // And it is a real limiter, not a stall: long-run throughput is close to (but under) 60/min.
        var perMinute = (sent.Count - HardcoverRateLimiter.BucketCapacity) / (sent[^1] - sent[0]).TotalMinutes;
        Assert.InRange(perMinute, 50, 56);
    }

    [Fact]
    public async Task AFullRefill_AllowsAnotherFullBurst_ButNotAnExtraRequestOnTopOfIt()
    {
        var f = new Fake();
        for (var i = 0; i < 8; i++) await f.Limiter.AcquireAsync(default);
        f.Now += TimeSpan.FromHours(1);

        for (var i = 0; i < 8; i++) await f.Limiter.AcquireAsync(default);
        Assert.Equal(TimeSpan.Zero, f.Waited);

        await f.Limiter.AcquireAsync(default);
        Assert.True(f.Waited > TimeSpan.Zero);
    }

    [Fact]
    public void AnExhaustedDailyQuota_OpensTheBreakerUntilTheServersResetTime()
    {
        var f = new Fake();
        f.Limiter.Observe(["\"Free\";r=8;t=42, \"daily\";r=0;t=35000"]);

        Assert.True(f.Limiter.IsBlocked);
        var ex = Assert.Throws<HttpRequestException>(() => f.Limiter.ThrowIfBlocked());
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);

        f.Now += TimeSpan.FromSeconds(35004);
        Assert.True(f.Limiter.IsBlocked); // the +5s margin
        f.Now += TimeSpan.FromSeconds(2);
        Assert.False(f.Limiter.IsBlocked);
    }

    [Fact]
    public async Task WhileTheBreakerIsOpen_AcquireFailsImmediately_WithoutWaiting()
    {
        var f = new Fake();
        f.Limiter.OpenBreaker(TimeSpan.FromHours(9), "daily quota");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => f.Limiter.AcquireAsync(default));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.Zero, f.Waited);
    }

    [Fact]
    public void ABreakerNeverShortensAnActiveBlock()
    {
        // The daily-quota breaker is open for 9h; another caller running out of short retries opens a
        // 1-minute one -- that must not replace it.
        var f = new Fake();
        f.Limiter.OpenBreaker(TimeSpan.FromHours(9), "daily quota");
        f.Limiter.OpenBreaker(TimeSpan.FromMinutes(1), "still rate-limited after 3 attempts");

        f.Now += TimeSpan.FromHours(1);
        Assert.True(f.Limiter.IsBlocked);
        Assert.Contains("daily quota", Assert.Throws<HttpRequestException>(() => f.Limiter.ThrowIfBlocked()).Message);
    }

    [Fact]
    public async Task ABreakerOpenedWhileACallWaitsForAToken_StopsThatCallInsteadOfSendingIt()
    {
        var now = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        HardcoverRateLimiter? limiter = null;
        limiter = new HardcoverRateLimiter(() => now, (t, _) =>
        {
            now += t;
            limiter!.OpenBreaker(TimeSpan.FromHours(1), "opened by another caller while this one waited");
            return Task.CompletedTask;
        });
        for (var i = 0; i < 8; i++) await limiter.AcquireAsync(default); // drains the bucket, no waiting yet

        await Assert.ThrowsAsync<HttpRequestException>(() => limiter.AcquireAsync(default)); // 9th must wait -> breaker opens
    }

    [Fact]
    public async Task A429OnOneCaller_PausesEveryoneElseForTheRetryAfter()
    {
        var f = new Fake();
        f.Limiter.NoteThrottled(TimeSpan.FromSeconds(31));

        await f.Limiter.AcquireAsync(default); // a DIFFERENT caller

        Assert.True(f.Waited >= TimeSpan.FromSeconds(31));
    }

    [Fact]
    public void TheServersPerMinuteRemaining_ClampsOurLocalEstimateDown_ButNeverUp()
    {
        var f = new Fake();
        f.Limiter.Observe(["\"Free\";r=2;t=30, \"daily\";r=4000;t=50000"]);
        Assert.Equal(2, f.Limiter.Tokens);
        Assert.Equal(4000, f.Limiter.DailyRemaining);

        f.Limiter.Observe(["\"Free\";r=9;t=1, \"daily\";r=3999;t=49999"]);
        Assert.Equal(2, f.Limiter.Tokens); // 9 does not raise what we already believe is left
        Assert.False(f.Limiter.IsBlocked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("\"Free\";r=abc;t=1")]
    [InlineData("\"Free\";r=99999999999;t=99999999999, \"daily\";r=99999999999;t=99999999999")] // overflows int
    public void AMalformedOrOverflowingHeader_IsIgnored_AndNeverThrows(string header)
    {
        var f = new Fake();

        f.Limiter.Observe([header]);

        Assert.Equal(HardcoverRateLimiter.BucketCapacity, f.Limiter.Tokens);
        Assert.False(f.Limiter.IsBlocked);
    }

    [Fact]
    public async Task Reset_ClearsTheBreakerAndPause_ForANewToken()
    {
        var f = new Fake();
        f.Limiter.OpenBreaker(TimeSpan.FromHours(9), "daily quota");
        f.Limiter.NoteThrottled(TimeSpan.FromMinutes(5));

        f.Limiter.Reset();

        Assert.False(f.Limiter.IsBlocked);
        await f.Limiter.AcquireAsync(default);
        // Only the one-token refill of the drained bucket -- not the 9h block or the 5-minute pause.
        Assert.True(f.Waited < TimeSpan.FromSeconds(2));
    }
}
