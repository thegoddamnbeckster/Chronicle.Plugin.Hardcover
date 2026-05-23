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
        // Hardcover author search situation (as of 2026):
        //   • _ilike is banned: "ilike and related operations are not permitted on this server."
        //   • search(query_type:"Author") returns 0 results for all queries (index broken).
        //   • _eq works — confirmed by GetAuthorByIdAsync and slug lookups.
        //
        // Strategy 1: Direct table query using _eq (case-sensitive exact match).
        //   Build name variants to improve coverage:
        //     "A.K. DuBoff" → ["A.K. DuBoff", "AK DuBoff"]
        //     "andy weir"   → ["andy weir", "Andy Weir"]   (title-case variant)
        //
        // Strategy 2: Book-based discovery.
        //   Search for books using the author name; if a book hit's author_names field
        //   contains our target, fetch that book's full contribution list to retrieve
        //   the author's numeric ID. No separate author-lookup API call is needed
        //   because FetchAuthorAsync is called later at enrichment time.

        var nameVariants = BuildAuthorNameVariants(titles);

        // ── Strategy 1: direct _eq table lookup ──────────────────────────────
        foreach (var name in nameVariants)
        {
            var data    = await _client!.GetAuthorsByNameExactAsync(name, ct: ct);
            var authors = data?.Authors ?? [];

            _log.Information(
                "Hardcover author _eq '{Name}' → {Count} hit(s) for '{Query}'",
                name, authors.Length, ctx.Name);

            if (authors.Length == 0) continue;

            var candidates = authors
                .Select(a => ScoreAuthorCandidateDirect(ctx, a))
                .Where(c => c.Score > 0)
                .OrderByDescending(c => c.Score)
                .Take(10)
                .ToList();

            _log.Information(
                "Hardcover author _eq '{Name}' → {CandidateCount} scored candidate(s) for '{Query}'",
                name, candidates.Count, ctx.Name);

            if (candidates.Count > 0) return candidates;
        }

        // ── Strategy 2: book-based author discovery ───────────────────────────
        _log.Information(
            "Hardcover author _eq found nothing for '{Name}' — trying book-based discovery",
            ctx.Name);
        return await DiscoverAuthorViaBooksAsync(ctx, titles, ct);
    }

    /// <summary>
    /// Builds a deduplicated list of name variants to try for an exact-match author search.
    /// </summary>
    private static List<string> BuildAuthorNameVariants(IReadOnlyList<string> titles)
    {
        var variants = new List<string>();
        var seen     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var title in titles.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (seen.Add(title)) variants.Add(title);

            // Dots stripped: "A.K. DuBoff" → "AK DuBoff"
            var noDots = Regex.Replace(title, @"\.(\s|$)", " ").Trim();
            noDots     = Regex.Replace(noDots, @"\s{2,}", " ");
            if (!string.Equals(noDots, title, StringComparison.OrdinalIgnoreCase) && seen.Add(noDots))
                variants.Add(noDots);

            // Title-case: "andy weir" → "Andy Weir" (handles sources that lowercase names)
            var titleCase = System.Globalization.CultureInfo.InvariantCulture.TextInfo
                .ToTitleCase(title.ToLowerInvariant());
            if (!string.Equals(titleCase, title, StringComparison.Ordinal) && seen.Add(titleCase))
                variants.Add(titleCase);
        }

        return variants;
    }

    /// <summary>
    /// Searches for books by the author name, then extracts the author's ID from
    /// the book's contribution list.  Used when the direct <c>_eq</c> name lookup
    /// finds nothing (e.g. Hardcover stores the author under a slightly different
    /// spelling than the file scanner recorded).
    /// </summary>
    private async Task<IReadOnlyList<ScoredCandidate>> DiscoverAuthorViaBooksAsync(
        MediaSearchContext ctx, IReadOnlyList<string> titles, CancellationToken ct)
    {
        var primaryName = titles.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? ctx.Name;
        var qn          = NormalizeStr(ctx.Name);

        var bookData = await _client!.SearchBooksAsync(primaryName, perPage: 5, ct: ct);
        var hits     = ParseSearchResults(bookData?.Search?.Results ?? default);

        _log.Information(
            "Hardcover book-based author discovery: '{Query}' → {Count} book hit(s)",
            primaryName, hits.Count);

        if (hits.Count == 0) return [];

        foreach (var hit in hits)
        {
            // Skip books whose author_names don't contain our target
            var authorNames = GetStr(hit, "author_names") ?? string.Empty;
            if (!NormalizeStr(authorNames).Contains(qn, StringComparison.Ordinal)) continue;

            var bookId = GetInt(hit, "id");
            if (bookId <= 0) continue;

            // Fetch book detail to get individual author IDs from contributions
            var detail = await _client!.GetBookByIdAsync(bookId, ct);
            var book   = detail?.Books?.FirstOrDefault();
            if (book is null) continue;

            foreach (var contrib in book.Contributions ?? [])
            {
                if (contrib.Author is null) continue;
                var an = NormalizeStr(contrib.Author.Name);
                if (!an.Contains(qn, StringComparison.Ordinal) &&
                    !qn.Contains(an, StringComparison.Ordinal)) continue;

                _log.Information(
                    "Hardcover book-based author discovery: found '{AuthorName}' (id={Id}) via book {BookId}",
                    contrib.Author.Name, contrib.Author.Id, bookId);

                var meta = new MediaMetadata
                {
                    ExternalId = $"hardcover:author:{contrib.Author.Id}",
                    Source     = "hardcover",
                    Title      = contrib.Author.Name,
                };
                var (score, reason) = ScoreTitle(ctx, contrib.Author.Name);
                if (score > 0)
                    return [new ScoredCandidate(meta, score, $"{reason}, via-book")];
            }
        }

        _log.Information(
            "Hardcover book-based author discovery: no match found for '{Name}'",
            ctx.Name);
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
        // Hardcover's search(query_type:"book") endpoint returns 0 results for all queries
        // (confirmed: even "The Martian", "Ilium", etc. return 0 hits).
        // Use direct _eq table lookup as the primary strategy; the search endpoint is kept
        // as a fallback in case Hardcover restores it on their end.
        //
        // _eq is case-sensitive, so we try multiple title variants per query.
        // Results carry full book detail (year, contributions, series) so scoring is rich.

        var allCandidates = new List<ScoredCandidate>();
        var seenIds       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Merge(IEnumerable<ScoredCandidate> incoming)
        {
            foreach (var c in incoming)
                if (c.Metadata.ExternalId is not null && seenIds.Add(c.Metadata.ExternalId))
                    allCandidates.Add(c);
        }

        var titleVariants = BuildBookTitleVariants(ctx, titles);
        var useYear       = ctx.Year.HasValue;

        // ── Strategy 1: direct _eq table lookup ──────────────────────────────
        foreach (var title in titleVariants)
        {
            var data  = await _client!.GetBooksByTitleExactAsync(title, ct: ct);
            var books = data?.Books ?? [];

            _log.Information(
                "Hardcover book _eq '{Title}' → {Count} hit(s) for '{Name}'",
                title, books.Length, ctx.Name);

            if (books.Length == 0) continue;

            var scored = books
                .Select(b => ScoreBookCandidateDirect(ctx, b, useYear))
                .Where(c => c.Score > 0)
                .ToList();

            Merge(scored);

            if (allCandidates.Any(c => c.Score >= 65)) goto done;
        }

        // ── Strategy 2: search-endpoint fallback (returns 0 today, kept for resilience) ──
        if (allCandidates.Count == 0)
        {
            var primaryTitle = ctx.PreciseName ?? titles.FirstOrDefault() ?? ctx.Name;
            var q            = ctx.ParentName is not null
                               ? $"{primaryTitle} {ctx.ParentName}"
                               : primaryTitle;

            _log.Information(
                "Hardcover book _eq found nothing for '{Name}' — trying search endpoint fallback '{Query}'",
                ctx.Name, q);

            var searchData = await _client!.SearchBooksAsync(q, ct: ct);
            var hits       = ParseSearchResults(searchData?.Search?.Results ?? default);

            _log.Information(
                "Hardcover book search '{Query}' → {Count} hit(s) for '{Name}'",
                q, hits.Count, ctx.Name);

            if (hits.Count > 0)
                Merge(hits.Select(h => ScoreBookCandidate(ctx, h, useYear)).Where(c => c.Score > 0));
        }

        done:
        return allCandidates
            .GroupBy(c => c.Metadata.ExternalId)
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => GetRatingsCount(c.Metadata))
            .Take(10)
            .ToList();
    }

    /// <summary>
    /// Builds a deduplicated ordered list of title strings to try for exact-match book lookup.
    /// PreciseName (short title) goes first; AltTitles follow; FilenameStem is appended last.
    /// A title-cased variant is added for each entry to handle lowercase source data.
    /// </summary>
    private static List<string> BuildBookTitleVariants(MediaSearchContext ctx, IReadOnlyList<string> titles)
    {
        var variants = new List<string>();
        var seen     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            if (seen.Add(s)) variants.Add(s);
            // Title-cased fallback for sources that store lowercase titles
            var tc = System.Globalization.CultureInfo.InvariantCulture.TextInfo
                     .ToTitleCase(s.ToLowerInvariant());
            if (!string.Equals(tc, s, StringComparison.Ordinal) && seen.Add(tc))
                variants.Add(tc);
        }

        Add(ctx.PreciseName);
        foreach (var t in titles) Add(t);
        if (!string.Equals(ctx.FilenameStem, ctx.Name, StringComparison.OrdinalIgnoreCase))
            Add(ctx.FilenameStem);

        return variants;
    }

    /// <summary>
    /// Scores an <see cref="HcBookDetail"/> returned by the direct Hasura table query.
    /// Mirrors the logic of <see cref="ScoreBookCandidate"/> but works with strongly-typed
    /// objects rather than the raw JSON dictionaries from the search endpoint.
    /// </summary>
    private static ScoredCandidate ScoreBookCandidateDirect(
        MediaSearchContext ctx, HcBookDetail book, bool useYear)
    {
        if (book.Id <= 0)
            return new ScoredCandidate(new MediaMetadata { Source = "hardcover" }, 0, "no id");

        var meta = new MediaMetadata
        {
            ExternalId   = $"hardcover:{book.Id}",
            Source       = "hardcover",
            Title        = book.Title,
            Year         = book.ReleaseYear,
            PosterUrl    = book.Image?.Url,
            Rating       = book.Rating,
            ExtendedData = JsonSerializer.SerializeToElement(
                               new { ratings_count = book.RatingsCount ?? 0 }),
        };

        var (score, reasons) = ScoreTitle(ctx, book.Title);
        var reasonList = new List<string> { reasons };

        // Year signals
        if (useYear && ctx.Year.HasValue && meta.Year.HasValue)
        {
            if (ctx.Year == meta.Year)
                { score += 20; reasonList.Add("year exact"); }
            else if (Math.Abs(ctx.Year.Value - meta.Year.Value) == 1)
                { score += 10; reasonList.Add("year ±1"); }
            else
                { score -= 10; reasonList.Add("year mismatch"); }
        }

        // PreciseName bonus
        if (ctx.PreciseName is not null)
        {
            if (string.Equals(ctx.PreciseName, book.Title, StringComparison.OrdinalIgnoreCase))
                { score += 15; reasonList.Add("precise exact"); }
            else if (book.Title.Contains(ctx.PreciseName, StringComparison.OrdinalIgnoreCase))
                { score += 5; reasonList.Add("precise partial"); }
        }

        // Author match against parent context
        if (ctx.ParentName is not null)
        {
            var authorNames = string.Join(" ",
                (book.Contributions ?? [])
                    .Where(c => c.Author is not null)
                    .Select(c => c.Author!.Name));

            if (!string.IsNullOrEmpty(authorNames))
            {
                var pn = NormalizeStr(ctx.ParentName);
                var an = NormalizeStr(authorNames);
                if (an.Contains(pn, StringComparison.Ordinal))
                    { score += 20; reasonList.Add("author exact"); }
                else if (an.Split(' ').Any(w => w.Length >= 3 && pn.Contains(w)))
                    { score += 10; reasonList.Add("author partial"); }
            }
        }

        return new ScoredCandidate(meta, Math.Max(0, score), string.Join(", ", reasonList));
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
