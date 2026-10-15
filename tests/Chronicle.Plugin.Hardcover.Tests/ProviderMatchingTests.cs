using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Hardcover.Tests;

/// <summary>
/// Whole-search tests against a fake Hardcover holding real records (recorded from the live API, 2026-10-14). Each case is a book or a
/// series the library holds, that Hardcover has, and that the plugin did not find before.
/// </summary>
public class ProviderMatchingTests
{
    private const int Gate = 50;   // Chronicle's confidence gate

    private static FakeHardcover.B Book(int id, string title, int? year, string slug, string[] authors, (string, double)[]? series = null, int ratings = 5) =>
        new(id, title, year, slug, authors, series, ratings);

    private static MediaSearchContext Level2(string name, string series, string author, int? year = null, IReadOnlyList<string>? alt = null) =>
        new(name, Year: year, ParentName: series, GrandparentName: author, HierarchyLevel: 2, MediaTypeName: "audiobooks", IsRealHierarchyPosition: true, AltTitles: alt);

    private static MediaSearchContext StandaloneBook(string name, string author, int? year = null) =>
        new(name, Year: year, ParentName: author, HierarchyLevel: 1, ChildCount: 0, MediaTypeName: "books", IsRealHierarchyPosition: true);

    private static MediaSearchContext SeriesItem(string name, string author, params string[] books) =>
        new(name, ParentName: author, HierarchyLevel: 1, ChildCount: books.Length, ChildNames: books, MediaTypeName: "audiobooks", IsRealHierarchyPosition: true);

    private static async Task<ScoredCandidate?> Best(FakeHardcover fake, MediaSearchContext ctx)
    {
        var found = await fake.Provider().SearchAsync(ctx);
        return found.OrderByDescending(c => c.Score).FirstOrDefault();
    }

    private static FakeHardcover BloodWorld()
    {
        var fake = new FakeHardcover();
        fake.Books.AddRange([
            Book(670408, "Blood World", 2017, "blood-world-2017", ["B.V. Larson"], [("Undying Mercenaries", 8)], 38),
            Book(583747, "Blood World", 2020, "blood-world", ["Chris Mooney"], null, 0),
            Book(1985767, "Blood World", null, "blood-world-db5170ed-9103-44f0-b6f8-dacd4fc40f10", [], null, 0),
            Book(2922522, "Blood World", null, "blood-world-ed809d17-08bd-43b0-9e72-42789376fcd9", ["Unknown"], null, 0),
            Book(2732456, "Blood World: Undying Mercenaries, Book 8", 2018, "blood-world-undying-mercenaries-book-8", ["B.V. Larson"], [("Undying Mercenaries", 8)], 0),
        ]);
        return fake;
    }

    // ══ books the library names plainly ═════════════════════════════════════

    [Fact]
    public async Task BloodWorld_IsFound_AndTheOtherBlood_WorldsAreNot()
    {
        var best = await Best(BloodWorld(), Level2("Blood World", "Undying Mercenaries", "B.V. Larson", 2017));

        Assert.Equal("hardcover:670408", best!.Metadata.ExternalId);
        Assert.True(best.Score >= Gate);
    }

    [Fact]
    public async Task BloodWorld_ByAnotherAuthor_IsRefused()
    {
        var found = await BloodWorld().Provider().SearchAsync(Level2("Blood World", "Undying Mercenaries", "B.V. Larson", 2017));
        Assert.DoesNotContain(found, c => c.Metadata.ExternalId == "hardcover:583747" && c.Score >= Gate);
    }

    [Fact]
    public async Task ABookWhoseNameCarriesAFileScanPrefix_IsFoundByItsRealTitleAndYear()
    {
        var fake = new FakeHardcover();
        fake.Books.Add(Book(670410, "Dust World", 2014, "dust-world", ["B.V. Larson"], [("Undying Mercenaries", 2)]));
        var best = await Best(fake, Level2("- - (2014) - Dust World", "Undying Mercenaries", "B.V. Larson"));
        Assert.Equal("hardcover:670410", best!.Metadata.ExternalId);
        Assert.True(best.Score >= 100, best.ScoreReason);      // title, year (from the name), series and author all agree
    }

