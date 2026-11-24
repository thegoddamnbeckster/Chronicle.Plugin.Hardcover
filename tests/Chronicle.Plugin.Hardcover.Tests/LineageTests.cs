namespace Chronicle.Plugin.Hardcover.Tests;

/// <summary>Where a book sits, so Chronicle files one added by hand under its author (and series) and never at the top with nothing above it.</summary>
public class LineageTests
{
    [Fact]
    public async Task ABookInASeries_SitsUnderItsAuthorAndItsSeries()
    {
        var hc = new FakeHardcover();
        hc.Books.Add(new(5, "Dune", 1965, "dune", ["Frank Herbert"], [("Dune Chronicles", 1)]));

        var meta = await hc.Provider().GetByIdAsync("hardcover:5");

        Assert.Equal(["Frank Herbert", "Dune Chronicles"], meta.Lineage.Select(a => a.Name));
        Assert.StartsWith("hardcover:author:", meta.Lineage[0].ExternalId);
        Assert.StartsWith("hardcover:series:", meta.Lineage[1].ExternalId);
    }

    [Fact]
    public async Task AStandaloneBook_SitsDirectlyUnderItsAuthor()
    {
        var hc = new FakeHardcover();
        hc.Books.Add(new(6, "The Green Brain", 1966, "green-brain", ["Frank Herbert"]));

        var meta = await hc.Provider().GetByIdAsync("hardcover:6");

        Assert.Equal(["Frank Herbert"], meta.Lineage.Select(a => a.Name));
    }

    [Fact]
    public async Task ABookWithNoNamedAuthor_HasNoLineage_EvenInASeries()
    {
        var hc = new FakeHardcover();
        hc.Books.Add(new(7, "Orphan", 2000, "orphan", [], [("Some Series", 1)]));

        Assert.Empty((await hc.Provider().GetByIdAsync("hardcover:7")).Lineage);
    }
}
