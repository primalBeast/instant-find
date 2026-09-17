# Instant Find

**Crowdstrike-friendly** instant filename/path search for Windows. Built as a clean user-mode app — no kernel drivers, no raw MFT or USN journal access, no packers or obfuscation.

Instant Find indexes your drives with ordinary .NET directory enumeration and SQLite FTS5, then searches as you type.

## Download (no GitHub login required)

Public release zips are attached to [GitHub Releases](https://github.com/primalBeast/instant-find/releases):

| What | URL pattern |
|------|-------------|
| Latest release page | https://github.com/primalBeast/instant-find/releases/latest |
| Direct zip (v1.0.7) | https://github.com/primalBeast/instant-find/releases/download/v1.0.7/InstantFind-win-x64.zip |

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

## Features (v1.0.7)

- **Regex toggle (`.*`) (v1.0.7)**: compact button to the right of **W** in the search bar. When on, the query body (after any path-scope prefix) is a **.NET regex** matched against the **filename** first; if the pattern contains `\` or `/`, the full path is also tried. Case follows **Aa**. Leaves FTS; filtered in memory/SQL and still capped by `maxResults`. Persisted in `settings.json` (`useRegex`, default off).
- **Path scope (v1.0.7)**: if the query starts with a Windows path ending in `\` (e.g. `C:\Work\`, `C:\Projects\*.pdf`, `C:\Projects\ budget`), that prefix scopes results to that directory (prefix match; case follows **Aa**). Remaining text is the search terms / regex body. Path-only (`C:\Projects\`) lists under that folder (still MaxResults). Drive chips still apply as an additional filter.
- **Open with… (v1.0.7)**: context menu → **Open with…** → **Favorite apps** (recent exe paths, last 8, in `settings.json`) and **Choose another app…** (OpenFileDialog starting in Program Files). Files only; soft-fails if the exe is missing.
- **Drag results out (v1.0.7)**: drag a result row from the grid to Explorer / other drop targets (OLE `FileDrop` with the real path). Uses the selected row.
- Instant as-you-type search after the first index completes
- **Live results refresh**: when the index updates (create/change/delete/rename via FileSystemWatcher), an active query re-runs automatically (debounced ~350ms) so new files appear within ~1 second — **silently** (no spinner / no “Searching…” flicker; status updates to the final count only)
- **Stable context menu (v1.0.5)**: silent refresh skips Clear/rebuild when the hit set is unchanged, preserves selection when it changes, and pauses while the context menu is open (plus a right-click `ContextTarget`) so Open / folder / Notepad++ / CMD / Git Bash stay enabled
- **And / Or in search bar (v1.0.5)**: moved from the title row into the search field row (right-justified, before the spinner)
- Compact search-bar tools: **Aa** / **W** use MinWidth + padding so glyphs are not clipped; ✕ stays small
- **Circular busy spinner** in the search bar for **user-initiated** searches only (typing / And-Or / MatchCase / WholeWord / Clear / drive chips) — not for watcher refreshes
- **And / Or** checkboxes (right side of the search bar, before the spinner): default And; both off = literal whitespace (spaces must appear in the name)
- **Drive chips** (bottom right): toggle indexed drive letters (`C:`, `D:`, …). Off hides that drive from results instantly and excludes it from Rebuild Index. No new per-drive watchers.
- Search bar tools: **✕** clear + refocus, **Aa** Match Case, **W** Whole Word, `.*` Regex, then the query box
- Match Case, Whole Word, Regex, or path-scope leaves FTS and uses SQL `LIKE` / in-memory filter (still capped by `maxResults`)
- Results columns: **Name**, **Path**, **Size**, **Date Modified** (Size/Date from index, not live disk)
- Context menu: Open, Open Containing Folder, **Open with…** (favorites + choose .exe), **Edit with Notepad++** (files only; soft-fails if Notepad++ is missing), **Open path in Command Prompt**, **Open path in Git Bash** (soft-fails if Git Bash is missing)
- Keyboard: type to search, `↓` into results, `Enter` open, `Ctrl+Enter` open containing folder, `Esc` back to search box
- Filters:
  - Substring match via FTS5 (case-insensitive, unless Match Case / Whole Word / Regex)
  - Wildcards `*` and `?` via SQL `LIKE` (e.g. `D*.pdf`, `*.pdf`, `test?.txt`)
  - Extension filter: `ext:pdf` (e.g. `invoice ext:pdf`)
  - Regex (`.*` toggle): .NET regex against filename (see above)
  - Path scope: leading `X:\dir\` prefix restricts hits to that directory
- Parallel multi-root indexing with progress status
- Skips inaccessible directories instead of failing
- **Size & Date Modified (v1.0.6)**: shown from the **SQLite index** (set at crawl / FileWatcher `IndexSinglePath`), not live disk on every search. Silent refresh and re-search now update Size/Modified **in place** when paths are unchanged but metadata changed (so the grid repaints without clearing selection / context menu)
- **Morphing Rebuild/Cancel (v1.0.6)**: one header button — idle shows **Rebuild Index** (accent) with Yes/No confirm; while indexing it becomes **Cancel** (danger outline) and stops the rebuild; returns to Rebuild Index when done or cancelled
- Rebuild Index from the toolbar (with confirm)

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

1. **Tag** — push a version tag: `git tag v1.0.7 && git push origin v1.0.7`
2. **Manual** — Actions → **Build & Release** → **Run workflow** with `version=1.0.7`

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
