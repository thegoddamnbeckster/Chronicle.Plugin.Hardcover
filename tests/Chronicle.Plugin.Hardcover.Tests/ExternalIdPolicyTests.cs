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

    [Fact]
    public void Both_providers_use_the_standard_plugin_id_and_it_matches_the_manifest()
    {
        const string id = "chronicle.plugin.hardcover";
        Assert.Equal(id, new HardcoverMetadataProvider().PluginId);
        Assert.Equal(id, new HardcoverImportProvider().PluginId);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "manifest.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        Assert.Contains($"\"plugin_id\": \"{id}\"", File.ReadAllText(Path.Combine(dir!.FullName, "manifest.json")));
    }
}
