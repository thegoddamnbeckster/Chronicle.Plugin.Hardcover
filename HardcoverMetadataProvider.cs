using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Serilog;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.Hardcover;

public sealed class HardcoverMetadataProvider : IMetadataProvider
{
    public string PluginId => "hardcover";
    public string Name     => "Hardcover";
    public string Version  => "1.1.0";
    public string Author   => "Michael Beck";

    private static readonly ILogger _log = Log.ForContext<HardcoverMetadataProvider>();

    private const string KeyApiToken = "api_token";   // shared with HardcoverImportProvider
    private HardcoverClient? _client;

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport
        {
            MediaTypeName   = "books",
            DisplayName     = "Books",
            HierarchyLevels = 3,
            HierarchyLabels = ["Author", "Series", "Book"],
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url",
                               "genres", "cast", "rating", "tags"],
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "audiobooks",
            DisplayName     = "Audiobooks",
            HierarchyLevels = 3,
            HierarchyLabels = ["Author", "Series", "Book"],
            InteractionVerb = "listened",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url",
                               "genres", "cast", "rating", "runtime_minutes", "tags"],
        },
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = KeyApiToken,
                Label       = "Hardcover API Token",
                Description = "Paste the full token from hardcover.app/account/api — the value starting with \"Bearer eyJ…\". Chronicle strips the \"Bearer \" prefix automatically. Hardcover tokens expire on January 1st each year; if you get 403 errors, go get a fresh token.",
                Type        = SettingType.Password,
                Required    = true,
            },
        ],
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue(KeyApiToken, out var token);
        _client?.Dispose();
        _client = string.IsNullOrWhiteSpace(token) ? null : new HardcoverClient(token.Trim());
    }

    // ── SearchAsync ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        var titles = context.AltTitles?.Count > 0
            ? context.AltTitles
            : (IReadOnlyList<string>)[context.Name];

        return context.HierarchyLevel switch
        {
            0 => await SearchAuthorsInternalAsync(context, titles, ct),
            1 => await SearchSeriesInternalAsync(context, titles, ct),
            _ => await SearchBooksInternalAsync(context, titles, ct),
        };
    }

    private async Task<IReadOnlyList<ScoredCandidate>> SearchAuthorsInternalAsync(
        MediaSearchContext ctx, IReadOnlyList<string> titles, CancellationToken ct)
    {
        // NOTE: Hardcover has disabled the _ilike operator on their public GraphQL API
        // ("ilike and related operations are not permitted on this server.").
        // We use SearchAuthorsAsync (the search index) which is not restricted.
        //
        // To maximise match rate we build an expanded set of query variants per title:
        //   1. Original name         "A.K. DuBoff"
        //   2. Dots stripped         "AK DuBoff"   (dots in initials confuse some search indexes)
        //   3. Surname only          "DuBoff"       (fallback when full-name search returns nothing)
        // Scoring still uses the original ctx.Name, so a surname-only hit for "Tchaikovsky"
        // that returns "Adrian Tchaikovsky" will score 60 (exact match after normalization).

        var queries = new List<string>();
        foreach (var title in titles.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            queries.Add(title);

            // Variant 2: strip dots from initials ("A.K." → "AK")
            var noDots = Regex.Replace(title, @"\.(\s|$)", " ").Trim();
            noDots = Regex.Replace(noDots, @"\s{2,}", " ");
            if (!string.Equals(noDots, title, StringComparison.OrdinalIgnoreCase))
                queries.Add(noDots);

            // Variant 3: surname only — last space-separated token, but only if it's
            // at least 4 chars (avoids using a bare initial as the search term)
            var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1 && words[^1].Length >= 4)
                queries.Add(words[^1]);
        }

        // Deduplicate while preserving order (best variant first)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries.Where(q => seen.Add(q)))
        {
            var data = await _client!.SearchAuthorsAsync(query, ct: ct);
            var hits = ParseSearchResults(data?.Search?.Results ?? default);

            _log.Debug(
                "Hardcover author search '{Query}' → {HitCount} raw hit(s) for '{Name}'",
                query, hits.Count, ctx.Name);

            if (hits.Count == 0) continue;

            var candidates = hits
                .Select(h => ScoreAuthorCandidate(ctx, h))
                .Where(c => c.Score > 0)
                .OrderByDescending(c => c.Score)
                .Take(10)
                .ToList();

            _log.Debug(
                "Hardcover author search '{Query}' → {CandidateCount} scored candidate(s) for '{Name}'",
                query, candidates.Count, ctx.Name);

            if (candidates.Any(c => c.Score >= 65)) return candidates;
            if (candidates.Count > 0) return candidates;
        }
        return [];
    }

    private async Task<IReadOnlyList<ScoredCandidate>> SearchSeriesInternalAsync(
        MediaSearchContext ctx, IReadOnlyList<string> titles, CancellationToken ct)
    {
        foreach (var title in titles.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            var query = ctx.ParentName is not null ? $"{title} {ctx.ParentName}" : title;
            var data  = await _client!.SearchSeriesAsync(query, ct: ct);
            var hits  = ParseSearchResults(data?.Search?.Results ?? default);
            if (hits.Count == 0 && ctx.ParentName is not null)
            {
                data = await _client!.SearchSeriesAsync(title, ct: ct);
                hits = ParseSearchResults(data?.Search?.Results ?? default);
            }
            if (hits.Count == 0) continue;

            var candidates = hits
                .Select(h => ScoreSeriesCandidate(ctx, h))
                .Where(c => c.Score > 0)
                .OrderByDescending(c => c.Score)
                .Take(10)
                .ToList();
            if (candidates.Count > 0) return candidates;
        }
        return [];
    }

    private async Task<IReadOnlyList<ScoredCandidate>> SearchBooksInternalAsync(
        MediaSearchContext ctx, IReadOnlyList<string> titles, CancellationToken ct)
    {
        var allCandidates = new List<ScoredCandidate>();

        async Task<bool> TryQuery(string queryTitle, bool useYear)
        {
            var q = ctx.ParentName is not null ? $"{queryTitle} {ctx.ParentName}" : queryTitle;
            var data = await _client!.SearchBooksAsync(q, ct: ct);
            var hits = ParseSearchResults(data?.Search?.Results ?? default);
            if (hits.Count == 0 && ctx.ParentName is not null)
            {
                data = await _client!.SearchBooksAsync(queryTitle, ct: ct);
                hits = ParseSearchResults(data?.Search?.Results ?? default);
            }
            if (hits.Count == 0) return false;

            var scored = hits
                .Select(h => ScoreBookCandidate(ctx, h, useYear))
                .Where(c => c.Score > 0)
                .ToList();
            allCandidates.AddRange(scored);
            return scored.Any(c => c.Score >= 65);
        }

        // Stage 1a: PreciseName + year
        if (ctx.PreciseName is not null && ctx.Year.HasValue)
            if (await TryQuery(ctx.PreciseName, true)) goto done;

        // Stage 1b: AltTitles + year
        if (ctx.Year.HasValue)
            foreach (var t in titles)
                if (!string.IsNullOrWhiteSpace(t) && await TryQuery(t, true)) goto done;

        // Stage 2a: AltTitles, no year
        foreach (var t in titles)
            if (!string.IsNullOrWhiteSpace(t)) await TryQuery(t, false);

        // Stage 2b: FilenameStem
        if (ctx.FilenameStem is not null &&
            !string.Equals(ctx.FilenameStem, ctx.Name, StringComparison.OrdinalIgnoreCase))
            await TryQuery(ctx.FilenameStem, false);

        done:
        return allCandidates
            .GroupBy(c => c.Metadata.ExternalId)
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => GetRatingsCount(c.Metadata))
            .Take(10)
            .ToList();
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();

        // Normalise Hardcover URLs to typed IDs
        if (externalId.StartsWith("https://hardcover.app/", StringComparison.OrdinalIgnoreCase))
            externalId = await ResolveHardcoverUrlAsync(externalId, ct);

        if (externalId.StartsWith("hardcover:author:", StringComparison.OrdinalIgnoreCase))
        {
            var id = int.Parse(externalId["hardcover:author:".Length..]);
            return await FetchAuthorAsync(id, ct);
        }
        if (externalId.StartsWith("hardcover:series:", StringComparison.OrdinalIgnoreCase))
        {
            var id = int.Parse(externalId["hardcover:series:".Length..]);
            return await FetchSeriesAsync(id, ct);
        }
        {
            var id = int.Parse(externalId["hardcover:".Length..]);
            return await FetchBookAsync(id, ct);
        }
    }

    private async Task<string> ResolveHardcoverUrlAsync(string url, CancellationToken ct)
    {
        var uri      = new Uri(url);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2) return url;

        var entityType = segments[0];
        var slug       = segments[1];

        return entityType switch
        {
            "books"   => $"hardcover:{(await _client!.GetBookBySlugAsync(slug, ct))?.Books?.FirstOrDefault()?.Id ?? throw new ArgumentException($"Book slug '{slug}' not found")}",
            "series"  => $"hardcover:series:{(await _client!.GetSeriesBySlugAsync(slug, ct))?.Series?.FirstOrDefault()?.Id ?? throw new ArgumentException($"Series slug '{slug}' not found")}",
            "authors" => $"hardcover:author:{(await _client!.GetAuthorBySlugAsync(slug, ct))?.Authors?.FirstOrDefault()?.Id ?? throw new ArgumentException($"Author slug '{slug}' not found")}",
            _ => url
        };
    }

    private async Task<MediaMetadata> FetchAuthorAsync(int id, CancellationToken ct)
    {
        var data   = await _client!.GetAuthorByIdAsync(id, ct);
        var author = data?.Authors?.FirstOrDefault()
            ?? throw new InvalidOperationException($"Hardcover author {id} not found.");
        return new MediaMetadata
        {
            ExternalId     = $"hardcover:author:{author.Id}",
            Source         = "hardcover",
            Title          = author.Name,
            Overview       = author.Bio,
            PosterUrl      = author.Image?.Url,
            AlternateNames = author.AlternateNames is { Length: > 0 }
                             ? [..author.AlternateNames]
                             : [],
        };
    }

    private async Task<MediaMetadata> FetchSeriesAsync(int id, CancellationToken ct)
    {
        var data   = await _client!.GetSeriesByIdAsync(id, ct);
        var series = data?.Series?.FirstOrDefault()
            ?? throw new InvalidOperationException($"Hardcover series {id} not found.");
        var posterUrl = series.BookSeries?.FirstOrDefault()?.Book?.Image?.Url;
        return new MediaMetadata
        {
            ExternalId   = $"hardcover:series:{series.Id}",
            Source       = "hardcover",
            Title        = series.Name,
            Overview     = series.Description,
            PosterUrl    = posterUrl,
            ExtendedData = JsonSerializer.SerializeToElement(new { is_completed = series.IsCompleted }),
        };
    }

    private async Task<MediaMetadata> FetchBookAsync(int id, CancellationToken ct)
    {
        var data = await _client!.GetBookByIdAsync(id, ct);
        var book = data?.Books?.FirstOrDefault()
            ?? throw new InvalidOperationException($"Hardcover book {id} not found.");

        var genres      = ExtractGenres(book.CachedTags);
        var cast        = BuildCast(book.Contributions, book.DefaultEdition?.Narrations);
        var seriesEntry = book.BookSeries?.FirstOrDefault();
        var isbn13      = book.BookMappings?.FirstOrDefault()?.Isbn13;
        var isbn10      = book.BookMappings?.FirstOrDefault()?.Isbn10;

        var extData = new Dictionary<string, object?>();
        if (book.Pages.HasValue)        extData["pages"]           = book.Pages;
        if (seriesEntry is not null)    extData["series_name"]     = seriesEntry.Series?.Name;
        if (seriesEntry is not null)    extData["series_position"] = seriesEntry.Position;
        if (isbn13 is not null)         extData["isbn13"]          = isbn13;
        if (isbn10 is not null)         extData["isbn10"]          = isbn10;
        if (book.RatingsCount.HasValue) extData["ratings_count"]   = book.RatingsCount;

        return new MediaMetadata
        {
            ExternalId     = $"hardcover:{book.Id}",
            Source         = "hardcover",
            Title          = book.Title,
            Overview       = book.Description,
            Year           = book.ReleaseYear,
            PosterUrl      = book.Image?.Url,
            Rating         = book.Rating,
            Genres         = genres,
            Cast           = cast,
            RuntimeMinutes = book.DefaultEdition?.AudioSeconds.HasValue == true
                             ? (int)Math.Round(book.DefaultEdition.AudioSeconds.Value / 60.0)
                             : null,
            ExtendedData   = JsonSerializer.SerializeToElement(extData),
        };
    }

    // ── GetImageAsync / HealthCheckAsync ──────────────────────────────────────

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        return await _client!.GetBytesAsync(url, ct);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null) return false;
        try
        {
            var me = await _client.GetMeAsync(ct);
            return me?.Me is { Length: > 0 };
        }
        catch { return false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException(
                "HardcoverMetadataProvider has not been configured. Call Configure() first.");
    }

    private static ScoredCandidate ScoreAuthorCandidate(
        MediaSearchContext ctx, Dictionary<string, JsonElement> hit)
    {
        var id    = GetInt(hit, "id");
        var name  = GetStr(hit, "name") ?? string.Empty;
        var photo = GetStr(hit, "image_url") ?? GetStr(hit, "image");
        var meta  = new MediaMetadata
        {
            ExternalId = id > 0 ? $"hardcover:author:{id}" : null,
            Source     = "hardcover",
            Title      = name,
            PosterUrl  = photo,
        };
        if (meta.ExternalId is null) return new ScoredCandidate(meta, 0, "no id");
        var (score, reason) = ScoreTitle(ctx, name);
        return new ScoredCandidate(meta, score, reason);
    }

    /// <summary>Scores an author record returned by the direct Hasura table query.</summary>
    private static ScoredCandidate ScoreAuthorCandidateDirect(
        MediaSearchContext ctx, HcAuthor author)
    {
        if (author.Id <= 0) return new ScoredCandidate(new MediaMetadata { Source = "hardcover" }, 0, "no id");
        var meta = new MediaMetadata
        {
            ExternalId     = $"hardcover:author:{author.Id}",
            Source         = "hardcover",
            Title          = author.Name,
            Overview       = author.Bio,
            PosterUrl      = author.Image?.Url,
            AlternateNames = author.AlternateNames is { Length: > 0 }
                             ? [..author.AlternateNames]
                             : [],
        };
        var (score, reason) = ScoreTitle(ctx, author.Name);
        return new ScoredCandidate(meta, score, reason);
    }

    private static ScoredCandidate ScoreSeriesCandidate(
        MediaSearchContext ctx, Dictionary<string, JsonElement> hit)
    {
        var id   = GetInt(hit, "id");
        var name = GetStr(hit, "name") ?? string.Empty;
        var meta = new MediaMetadata
        {
            ExternalId = id > 0 ? $"hardcover:series:{id}" : null,
            Source     = "hardcover",
            Title      = name,
        };
        if (meta.ExternalId is null) return new ScoredCandidate(meta, 0, "no id");
        var (score, reason) = ScoreTitle(ctx, name);
        return new ScoredCandidate(meta, score, reason);
    }

    private static ScoredCandidate ScoreBookCandidate(
        MediaSearchContext ctx, Dictionary<string, JsonElement> hit, bool useYear)
    {
        var id        = GetInt(hit, "id");
        var title     = GetStr(hit, "title") ?? string.Empty;
        var year      = GetInt(hit, "release_year");
        var imageUrl  = GetStr(hit, "image") ?? GetStr(hit, "cached_image");
        var authorStr = GetStr(hit, "author_names") ?? string.Empty;

        var meta = new MediaMetadata
        {
            ExternalId   = id > 0 ? $"hardcover:{id}" : null,
            Source       = "hardcover",
            Title        = title,
            Year         = year > 0 ? year : null,
            PosterUrl    = imageUrl,
            ExtendedData = JsonSerializer.SerializeToElement(new { ratings_count = GetInt(hit, "ratings_count") }),
        };
        if (meta.ExternalId is null) return new ScoredCandidate(meta, 0, "no id");

        var (score, reasons) = ScoreTitle(ctx, title);
        var reasonList = new List<string> { reasons };

        // Year signals
        if (useYear && ctx.Year.HasValue && meta.Year.HasValue)
        {
            if (ctx.Year == meta.Year)                                    { score += 20; reasonList.Add("year exact"); }
            else if (Math.Abs(ctx.Year.Value - meta.Year.Value) == 1)    { score += 10; reasonList.Add("year ±1"); }
            else                                                          { score -= 10; reasonList.Add("year mismatch"); }
        }

        // PreciseName bonus
        if (ctx.PreciseName is not null)
        {
            if (string.Equals(ctx.PreciseName, title, StringComparison.OrdinalIgnoreCase))
                { score += 15; reasonList.Add("precise exact"); }
            else if (title.Contains(ctx.PreciseName, StringComparison.OrdinalIgnoreCase))
                { score += 5; reasonList.Add("precise partial"); }
        }

        // Author match
        if (ctx.ParentName is not null && !string.IsNullOrEmpty(authorStr))
        {
            var pn = NormalizeStr(ctx.ParentName);
            var an = NormalizeStr(authorStr);
            if (an.Contains(pn, StringComparison.Ordinal))
                { score += 20; reasonList.Add("author exact"); }
            else if (an.Split(' ').Any(w => pn.Contains(w)))
                { score += 10; reasonList.Add("author partial"); }
        }

        return new ScoredCandidate(meta, Math.Max(0, score), string.Join(", ", reasonList));
    }

    private static (int score, string reason) ScoreTitle(MediaSearchContext ctx, string candidateTitle)
    {
        var cn = NormalizeStr(candidateTitle);
        var qn = NormalizeStr(ctx.AltTitles?.FirstOrDefault() ?? ctx.Name);
        if (string.Equals(cn, qn, StringComparison.Ordinal))
            return (60, "title exact");
        if (cn.Contains(qn, StringComparison.Ordinal) || qn.Contains(cn, StringComparison.Ordinal))
            return (30, "title contains");
        return (0, "no title match");
    }

    private static string NormalizeStr(string s) =>
        Regex.Replace(s.Trim(), @"[:\-,\.']", " ").Replace("  ", " ").Trim().ToLowerInvariant();

    private static int GetRatingsCount(MediaMetadata m)
    {
        if (m.ExtendedData is not { } ext) return 0;
        if (ext.TryGetProperty("ratings_count", out var p) && p.ValueKind == JsonValueKind.Number)
            return p.GetInt32();
        return 0;
    }

    private static List<Dictionary<string, JsonElement>> ParseSearchResults(JsonElement results)
    {
        var list = new List<Dictionary<string, JsonElement>>();
        if (results.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var d = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in item.EnumerateObject())
                d[prop.Name] = prop.Value;
            list.Add(d);
        }
        return list;
    }

    private static string? GetStr(Dictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(Dictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static List<string> BuildCast(HcContribution[]? contributions, HcNarration[]? narrations)
    {
        var list = new List<string>();
        foreach (var c in contributions ?? [])
            if (c.Author is not null)
                list.Add($"{(c.Contribution ?? "Author")}:{c.Author.Name}");
        foreach (var n in narrations ?? [])
            if (n.Narrator is not null)
                list.Add($"Narrator:{n.Narrator.Name}");
        return list;
    }

    private static List<string> ExtractGenres(JsonElement? cachedTags)
    {
        var genres = new List<string>();
        if (cachedTags is not { } tags || tags.ValueKind != JsonValueKind.Array) return genres;
        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.TryGetProperty("tag", out var t) && t.ValueKind == JsonValueKind.String)
                genres.Add(t.GetString()!);
            else if (tag.ValueKind == JsonValueKind.String)
                genres.Add(tag.GetString()!);
        }
        return genres;
    }
}