    [Fact]
    public async Task AYearInTheName_IsUsedAsTheYear_NotSearchedAsPartOfTheTitle()
    {
        var fake = new FakeHardcover();
        fake.Books.Add(Book(524206, "Happy Place", 2023, "happy-place", ["Emily Henry"]));
        fake.Books.Add(Book(1893681, "Happy place. Una vacanza particolare", 2024, "happy-place-una-vacanza-particolare", ["Emily Henry"]));
        var best = await Best(fake, StandaloneBook("Happy Place (2023)", "Emily Henry"));
        Assert.Equal("hardcover:524206", best!.Metadata.ExternalId);
        Assert.Contains("year exact", best.ScoreReason);
    }

    [Fact]
    public async Task TheSlugWithTheYear_IsTried_WhenThePlainSlugIsAnotherBook()
    {
        // The exact title lookup is blocked here (as when Hardcover's index returns another row), so only the slugs can find it.
        var fake = new FakeHardcover();
        fake.Books.Add(Book(583747, "Blood World", 2020, "blood-world", ["Chris Mooney"], null, 0));
        fake.Books.Add(Book(670408, "Blood-World", 2017, "blood-world-2017", ["B.V. Larson"], [("Undying Mercenaries", 8)], 38));
        var best = await Best(fake, Level2("Blood World", "Undying Mercenaries", "B.V. Larson", 2017));
        Assert.Equal("hardcover:670408", best!.Metadata.ExternalId);
    }

    // ══ books Hardcover gives a subtitle ════════════════════════════════════

    [Fact]
    public async Task DialDForDeadman_IsFound_ThoughHardcoverTitlesItWithASubtitleAndTheSeries()
    {
        var fake = new FakeHardcover();
        fake.Books.AddRange([
            Book(544515, "Dial D for Deadman: A Space Team Universe Sci Fi Comedy (Dan Deadman Space Detective, Book 1)", 2025, "dial-d-for-deadman", ["Barry J. Hutchison", "Phil Thron"], [("Dan Deadman Space Detective Series", 1)]),
            Book(1404499, "Dial D for Deadman, A Space Team Universe Novel:", 2017, "dial-d-for-deadman-a-space-team-universe-novel", ["Barry J. Hutchison", "Phil Thron"], [("Dan Deadman Space Detective", 1)]),
        ]);
        var best = await Best(fake, Level2("Dial D for Deadman", "Dan Deadman Space Detective", "Barry J. Hutchison", 2018));
        Assert.NotNull(best);
        Assert.True(best!.Score >= Gate, best.ScoreReason);
        Assert.Contains("series agrees", best.ScoreReason);
    }

    [Fact]
    public async Task MindHack_IsFoundByTitleRange_ThoughNeitherItsTitleNorItsSlugIsTheLibrarys()
    {
        var fake = new FakeHardcover();
        fake.Books.AddRange([
            Book(2076774, "Mind Hack: A Cyberpunk Saga", 2021, "mind-hack-a-cyberpunk-saga", ["Matthew A. Goodwin"], [("A Cyberpunk Saga", 4)]),
            Book(1307, "Mind Hacks: Tips & Tools for Using Your Brain", 2004, "mind-hacks", ["Tom Stafford", "Matt Webb"], null, 400),
            Book(432133, "Mind Hacking", 2016, "mind-hacking", ["John Hargrave"], null, 900),
        ]);
        var best = await Best(fake, Level2("Mind Hack", "A Cyberpunk Saga", "Matthew A. Goodwin", 2021));
        Assert.Equal("hardcover:2076774", best!.Metadata.ExternalId);
        Assert.True(best.Score >= Gate);
    }

    [Fact]
    public async Task AShortTitle_DoesNotMatchTheLongerTitlesThatMerelyStartWithIt()
    {
        var fake = new FakeHardcover();
        fake.Books.AddRange([
            Book(453559, "Kiki's Delivery Service", 1985, "kikis-delivery-service", ["Eiko Kadono"], null, 122),
            Book(455069, "Kiki Kallira Breaks a Kingdom", 2021, "kiki-kallira-breaks-a-kingdom", ["Sangu Mandanna"]),
        ]);
        var found = await fake.Provider().SearchAsync(Level2("Kiki", "The Token", "Nathan Hystad"));
        Assert.DoesNotContain(found, c => c.Score >= Gate);
    }

