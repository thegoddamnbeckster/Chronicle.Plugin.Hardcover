using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Chronicle.Plugin.Hardcover.Tests;

/// <summary>
/// The 429 / quota behaviour that turned a burst of traffic into an all-day lockout
/// (root-caused live 2026-09-28).
/// </summary>
public class HardcoverClientTests
{
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var i = Math.Min(Interlocked.Increment(ref _calls) - 1, script.Length - 1);
            return Task.FromResult(script[i]());
        }
    }

    private static HttpResponseMessage Ok(string? rateLimit = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":{\"authors\":[]}}", Encoding.UTF8, "application/json"),
        };
        if (rateLimit is not null) r.Headers.TryAddWithoutValidation("RateLimit", rateLimit);
        return r;
    }

    private static HttpResponseMessage TooMany(TimeSpan? delta = null, DateTimeOffset? date = null, string? rateLimit = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (delta is not null) r.Headers.RetryAfter = new RetryConditionHeaderValue(delta.Value);
        if (date is not null) r.Headers.RetryAfter = new RetryConditionHeaderValue(date.Value);
        if (rateLimit is not null) r.Headers.TryAddWithoutValidation("RateLimit", rateLimit);
        return r;
    }

    private static (HardcoverClient client, HardcoverRateLimiterTests.Fake clock, ScriptedHandler handler) Make(params Func<HttpResponseMessage>[] script)
    {
        var clock = new HardcoverRateLimiterTests.Fake();
        var handler = new ScriptedHandler(script);
        return (new HardcoverClient("token", handler, clock.Limiter), clock, handler);
    }

    private static Task Query(HardcoverClient c) => c.GetAuthorsByNameExactAsync("X", ct: default);

    [Fact]
    public async Task ALongRetryAfter_IsTheDailyCap_FailsFastWithA429_AndBlocksLaterCallsWithoutTouchingTheNetwork()
    {
        var (client, clock, handler) = Make(() => TooMany(TimeSpan.FromSeconds(35000)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Query(client));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.True(clock.Limiter.IsBlocked);
        Assert.True(clock.Waited < TimeSpan.FromSeconds(5)); // did not wait the ~9.7 hours

        await Assert.ThrowsAsync<HttpRequestException>(() => Query(client));
        Assert.Equal(1, handler.Calls); // second call never reached the network
    }

    [Fact]
    public async Task ARetryAfterGivenAsAnHttpDate_IsHonoured_SoADailyCapIsNotMistakenForAShortWait()
    {
        var (client, clock, handler) = Make(() => TooMany(date: DateTimeOffset.UtcNow.AddHours(9)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Query(client));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.True(clock.Limiter.IsBlocked);
    }

    [Fact]
    public async Task AShortRetryAfter_PausesAndRetries_ThenSucceeds()
    {
        var (client, clock, handler) = Make(() => TooMany(TimeSpan.FromSeconds(20)), () => Ok());

        await Query(client); // must not throw

        Assert.Equal(2, handler.Calls);
        Assert.True(clock.Waited >= TimeSpan.FromSeconds(20));
        Assert.False(clock.Limiter.IsBlocked);
    }

    [Fact]
    public async Task ThreeShortRateLimitsInARow_GiveUpWithA429_WithoutASleepAfterTheLastAttempt_AndOpenAOneMinuteBreaker()
    {
        var (client, clock, handler) = Make(() => TooMany(TimeSpan.FromSeconds(20)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Query(client));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(3, handler.Calls);
        // Two pauses of ~21s (after attempts 1 and 2) -- and none after the third.
        Assert.InRange(clock.Waited.TotalSeconds, 42, 50);
        Assert.True(clock.Limiter.IsBlocked);
    }

    [Fact]
    public async Task ADailyRemainingOfZeroOnA429_FailsFastEvenWithoutALongRetryAfter()
    {
        var (client, clock, handler) = Make(() => TooMany(TimeSpan.FromSeconds(1), rateLimit: "\"Free\";r=0;t=30, \"daily\";r=0;t=30000"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Query(client));

        Assert.Equal(1, handler.Calls);
        Assert.True(clock.Limiter.IsBlocked);
    }

    [Fact]
    public async Task AnOkResponseThatReportsTheDailyQuotaExhausted_BlocksTheNextCall()
    {
        var (client, clock, handler) = Make(() => Ok("\"Free\";r=5;t=10, \"daily\";r=0;t=20000"));

        await Query(client); // this one succeeded
        Assert.True(clock.Limiter.IsBlocked);

        await Assert.ThrowsAsync<HttpRequestException>(() => Query(client));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task APersistent403_ThrowsTheTokenMessageOnce_ThenFailsFastInsteadOfRetryingForEveryItem()
    {
        var (client, clock, handler) = Make(() => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => Query(client));
        Assert.Contains("token", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.Calls);

        var second = await Assert.ThrowsAsync<HttpRequestException>(() => Query(client));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(3, handler.Calls);
    }
}
