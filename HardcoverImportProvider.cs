using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Hardcover;

/// <summary>
/// Chronicle import plugin for Hardcover.app.
///
/// Hardcover uses a GraphQL API with simple Bearer token authentication.
/// Users generate their API token at hardcover.app/account/api and paste it
/// into the plugin settings — no OAuth device flow is required.
///
/// Reading history status mapping (Hardcover → Chronicle):
///   Status 3 (Read)             → LibraryStatus.Completed  (ImportedWatchEvent)
///   Status 1 (Want to Read)     → LibraryStatus.PlanToWatch (ImportedWatchlistEntry)
///
/// Rating conversion:
///   Hardcover 0.5–5 stars × 2 = Chronicle 1–10 scale
///   (e.g. ★★★ = 3.0 → 6 in Chronicle)
/// </summary>
public sealed class HardcoverImportProvider : IImportProvider
{
    // ── IImportProvider identity ──────────────────────────────────────────────

    public string PluginId    => "hardcover";
    public string Name        => "Hardcover";
    public string Version     => "1.0.0";
    public string Author      => "Michael Beck";
    public string Description =>
        "Import reading history, ratings and want-to-read list from Hardcover.app via GraphQL";

    // ── Settings keys ─────────────────────────────────────────────────────────

    private const string KeyApiToken = "api_token";

    // ── Runtime state ─────────────────────────────────────────────────────────

    private string?           _apiToken;
    private HardcoverClient?  _client;

    // ── Settings schema ───────────────────────────────────────────────────────

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = KeyApiToken,
                Label       = "Hardcover API Token",
                Description = "Your personal API token from hardcover.app/account/api. " +
                              "This is never shared — it is stored encrypted in Chronicle's database.",
                Type        = SettingType.Password,
                Required    = true,
            },
        ]
    };

    // ── Configure ─────────────────────────────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue(KeyApiToken, out _apiToken);
        _apiToken = _apiToken?.Trim();

        _client?.Dispose();
        _client = null; // Re-created lazily with new token
    }

    // ── Auth — API key only (no device flow) ──────────────────────────────────

    public Task<DeviceAuthStart> StartAuthAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Hardcover uses API key authentication, not a device flow. " +
            "Generate your token at hardcover.app/account/api and paste it " +
            "into Plugins → Hardcover → Settings → API Token.");

    public Task<DeviceAuthPollResult> PollAuthAsync(
        string pollCode, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Hardcover does not use a device code / poll flow.");

    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiToken)) return false;
        try
        {
            var me = await GetOrCreateClient().GetMeAsync(ct);
            return me?.Me is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    public ImportCapabilities GetCapabilities() =>
        new(SupportsHistory: true, SupportsRatings: true, SupportsWatchlist: true,
            RequiresDeviceAuth: false);

    // ── Import — history ──────────────────────────────────────────────────────

    public async Task<List<ImportedWatchEvent>> GetWatchHistoryAsync(
        DateTimeOffset? since = null, CancellationToken ct = default)
    {
        EnsureToken();
        var data = await GetOrCreateClient().GetReadBooksAsync(ct);
        var books = data?.UserBooks ?? [];

        var result = new List<ImportedWatchEvent>();
        foreach (var ub in books)
        {
            if (ub.Book is null) continue;

            // Determine watch date: use the most recent finished_at, then inserted_at, then now
            var finishedStr = ub.Reads?.FirstOrDefault()?.FinishedAt;
            var watchedAt   = ParseDate(finishedStr) ?? DateTimeOffset.UtcNow;

            if (since.HasValue && watchedAt < since.Value) continue;

            result.Add(new ImportedWatchEvent(
                ExternalId:      $"hardcover:{ub.Book.Id}",
                AdditionalIds:   BuildIds(ub),
                MediaType:       "book",
                Title:           ub.Book.Title,
                Year:            ub.Book.ReleaseYear,
                WatchedAt:       watchedAt,
                ProgressPercent: 100.0
            ));
        }

        return result;
    }

    // ── Import — ratings ──────────────────────────────────────────────────────

    public async Task<List<ImportedRating>> GetRatingsAsync(CancellationToken ct = default)
    {
        EnsureToken();
        var data = await GetOrCreateClient().GetRatingsAsync(ct);
        var books = data?.UserBooks ?? [];

        var result = new List<ImportedRating>();
        foreach (var ub in books)
        {
            if (ub.Book is null || !ub.Rating.HasValue) continue;

            // Hardcover 0.5–5 → Chronicle 1–10 (multiply by 2, round, clamp)
            var chronicleRating = (int)Math.Round(ub.Rating.Value * 2, MidpointRounding.AwayFromZero);
            chronicleRating = Math.Clamp(chronicleRating, 1, 10);

            var ratedAt = ParseDate(ub.InsertedAt) ?? DateTimeOffset.UtcNow;

            result.Add(new ImportedRating(
                ExternalId:    $"hardcover:{ub.Book.Id}",
                AdditionalIds: BuildIds(ub),
                MediaType:     "book",
                Title:         ub.Book.Title,
                Year:          ub.Book.ReleaseYear,
                Rating:        chronicleRating,
                RatedAt:       ratedAt));
        }

        return result;
    }

    // ── Import — watchlist ────────────────────────────────────────────────────

    public async Task<List<ImportedWatchlistEntry>> GetWatchlistAsync(CancellationToken ct = default)
    {
        EnsureToken();
        var data = await GetOrCreateClient().GetWantToReadAsync(ct);
        var books = data?.UserBooks ?? [];

        var result = new List<ImportedWatchlistEntry>();
        foreach (var ub in books)
        {
            if (ub.Book is null) continue;

            var addedAt = ParseDate(ub.InsertedAt) ?? DateTimeOffset.UtcNow;

            result.Add(new ImportedWatchlistEntry(
                ExternalId:    $"hardcover:{ub.Book.Id}",
                AdditionalIds: BuildIds(ub),
                MediaType:     "book",
                Title:         ub.Book.Title,
                Year:          ub.Book.ReleaseYear,
                AddedAt:       addedAt));
        }

        return result;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default) =>
        await IsAuthenticatedAsync(ct);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, string> BuildIds(UserBook ub)
    {
        var d = new Dictionary<string, string>();
        if (ub.Book?.Id > 0)
            d["hardcover"] = ub.Book.Id.ToString();

        var mapping = ub.Book?.BookMappings?.FirstOrDefault();
        if (mapping?.Isbn13 is not null) d["isbn13"] = mapping.Isbn13;
        if (mapping?.Isbn10 is not null) d["isbn"]   = mapping.Isbn10;

        return d;
    }

    private static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, out var d) ? d : null;
    }

    private void EnsureToken()
    {
        if (string.IsNullOrWhiteSpace(_apiToken))
            throw new InvalidOperationException(
                "Hardcover API token is not configured. " +
                "Get it at hardcover.app/account/api and set it in " +
                "Plugins → Hardcover → Settings.");
    }

    private HardcoverClient GetOrCreateClient() =>
        _client ??= new HardcoverClient(_apiToken!);
}
