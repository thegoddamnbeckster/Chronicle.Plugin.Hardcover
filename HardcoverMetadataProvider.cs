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

        // When enriching a known item, route to the correct level.
        // When ParentName is set, the hierarchy context is unambiguous.
        if (context.ParentName is not null)
        {
            return context.HierarchyLevel switch
            {
                0 => await SearchAuthorsInternalAsync(context, titles, ct),
                1 => await SearchSeriesInternalAsync(context, titles, ct),
                _ => await SearchBooksInternalAsync(context, titles, ct),
            };
        }

        // No parent context — this is an open Add Media search. Run all three in
        // parallel and merge. Each is wrapped so a timeout/error in one doesn't
        // discard results from the others.
        static async Task<IReadOnlyList<ScoredCandidate>> Safe(Task<IReadOnlyList<ScoredCandidate>> t)
        {
            try { return await t; }
            catch { return []; }
        }

        var bookTask   = Safe(SearchBooksInternalAsync(context, titles, ct));
        var seriesTask = Safe(SearchSeriesInternalAsync(context, titles, ct));
        var authorTask = Safe(SearchAuthorsInternalAsync(context, titles, ct));
        await Task.WhenAll(bookTask, seriesTask, authorTask);

        return [.. bookTask.Result, .. seriesTask.Result, .. authorTask.Result];
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
        // search(query_type:"Series") returns 0 for all queries — same issue as author
        // and book search endpoints. Use _eq table lookup as the primary strategy.

        var nameVariants = BuildSeriesNameVariants(titles);

        // ── Strategy 1: direct _eq table lookup ──────────────────────────────
        foreach (var name in nameVariants)
        {
            var data   = await _client!.GetSeriesByNameExactAsync(name, ct: ct);
            var series = data?.Series ?? [];

            _log.Information(
                "Hardcover series _eq '{Name}' → {Count} hit(s) for '{Query}'",
                name, series.Length, ctx.Name);

            if (series.Length == 0) continue;

            var candidates = series
                .Select(s => ScoreSeriesCandidateDirect(ctx, s))
                .Where(c => c.Score > 0)
                .OrderByDescending(c => c.Score)
                .Take(10)
                .ToList();

            if (candidates.Count > 0) return candidates;
        }

        // ── Strategy 2: slug-based lookup ────────────────────────────────────
        // Converts names to Hardcover's slug format (lowercase-hyphenated), bypassing
        // case sensitivity and punctuation differences entirely.
        // "Echoes From the Moon" → "echoes-from-the-moon" → exact slug match.
        var slugVariants = BuildSlugVariants(titles.Concat([ctx.Name]).ToList());

        foreach (var slug in slugVariants)
        {
            // Same resolution ResolveHardcoverUrlAsync (Fix Match) uses — see
            // ResolveSeriesSlugAsync's doc comment for why this must not be reimplemented
            // as a one-shot combined query.
            var resolved = await ResolveSeriesSlugAsync(slug, ct);
            var series   = resolved is null ? [] : new[] { resolved };

            _log.Information(
                "Hardcover series slug '{Slug}' → {Result} for '{Name}'",
                slug, resolved is null ? "no match" : $"id={resolved.Id}", ctx.Name);

            if (series.Length == 0) continue;

            var candidates = series
                .Select(s => ScoreSeriesCandidateDirect(ctx, s))
                .Where(c => c.Score > 0)
                .OrderByDescending(c => c.Score)
                .Take(10)
                .ToList();

            if (candidates.Count > 0) return candidates;
        }

        // ── Strategy 3: search-endpoint fallback (returns 0 today, kept for resilience) ──
        _log.Information(
            "Hardcover series slug found nothing for '{Name}' — trying search endpoint fallback",
            ctx.Name);

        foreach (var title in titles.Where(t => IsUsableTitle(t)))
        {
            var clean = StripAudiobookArtifacts(title);
            var data  = await _client!.SearchSeriesAsync(clean, ct: ct);
            var hits  = ParseSearchResults(data?.Search?.Results ?? default);

            _log.Information(
                "Hardcover series search '{Query}' → {Count} hit(s) for '{Name}'",
                clean, hits.Count, ctx.Name);

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

    private static List<string> BuildSeriesNameVariants(IReadOnlyList<string> titles)
    {
        var variants = new List<string>();
        var seen     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? s)
        {
            if (!IsUsableTitle(s)) return;
            var clean = StripAudiobookArtifacts(s!);
            if (!IsUsableTitle(clean)) return;
            if (seen.Add(clean)) variants.Add(clean);

            // Title-cased fallback (handles sources that preserve wrong casing)
            var tc = System.Globalization.CultureInfo.InvariantCulture.TextInfo
                     .ToTitleCase(clean.ToLowerInvariant());
            if (!string.Equals(tc, clean, StringComparison.Ordinal) && seen.Add(tc))
                variants.Add(tc);
        }

        foreach (var title in titles) Add(title);
        return variants;
    }

    /// <summary>
    /// Generates deduplicated Hardcover slugs to try for slug-based lookups.
    /// Slugs bypass case sensitivity and punctuation differences.
    /// </summary>
    private static List<string> BuildSlugVariants(IReadOnlyList<string> names)
    {
        var seen  = new HashSet<string>(StringComparer.Ordinal);
        var slugs = new List<string>();

        foreach (var name in names.Where(n => IsUsableTitle(n)))
        {
            var stripped = StripAudiobookArtifacts(name);
            foreach (var candidate in new[] { name, stripped })
            {
                if (!IsUsableTitle(candidate)) continue;
                var slug = ToHardcoverSlug(candidate);
                if (slug.Length >= 3 && seen.Add(slug))
                    slugs.Add(slug);
            }
        }
        return slugs;
    }

    /// <summary>Scores an <see cref="HcSeries"/> from the direct Hasura table query.</summary>
    private static ScoredCandidate ScoreSeriesCandidateDirect(
        MediaSearchContext ctx, HcSeries series)
    {
        if (series.Id <= 0)
            return new ScoredCandidate(new MediaMetadata { Source = "hardcover" }, 0, "no id");

        var firstBook = series.BookSeries?.FirstOrDefault()?.Book;
        var meta = BuildSeriesMetadata(series);

        var (score, reasons) = ScoreTitle(ctx, series.Name);
        var reasonList = new List<string> { reasons };

        // Author match — use the first book's contributions to identify the series author.
        // A match boosts confidence; a clear mismatch penalises so a same-named series by
        // a different author doesn't tie with the correct one.
        if (ctx.ParentName is not null && firstBook?.Contributions is { Length: > 0 })
        {
            var authorNames = string.Join(" ", firstBook.Contributions
                .Where(c => c.Author is not null)
                .Select(c => c.Author!.Name));

            var pn = NormalizeStr(ctx.ParentName);
            var an = NormalizeStr(authorNames);

            if (an.Contains(pn, StringComparison.Ordinal))
                { score += 20; reasonList.Add("author exact"); }
            else if (an.Split(' ').Any(w => w.Length >= 3 && pn.Contains(w, StringComparison.Ordinal)))
                { score += 10; reasonList.Add("author partial"); }
            else
                // Hard reject: author context present, book's author clearly doesn't match.
                return new ScoredCandidate(meta, 0, "author mismatch — hard reject");
        }

        return new ScoredCandidate(meta, Math.Max(0, score), string.Join(", ", reasonList));
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
        // When the author is known, include it in the filter — this prevents false
        // positives on common titles and makes the match authoritative.
        // Fall through to title-only if the author name doesn't match exactly
        // (e.g. casing differences between Chronicle's stored name and Hardcover's).
        var authorName = GetBookAuthorName(ctx);
        foreach (var title in titleVariants)
        {
            BookDetailData? data;

            if (authorName is not null)
            {
                data = await _client!.GetBooksByTitleAndAuthorAsync(title, authorName, ct: ct);
                _log.Information(
                    "Hardcover book _eq title='{Title}' author='{Author}' → {Count} hit(s) for '{Name}'",
                    title, authorName, data?.Books?.Length ?? 0, ctx.Name);

                // Author name didn't match exactly — retry on title alone
                if (!(data?.Books?.Length > 0))
                {
                    data = await _client!.GetBooksByTitleExactAsync(title, ct: ct);
                    _log.Information(
                        "Hardcover book _eq title='{Title}' (author-fallback) → {Count} hit(s) for '{Name}'",
                        title, data?.Books?.Length ?? 0, ctx.Name);
                }
            }
            else
            {
                data = await _client!.GetBooksByTitleExactAsync(title, ct: ct);
                _log.Information(
                    "Hardcover book _eq title='{Title}' → {Count} hit(s) for '{Name}'",
                    title, data?.Books?.Length ?? 0, ctx.Name);
            }

            var books = data?.Books ?? [];
            if (books.Length == 0) continue;

            var scored = books
                .Select(b => ScoreBookCandidateDirect(ctx, b, useYear))
                .Where(c => c.Score > 0)
                .ToList();

            Merge(scored);

            // NOTE: deliberately no early-exit here even once a candidate clears the
            // acceptance threshold. Hardcover's title index can return a different (often
            // sparser, orphaned/duplicate) book row than the one its slug resolves to for
            // the exact same title — short-circuiting here meant Strategy 2 (slug) never
            // ran once Strategy 1 found anything "good enough", so a stale duplicate with
            // no cover art could win purely because it was the first result, without the
            // better-populated candidate ever entering the pool to compete on the
            // data-completeness tiebreaker below.
        }

        // ── Strategy 2: slug-based lookup ────────────────────────────────────
        // Bypasses case sensitivity and punctuation. "Project Hail Mary (Unabridged)"
        // strips to "Project Hail Mary" → slug "project-hail-mary" → exact match.
        // Always attempted (not just when Strategy 1 found nothing) — see note above.
        // Every candidate still goes through the same ScoreBookCandidateDirect scoring
        // and author hard-reject, so widening the pool here can only ever help pick a
        // better-populated match for the same title/author, never accept a wrong book.
        {
            var allTitles  = new List<string> { ctx.Name };
            if (ctx.PreciseName is not null) allTitles.Insert(0, ctx.PreciseName);
            allTitles.AddRange(titles);
            var slugVariants = BuildSlugVariants(allTitles);

            foreach (var slug in slugVariants)
            {
                // Same resolution ResolveHardcoverUrlAsync (Fix Match) uses — see
                // ResolveBookSlugAsync's doc comment for why this must not be reimplemented
                // as a one-shot combined query.
                var book = await ResolveBookSlugAsync(slug, ct);

                _log.Information(
                    "Hardcover book slug '{Slug}' → {Result} for '{Name}'",
                    slug, book is null ? "no match" : $"id={book.Id}", ctx.Name);

                if (book is null) continue;

                var scored = ScoreBookCandidateDirect(ctx, book, useYear);
                if (scored.Score > 0) Merge([scored]);
            }
        }

        // ── Strategy 3: search-endpoint fallback (returns 0 today, kept for resilience) ──
        if (allCandidates.Count == 0)
        {
            var primaryTitle = ctx.PreciseName ?? titles.FirstOrDefault() ?? ctx.Name;
            var q            = authorName is not null
                               ? $"{primaryTitle} {authorName}"
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

        return allCandidates
            .GroupBy(c => c.Metadata.ExternalId)
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => GetRatingsCount(c.Metadata))
            .Take(10)
            .ToList();
    }

    /// <summary>
    /// The author name to use when matching a BOOK. Chronicle's audiobook hierarchy is
    /// Author(0) -> Series(1) -> Book(2) when a series exists, or Author(0) -> Book(1) when
    /// it doesn't. `ctx.ParentName` is always the book's IMMEDIATE parent — for a book under
    /// a series that's the series name, not the author; the author is `ctx.GrandparentName`
    /// in that case. Using `ctx.ParentName` unconditionally as "the author" was a real,
    /// confirmed bug: it compared a book's real author against its series name, hard-rejecting
    /// correct candidates that had real author data while sparse duplicates with no
    /// contributor data at all skipped the check and won by default.
    /// </summary>
    private static string? GetBookAuthorName(MediaSearchContext ctx) =>
        ctx.HierarchyLevel == 2 ? ctx.GrandparentName : ctx.ParentName;

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
            if (!IsUsableTitle(s)) return;
            var clean = StripAudiobookArtifacts(s!);
            if (!IsUsableTitle(clean)) return;
            if (seen.Add(clean)) variants.Add(clean);
            // Title-cased fallback for sources that store lowercase titles
            var tc = System.Globalization.CultureInfo.InvariantCulture.TextInfo
                     .ToTitleCase(clean.ToLowerInvariant());
            if (!string.Equals(tc, clean, StringComparison.Ordinal) && seen.Add(tc))
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

        // The book record itself often has no image — Hardcover attaches cover art to
        // editions, and the website assembles a displayed cover from one of those rather
        // than the bare book entity. Fall back to the default physical edition's image
        // when the book-level one is absent.
        var posterUrl = book.Image?.Url ?? book.DefaultEdition?.Image?.Url;

        var meta = new MediaMetadata
        {
            ExternalId   = $"hardcover:{book.Id}",
            Source       = "hardcover",
            Title        = book.Title,
            Year         = book.ReleaseYear,
            PosterUrl    = posterUrl,
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

        // Author match against parent context. For a book under a series (3-level
        // Author -> Series -> Book hierarchy, HierarchyLevel 2), ctx.ParentName is the
        // SERIES, not the author — the author is ctx.GrandparentName. Using the wrong
        // field here was a real, confirmed bug: it made the check compare the book's real
        // author (e.g. "Dan Simmons") against the series name (e.g. "Hyperion Cantos"),
        // hard-rejecting the correct, well-populated candidate — while a sparse duplicate
        // with NO contributor data at all skipped this check entirely (only runs when
        // authorNames is non-empty) and won by default, purely because it had less data.
        var bookAuthorName = GetBookAuthorName(ctx);
        if (bookAuthorName is not null)
        {
            var authorNames = string.Join(" ",
                (book.Contributions ?? [])
                    .Where(c => c.Author is not null)
                    .Select(c => c.Author!.Name));

            if (!string.IsNullOrEmpty(authorNames))
            {
                var pn = NormalizeStr(bookAuthorName);
                var an = NormalizeStr(authorNames);
                if (an.Contains(pn, StringComparison.Ordinal))
                    { score += 20; reasonList.Add("author exact"); }
                else if (an.Split(' ').Any(w => w.Length >= 3 && pn.Contains(w, StringComparison.Ordinal)))
                    { score += 10; reasonList.Add("author partial"); }
                else
                    // Hard reject: we have the author in context AND the book's author clearly
                    // doesn't match. No title or year signal can overcome a wrong author.
                    return new ScoredCandidate(meta, 0, "author mismatch — hard reject");
            }
        }

        // Data-completeness tiebreaker. Hardcover can have multiple book rows for what is
        // really the same title/edition (duplicate community entries, sparse stubs) — without
        // this, a well-curated candidate with cover art can lose a tie to an empty duplicate
        // purely on result order. Kept small relative to title/year/author signals (max ~60+)
        // so it only ever breaks near-ties, never overrides a genuinely better match.
        if (!string.IsNullOrEmpty(posterUrl))
            { score += 5; reasonList.Add("has cover art"); }
        if ((book.RatingsCount ?? 0) > 0)
            { score += 2; reasonList.Add("has ratings"); }

        return new ScoredCandidate(meta, Math.Max(0, score), string.Join(", ", reasonList));
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();

        // Normalise Hardcover URLs to typed IDs.
        // Accept both https:// and http:// so that pasting from a browser (which may
        // redirect http → https) still works.
        if (externalId.StartsWith("https://hardcover.app/", StringComparison.OrdinalIgnoreCase) ||
            externalId.StartsWith("http://hardcover.app/",  StringComparison.OrdinalIgnoreCase))
        {
            // Book and series URLs resolve straight to full metadata via the same
            // ResolveBookSlugAsync/ResolveSeriesSlugAsync automatic search uses — not routed
            // back through the generic id-string dispatch below, so there's exactly one place
            // each kind of slug ever gets resolved. (Authors have no automatic-search slug
            // strategy to drift against, so that case stays on the generic string dispatch.)
            var bookSlug = TryExtractSlug(externalId, "books");
            if (bookSlug is not null)
            {
                var book = await ResolveBookSlugAsync(bookSlug, ct)
                    ?? throw new ArgumentException($"Book slug '{bookSlug}' not found");
                return BuildBookMetadata(book);
            }

            var seriesSlug = TryExtractSlug(externalId, "series");
            if (seriesSlug is not null)
            {
                var series = await ResolveSeriesSlugAsync(seriesSlug, ct)
                    ?? throw new ArgumentException($"Series slug '{seriesSlug}' not found");
                return BuildSeriesMetadata(series);
            }

            externalId = await ResolveHardcoverUrlAsync(externalId, ct);
        }

        if (externalId.StartsWith("hardcover:author:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = externalId["hardcover:author:".Length..];
            if (!int.TryParse(raw, out var id))
                throw new ArgumentException($"Invalid Hardcover author ID '{raw}' in: {externalId}");
            return await FetchAuthorAsync(id, ct);
        }
        else if (externalId.StartsWith("hardcover:series:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = externalId["hardcover:series:".Length..];
            if (!int.TryParse(raw, out var id))
                throw new ArgumentException($"Invalid Hardcover series ID '{raw}' in: {externalId}");
            return await FetchSeriesAsync(id, ct);
        }
        else if (externalId.StartsWith("hardcover:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = externalId["hardcover:".Length..];
            if (!int.TryParse(raw, out var id))
                throw new ArgumentException($"Invalid Hardcover book ID '{raw}' in: {externalId}");
            return await FetchBookAsync(id, ct);
        }
        else
        {
            throw new ArgumentException(
                $"Invalid Hardcover external ID: '{externalId}'. " +
                "Expected hardcover:{{id}}, hardcover:series:{{id}}, hardcover:author:{{id}}, " +
                "or a hardcover.app URL.");
        }
    }

    /// <summary>Returns the slug for a /{entityType}/{slug} Hardcover URL, or null for any other shape.</summary>
    private static string? TryExtractSlug(string url, string entityType)
    {
        var segments = new Uri(url).AbsolutePath.Trim('/').Split('/');
        return segments.Length >= 2 && segments[0] == entityType ? segments[1] : null;
    }

    /// <summary>Resolves a /authors/{slug} Hardcover URL to a typed ID string.
    /// Book and series URLs are handled separately in GetByIdAsync via
    /// ResolveBookSlugAsync/ResolveSeriesSlugAsync — see there.</summary>
    private async Task<string> ResolveHardcoverUrlAsync(string url, CancellationToken ct)
    {
        var uri      = new Uri(url);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2 || string.IsNullOrWhiteSpace(segments[0]))
            throw new ArgumentException(
                $"Cannot extract content type from Hardcover URL: '{url}'. " +
                "Expected /books/{{slug}}, /series/{{slug}}, or /authors/{{slug}}.");

        var entityType = segments[0];
        var slug       = segments[1];

        return entityType switch
        {
            "authors" => $"hardcover:author:{(await _client!.GetAuthorBySlugAsync(slug, ct))?.Authors?.FirstOrDefault()?.Id ?? throw new ArgumentException($"Author slug '{slug}' not found")}",
            _ => throw new ArgumentException(
                $"Unrecognised Hardcover URL path '{entityType}' in: {url}. " +
                "Expected /books/{{slug}}, /series/{{slug}}, or /authors/{{slug}}.")
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
        return BuildSeriesMetadata(series);
    }

    /// <summary>
    /// Resolves a series slug (e.g. the "hyperion-cantos" in hardcover.app/series/hyperion-cantos)
    /// to full series detail via the thin id-only query, then a fetch by id. See
    /// ResolveBookSlugAsync's doc comment — same reasoning, same bug class, applies here too.
    /// Shared by Fix Match's pasted-URL handling and automatic search's slug strategy.
    /// </summary>
    private async Task<HcSeries?> ResolveSeriesSlugAsync(string slug, CancellationToken ct)
    {
        var idData = await _client!.GetSeriesBySlugAsync(slug, ct);
        var id = idData?.Series?.FirstOrDefault()?.Id;
        if (id is null or <= 0) return null;
        var data = await _client!.GetSeriesByIdAsync(id.Value, ct);
        return data?.Series?.FirstOrDefault();
    }

    /// <summary>The one canonical HcSeries -> MediaMetadata conversion — see BuildBookMetadata.</summary>
    private static MediaMetadata BuildSeriesMetadata(HcSeries series)
    {
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
        return BuildBookMetadata(book);
    }

    /// <summary>
    /// Resolves a book slug (e.g. the "endymion" in hardcover.app/books/endymion) to full
    /// book detail via the thin id-only query, then a fetch by id — deliberately NOT a single
    /// combined slug+fields query. Both filter on the same slug, but they can return DIFFERENT
    /// book rows for the identical slug (confirmed: Hardcover had reassigned/merged a slug and
    /// the combined query kept returning the old orphaned duplicate while this two-step
    /// resolution, matching what hardcover.app's own routing does, found the current one).
    /// Shared by Fix Match's pasted-URL handling and automatic search's slug strategy so the
    /// two can never independently drift back into different resolution logic.
    /// </summary>
    private async Task<HcBookDetail?> ResolveBookSlugAsync(string slug, CancellationToken ct)
    {
        var idData = await _client!.GetBookBySlugAsync(slug, ct);
        var id = idData?.Books?.FirstOrDefault()?.Id;
        if (id is null or <= 0) return null;
        var data = await _client!.GetBookByIdAsync(id.Value, ct);
        return data?.Books?.FirstOrDefault();
    }

    /// <summary>
    /// Converts a Hardcover book detail into Chronicle's generic metadata shape. The one
    /// canonical conversion — used by every path that ends up with an HcBookDetail (id fetch,
    /// slug resolution, Fix Match) — so poster/field mapping can't drift between them.
    /// </summary>
    private static MediaMetadata BuildBookMetadata(HcBookDetail book)
    {
        var genres      = ExtractGenres(book.CachedTags);
        var cast        = BuildCast(book.Contributions, book.DefaultEdition?.Narrations);
        var seriesEntry = book.BookSeries?.FirstOrDefault();
        var isbn13      = book.BookMappings?.FirstOrDefault()?.Isbn13;
        var isbn10      = book.BookMappings?.FirstOrDefault()?.Isbn10;
        // The book record itself often has no image — Hardcover attaches cover art to
        // editions, and the website assembles a displayed cover from one of those rather
        // than the bare book entity. Fall back to the default physical edition's image.
        var posterUrl   = book.Image?.Url ?? book.DefaultEdition?.Image?.Url;

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
            PosterUrl      = posterUrl,
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
            throw new Chronicle.Plugins.PluginAuthException(
                "chronicle.plugin.hardcover",
                "Hardcover plugin is not configured — set an API token in Settings → Plugins → Hardcover.");
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

        // Author match — see GetBookAuthorName's doc comment for why this isn't ctx.ParentName.
        var bookAuthorName = GetBookAuthorName(ctx);
        if (bookAuthorName is not null && !string.IsNullOrEmpty(authorStr))
        {
            var pn = NormalizeStr(bookAuthorName);
            var an = NormalizeStr(authorStr);
            if (an.Contains(pn, StringComparison.Ordinal))
                { score += 20; reasonList.Add("author exact"); }
            else if (an.Split(' ').Any(w => pn.Contains(w)))
                { score += 10; reasonList.Add("author partial"); }
        }

        // Data-completeness tiebreaker — see ScoreBookCandidateDirect for rationale.
        if (!string.IsNullOrEmpty(imageUrl))
            { score += 5; reasonList.Add("has cover art"); }
        if (GetInt(hit, "ratings_count") > 0)
            { score += 2; reasonList.Add("has ratings"); }

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

    /// <summary>
    /// Converts a name to the slug format Hardcover uses in its URLs:
    /// lowercase, non-alphanumeric runs replaced with a single hyphen, leading/trailing hyphens stripped.
    /// "Echoes From the Moon" → "echoes-from-the-moon"
    /// "A.K. DuBoff"         → "ak-duboff"
    /// </summary>
    private static string ToHardcoverSlug(string name)
    {
        var s = name.ToLowerInvariant();
        s = Regex.Replace(s, @"[^a-z0-9]+", "-");
        return s.Trim('-');
    }

    /// <summary>
    /// Removes audiobook-specific suffix tags that the file scanner appends to names
    /// but that Hardcover does not include in series/book titles.
    /// "Odds On (Unabridged)"   → "Odds On"
    /// "Project Hail Mary (Unabridged)" → "Project Hail Mary"
    /// </summary>
    private static string StripAudiobookArtifacts(string name)
    {
        // Remove trailing format qualifiers
        var s = Regex.Replace(name, @"\s*\((?:Un)?abridged\)\s*$", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*\(Book\s+[\d.]+\)\s*$", string.Empty, RegexOptions.IgnoreCase);
        return s.Trim();
    }

    /// <summary>
    /// Returns true if <paramref name="title"/> looks like a clean, usable search term —
    /// filters out file-scanner artifact strings such as
    /// "- - (2024) - Echoes from the Moon - The Token, Book 1".
    /// </summary>
    private static bool IsUsableTitle(string? title, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (title.Length > maxLength)         return false;
        // Starts with separator characters — file-path artefact
        if (Regex.IsMatch(title, @"^[\-–—\s]+")) return false;
        return true;
    }

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
