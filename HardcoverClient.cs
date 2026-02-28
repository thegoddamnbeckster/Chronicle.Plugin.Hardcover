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
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiToken}");
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
                }
                inserted_at
              }
            }
            """, ct: ct);

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
