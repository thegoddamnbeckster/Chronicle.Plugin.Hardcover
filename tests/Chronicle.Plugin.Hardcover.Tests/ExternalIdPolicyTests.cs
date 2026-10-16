using Chronicle.Plugin.Hardcover;
using Xunit;

namespace Chronicle.Plugin.Hardcover.Tests;

public class ExternalIdPolicyTests
{
    [Fact]
    public void TheProviderDeclaresHowItsIdsBehave()
    {
        var p = new HardcoverMetadataProvider().GetExternalIdPolicy();
        Assert.Equal("hardcover", p.Source);
        Assert.Contains(p.SeriesIdMarker!, "hardcover:series:12");
    }
}
