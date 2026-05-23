using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Serilog;
using Serilog.Events;

namespace Chronicle.Plugin.Hardcover;

/// <summary>
/// Thin HTTP wrapper around the Hardcover GraphQL endpoint.
/// Handles Bearer-token authentication, 429 rate-limit back-off, and proactive
/// rate limiting (Hardcover allows 60 requests/minute = 1 req/sec).
///
/// Hardcover returns 403 for BOTH invalid tokens AND rate limiting, so the client
/// treats 403 as a retryable error with exponential backoff — only surfacing a
/// "token invalid/expired" error if 403 persists across all retry attempts.
/// </summary>
internal sealed class HardcoverClient : IDisposable
{
    private const string GraphQlUrl = "https://api.hardcover.app/v1/graphql";

    /// <summary>
    /// Minimum milliseconds between successive requests to stay within Hardcover's
    /// 60 req/min rate limit. Applied globally across all concurrent callers via a
    /// static semaphore + timestamp.
    /// </summary>
    private const int MinRequestIntervalMs = 1100;   // 1.1 s — slightly above 1/s limit

    // Static gate shared across all HardcoverClient instances in this process so that
    // even multiple concurrent enrichment tasks respect the single rate limit.
    private static readonly SemaphoreSlim   _rateSem  = new(1, 1);
    private static          long            _lastTick  = 0;          // Environment.TickCount64

    private static readonly ILogger _log = Log.ForContext<HardcoverClient>();

    private readonly HttpClient _http;
    private readonly string     _tokenPreview;   // masked — first 12 JWT chars for diagnostics

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public HardcoverClient(string apiToken)
    {
        // The Hardcover API page shows the full "Bearer eyJ..." header value.
        // Accept either the raw JWT or the full "Bearer {jwt}" string.
        var jwt = apiToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? apiToken["Bearer ".Length..].Trim()
            : apiToken.Trim();

        _tokenPreview = jwt.Length >= 12
            ? jwt[..12] + "…"
            : string.IsNullOrWhiteSpace(jwt) ? "(empty)" : "(short:" + jwt.Length + "chars)";

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        _http.DefaultRequestHeaders.Add(
            "User-Agent", "Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)");
        _http.Timeout = TimeSpan.FromSeconds(30);

        _log.Information("HardcoverClient created — token prefix: {Prefix}", _tokenPreview);
    }

