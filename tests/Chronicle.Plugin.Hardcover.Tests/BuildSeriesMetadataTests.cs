using System.Text.Json;

namespace Chronicle.Plugin.Hardcover.Tests;

public class BuildSeriesMetadataTests
{
    private static HcBookSeriesEntry Entry(int id, string title, double? position) => new()
    {
        Position = position,
        Book = new HcBookStub { Id = id, Title = title, ReleaseYear = 2020 },
    };

    private static (double? position, string[] alternates) Extra(MediaMetadataView r) => (r.Position, r.Alternates);

    private sealed record MediaMetadataView(string Id, double? Position, string[] Alternates);

    private static List<MediaMetadataView> Build(params HcBookSeriesEntry[] entries)
    {
        var meta = HardcoverMetadataProvider.BuildSeriesMetadata(new HcSeries { Id = 1, Name = "S", BookSeries = entries });
        return meta.Results!.Select(r =>
        {
            var ext = r.ExtendedData!.Value;
            var pos = ext.TryGetProperty("seriesPosition", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : (double?)null;
            var alts = ext.TryGetProperty("alternateIds", out var a)
                ? a.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
            return new MediaMetadataView(r.ExternalId!, pos, alts);
        }).ToList();
    }

    [Fact]
    public void EditionsAtOnePosition_CollapseToOneRepresentative_AndTheOthersBecomeAlternateIds()
    {
        // Ready Player One shape: the English original plus translations, all at position 1.
        var results = Build(
            Entry(30, "Ready Player One (Português)", 1),
            Entry(20, "Ready Player One", 1),
            Entry(10, "Ready Player One", 1));

        var only = Assert.Single(results);
        Assert.Equal("hardcover:10", only.Id); // ASCII title, lowest id
        Assert.Equal(["hardcover:20", "hardcover:30"], only.Alternates.OrderBy(x => x));
    }

    [Fact]
    public void BooksWithNoPosition_AreNotEditionsOfEachOther_SoNoneIsAnotherOnesAlternate()
    {
        var results = Build(Entry(1, "Companion Novel", null), Entry(2, "Omnibus", null), Entry(3, "Book One", 1));

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Empty(r.Alternates));
    }

    [Fact]
    public void FractionalPositions_StayDistinctFromTheWholeNumberBesideThem()
    {
        var results = Build(Entry(1, "Leviathan Wakes", 1), Entry(2, "The Butcher of Anderson Station", 1.1), Entry(3, "Caliban's War", 2));

        Assert.Equal([1.0, 1.1, 2.0], results.Select(r => r.Position));
        Assert.All(results, r => Assert.Empty(r.Alternates));
    }

    private static HcBookSeriesEntry WithCover(int id, string title, double? position, string? cover) => new()
    {
        Position = position,
        Book = new HcBookStub { Id = id, Title = title, ReleaseYear = 2014, Image = cover is null ? null : new HcImage { Url = cover } },
    };

    private static string? PosterOf(params HcBookSeriesEntry[] entries) =>
        HardcoverMetadataProvider.BuildSeriesMetadata(new HcSeries { Id = 1, Name = "Red Rising Saga", BookSeries = entries }).PosterUrl;

    [Fact]
    public void SeriesPoster_IsTheCoverOfTheOriginalLanguageFirstBook_NotAForeignEditionThatHappensToComeFirst()
    {
        // Live (2026-10-21): Red Rising Saga showed the Hungarian edition "Vörös Lázadás" as its poster.
        var poster = PosterOf(
            WithCover(5, "Vörös Lázadás", 1, "https://img/hungarian.jpg"),
            WithCover(9, "Red Rising", 1, "https://img/english.jpg"),
            WithCover(20, "Golden Son", 2, "https://img/golden.jpg"));

        Assert.Equal("https://img/english.jpg", poster);
    }

    [Fact]
    public void SeriesPoster_FallsToTheNextBookWithACover_WhenTheFirstHasNone()
    {
        var poster = PosterOf(WithCover(9, "Red Rising", 1, null), WithCover(20, "Golden Son", 2, "https://img/golden.jpg"));

        Assert.Equal("https://img/golden.jpg", poster);
    }

    [Fact]
    public void SeriesPoster_IsNullWhenNoBookHasACover()
    {
        Assert.Null(PosterOf(WithCover(9, "Red Rising", 1, null)));
    }

    [Fact]
    public void SeriesSourceUrl_IsTheSlugPage_AndAbsentWithoutASlug()
    {
        var withSlug = HardcoverMetadataProvider.BuildSeriesMetadata(new HcSeries { Id = 1033, Name = "Red Rising Saga", Slug = "red-rising-saga", BookSeries = [] });
        var without = HardcoverMetadataProvider.BuildSeriesMetadata(new HcSeries { Id = 1033, Name = "Red Rising Saga", BookSeries = [] });

        Assert.Equal("https://hardcover.app/series/red-rising-saga", withSlug.SourceUrl);
        Assert.Null(without.SourceUrl);
    }

    [Fact]
    public void PageUrl_EscapesTheSlug_AndNeedsOne()
    {
        Assert.Equal("https://hardcover.app/books/biomancer-2", HardcoverMetadataProvider.PageUrl("books", "biomancer-2"));
        Assert.Null(HardcoverMetadataProvider.PageUrl("books", " "));
        Assert.Equal("https://hardcover.app/authors/a%2Fb", HardcoverMetadataProvider.PageUrl("authors", "a/b"));
    }
}
