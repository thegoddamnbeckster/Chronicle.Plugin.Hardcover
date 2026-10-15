using Chronicle.Plugin.Hardcover;

namespace Chronicle.Plugin.Hardcover.Tests;

/// <summary>The text rules, on names taken from a real library and real Hardcover titles (2026-10-14).</summary>
public class MatchingTests
{
    // ── what the library calls a book ────────────────────────────────────────

    [Theory]
    [InlineData("Happy Place (2023)", "Happy Place", 2023)]
    [InlineData("Outliers: The Story of Success (2008)", "Outliers: The Story of Success", 2008)]
    [InlineData("Lolita (1955)", "Lolita", 1955)]
    [InlineData("The Great Hunt (1990)", "The Great Hunt", 1990)]
    [InlineData("- - (2014) - Dust World", "Dust World", 2014)]
    [InlineData("- - (2018) - Dark World", "Dark World", 2018)]
    [InlineData("Odds On (Unabridged)", "Odds On", null)]
    [InlineData("Warbreaker (1 of 3) [Dramatized Adaptation]", "Warbreaker", null)]
    [InlineData("White Sand: Volume Three [Dramatized Adaptation]", "White Sand: Volume Three", null)]
    [InlineData("Blood World", "Blood World", null)]
    public void Parse_SeparatesTheTitleFromAYearAndFileScanLeftovers(string name, string title, int? year)
    {
        var p = Matching.Parse(name);
        Assert.Equal(title, p.Title);
        Assert.Equal(year, p.Year);
    }

    [Theory]
    [InlineData("Dial D for Deadman: A Space Team Universe Sci Fi Comedy (Dan Deadman Space Detective, Book 1)", "Dial D for Deadman: A Space Team Universe Sci Fi Comedy")]
    [InlineData("Blood World: Undying Mercenaries, Book 8", "Blood World")]
    [InlineData("Rebel Fleet #4", "Rebel Fleet")]
    [InlineData("Murderbot Diaries #3", "Murderbot Diaries")]
    [InlineData("Echoes from the Moon - The Token, Book 1", "Echoes from the Moon")]
    [InlineData("The Well of Ascension, Part 2", "The Well of Ascension, Part 2")]   // part of the title, not a series position
    [InlineData("Blood World", "Blood World")]
    public void StripSeriesTail_RemovesASeriesPosition_NotARealTitle(string title, string expected) =>
        Assert.Equal(expected, Matching.StripSeriesTail(title));

    // ── titles ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Blood World", "Blood World", Matching.TitleMatch.Exact)]
    [InlineData("blood world", "Blood World", Matching.TitleMatch.Exact)]
    [InlineData("Deadman’s Run", "Deadman's Run", Matching.TitleMatch.Exact)]
    [InlineData("The Great Hunt (1990)", "The Great Hunt", Matching.TitleMatch.Exact)]
    [InlineData("Blood World", "Blood World: Undying Mercenaries, Book 8", Matching.TitleMatch.Exact)]   // a series tail is not a subtitle
    [InlineData("Mind Hack", "Mind Hack: A Cyberpunk Saga", Matching.TitleMatch.Prefix)]
    [InlineData("Dial D for Deadman", "Dial D for Deadman: A Space Team Universe Sci Fi Comedy (Dan Deadman Space Detective, Book 1)", Matching.TitleMatch.Prefix)]
    [InlineData("Dial D for Deadman", "Dial D for Deadman, A Space Team Universe Novel:", Matching.TitleMatch.Prefix)]
    [InlineData("Outliers: The Story of Success", "Outliers", Matching.TitleMatch.Prefix)]
    [InlineData("Kiki", "Kiki's Delivery Service", Matching.TitleMatch.None)]
    [InlineData("Mind Hack", "Mind Hacks: Tips & Tools for Using Your Brain", Matching.TitleMatch.None)]
    [InlineData("Mind Hack", "Mind Hacking", Matching.TitleMatch.None)]
    [InlineData("Alter", "Altered Carbon", Matching.TitleMatch.None)]
    [InlineData("Dust World", "Home World", Matching.TitleMatch.None)]
    public void Relate_TellsATitleFromASubtitledTitle_AndFromAnotherBook(string library, string hardcover, Matching.TitleMatch expected) =>
        Assert.Equal(expected, Matching.Relate(library, hardcover));

    // ── slugs ────────────────────────────────────────────────────────────────

