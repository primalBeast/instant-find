# Instant Find

**Crowdstrike-friendly** instant filename/path search for Windows. Built as a clean user-mode app — no kernel drivers, no raw MFT or USN journal access, no packers or obfuscation.

Instant Find indexes your drives with ordinary .NET directory enumeration and SQLite FTS5, then searches as you type.

## Download (no GitHub login required)

Public release zips are attached to [GitHub Releases](https://github.com/primalBeast/instant-find/releases):

| What | URL pattern |
|------|-------------|
| Latest release page | https://github.com/primalBeast/instant-find/releases/latest |
| Direct zip (v1.0.3) | https://github.com/primalBeast/instant-find/releases/download/v1.0.3/InstantFind-win-x64.zip |

1. Download `InstantFind-win-x64.zip`
2. Unzip anywhere (portable)
3. Run `InstantFind.exe`

No installer and no admin elevation required for normal use.

## Crowdstrike-safe design

| Allowed | Hard bans |
|---------|-----------|
| User-mode `Directory.EnumerateFileSystemEntries` | Kernel drivers / filter drivers |
| SQLite FTS5 index in `%AppData%\InstantFind` | Raw NTFS MFT parsing |
| `FileSystemWatcher` incremental updates | USN journal raw access |
| Standard Win32 open / Explorer select | Packers, obfuscators, injectors |
| Runs as the logged-in user | Anything requiring admin by design |

Settings and the index database live under:

```text
%AppData%\InstantFind\
  settings.json
  index.db
```

`maxResults` defaults to **10000** (editable in `settings.json` only — no settings UI). When the cap is hit, the status bar says the limit was reached so you can refine the query.

## Features (v1.0.3)

- Instant as-you-type search after the first index completes
- **Live results refresh**: when the index updates (create/change/delete/rename via FileSystemWatcher), an active query re-runs automatically (debounced ~350ms) so new files appear within ~1 second
- Compact search-bar tools (✕ / Aa / W) sized ~20–22px
- Search spinner while a query is in flight (search runs off the UI thread)
- **And / Or** checkboxes (right of title): default And; both off = literal whitespace (spaces must appear in the name)
- **Drive chips** (bottom right): toggle indexed drive letters (`C:`, `D:`, …). Off hides that drive from results instantly and excludes it from Rebuild Index. No new per-drive watchers.
- Search bar tools: **✕** clear + refocus, **Aa** Match Case, **W** Whole Word, then the query box
- Match Case or Whole Word leaves FTS and uses SQL `LIKE` / `=` (still capped by `maxResults`)
- Results columns: **Name**, **Path**, **Size**, **Date Modified**
- Context menu: Open, Open Containing Folder, **Edit with Notepad++** (files only; soft-fails if Notepad++ is missing)
- Keyboard: type to search, `↓` into results, `Enter` open, `Ctrl+Enter` open containing folder, `Esc` back to search box
- Filters:
  - Substring match via FTS5 (case-insensitive, unless Match Case / Whole Word)
  - Wildcards `*` and `?` via SQL `LIKE` (e.g. `D*.pdf`, `*.pdf`, `test?.txt`)
  - Extension filter: `ext:pdf` (e.g. `invoice ext:pdf`)
- Parallel multi-root indexing with progress status
- Skips inaccessible directories instead of failing
- Rebuild Index / Cancel from the toolbar

## Build from source

Requirements: Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/primalBeast/instant-find.git
cd instant-find
dotnet restore InstantFind.sln
dotnet test InstantFind.sln -c Release
dotnet publish src\InstantFind\InstantFind.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\InstantFind
```

Run:

```powershell
.\publish\InstantFind\InstantFind.exe
```

> WPF targets Windows. Linux/macOS agents can edit and push source; **build and test run on Windows** (local or GitHub Actions `windows-latest`).

## Release via GitHub Actions

Workflow: [`.github/workflows/release.yml`](.github/workflows/release.yml)

Triggers:

1. **Tag** — push a version tag: `git tag v1.0.3 && git push origin v1.0.3`
2. **Manual** — Actions → **Build & Release** → **Run workflow** with `version=1.0.3`

Produces `InstantFind-win-x64.zip` on the Release assets.

## Solution layout

```text
InstantFind.sln
src/InstantFind/          # WPF app (.NET 8)
tests/InstantFind.Tests/  # xUnit query-matching / mode tests
.github/workflows/        # win-x64 publish + Release upload
```

## License

MIT — see [LICENSE](LICENSE).

**Instant Find** is an independent project and is not affiliated with voidtools.
