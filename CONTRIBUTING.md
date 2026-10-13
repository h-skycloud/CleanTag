# Contributing to CleanTag

## Development

CleanTag is an unpackaged WinUI 3 / .NET 10 desktop app for Windows x64.

```powershell
dotnet build CleanTag.sln -c Release -p:Platform=x64
dotnet run --project Tests\CleanTag.Tests.csproj -c Release
```

Launch the unpackaged executable after a successful build:

```powershell
.\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\CleanTag.exe
```

Use a throwaway catalog while testing UI:

```powershell
.\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\CleanTag.exe --data-dir <temp-data> --folder <sample-files>
```

## Rules

- Keep file contents on disk. The catalog stores metadata only.
- Do not delete files except through the explicit review flow.
- Tests must only delete files they created.
- Prefer WinUI controls and theme resources over custom chrome.
- Support light, dark, and high contrast.