    [Fact]
    public void BookSlugs_AreThePlainSlug_ThenTheSlugWithTheYear()
    {
        // "blood-world" is by Chris Mooney; B.V. Larson's book is "blood-world-2017".
        Assert.Equal(["blood-world", "blood-world-2017"], Matching.BookSlugs(["Blood World"], 2017));
        Assert.Equal(["blood-world"], Matching.BookSlugs(["Blood World"], null));
    }

    [Fact]
    public void SeriesSlugs_IncludeTheNameAndAuthorSlug()
    {
        var slugs = Matching.SeriesSlugs(["Birthright"], "Rick Partlow");
        Assert.Equal(["birthright", "birthright-rick-partlow"], slugs);
    }

    [Theory]
    [InlineData("Mind Hack", "Mind Hack", "Mind Hacl")]
    [InlineData("Alter", "Alter", "Altes")]
    public void PrefixRange_RaisesTheLastLetter(string prefix, string low, string high) =>
        Assert.Equal((low, high), Matching.PrefixRange(prefix));

    // ── series ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Dan Deadman Space Detective", "Dan Deadman Space Detective Series", Matching.TitleMatch.Exact)]
    [InlineData("Imperium - Mech", "Imperium: Mech", Matching.TitleMatch.Exact)]
    [InlineData("Imperium Series #3", "Imperium series", Matching.TitleMatch.Exact)]
    [InlineData("Four Horsemen Universe", "Four Horsemen Universe", Matching.TitleMatch.Exact)]
    [InlineData("Undying Mercenaries", "Undying Mercenaries #8", Matching.TitleMatch.Exact)]
    [InlineData("Hunger - 1-3", "Hunger", Matching.TitleMatch.Exact)]
    [InlineData("The Stormlight Archive - 01", "The Stormlight Archive", Matching.TitleMatch.Exact)]
    [InlineData("Mother of Learning - - (Unknown)", "Mother of Learning", Matching.TitleMatch.Exact)]
    [InlineData("Captive's War #1; The Captive's War", "The Captive's War", Matching.TitleMatch.Exact)]
    [InlineData("Mech Wars - 1-4", "Mech Wars", Matching.TitleMatch.Exact)]
    [InlineData("Imperium", "Imperium Galactic", Matching.TitleMatch.Contains)]   // weak: the author and the books decide
    [InlineData("Star Force", "Star Force: Phase 1, The Origin Series", Matching.TitleMatch.Prefix)]
    public void RelateSeries_IgnoresSeriesWordsAndNumbers(string library, string hardcover, Matching.TitleMatch expected) =>
        Assert.Equal(expected, Matching.RelateSeries(library, hardcover));

    [Fact]
    public void SeriesNames_TryTheNameWithoutItsTail_AndWithSeriesAdded()
    {
        var names = Matching.SeriesNames(["Imperium Series #3"]);
        Assert.Contains("Imperium", names);
        Assert.Contains("Imperium Series", names);
        Assert.Contains("Imperium: Mech", Matching.SeriesNames(["Imperium - Mech"]));
    }

    // ── authors ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("B.V. Larson", new[] { "B. V. Larson" }, Matching.AuthorMatch.Exact)]
    [InlineData("B.V. Larson", new[] { "B.V. Larson" }, Matching.AuthorMatch.Exact)]
    [InlineData("James S. A. Corey", new[] { "James S.A. Corey" }, Matching.AuthorMatch.Exact)]
    [InlineData("Barry J. Hutchison", new[] { "Barry J. Hutchison", "Phil Thron" }, Matching.AuthorMatch.Exact)]
    [InlineData("Jason Cordova", new[] { "Jason Córdova" }, Matching.AuthorMatch.Exact)]
    [InlineData("Chris Kennedy", new[] { "Jon R. Osborne", "Paul Elard Cooley" }, Matching.AuthorMatch.Different)]
    [InlineData("Rick Partlow", new[] { "Mike Resnick" }, Matching.AuthorMatch.Different)]
    [InlineData("Brandon Sanderson", new[] { "Unknown" }, Matching.AuthorMatch.Unknown)]
    [InlineData("Brandon Sanderson", new string[0], Matching.AuthorMatch.Unknown)]
    [InlineData("Kevin Ikenberry", new[] { "Kevin Steverson" }, Matching.AuthorMatch.Different)]
    [InlineData("Kevin Ikenberry", new[] { "K. Ikenberry" }, Matching.AuthorMatch.Partial)]
    public void RelateAuthor_ComparesInitialsAndAccents(string library, string[] credited, Matching.AuthorMatch expected) =>
        Assert.Equal(expected, Matching.RelateAuthor(library, credited));
}
