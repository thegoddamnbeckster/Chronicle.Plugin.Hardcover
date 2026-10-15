using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.Hardcover;

/// <summary>
/// The text rules behind Hardcover matching, kept apart from the network code so they can be tested on their own. Every rule here
/// comes from a case measured against the live service (2026-10-14, B.V. Larson, Barry J. Hutchison and the shared-universe
/// series: Four Horsemen, Birthright, Order of the Centurion):
///
///  * a library name often carries a "(2023)" or a file-scan prefix ("- - (2014) - Dust World"), which no title lookup can match;
///  * Hardcover titles often carry a subtitle or the series ("Dial D for Deadman: A Space Team Universe Sci Fi Comedy (Dan Deadman
///    Space Detective, Book 1)", "Mind Hack: A Cyberpunk Saga") that the library leaves out, so an exact title never matches;
///  * Hardcover slugs get a year, or the author, when the plain slug is taken ("blood-world-2017", "birthright-rick-partlow");
///  * a shared universe credits one author in the library and the real writers in Hardcover, so the author cannot be required
///    when the series and the title agree.
/// </summary>
public static class Matching
{
    // ── folding ───────────────────────────────────────────────────────────────

    /// <summary>Lower case, accents and typographic punctuation folded to plain ASCII-ish text.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(c switch
            {
                '‘' or '’' or '‛' or '′' or 'ʼ' or '´' => '\'',
                '“' or '”' or '„' or '″' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => '-',
                ' ' or ' ' or ' ' or ' ' => ' ',
                _ => c,
            });
        var decomposed = sb.ToString().Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plain.Append(c);
        return plain.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>The words of a text: folded, everything that is not a letter or digit a space, spaces collapsed. "B.V. Larson" and "B. V. Larson" are both "b v larson".</summary>
    public static string Words(string? text)
    {
        var sb = new StringBuilder();
        foreach (var c in Fold(text).Replace("&", " and "))
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>The comparison key of a title: its words without spaces and without a leading article. Equal keys are the same title.</summary>
    public static string Key(string? text)
    {
        var w = Words(text);
        w = Regex.Replace(w, @"^(the|a|an) ", "");
        return w.Replace(" ", "");
    }

    /// <summary>An author name with the spaces taken out, so initials compare whether or not they are spaced.</summary>
    public static string Compact(string? text) => Words(text).Replace(" ", "");

    // ── what the library calls a book ─────────────────────────────────────────

    public sealed record NameParts(string Title, int? Year);

    private static readonly Regex ScanPrefix = new(@"^[\s\-–—]*(?:\(\s*(?<y>\d{4}|Unknown)\s*\)\s*)?[\-–—]\s*(?<rest>.+)$", RegexOptions.Compiled);
    private static readonly Regex YearSuffix = new(@"\s*[\(\[]\s*(?<y>1[5-9]\d{2}|20\d{2})\s*[\)\]]\s*$", RegexOptions.Compiled);
    private static readonly Regex FormatSuffix = new(
        @"\s*[\(\[]\s*(?:un)?abridged\s*[\)\]]\s*$|\s*\[\s*dramati[sz]ed adaptation\s*\]\s*$|\s*\(\s*\d+\s+of\s+\d+\s*\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A library name as a title and, if it said one, a year: "Happy Place (2023)" is "Happy Place" and 2023; "- - (2014) - Dust World"
    /// (a file-scan leftover) is "Dust World" and 2014; "Warbreaker (1 of 3) [Dramatized Adaptation]" is "Warbreaker".
    /// </summary>
    public static NameParts Parse(string? name)
    {
        var text = (name ?? "").Trim();
        int? year = null;

        // A file-scan leftover: "- - (2014) - Dust World" or "Series - 2 - (2014) - Title" keeps the last segment.
        if (text.StartsWith('-') || text.StartsWith('–') || text.StartsWith('—'))
        {
            var m = ScanPrefix.Match(text);
            if (m.Success)
            {
                if (int.TryParse(m.Groups["y"].Value, out var y)) year = y;
                text = m.Groups["rest"].Value.Trim();
                // "- - (2014) - Dust World": what is left may still begin with a dash segment
                while (text.StartsWith('-')) text = text[1..].Trim();
                var inner = Regex.Match(text, @"^\(\s*(?<y>\d{4})\s*\)\s*[\-–—]\s*(?<rest>.+)$");
                if (inner.Success) { year = int.Parse(inner.Groups["y"].Value); text = inner.Groups["rest"].Value.Trim(); }
            }
        }

        for (var guard = 0; guard < 4; guard++)
        {
            var f = FormatSuffix.Replace(text, "");
            var y = YearSuffix.Match(f);
            if (y.Success) { year ??= int.Parse(y.Groups["y"].Value); f = YearSuffix.Replace(f, ""); }
            if (f == text) break;
            text = f;
        }
        return new NameParts(text.Trim(), year);
    }

    // ── series and volume tails ───────────────────────────────────────────────

    private static readonly Regex[] SeriesTails =
    [
        // "(Dan Deadman Space Detective, Book 1)" and "(Series #3)"
        new(@"\s*\([^()]*?,?\s*(?:book|vol(?:ume)?\.?|part|#)\s*\d+(?:\.\d+)?\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // ": Undying Mercenaries, Book 8" and ", Book 8" and " Book 8"
        new(@"\s*[:,\-–]\s*[^:,\-–]*?,\s*book\s*\d+(?:\.\d+)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\s*[:,\-–]?\s*book\s*\d+(?:\.\d+)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // " #4"
        new(@"\s*#\s*\d+(?:\.\d+)?\s*$", RegexOptions.Compiled),
    ];

    /// <summary>The title without a trailing series position ("... (Dan Deadman Space Detective, Book 1)", ": Undying Mercenaries, Book 8", " #4").</summary>
    public static string StripSeriesTail(string title)
    {
        var text = title.Trim();
        for (var guard = 0; guard < 3; guard++)
        {
            var before = text;
            foreach (var re in SeriesTails) text = re.Replace(text, "").Trim();
            if (text == before) break;
        }
        return text.Length >= 2 ? text : title.Trim();
    }

    /// <summary>The part of a title before its subtitle: "Mind Hack: A Cyberpunk Saga" is "Mind Hack". Null when there is no subtitle.</summary>
    public static string? MainTitle(string title)
    {
        var i = title.IndexOfAny([':', '(']);
        if (i < 0)
        {
            var dash = title.IndexOf(" - ", StringComparison.Ordinal);
            i = dash;
        }
        if (i <= 0) return null;
        var main = title[..i].Trim().TrimEnd(',');
        return main.Length >= 3 ? main : null;
    }

    // ── comparing titles ──────────────────────────────────────────────────────

    public enum TitleMatch { None, Contains, Prefix, Exact }

    private static readonly char[] SubtitleBoundary = [':', '(', ',', '-', '–', '—', '['];

    /// <summary>
    /// How a library title relates to a Hardcover title. Exact: the same title (a trailing series position ignored on either side).
    /// Prefix: one is the other followed by a subtitle or series ("Mind Hack" / "Mind Hack: A Cyberpunk Saga"); the boundary must be
    /// a subtitle mark, so "Kiki" is not "Kiki's Delivery Service" and "Mind Hack" is not "Mind Hacks". Contains: one title inside
    /// the other, a weak signal.
    /// </summary>
    public static TitleMatch Relate(string libraryTitle, string hardcoverTitle)
    {
        var lib = StripSeriesTail(Parse(libraryTitle).Title);
        var hc = StripSeriesTail(hardcoverTitle);
        var kl = Key(lib);
        var kh = Key(hc);
        if (kl.Length == 0 || kh.Length == 0) return TitleMatch.None;
        if (kl == kh || Key(Parse(libraryTitle).Title) == Key(hardcoverTitle)) return TitleMatch.Exact;

        if (IsSubtitleExtension(lib, hc) || IsSubtitleExtension(hc, lib)) return TitleMatch.Prefix;

        if (kl.Length >= 6 && kh.Length >= 6 && ContainsWords(Words(lib), Words(hc)))
            return TitleMatch.Contains;
        return TitleMatch.None;
    }

    /// <summary>True when one text's words appear in the other as a run of whole words ("Blood World" is in "The Blood World Saga", "Mind Hack" is not in "Mind Hacking").</summary>
    private static bool ContainsWords(string a, string b) =>
        (" " + b + " ").Contains(" " + a + " ", StringComparison.Ordinal) || (" " + a + " ").Contains(" " + b + " ", StringComparison.Ordinal);

    /// <summary>True when <paramref name="longer"/> is <paramref name="shorter"/> followed by a subtitle mark and more text.</summary>
    private static bool IsSubtitleExtension(string shorter, string longer)
    {
        var s = Fold(shorter).Trim().TrimEnd(':', ',').Trim();
        var l = Fold(longer).Trim();
        if (s.Length < 3 || l.Length <= s.Length || !l.StartsWith(s, StringComparison.Ordinal)) return false;
        var rest = l[s.Length..].TrimStart(' ');
        // the first character after the title (ignoring spaces) must be a subtitle mark, and the title must end on a word boundary
        return rest.Length > 0 && SubtitleBoundary.Contains(rest[0]) && (l[s.Length] == ' ' || SubtitleBoundary.Contains(l[s.Length]));
    }

    // ── retrieval helpers ─────────────────────────────────────────────────────

    /// <summary>The titles to look a book up under, best first: the clean title, then without a series tail.</summary>
    public static List<string> BookTitles(IEnumerable<string?> sources)
    {
        var list = new List<string>();
        void Add(string? t)
        {
            if (string.IsNullOrWhiteSpace(t) || t.Length > 120) return;
            var parts = Parse(t);
            foreach (var candidate in new[] { parts.Title, StripSeriesTail(parts.Title) })
                if (candidate.Length >= 2 && !list.Contains(candidate, StringComparer.OrdinalIgnoreCase)) list.Add(candidate);
        }
        foreach (var s in sources) Add(s);
        return list;
    }

    public static string Slug(string? text)
    {
        var s = Regex.Replace(Fold(text), @"[^a-z0-9]+", "-");
        return s.Trim('-');
    }

    /// <summary>
    /// The slugs to try for a book, best first. Hardcover gives a book the plain slug of its title only when nothing holds it, so
    /// the plain slug is often another book (the plain "blood-world" is by Chris Mooney; B.V. Larson's is "blood-world-2017"): each
    /// title is tried plain, then with its year.
    /// </summary>
    public static List<string> BookSlugs(IEnumerable<string> titles, int? year)
    {
        var slugs = new List<string>();
        foreach (var t in titles)
        {
            var slug = Slug(t);
            if (slug.Length < 3) continue;
            if (!slugs.Contains(slug)) slugs.Add(slug);
            if (year is > 0 && !slugs.Contains($"{slug}-{year}")) slugs.Add($"{slug}-{year}");
        }
        return slugs;
    }

    /// <summary>The slugs to try for a series: the plain name, and the name with its author ("birthright-rick-partlow") where the plain one is taken.</summary>
    public static List<string> SeriesSlugs(IEnumerable<string> names, string? author)
    {
        var slugs = new List<string>();
        var authorSlug = Slug(author);
        foreach (var n in names)
        {
            var slug = Slug(n);
            if (slug.Length < 3) continue;
            if (!slugs.Contains(slug)) slugs.Add(slug);
            if (authorSlug.Length > 0 && !slugs.Contains($"{slug}-{authorSlug}")) slugs.Add($"{slug}-{authorSlug}");
        }
        return slugs;
    }

    /// <summary>
    /// Bounds for a range on the title that includes every title beginning with <paramref name="prefix"/>: the prefix and the prefix with
    /// its last letter raised. Hardcover's title ordering is linguistic (spaces and punctuation barely count), so this range also holds
    /// unrelated neighbours ("Mind Hacks", "Mindhacker"); callers keep only the titles <see cref="Relate"/> accepts, and restrict the
    /// range by author where the author is known so that a short title does not return thousands of books.
    /// </summary>
    public static (string Low, string High) PrefixRange(string prefix)
    {
        var p = prefix.Trim();
        return (p, p[..^1] + (char)(p[^1] + 1));
    }

    // ── series names ──────────────────────────────────────────────────────────

    private static readonly Regex SeriesWord = new(@"\s+(series|saga|trilogy|universe|cycle|chronicles|novels|books|collection)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeriesNumberTail = new(@"\s*#\s*\d+(?:\.\d+)?\s*$", RegexOptions.Compiled);
    private static readonly Regex SeriesParen = new(@"\s*\((?:publication|chronological|reading)[^)]*\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SeriesVolumeRange = new(@"\s+-\s+\d+(?:\s*-\s*\d+)?\s*$", RegexOptions.Compiled);
    private static readonly Regex SeriesUnknownTail = new(@"\s*-\s*-\s*\([^)]*\)\s*$", RegexOptions.Compiled);

    /// <summary>The name of a series without "#3", a volume range ("- 1-3", "- 01"), a file-scan "- - (Unknown)", a "Series"/"Saga"/"Universe" ending or a "(Publication Order)" note.</summary>
    public static string SeriesCore(string name)
    {
        var s = Parse(name).Title;
        // "Captive's War #1; The Captive's War": the name is what follows the position
        var hashed = Regex.Match(s, @"^.*#\s*\d+\s*;\s*(?<name>.+)$");
        if (hashed.Success) s = hashed.Groups["name"].Value.Trim();
        for (var guard = 0; guard < 4; guard++)
        {
            var before = s;
            s = SeriesUnknownTail.Replace(s, "");
            s = SeriesVolumeRange.Replace(s, "");
            s = SeriesNumberTail.Replace(s, "");
            s = SeriesParen.Replace(s, "");
            s = SeriesWord.Replace(s, "");
            s = s.Trim();
            if (s == before) break;
        }
        return s.Length >= 2 ? s : name.Trim();
    }

    /// <summary>The names to look a series up under: as given, without its tail, and with ": " for " - " ("Imperium - Mech" is Hardcover's "Imperium: Mech").</summary>
    public static List<string> SeriesNames(IEnumerable<string?> sources)
    {
        var list = new List<string>();
        void Add(string? n)
        {
            if (string.IsNullOrWhiteSpace(n) || n.Length > 100) return;
            foreach (var v in new[] { Parse(n).Title, SeriesCore(n), SeriesCore(n).Replace(" - ", ": "), SeriesCore(n) + " Series" })
                if (v.Length >= 2 && !list.Contains(v, StringComparer.OrdinalIgnoreCase)) list.Add(v);
        }
        foreach (var s in sources) Add(s);
        return list;
    }

    /// <summary>How a library series name relates to a Hardcover series name, ignoring "Series", "#3" and similar tails on both sides.</summary>
    public static TitleMatch RelateSeries(string libraryName, string hardcoverName)
    {
        var a = SeriesCore(libraryName);
        var b = SeriesCore(hardcoverName);
        var ka = Key(a);
        var kb = Key(b);
        if (ka.Length == 0 || kb.Length == 0) return TitleMatch.None;
        if (ka == kb) return TitleMatch.Exact;
        if (IsSubtitleExtension(a, b) || IsSubtitleExtension(b, a)) return TitleMatch.Prefix;
        if (ka.Length >= 6 && kb.Length >= 6 && ContainsWords(Words(a), Words(b)))
            return TitleMatch.Contains;
        return TitleMatch.None;
    }

    // ── authors ───────────────────────────────────────────────────────────────

    public enum AuthorMatch { Unknown, Exact, Partial, Different }

    /// <summary>
    /// How a library author relates to the authors Hardcover credits. Initials compare spaced or not ("B.V. Larson" is "B. V. Larson"),
    /// accents and curly punctuation are folded, and any credited name counts. Unknown when Hardcover lists no real author.
    /// </summary>
    public static AuthorMatch RelateAuthor(string? libraryAuthor, IEnumerable<string> hardcoverAuthors)
    {
        var credited = hardcoverAuthors.Where(a => !string.IsNullOrWhiteSpace(a) && !a.Equals("Unknown", StringComparison.OrdinalIgnoreCase)).ToList();
        if (credited.Count == 0 || string.IsNullOrWhiteSpace(libraryAuthor)) return AuthorMatch.Unknown;

        var lib = Compact(libraryAuthor);
        if (lib.Length == 0) return AuthorMatch.Unknown;
        if (credited.Any(a => Compact(a).Contains(lib, StringComparison.Ordinal) || lib.Contains(Compact(a), StringComparison.Ordinal) && Compact(a).Length >= 5))
            return AuthorMatch.Exact;

        var libWords = Words(libraryAuthor).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var surname = libWords.LastOrDefault();
        if (surname is { Length: >= 3 } && credited.Any(a => Words(a).Split(' ').Contains(surname)))
            return AuthorMatch.Partial;
        return AuthorMatch.Different;
    }
}
