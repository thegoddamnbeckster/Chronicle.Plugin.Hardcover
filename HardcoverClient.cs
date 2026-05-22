using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chronicle.Plugin.Hardcover;

/// <summary>
/// Thin HTTP wrapper around the Hardcover GraphQL endpoint.
/// Handles Bearer-token authentication and 429 rate-limit back-off.
/// </summary>
internal sealed class HardcoverClient : IDisposable
{
    private const string GraphQlUrl = "https://api.hardcover.app/v1/graphql";

    private readonly HttpClient _http;

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

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        _http.DefaultRequestHeaders.Add(
            "User-Agent", "Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)");
        _http.Timeout = TimeSpan.FromSeconds(30);
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

    /// <summary>
    /// Queries the authors table directly via Hasura using case-insensitive name matching.
    /// More reliable than the search index for author lookups.
    /// Pass a plain name for exact match, or wrap in % for contains: "%B. V. Larson%".
    /// </summary>
    public Task<AuthorData?> GetAuthorsByNameAsync(string name, int limit = 5, CancellationToken ct = default) =>
        QueryAsync<AuthorData>("""
            query GetAuthorsByName($name: String!, $n: Int!) {
              authors(where: { name: { _ilike: $name } }, limit: $n) {
                id name bio slug
                image { url }
              }
            }
            """, new { name, n = limit }, ct);

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
                id name bio slug
                image { url }
              }
            }
            """, new { id }, ct);

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

    public Task<SlugLookupData<HcIdOnly>?> GetSeriesBySlugAsync(string slug, CancellationToken ct = default) =>
        QueryAsync<SlugLookupData<HcIdOnly>>("""
            query GetSeriesBySlug($slug: String!) {
              series(where: { slug: { _eq: $slug } }, limit: 1) { id }
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

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var resp = await _http.PostAsJsonAsync(GraphQlUrl, body, ct);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                await Task.Delay(retryAfter, ct);
                continue;
            }

            resp.EnsureSuccessStatusCode();

            var result = await resp.Content.ReadFromJsonAsync<GraphQlResponse<T>>(JsonOpts, ct);

            if (result?.Errors is { Length: > 0 })
                throw new InvalidOperationException(
                    $"Hardcover GraphQL error: {result.Errors[0].Message}");

            return result!.Data;
        }

        throw new InvalidOperationException("Hardcover API rate limit exceeded after retries.");
    }

    public void Dispose() => _http.Dispose();
}
