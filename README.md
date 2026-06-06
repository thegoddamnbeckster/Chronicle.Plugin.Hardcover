# Chronicle.Plugin.Hardcover

Book and audiobook metadata plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Fetches book metadata — titles, overviews, cover art, authors, series, ratings — from [Hardcover.app](https://hardcover.app/) via the Hardcover GraphQL API. Also imports your reading history, ratings, and want-to-read list.

**Plugin ID:** `hardcover`
**Version:** 1.1.3
**Auth:** Hardcover API key (free account)

---

## Supported Media Types

| Media Type | Fields |
|------------|--------|
| `books` | title, overview, year, poster_url (cover art), genres, cast (author, narrator), rating, series |
| `audiobooks` | title, overview, year, poster_url, cast (author, narrator), series |

## External ID Format

| Format | Refers to |
|--------|-----------|
| `hardcover:{id}` | A specific book (e.g. `hardcover:142857`) |
| `hardcover:series:{id}` | A book series (e.g. `hardcover:series:4321`) |
| `hardcover:author:{id}` | An author (e.g. `hardcover:author:9999`) |

Fix Match accepts full Hardcover URLs:
- `https://hardcover.app/books/the-way-of-kings`
- `https://hardcover.app/series/the-stormlight-archive`
- `https://hardcover.app/authors/brandon-sanderson`

---

## Features

- **Metadata enrichment** — covers, overviews, publication year, genres, authors, narrators, series position
- **Reading history import** — syncs finished books with completion dates
- **Ratings import** — syncs your Hardcover star ratings (1–5 → scaled to 1–10)
- **Want-to-read import** — items on your Hardcover Want to Read list imported as *Plan to Read*
- **Delta sync** — only fetches activity since the last sync using `updated_at` timestamps
- **Series hierarchy** — books with a series are grouped under the series as a parent item

---

## Setup

1. Log into [Hardcover.app](https://hardcover.app/) and go to **Settings → API**.
2. Copy your API token.
3. In Chronicle → **Plugins**, install this plugin and enter your API token under Settings.

No OAuth flow — a single API token is all you need.

---

## Configuration

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `api_key` | ✓ | — | Your Hardcover API token from hardcover.app/settings |

---

## Background Tasks

| Task | Default Schedule | Purpose |
|------|-----------------|---------|
| `fetch-missing-metadata` | Daily 4:00 UTC | Enriches newly imported books/audiobooks that have no metadata yet |
| `resync-all-metadata` | Weekly Sunday 3:00 UTC (disabled) | Re-downloads all Hardcover metadata |
| `import-all` | Disabled | One-time full import of your reading history, ratings, and want-to-read list |
| `delta-sync` | Hourly | Imports reading activity added since the last sync |

Run **Import All** first after connecting your account, then let **Delta Sync** keep it current.

---

## Repository Structure

```
Chronicle.Plugin.Hardcover/
├── Chronicle.Plugin.Hardcover.csproj
├── manifest.json
├── HardcoverMetadataProvider.cs   # IMetadataProvider implementation
├── HardcoverImportProvider.cs     # IImportProvider implementation (reading history)
├── HardcoverClient.cs             # GraphQL client
└── HardcoverModels.cs             # GraphQL response models
```

---

## Building

```powershell
dotnet build -c Release
```

Copy output to your Chronicle plugin directory:

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\hardcover"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\*.dll" $pluginDir
Copy-Item "manifest.json"           $pluginDir
```

> **Important:** `Chronicle.Plugins.dll` must **not** be in the plugin directory — Chronicle provides it. The `.csproj` sets `<Private>false</Private>` on the Chronicle.Plugins reference to ensure this.

---

## Development

The plugin references `Chronicle.Plugins` via a local project reference:

```xml
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
```

Both repositories must be cloned as siblings for this to resolve:

```
<base>\
  Chronicle\
  Chronicle.Plugin.Hardcover\
```

---

## License

MIT