    // ══ shared universes: the library credits one author, Hardcover the writers ═

    [Fact]
    public async Task AShareduniverseBook_IsFound_WhenItsSeriesAgrees_EvenThoughTheAuthorDiffers()
    {
        // Birthright: the library credits Rick Partlow, Hardcover credits Mike Resnick; the series is the same.
        var fake = new FakeHardcover();
        fake.Books.Add(Book(468680, "Starship: Flagship", 2009, "starship-flagship", ["Mike Resnick"], [("Birthright", 33)], 7));
        var best = await Best(fake, Level2("Starship: Flagship", "Birthright", "Rick Partlow"));
        Assert.Equal("hardcover:468680", best!.Metadata.ExternalId);
        Assert.True(best.Score >= Gate, best.ScoreReason);
        Assert.Contains("author differs, series agrees", best.ScoreReason);
    }

    [Fact]
    public async Task ASameNamedBookByAnotherAuthor_IsRefused_WhenItsSeriesDoesNotAgree()
    {
        // "The Outpost": Devney Perry's, in the Jamison Valley series; the library's is in Birthright.
        var fake = new FakeHardcover();
        fake.Books.Add(Book(689666, "The Outpost", 2017, "the-outpost", ["Devney Perry"], [("Jamison Valley", 4)], 8));
        Assert.Empty((await fake.Provider().SearchAsync(Level2("The Outpost", "Birthright", "Rick Partlow"))).Where(c => c.Score >= Gate));
    }

    [Fact]
    public async Task FourHorsemen_LuckIsNotAFactor_IsFound_ByItsSeries()
    {
        var fake = new FakeHardcover();
        fake.Books.Add(Book(2003245, "Luck is Not a Factor", 2018, "luck-is-not-a-factor", ["Jon R. Osborne", "Paul Elard Cooley"], [("Four Horsemen Universe", 21)]));
        var best = await Best(fake, Level2("Luck is Not a Factor", "Four Horsemen Universe", "Chris Kennedy"));
        Assert.Equal("hardcover:2003245", best!.Metadata.ExternalId);
    }

    [Fact]
    public async Task ABookInTheLibrariesSeriesButNotCreditedToItsAuthor_IsFoundThroughTheSeries_WhenItsTitleHasASubtitle()
    {
        // Hardcover: "Sons of the Lion: Omega War Book 1"; the library holds "Sons of the Lion" under Omega War by Chris Kennedy.
        var fake = new FakeHardcover();
        fake.Books.Add(Book(1914788, "Sons of the Lion: Omega War Book 1", 2019, "sons-of-the-lion-omega-war-book-1", ["Jason Córdova"], [("Omega War", 1)]));
        fake.SeriesList.Add(new FakeHardcover.S(171955, "Omega War", "omega-war", "Chris Kennedy", 12, [(1914788, "Sons of the Lion: Omega War Book 1")]));
        var best = await Best(fake, Level2("Sons of the Lion", "Omega War", "Chris Kennedy", 2019));
        Assert.Equal("hardcover:1914788", best!.Metadata.ExternalId);
    }

    // ══ budget ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AStrongMatch_StopsAtOnce_WithoutTheWiderLookups()
    {
        var fake = BloodWorld();
        await fake.Provider().SearchAsync(Level2("Blood World", "Undying Mercenaries", "B.V. Larson", 2017));
        Assert.DoesNotContain("GetBooksByTitleRange", fake.Operations);
        Assert.DoesNotContain("GetBooksByTitleRangeAndAuthor", fake.Operations);
        Assert.True(fake.Requests <= 3, string.Join(", ", fake.Operations));
    }

    [Fact]
    public async Task NoSearch_UsesAnOperatorHardcoverCanRefuse()
    {
        // _ilike has been disabled by Hardcover before, answered with a 403 that opens the client's breaker for ten minutes.
        var fake = new FakeHardcover();
        fake.Books.Add(Book(1, "Some Book: Subtitle", 2020, "some-book-subtitle", ["An Author"]));
        await fake.Provider().SearchAsync(Level2("Some Book", "Some Series", "An Author"));
        Assert.All(fake.Operations, op => Assert.DoesNotContain("ilike", op, StringComparison.OrdinalIgnoreCase));
    }

