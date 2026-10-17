using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Hardcover.Tests;

public class AuthorAliasTests
{
    [Fact]
    public async Task AnAuthor_ListsThePenNamesHardcoverKeepsAsAuthorsOfTheirOwn_BesideItsAlternateNames()
    {
        // hardcover.app/authors/jeremy-robinson shows "Aliases: Jeremy Bishop": a separate author record whose alias_id is his.
        var hc = new FakeHardcover();
        hc.AuthorList.Add(new(131566, "Jeremy Robinson", ["J. Robinson"]));
        hc.AuthorList.Add(new(108363, "Jeremy Bishop", null, AliasOf: 131566));
        hc.AuthorList.Add(new(5, "Someone Else"));

        var meta = await hc.Provider().GetByIdAsync("hardcover:author:131566");

        Assert.Equal("Jeremy Robinson", meta.Title);
        Assert.Equal(["J. Robinson", "Jeremy Bishop"], meta.AlternateNames);
        Assert.Contains("GetAuthorAliases", hc.Operations);
    }

    [Fact]
    public async Task AnAuthorWithNoAliases_IsJustTheAuthor()
    {
        var hc = new FakeHardcover();
        hc.AuthorList.Add(new(5, "Someone Else"));

        var meta = await hc.Provider().GetByIdAsync("hardcover:author:5");

        Assert.Empty(meta.AlternateNames);
    }

    [Fact]
    public async Task WhenHardcoverRefusesTheAliasQuery_TheAuthorStillComesBack_AndItIsNotAskedAgain()
    {
        var hc = new FakeHardcover { RefuseAliasQuery = true };
        hc.AuthorList.Add(new(1, "First", ["F."]));
        hc.AuthorList.Add(new(2, "Second"));
        var provider = hc.Provider();

        var first = await provider.GetByIdAsync("hardcover:author:1");
        var second = await provider.GetByIdAsync("hardcover:author:2");

        Assert.Equal("First", first.Title);
        Assert.Equal(["F."], first.AlternateNames);
        Assert.Equal("Second", second.Title);
        Assert.Equal(1, hc.Operations.Count(o => o == "GetAuthorAliases"));
    }
}
