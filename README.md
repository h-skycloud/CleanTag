# CleanTag

Windows için yerel dosya inceleme ve etiketleme uygulaması. Dosyaları önizleyin, tutun veya silin, ardından etiketlerle yeniden bulun. Dosyalar diskte oldukları yerde kalır; CleanTag yalnızca metadata tutar.

Local Windows app for reviewing files with a real preview, then tagging them so you can find them again. Files stay where they are. The catalog never copies file contents.

![.NET](https://img.shields.io/badge/.NET-10-512BD4) ![WinUI](https://img.shields.io/badge/WinUI-3-0078D4) ![License](https://img.shields.io/badge/license-MIT-green)

## Features

- **Gelenler / Inbox** — card-based review: keep, recycle, or defer with keyboard, buttons, or swipe. Confirm before delete.
- **Kütüphane / Library** — search and filter tagged files and folders across registered locations.
- **Tags** — names, colors, groups, aliases, bulk add/remove, undo. File tags and folder tags are separate modes.
- **Preview** — images, SVG, PDF pages, audio/video, text, Office/OpenDocument extracts, archives, and Windows thumbnail fallbacks.
- **Safety** — Recycle Bin by default, optional permanent delete (off each launch), skip changed/missing files, never upload files.

## Requirements

- Windows 10 1809 or later (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source

## Build

```powershell
dotnet build CleanTag.sln -c Release -p:Platform=x64
dotnet run --project Tests\CleanTag.Tests.csproj -c Release
dotnet publish CleanTag.csproj -c Release -p:Platform=x64 -o publish
```

The unpackaged output is self-contained. Keep the published folder together when you move it.

```powershell
.\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\CleanTag.exe
```

## Usage

1. Open CleanTag and choose a folder. Nested files are scanned; large files sort first in review.
2. Preview the current file. **Left / Recycle** sends it to the Recycle Bin. **Right / Keep** remembers the decision. **Defer** brings it back at the end of the session.
3. Tag while reviewing, or switch to **Library** for gallery/list search, filters, and bulk tags.
4. Add more locations from Library. File tagging scans files; folder tagging can register a folder without scanning every file inside.

Arrow keys map to review actions unless you are typing in a text field. **Ask before delete** is on by default. Permanent delete is off at every launch and is not secure erasure.

## Data

Catalog path: `%LOCALAPPDATA%\CleanTag\catalog.sqlite`

If you previously used LookClean, CleanTag copies that catalog on first run and then uses the CleanTag folder. File contents are not stored. Use Library backup/restore/export for metadata only.

The first-run migration uses a consistent SQLite backup, including committed WAL data while the old app is open. The old folder is preserved and an existing CleanTag catalog is never overwritten. Previous JSON-only history is also supported. Failed migration keeps the startup error visible and can be retried. Technical basis: [Microsoft.Data.Sqlite online backup](https://learn.microsoft.com/dotnet/standard/data/sqlite/backup).

## Project status

CleanTag 0.1.0 is an early Windows x64 release. The current UI is Turkish; English localization, a signed installer, and automatic updates are future work. Published executables are currently unsigned. Preview support depends on Windows codecs and thumbnail providers; Office previews are extracts rather than full page layout. Folder tags do not propagate to their contents.

GitHub Actions builds the solution, runs the checks and produces a downloadable `CleanTag-windows-x64.zip` artifact. Extract the complete archive and launch `CleanTag.exe`. The local test suite includes native recycle and real filesystem watcher checks and operates only on fixtures it creates.

Isolated UI testing:

```text
CleanTag.exe --data-dir <data-folder> --folder <sample-files> [--library]
```

## Architecture

| Project | Role |
| --- | --- |
| `CleanTag` | WinUI shell, review, library, preview, tagging UI |
| `CleanTag.Core` | scan, SQLite catalog, identity, monitoring, preview readers |
| `CleanTag.Tests` | catalog, scan, history, recycle, and watcher checks |

Packaging model: unpackaged, Windows App SDK self-contained.

## License

MIT. See [LICENSE](LICENSE). Preview of archives uses [SharpCompress](https://github.com/adamhathcock/sharpcompress).
