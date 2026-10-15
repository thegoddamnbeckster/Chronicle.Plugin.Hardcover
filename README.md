# Chronicle.Plugin.Hardcover

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.Hardcover?label=Chronicle.Plugin.Hardcover&color=a0522d)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Hardcover/releases/latest)

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

### How a book is found (1.4.0)

Hardcover's own search returns nothing, so books are found by direct lookups, cheapest first, stopping at the first strong match:

1. the exact title (with the author, then without), then the title in Title Case;
2. the book's slug, **plain and with its year** (the plain `blood-world` is Chris Mooney's; B.V. Larson's is `blood-world-2017`);
3. a **title range restricted to the author**, which finds `Mind Hack: A Cyberpunk Saga` for `Mind Hack`;
4. **through the book's series**: the Hardcover series of that name lists its books under their full titles;
5. a title range on its own (a range, never `_ilike`: Hardcover has disabled `_ilike` before, and a refused operator opens this client's breaker for ten minutes).

What the library calls a book is cleaned first: a `(2023)` in the name is a year, a file-scan leftover (`- - (2014) - Dust World`) is a title and a year, `(Unabridged)` and `[Dramatized Adaptation]` are dropped, and a trailing series position is ignored on either side (`Blood World: Undying Mercenaries, Book 8`, `Rebel Fleet #4`).

Scoring: title exact 60, or the same title with a subtitle or series on one side 55 (`Mind Hack` / `Mind Hack: A Cyberpunk Saga`; never `Kiki` / `Kiki's Delivery Service`); year exact +20; **the library's series is one of the book's series +25**; author exact +20, with initials compared spaced or not (`B.V. Larson` is `B. V. Larson`). **A different author refuses the book unless its series agrees**: a shared universe (Four Horsemen, Birthright, Order of the Centurion) credits one author in the library and the real writers in Hardcover.

Series are found the same way (exact name, slug, the name-and-author slug Hardcover uses when the plain one is taken, names that begin with ours), ignoring `Series`, `Saga`, `Universe`, `#3` and volume ranges (`Hunger - 1-3`). Hardcover keeps empty entries beside real series (`Undying Mercenaries #8` with no books), so an entry with books is preferred, and **a series whose author differs is confirmed by the books the library already holds under it**.

Measured on 42 library items the older matcher had left not found, the live service gave a match for 35. After updating, press Reset on the Not Found tab of Hardcover's enrichment page to try them again.

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
