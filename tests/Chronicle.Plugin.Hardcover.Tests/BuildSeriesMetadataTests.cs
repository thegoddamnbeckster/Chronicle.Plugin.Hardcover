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
}
