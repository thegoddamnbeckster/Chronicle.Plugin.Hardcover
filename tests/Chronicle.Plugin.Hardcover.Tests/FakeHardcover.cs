using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.Hardcover.Tests;

/// <summary>
/// A small Hardcover: books and series held in memory, answering the GraphQL operations the plugin sends by their operation name.
/// The data in the tests is real (recorded from the live API on 2026-10-14), so a case here is a case that failed in the library.
/// </summary>
internal sealed class FakeHardcover : HttpMessageHandler
{
    public sealed record B(int Id, string Title, int? Year, string Slug, string[] Authors, (string Series, double Pos)[]? Series = null, int Ratings = 1, string? Image = "https://img/x.jpg");
    public sealed record S(int Id, string Name, string Slug, string? Author, int Primary, (int BookId, string Title)[]? Books = null);

    public sealed record A(int Id, string Name, string[]? Alternate = null, int? AliasOf = null);
    public List<A> AuthorList { get; } = [];
    /// <summary>When set, the alias query is refused the way a GraphQL validation error is (the field is not exposed).</summary>
    public bool RefuseAliasQuery { get; set; }
    public List<B> Books { get; } = [];
    public List<S> SeriesList { get; } = [];
    public List<string> Operations { get; } = [];

    public int Requests => Operations.Count;

    private static string Cmp(string s) => s.ToLowerInvariant();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content!.ReadAsStringAsync(ct).Result;
        using var doc = JsonDocument.Parse(body);
        var query = doc.RootElement.GetProperty("query").GetString()!;
        var vars = doc.RootElement.TryGetProperty("variables", out var v) ? v : default;
        var op = Regex.Match(query, @"query\s+(\w+)").Groups[1].Value;
        Operations.Add(op);

        string Var(string name) => vars.GetProperty(name).GetString()!;
        int IntVar(string name) => vars.GetProperty(name).GetInt32();

        object data = op switch
        {
            "GetBooksByTitleExact" => new { books = Books.Where(b => b.Title == Var("title")).Take(10).Select(Json) },
            "GetBooksByTitleAndAuthor" => new { books = Books.Where(b => b.Title == Var("title") && b.Authors.Contains(Var("author"))).Take(10).Select(Json) },
            "GetBookBySlug" => new { books = Books.Where(b => b.Slug == Var("slug")).Take(1).Select(b => new { id = b.Id }) },
            "GetBook" => new { books = Books.Where(b => b.Id == IntVar("id")).Select(Json) },
            "GetBooksByTitleRange" => new { books = Books.Where(b => InRange(b.Title, Var("low"), Var("high"))).OrderByDescending(b => b.Ratings).Take(IntVar("n")).Select(Json) },
            "GetBooksByTitleRangeAndAuthor" => new { books = Books.Where(b => InRange(b.Title, Var("low"), Var("high")) && b.Authors.Contains(Var("author"))).OrderByDescending(b => b.Ratings).Take(IntVar("n")).Select(Json) },
            "GetAuthor" => new { authors = AuthorList.Where(a => a.Id == IntVar("id")).Select(a => new { id = a.Id, name = a.Name, bio = (string?)null, slug = (string?)null, alternate_names = a.Alternate, image = (object?)null }) },
            "GetAuthorAliases" when RefuseAliasQuery => throw new InvalidOperationException("Hardcover GraphQL error: field 'alias_id' not found in type: 'authors_bool_exp'"),
            "GetAuthorAliases" => new { authors = AuthorList.Where(a => a.AliasOf == IntVar("id")).Select(a => new { id = a.Id, name = a.Name }) },
            "GetSeriesByNameExact" => new { series = SeriesList.Where(s => s.Name == Var("name")).OrderByDescending(s => s.Primary).Take(10).Select(SeriesJson) },
            "GetSeriesByNameRange" => new { series = SeriesList.Where(s => InRange(s.Name, Var("low"), Var("high"))).OrderByDescending(s => s.Primary).Take(IntVar("n")).Select(SeriesJson) },
            "GetSeriesBySlug" => new { series = SeriesList.Where(s => s.Slug == Var("slug")).Take(1).Select(s => new { id = s.Id }) },
            "GetSeries" => new { series = SeriesList.Where(s => s.Id == IntVar("id")).Select(SeriesJson) },
            // the search endpoint returns nothing today (the plugin only keeps it as a last resort)
            "SearchBooks" or "SearchSeries" or "SearchAuthors" => new { search = new { results = Array.Empty<object>() } },
            _ => throw new InvalidOperationException($"Unexpected Hardcover operation {op}"),
        };

        var json = JsonSerializer.Serialize(new { data });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static bool InRange(string value, string low, string high) =>
        string.Compare(value, low, StringComparison.OrdinalIgnoreCase) >= 0 && string.Compare(value, high, StringComparison.OrdinalIgnoreCase) < 0;

    private static object Json(B b) => new
    {
        id = b.Id, title = b.Title, slug = b.Slug, release_year = b.Year, ratings_count = b.Ratings, rating = 4.0,
        image = b.Image is null ? null : new { url = b.Image },
        contributions = b.Authors.Select((a, i) => new { author = new { id = 1000 + i, name = a }, contribution = (string?)null }),
        book_series = (b.Series ?? []).Select(x => new { position = x.Pos, series = new { id = 1, name = x.Series } }),
    };

    private static object SeriesJson(S s) => new
    {
        id = s.Id, name = s.Name, slug = s.Slug, books_count = s.Primary, primary_books_count = s.Primary,
        author = s.Author is null ? null : new { name = s.Author },
        book_series = (s.Books ?? []).Select((b, i) => new { position = (double)(i + 1), book = new { id = b.BookId, title = b.Title, release_year = (int?)2020, contributions = Array.Empty<object>() } }),
    };

    public HardcoverMetadataProvider Provider()
    {
        var clock = new HardcoverRateLimiterTests.Fake();
        return new HardcoverMetadataProvider(new HardcoverClient("token", this, clock.Limiter));
    }
}
