namespace Chronicle.Plugin.Hardcover.Tests;

/// <summary>What is beneath an author (its series and standalone books) and beneath a series (its books in order), as Chronicle asks when the user adds an author by hand.</summary>
public class ChildrenTests
{
    [Fact]
    public async Task AnAuthor_ListsItsSeriesOnce_AndTheBooksThatBelongToNone()
    {
        var hc = new FakeHardcover();
        hc.AuthorList.Add(new(7, "Frank Herbert"));
        hc.Books.Add(new(1, "Dune", 1965, "dune", ["Frank Herbert"], [("Dune Chronicles", 1)]));
        hc.Books.Add(new(2, "Dune Messiah", 1969, "dune-messiah", ["Frank Herbert"], [("Dune Chronicles", 2)]));
        hc.Books.Add(new(3, "The Green Brain", 1966, "green-brain", ["Frank Herbert"]));
        hc.Books.Add(new(4, "Other Author Book", 2000, "other", ["Someone Else"]));

        var children = await hc.Provider().GetChildrenAsync("hardcover:author:7", 0);

        Assert.Equal(["Dune Chronicles", "The Green Brain"], children.Select(c => c.Title));
        Assert.Equal("hardcover:series:1", children[0].ExternalId);
        Assert.Equal("hardcover:3", children[1].ExternalId);
        Assert.Equal(1966, children[1].Year);
    }

    [Fact]
    public async Task AnAuthorWithMoreBooksThanOnePage_ListsThemAll()
    {
        var hc = new FakeHardcover();
        hc.AuthorList.Add(new(7, "Prolific"));
        for (var i = 1; i <= 250; i++)
            hc.Books.Add(new(i, $"Book {i:D3}", 1900 + i % 100, $"b{i}", ["Prolific"]));

        var children = await hc.Provider().GetChildrenAsync("hardcover:author:7", 0);

        Assert.Equal(250, children.Count);
    }

    [Fact]
    public async Task ASeries_ListsItsBooksInReadingOrder_WithTheirPositions()
    {
        var hc = new FakeHardcover();
        hc.SeriesList.Add(new(9, "The Series", "the-series", "Author", 2, [(11, "Book One"), (12, "Book Two")]));

        var children = await hc.Provider().GetChildrenAsync("hardcover:series:9", 1);

        Assert.Equal(["Book One", "Book Two"], children.Select(c => c.Title));
        Assert.Equal([1, 2], children.Select(c => c.Number));
        Assert.Equal("hardcover:11", children[0].ExternalId);
    }

    [Fact]
    public async Task ABookHasNothingBeneathIt()
    {
        var hc = new FakeHardcover();
        Assert.Empty(await hc.Provider().GetChildrenAsync("hardcover:11", 2));
    }
}