    // ══ series ══════════════════════════════════════════════════════════════

    private static FakeHardcover Deadman()
    {
        var fake = new FakeHardcover();
        fake.SeriesList.AddRange([
            new FakeHardcover.S(23909, "Dan Deadman Space Detective", "dan-deadman-space-detective", "Barry J. Hutchison", 3),
            new FakeHardcover.S(288803, "Dan Deadman Space Detective Series", "dan-deadman-space-detective-series", "Barry J. Hutchison", 3),
            new FakeHardcover.S(185850, "Dan Deadman Space Detective Series #1", "dan-deadman-space-detective-series-1", "Barry J. Hutchison", 0),
        ]);
        return fake;
    }

    [Fact]
    public async Task ASeries_PrefersTheOneWithBooks_ToTheEmptyNumberedEntry()
    {
        var best = await Best(Deadman(), SeriesItem("Dan Deadman Space Detective", "Barry J. Hutchison", "Dial D for Deadman"));
        Assert.True(best!.Metadata.ExternalId is "hardcover:series:23909" or "hardcover:series:288803", best.Metadata.ExternalId);
        Assert.True(best.Score >= Gate);
    }

    [Fact]
    public async Task ASeriesNamedWithADash_IsFoundUnderHardcoversColon()
    {
        var fake = new FakeHardcover();
        fake.SeriesList.Add(new FakeHardcover.S(74824, "Imperium: Mech", "imperium-mech", "B.V. Larson", 4));
        fake.SeriesList.Add(new FakeHardcover.S(212414, "Imperium", "imperium-travis-starnes", "Travis Starnes", 6));
        var all = await fake.Provider().SearchAsync(SeriesItem("Imperium - Mech", "B.V. Larson", "Mech Book One"));
        Assert.True(all.Count > 0, string.Join(", ", fake.Operations));
        Assert.Equal("hardcover:series:74824", all.OrderByDescending(c => c.Score).First().Metadata.ExternalId);
    }

    [Fact]
    public async Task ASeriesWhoseAuthorDiffers_IsConfirmedByTheBooksTheLibraryHolds()
    {
        // Birthright: the library credits Rick Partlow, the series Hardcover holds is Mike Resnick's; the books are the same.
        var fake = new FakeHardcover();
        fake.SeriesList.Add(new FakeHardcover.S(2058, "Birthright", "birthright", "Mike Resnick", 33, [(468680, "Starship: Flagship"), (1, "The Outpost: Birthright Book 2")]));
        fake.SeriesList.Add(new FakeHardcover.S(2073, "Birthright", "birthright-joshua-williamson", "Joshua Williamson", 9, [(10, "Birthright, Vol. 1: Homecoming")]));
        var best = await Best(fake, SeriesItem("Birthright", "Rick Partlow", "Starship: Flagship", "The Outpost"));
        Assert.Equal("hardcover:series:2058", best!.Metadata.ExternalId);
        Assert.Contains("the library's books", best.ScoreReason);
    }

    [Fact]
    public async Task ASeriesWhoseAuthorDiffers_AndWhoseBooksDoNotMatch_IsRefused()
    {
        var fake = new FakeHardcover();
        fake.SeriesList.Add(new FakeHardcover.S(2073, "Birthright", "birthright-joshua-williamson", "Joshua Williamson", 9, [(10, "Birthright, Vol. 1: Homecoming")]));
        Assert.Empty((await fake.Provider().SearchAsync(SeriesItem("Birthright", "Rick Partlow", "Starship: Flagship"))).Where(c => c.Score >= Gate));
    }

    [Fact]
    public async Task ASeriesSlug_WithTheAuthor_IsTried()
    {
        var fake = new FakeHardcover();
        // Only reachable by "name-author" slug: the exact name differs in case and no range sees it.
        fake.SeriesList.Add(new FakeHardcover.S(305780, "BIRTHRIGHT", "birthright-rick-partlow", "Rick Partlow", 5, [(7, "Birthright Book 1")]));
        var best = await Best(fake, SeriesItem("Birthright", "Rick Partlow", "Birthright Book 1"));
        Assert.Equal("hardcover:series:305780", best!.Metadata.ExternalId);
    }
}