    /// <summary>
    /// Acquires the global rate-limit gate, waits if the last request was less than
    /// <see cref="MinRequestIntervalMs"/> ago, then updates the timestamp.
    /// </summary>
    private static async Task ThrottleAsync(CancellationToken ct)
    {
        await _rateSem.WaitAsync(ct);
        try
        {
            var now  = Environment.TickCount64;
            var wait = (int)(MinRequestIntervalMs - (now - _lastTick));
            if (wait > 0)
                await Task.Delay(wait, ct);
            _lastTick = Environment.TickCount64;
        }
        finally
        {
            _rateSem.Release();
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Fetches the current user's identity — used to verify the token.</summary>
    public Task<MeData?> GetMeAsync(CancellationToken ct = default) =>
        QueryAsync<MeData>(
            "query Me { me { id username } }", ct: ct);

    /// <summary>Returns books the user has marked as "Read" (status_id = 3).</summary>
    public Task<UserBooksData?> GetReadBooksAsync(CancellationToken ct = default) =>
        QueryAsync<UserBooksData>(
            """
            query GetReadBooks {
              user_books(where: { status_id: { _eq: 3 } }, limit: 1000) {
                id
                book {
                  id title release_year
                  book_mappings { isbn_13 isbn_10 }
                  contributions(limit: 1) { author { name } }
                  book_series(limit: 1) { position series { name } }
                }
                rating
                user_book_reads(order_by: { finished_at: desc }, limit: 1) {
                  finished_at started_at
                }
              }
            }
            """, ct: ct);

    /// <summary>Returns books the user has given a rating to.</summary>
    public Task<UserBooksData?> GetRatingsAsync(CancellationToken ct = default) =>
        QueryAsync<UserBooksData>(
            """
            query GetRatings {
              user_books(where: { rating: { _is_null: false } }, limit: 1000) {
                id
                book {
                  id title release_year
                  book_mappings { isbn_13 isbn_10 }
                  contributions(limit: 1) { author { name } }
                  book_series(limit: 1) { position series { name } }
                }
                rating
                inserted_at
              }
            }
            """, ct: ct);

    /// <summary>Returns books the user wants to read (status_id = 1).</summary>
    public Task<UserBooksData?> GetWantToReadAsync(CancellationToken ct = default) =>
        QueryAsync<UserBooksData>(
            """
            query GetWantToRead {
              user_books(where: { status_id: { _eq: 1 } }, limit: 1000) {
                id
                book {
                  id title release_year
                  book_mappings { isbn_13 isbn_10 }
                  contributions(limit: 1) { author { name } }
                  book_series(limit: 1) { position series { name } }
                }
                inserted_at
              }
            }
            """, ct: ct);

    // ── Search ────────────────────────────────────────────────────────────────

    public Task<SearchData?> SearchAuthorsAsync(string query, int perPage = 10, CancellationToken ct = default) =>
        QueryAsync<SearchData>("""
            query SearchAuthors($q: String!, $n: Int!) {
              search(query: $q, query_type: "Author", per_page: $n) { results }
            }
            """, new { q = query, n = perPage }, ct);

    // NOTE: GetAuthorsByNameAsync (which used _ilike for case-insensitive name matching) has been
    // removed. Hardcover disabled the _ilike operator on their public API in 2026, returning
    // 403 "ilike and related operations are not permitted on this server." for any query that
    // used it. Author name searches now go through SearchAuthorsAsync (the search index) instead.

    public Task<SearchData?> SearchSeriesAsync(string query, int perPage = 10, CancellationToken ct = default) =>
        QueryAsync<SearchData>("""
            query SearchSeries($q: String!, $n: Int!) {
              search(query: $q, query_type: "Series", per_page: $n) { results }
            }
            """, new { q = query, n = perPage }, ct);

    public Task<SearchData?> SearchBooksAsync(string query, int perPage = 10, CancellationToken ct = default) =>
        QueryAsync<SearchData>("""
            query SearchBooks($q: String!, $n: Int!) {
              search(query: $q, query_type: "book", per_page: $n) { results }
            }
            """, new { q = query, n = perPage }, ct);

    // ── Detail fetches ────────────────────────────────────────────────────────

    public Task<AuthorData?> GetAuthorByIdAsync(int id, CancellationToken ct = default) =>
        QueryAsync<AuthorData>("""
            query GetAuthor($id: Int!) {
              authors(where: { id: { _eq: $id } }) {
                id name bio slug alternate_names
                image { url }
              }
            }
            """, new { id }, ct);

    /// <summary>
    /// Case-sensitive exact name lookup via <c>_eq</c>.
    /// Hardcover disabled <c>_ilike</c> and the <c>search(query_type:"Author")</c> endpoint
    /// returns 0 results for all queries, so this is the primary author discovery path.
    /// </summary>
    public Task<AuthorData?> GetAuthorsByNameExactAsync(string name, int limit = 5, CancellationToken ct = default) =>
        QueryAsync<AuthorData>("""
            query GetAuthorsByNameExact($name: String!, $n: Int!) {
              authors(where: { name: { _eq: $name } }, limit: $n) {
                id name bio slug alternate_names
                image { url }
              }
            }
            """, new { name, n = limit }, ct);

    /// <summary>
    /// Case-sensitive exact name lookup via <c>_eq</c>.
    /// The <c>search(query_type:"Series")</c> endpoint returns 0 results for all queries,
    /// so this is the primary series discovery path.
    /// </summary>
    public Task<SeriesData?> GetSeriesByNameExactAsync(string name, int limit = 10, CancellationToken ct = default) =>
        QueryAsync<SeriesData>("""
            query GetSeriesByNameExact($name: String!, $n: Int!) {
              series(where: { name: { _eq: $name } }, limit: $n) {
                id name description slug is_completed
                book_series(order_by: { position: asc }, limit: 1) {
                  book { id title image { url } }
                }
              }
            }
            """, new { name, n = limit }, ct);

    public Task<SeriesData?> GetSeriesByIdAsync(int id, CancellationToken ct = default) =>
        QueryAsync<SeriesData>("""
            query GetSeries($id: Int!) {
              series(where: { id: { _eq: $id } }) {
                id name description slug is_completed
                book_series(order_by: { position: asc }, limit: 1) {
                  book { id title image { url } }
                }
              }
            }
            """, new { id }, ct);

    /// <summary>
    /// Exact title + author lookup via <c>_eq</c> on both fields.
    /// Preferred when the author name is known — prevents false positives on
    /// common titles and gives more targeted results.
    /// </summary>
    public Task<BookDetailData?> GetBooksByTitleAndAuthorAsync(
        string title, string authorName, int limit = 10, CancellationToken ct = default) =>
        QueryAsync<BookDetailData>("""
            query GetBooksByTitleAndAuthor($title: String!, $author: String!, $n: Int!) {
              books(where: {
                title: { _eq: $title },
                contributions: { author: { name: { _eq: $author } } }
              }, limit: $n) {
                id title subtitle description release_year pages rating ratings_count
                cached_tags
                image { url }
                contributions { author { id name } contribution }
                book_series { position series { id name } }
                book_mappings { isbn_13 isbn_10 }
                default_physical_edition {
                  audio_seconds
                  narrations { narrator { name } }
                }
              }
            }
            """, new { title, author = authorName, n = limit }, ct);

    /// <summary>
    /// Case-sensitive exact title lookup via <c>_eq</c>.
    /// Used as a fallback when the author name is unknown or when
    /// <see cref="GetBooksByTitleAndAuthorAsync"/> returns no results
    /// (e.g. author name casing differs between Chronicle and Hardcover).
    /// </summary>
    public Task<BookDetailData?> GetBooksByTitleExactAsync(string title, int limit = 10, CancellationToken ct = default) =>
        QueryAsync<BookDetailData>("""
            query GetBooksByTitleExact($title: String!, $n: Int!) {
              books(where: { title: { _eq: $title } }, limit: $n) {
                id title subtitle description release_year pages rating ratings_count
                cached_tags
                image { url }
                contributions { author { id name } contribution }
                book_series { position series { id name } }
                book_mappings { isbn_13 isbn_10 }
                default_physical_edition {
                  audio_seconds
                  narrations { narrator { name } }
                }
              }
            }
            """, new { title, n = limit }, ct);

    public Task<BookDetailData?> GetBookByIdAsync(int id, CancellationToken ct = default) =>
        QueryAsync<BookDetailData>("""
            query GetBook($id: Int!) {
              books(where: { id: { _eq: $id } }) {
                id title subtitle description release_year pages rating ratings_count
                cached_tags
                image { url }
                contributions { author { id name } contribution }
                book_series { position series { id name } }
                book_mappings { isbn_13 isbn_10 }
                default_physical_edition {
                  audio_seconds
                  narrations { narrator { name } }
                }
              }
            }
            """, new { id }, ct);

    // ── Slug resolution (Fix Match) ───────────────────────────────────────────

    public Task<SlugLookupData<HcIdOnly>?> GetBookBySlugAsync(string slug, CancellationToken ct = default) =>
        QueryAsync<SlugLookupData<HcIdOnly>>("""
            query GetBookBySlug($slug: String!) {
              books(where: { slug: { _eq: $slug } }, limit: 1) { id }
            }
            """, new { slug }, ct);

    /// <summary>
    /// Slug lookup that returns full book detail — avoids the extra round-trip of
    /// <see cref="GetBookBySlugAsync"/> + <see cref="GetBookByIdAsync"/>.
    /// </summary>
    public Task<BookDetailData?> GetBookBySlugFullAsync(string slug, CancellationToken ct = default) =>
        QueryAsync<BookDetailData>("""
            query GetBookBySlugFull($slug: String!) {
              books(where: { slug: { _eq: $slug } }, limit: 1) {
                id title subtitle description release_year pages rating ratings_count
                cached_tags
                image { url }
                contributions { author { id name } contribution }
                book_series { position series { id name } }
                book_mappings { isbn_13 isbn_10 }
                default_physical_edition {
                  audio_seconds
                  narrations { narrator { name } }
                }
              }
            }
            """, new { slug }, ct);

    public Task<SlugLookupData<HcIdOnly>?> GetSeriesBySlugAsync(string slug, CancellationToken ct = default) =>
        QueryAsync<SlugLookupData<HcIdOnly>>("""
            query GetSeriesBySlug($slug: String!) {
              series(where: { slug: { _eq: $slug } }, limit: 1) { id }
            }
            """, new { slug }, ct);

    /// <summary>
    /// Slug lookup that returns full series detail — avoids the extra round-trip of
    /// <see cref="GetSeriesBySlugAsync"/> + <see cref="GetSeriesByIdAsync"/>.
    /// </summary>
    public Task<SeriesData?> GetSeriesBySlugFullAsync(string slug, CancellationToken ct = default) =>
        QueryAsync<SeriesData>("""
            query GetSeriesBySlugFull($slug: String!) {
              series(where: { slug: { _eq: $slug } }, limit: 1) {
                id name description slug is_completed
                book_series(order_by: { position: asc }, limit: 1) {
                  book { id title image { url } }
                }
              }
            }
            """, new { slug }, ct);

    public Task<SlugLookupData<HcIdOnly>?> GetAuthorBySlugAsync(string slug, CancellationToken ct = default) =>
        QueryAsync<SlugLookupData<HcIdOnly>>("""
            query GetAuthorBySlug($slug: String!) {
              authors(where: { slug: { _eq: $slug } }, limit: 1) { id }
            }
            """, new { slug }, ct);

    // ── Binary download ───────────────────────────────────────────────────────

    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    // ── Core request ──────────────────────────────────────────────────────────

    private async Task<T?> QueryAsync<T>(string query, object? variables = null, CancellationToken ct = default)
    {
        var body = new GraphQlRequest(query, variables);

        // Retry delays for transient failures: 429 and 403 (Hardcover uses 403 for both
        // invalid-token and rate-limit scenarios, so we retry with back-off first).
        var retryDelays = new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) };

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await ThrottleAsync(ct);

            _log.Debug("Hardcover request attempt {Attempt}/3 token={Token}", attempt + 1, _tokenPreview);

            using var resp = await _http.PostAsJsonAsync(GraphQlUrl, body, ct);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = resp.Headers.RetryAfter?.Delta ?? retryDelays[Math.Min(attempt, retryDelays.Length - 1)];
                _log.Warning("Hardcover 429 TooManyRequests — waiting {Secs}s before retry (attempt {Attempt}/3)",
                    retryAfter.TotalSeconds, attempt + 1);
                await Task.Delay(retryAfter, ct);
                continue;
            }

