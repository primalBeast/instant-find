# Instant Find

**Crowdstrike-friendly** instant filename/path search for Windows — inspired by the idea of Everything, built as a clean user-mode app.

Instant Find indexes your drives with ordinary .NET directory enumeration and SQLite FTS5, then searches as you type. No kernel drivers. No raw MFT or USN journal access. No packers or obfuscation.

## Download (no GitHub login required)

Public release zips are attached to [GitHub Releases](https://github.com/primalBeast/instant-find/releases):

| What | URL pattern |
|------|-------------|
| Latest release page | https://github.com/primalBeast/instant-find/releases/latest |
| Direct zip (once a tag exists) | `https://github.com/primalBeast/instant-find/releases/download/vX.Y.Z/InstantFind-win-x64.zip` |

Example after `v1.0.0` is published:

```text
https://github.com/primalBeast/instant-find/releases/download/v1.0.0/InstantFind-win-x64.zip
```

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

## Features

- Instant as-you-type search after the first index completes
- Results columns: **Name**, **Path**, **Size**, **Date Modified**
- Keyboard: type to search, `↓` into results, `Enter` open, `Ctrl+Enter` open containing folder, `Esc` back to search box
- Double-click opens the file (or folder)
- Filters:
  - Substring match (case-insensitive)
  - Wildcards `*` and `?`
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

1. **Tag** — push a version tag: `git tag v1.0.0 && git push origin v1.0.0`
2. **Manual** — Actions → **Build & Release** → **Run workflow** (optional version input)

Produces `InstantFind-win-x64.zip` on the Release assets.

## Solution layout

```text
InstantFind.sln
src/InstantFind/          # WPF app (.NET 8)
tests/InstantFind.Tests/  # xUnit query-matching tests
.github/workflows/        # win-x64 publish + Release upload
```

## License

MIT — see [LICENSE](LICENSE).

**Instant Find** is an independent project and is not affiliated with voidtools or Everything.
