using System.Text.Json;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Hardcover.Tests;

/// <summary>
/// Runs library items (from a JSON file) through the real Hardcover with the real plugin, and writes what each finds. Off unless
/// HARDCOVER_LIVE=1, HARDCOVER_TOKEN is set (never printed) and HARDCOVER_CASES / HARDCOVER_OUT name the files. Paced by the plugin's
/// own rate limiter.
/// </summary>
public class LiveSampleTests
{
    private sealed record Case(string Name, int Level, string? Parent, string? Grandparent, int? Year, string[]? Children, string Type, string? Previous);

    [Fact]
    public async Task RunTheSample()
    {
        if (Environment.GetEnvironmentVariable("HARDCOVER_LIVE") != "1") return;
        var token = Environment.GetEnvironmentVariable("HARDCOVER_TOKEN")!;
        var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(Environment.GetEnvironmentVariable("HARDCOVER_CASES")!),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var provider = new HardcoverMetadataProvider();
        provider.Configure(new Dictionary<string, string> { ["api_token"] = token });
        var results = new List<Dictionary<string, object?>>();
        foreach (var c in cases)
        {
            var ctx = new MediaSearchContext(c.Name, Year: c.Year, ParentName: c.Parent, GrandparentName: c.Grandparent,
                ChildCount: c.Children?.Length ?? 0, ChildNames: c.Children, HierarchyLevel: c.Level, MediaTypeName: c.Type, IsRealHierarchyPosition: true);
            try
            {
                var found = await provider.SearchAsync(ctx);
                var top = found.OrderByDescending(f => f.Score).FirstOrDefault();
                results.Add(new Dictionary<string, object?> { ["name"] = c.Name, ["level"] = c.Level, ["parent"] = c.Parent, ["grandparent"] = c.Grandparent, ["year"] = c.Year, ["previous"] = c.Previous,
                    ["id"] = top?.Metadata.ExternalId, ["title"] = top?.Metadata.Title, ["hcYear"] = top?.Metadata.Year, ["score"] = top?.Score, ["reason"] = top?.ScoreReason, ["accepted"] = top is { Score: >= 50 } });
            }
            catch (Exception ex)
            {
                results.Add(new Dictionary<string, object?> { ["name"] = c.Name, ["level"] = c.Level, ["error"] = ex.GetType().Name + ": " + ex.Message });
                if (ex is HttpRequestException) break;   // quota: stop rather than hammer
            }
            File.WriteAllText(Environment.GetEnvironmentVariable("HARDCOVER_OUT")!, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