            // Hardcover returns 403 for BOTH invalid tokens AND rate limiting.
            // Retry with back-off; if it persists across all attempts it's a bad token.
            if (resp.StatusCode == HttpStatusCode.Forbidden ||
                resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                var responseBody = string.Empty;
                try { responseBody = await resp.Content.ReadAsStringAsync(ct); }
                catch { /* ignore read failure */ }

                _log.Warning(
                    "Hardcover {Status} on attempt {Attempt}/3 — token={Token} — response body: {Body}",
                    (int)resp.StatusCode, attempt + 1, _tokenPreview,
                    string.IsNullOrWhiteSpace(responseBody) ? "(empty)" : responseBody);

                if (attempt < 2)
                {
                    _log.Warning("Backing off {Secs}s before retry", retryDelays[attempt].TotalSeconds);
                    await Task.Delay(retryDelays[attempt], ct);
                    continue;
                }

                throw new InvalidOperationException(
                    $"Hardcover API returned {(int)resp.StatusCode} after 3 attempts — " +
                    $"token prefix: {_tokenPreview} — response: {(string.IsNullOrWhiteSpace(responseBody) ? "(empty)" : responseBody)}. " +
                    "Your API token may be invalid or expired (tokens reset January 1st each year). " +
                    "Go to hardcover.app/account/api, copy the current token, " +
                    "and re-enter it in Settings → Plugins → Hardcover.");
            }

            resp.EnsureSuccessStatusCode();

            var result = await resp.Content.ReadFromJsonAsync<GraphQlResponse<T>>(JsonOpts, ct);

            if (result?.Errors is { Length: > 0 })
                throw new InvalidOperationException(
                    $"Hardcover GraphQL error: {result.Errors[0].Message}");

            _log.Debug("Hardcover request OK on attempt {Attempt}/3", attempt + 1);
            return result!.Data;
        }

        throw new InvalidOperationException("Hardcover API rate limit exceeded after retries.");
    }

    public void Dispose() => _http.Dispose();
}
