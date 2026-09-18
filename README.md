# Instant Find

**Crowdstrike-friendly** instant filename/path search for Windows. Built as a clean user-mode app — no kernel drivers, no raw MFT or USN journal access, no packers or obfuscation.

Instant Find indexes your drives with ordinary .NET directory enumeration and SQLite FTS5, then searches as you type.

## Download (no GitHub login required)

Public release zips are attached to [GitHub Releases](https://github.com/primalBeast/instant-find/releases):

| What | URL pattern |
|------|-------------|
| Latest release page | https://github.com/primalBeast/instant-find/releases/latest |
| Direct zip (v1.0.18) | https://github.com/primalBeast/instant-find/releases/download/v1.0.18/InstantFind-win-x64.zip |

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
  index-rebuild.db   (temp during rebuild; discarded if leftover)
```

**Indexed roots**: only physical local Fixed/Removable volumes. Never indexed (pruned from `indexedRoots` / chips on load):

- Mapped network drives (`DriveType.Network`) and UNC (`\\server\share`)
- **Cloud-mapped letters** that often report as Fixed — Google Drive for desktop / File Stream (e.g. `G:`), OneDrive, Dropbox, and similar (volume label + root folder markers)

`InstantFind-error.log` is written **next to `InstantFind.exe`** (portable zip folder) on index failures and notable sharing-violation soft-skips.

`maxResults` defaults to **10000** (editable in `settings.json` only — no settings UI). When the cap is hit, the status bar says the limit was reached so you can refine the query.

## Features (v1.0.18)

- **Close-before-swap (v1.0.18)**: Rebuild fully checkpoints/closes/disposes the live SQLite connection (and clears pools; pooling off) **before** `File.Replace` of `index.db`, and deletes `-wal`/`-shm` alongside. Longer swap retries (20×100ms) for AV. Soft-skip / cloud-drive exclude / error log unchanged.
- **Error log beside exe (v1.0.17)**: On index failure (and soft-skip summaries / sharing violations), appends `InstantFind-error.log` next to `InstantFind.exe` with UTC time, version, full exception (type/message/HResult/stack), failed path, live vs rebuild DB paths, second-instance hint, and skip counts. Paste that log when reporting index issues.
- **DB lock harden (v1.0.17)**: SQLite `busy_timeout`, open/swap/delete retries with backoff, watchers+prune paused during rebuild, concurrent Rebuild ignored. If live/rebuild `File.Replace` still fails after retries, **previous index is kept** and status points at the error log (no cryptic bare sharing message only).
- **No cloud-mapped letters (v1.0.17)**: Google Drive for desktop / File Stream, OneDrive, Dropbox, etc. are excluded even when `DriveType` is Fixed (label + root markers via `DriveHelpers.IsIndexableLocalDrive`). Saved `IndexedRoots` / chips pruned on load.
- **Skip locked files (v1.0.16)**: Rebuild no longer hard-fails when a content file is locked (`IOException` / sharing violation / access denied). Crawl skips those entries and continues; status can show `skipped N locked`.
- **No network / mapped drives (v1.0.16)**: Defaults, EnsureDefaults, Rebuild, and drive chips exclude `DriveType.Network` mapped letters and UNC roots (`\\server\share`).

- **Exclude (v1.0.15)**: Filter popup → Exclude. Common folders with checkboxes (defaults: Windows, Program Files, Recycle Bin, System Volume Information, Recovery) plus custom folders via **+**. Search hides them; rebuild skips them.

- **Help (v1.0.15)**: `?` button top-right opens a themed Help window (Esc to close). Drive chip tooltips say “toggle drive in results” only.

- **NOT / Filter popup / size·date / macros / columns (v1.0.15)**:
  - **NOT**: Everything-style `!term` / `!*.tmp` excludes matches (works with And/Or, path scope, macros).
  - **Filter** button (left of And/Or): anchored popup (app theme) with Size / Date modified / Type / Saved filters. Active filters highlight the button; **Clear filters** removes `size:`, `dm:`, and type macros.
  - **Size query**: `size:>1mb`, `size:<100kb`, `size:1mb..10mb` (units b/kb/mb/gb).
  - **Date modified**: `dm:today`, `dm:yesterday`, `dm:thisweek`, `dm:thismonth`, `dm:thisyear`, `dm:2024-01-01..2024-12-31`, `dm:>2024-06-01`.
  - **Type macros**: `doc:`, `img:`/`image:`, `vid:`/`video:`, `audio:`, `zip:`/`archive:`, `code:`, `xls:`, `ppt:`, `exe:`, `font:`, `iso:` — expand to common extension sets (same as Filter → Type).
  - **Saved filters**: name + save current query; one-click activate; persisted in `settings.json`.
  - **Extra columns**: context menu → Show Extension / Show Attributes (R/H/S/A/… from index or live `File.GetAttributes`).
- **Path scope + path term spaces (v1.0.12)**: `C:\Logs  \72` (spaces, no trailing `\` after the folder) scopes to `C:\Logs\` and applies Everything-like segment prefix `\72`. Also `C:\Logs\`, `C:\Logs\ \72` / `C:\Logs\\72`, global `\72`, and normal name search under a scoped path. Search always completes (spinner off); silent refresh no longer orphans in-flight searches.
- **Path term `\72` (v1.0.11)**: Everything-like — a term starting with `\` matches a **path/directory segment** that **starts with** the rest (e.g. `\72` hits `C:\data\72folder\…` and `C:\docs\72report.txt`, but not bare-name FTS that ignores `\` and would match `6728` / `x72y`). Leaves FTS; SQL `LIKE` on `path` only. Drive path-scope unchanged: `C:\foo\` still scopes.
- **App icon (v1.0.10)**: Windows exe / window icon from `Assets/app.ico` (multi-size). Source art: ![Instant Find icon](src/InstantFind/Assets/app-icon.png)
- **Literal `_` / `-` in search (v1.0.10)**: FTS5 `unicode61` treats `_` and `-` (and other non-alphanumeric characters) as token separators, so a plain FTS query like `72_` previously matched names such as `6728`. Terms containing those characters now **leave FTS** and use SQL `LIKE` with `ESCAPE '\'` (same as wildcards), so `72_` requires a literal underscore and `a-b` requires a literal hyphen.
- **Atomic rebuild + delete prune (v1.0.9)**: Rebuild writes to a temporary `index-rebuild.db` beside the live index and **atomically swaps** only on success. Cancel or crash mid-rebuild **keeps the previous live index** (leftover rebuild files are discarded on next launch). Progress shows **Preparing rebuild…** / **Scanning… N items — path** immediately (no stuck “Starting index…”). Cancel is cooperative; UI shows **Indexing cancelled — previous index kept.** and **restarts watchers**. Deleted/renamed folders remove rows by `path` and by `directory` (exact + under). Watcher buffer overflows schedule a **background prune** of missing paths; search refresh also drops hits that no longer exist on disk.
- **Search perf + regex fixes (v1.0.8)**: path scope no longer forces a slow LIKE path — FTS stays on with a SQL path prefix filter. Only Match Case / Whole Word / wildcards / Regex leave FTS. Plain FTS skips redundant per-row Matches. `INDEX` on `files(path)`. Regex: shell-glob fallback so `*.pdf` with Regex on finds files; longest alphanumeric literal used as a SQL `LIKE` prefilter; invalid patterns (e.g. `(?`) show status **Invalid regex** (no crash). Scope rule: only when the path prefix **ends with `\`** — `C:\Projects` is a normal FTS term; `C:\Projects\` / `C:\Projects\foo` / `C:\` scope as expected.
- **Regex toggle (`.*`) (v1.0.7+)**: compact button to the right of **W**. Query body is a **.NET regex** against the **filename** (path too if the pattern has `\`/`/`). If compile fails and the pattern looks like a shell glob (`*`/`?` without `(`, `[`, or `{`), converts glob→regex. Case follows **Aa**. Leaves FTS; capped by `maxResults`. Persisted (`useRegex`, default off).
- **Path scope (v1.0.7+ / v1.0.12)**: leading Windows path **ending in `\`** scopes results (e.g. `C:\Work\`, `C:\Projects\*.pdf`). `C:\Projects` (no trailing `\`) is **not** a scope — **except** when followed by whitespace and a path term (`C:\Logs  \72` → scope `C:\Logs\` + `\72`, v1.0.12). Path-only (`C:\Projects\`) lists under that folder. Kept with FTS when possible (v1.0.8). Drive chips still apply.
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
- Match Case, Whole Word, wildcards, Regex, or terms with FTS separators (`_` / `-` / other non-alphanumeric) leave FTS and use SQL `LIKE` / in-memory filter (still capped by `maxResults`). Path scope alone keeps FTS + SQL path prefix when terms are FTS-safe (v1.0.8 / v1.0.10)
- Results columns: **Name**, **Path**, **Size**, **Date Modified** (Size/Date from index); optional **Extension** / **Attributes** via context menu
- Context menu: Open, Open Containing Folder, **Open with…** (favorites + choose .exe), **Edit with Notepad++** (files only; soft-fails if Notepad++ is missing), **Open path in Command Prompt**, **Open path in Git Bash** (soft-fails if Git Bash is missing)
- Keyboard: type to search, `↓` into results, `Enter` open, `Ctrl+Enter` open containing folder, `Esc` back to search box
- Filters:
  - Substring match via FTS5 (case-insensitive, unless Match Case / Whole Word / Regex)
  - Wildcards `*` and `?` via SQL `LIKE` (e.g. `D*.pdf`, `*.pdf`, `test?.txt`)
  - NOT: `!term` / `!*.tmp` excludes matches
  - Extension filter: `ext:pdf` (e.g. `invoice ext:pdf`)
  - Type macros: `doc:`, `img:`, `vid:`, `audio:`, `zip:`, `code:`, …
  - Size: `size:>1mb`, `size:1mb..10mb`
  - Date modified: `dm:today`, `dm:thisyear`, `dm:2024-01-01..2024-12-31`
  - Regex (`.*` toggle): .NET regex against filename (see above)
  - Path scope: leading `X:\dir\` prefix restricts hits to that directory
  - Path term: leading `\` (e.g. `\72`) matches path segments that start with the rest
  - Filter popup (search bar) for size / date / type / saved filters
- Parallel multi-root indexing with progress status
- Skips inaccessible / locked files and directories instead of failing the whole rebuild (v1.0.16)
- **Size & Date Modified (v1.0.6)**: shown from the **SQLite index** (set at crawl / FileWatcher `IndexSinglePath`), not live disk on every search. Silent refresh and re-search now update Size/Modified **in place** when paths are unchanged but metadata changed (so the grid repaints without clearing selection / context menu)
- **Morphing Rebuild/Cancel (v1.0.6)**: one header button — idle shows **Rebuild Index** (accent) with Yes/No confirm; while indexing it becomes **Cancel** (danger outline) and stops the rebuild; returns to Rebuild Index when done or cancelled
- Rebuild Index from the toolbar (with confirm)

## Query syntax (v1.0.18)

| Syntax | Meaning |
|--------|---------|
| `report budget` | And (default): all terms |
| `report` + Or checked | Any term |
| `!temp` / `report !*.tmp` | Exclude term / wildcard |
| `ext:pdf` | Extension filter |
| `doc:` `img:` `vid:` `audio:` `zip:` `code:` … | Type macros → extension sets |
| `size:>1mb` `size:<100kb` `size:1mb..10mb` | Size filter (files only) |
| `dm:today` `dm:thisweek` `dm:thisyear` | Date-modified presets |
| `dm:2024-01-01..2024-12-31` | Date-modified range |
| `C:\Work\` | Path scope (trailing `\`) |
| `\72` | Path segment prefix (Everything-like) |
| `.*` toggle | .NET regex on filename |

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

1. **Tag** — push a version tag: `git tag v1.0.18 && git push origin v1.0.18`
2. **Manual** — Actions → **Build & Release** → **Run workflow** with `version=1.0.18`

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
