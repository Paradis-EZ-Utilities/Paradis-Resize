# Build and publish

Quick reference. What the app does and why is in [README.md](README.md) and
[DECISIONS.md](DECISIONS.md).

## Prerequisites

- **.NET 10 SDK** — check with `dotnet --version` (needs 10.0 or later).
  Install from https://dotnet.microsoft.com/download/dotnet/10.0, or with
  `winget install Microsoft.DotNet.SDK.10`.
- **64-bit Windows 10 or 11** to run it.
- Optional: Visual Studio 2022 (17.14+) or 2026 with the *.NET desktop development* workload.

libvips comes from NuGet (`NetVips.Native.win-x64`); nothing else needs installing.

## Build, run, test

```powershell
dotnet build
dotnet run --project src/ParadisResize
dotnet test
```

The tests exercise the real engine against the real libvips (the `NetVips.Native` package supplies
it for Windows, Linux or macOS), on images they generate themselves. The app project also builds,
but doesn't run, on Linux and macOS (`EnableWindowsTargeting`).

The large-image tests write a 348 MB, 12000 × 10000 PNG and measure time and peak memory. They take
a few minutes and are skipped unless asked for:

```powershell
$env:PARADIS_LARGE_TESTS = "1"; dotnet test --filter "FullyQualifiedName~LargeImageTests" --logger "console;verbosity=detailed"
```

(Peak memory is measured exactly on Linux; on Windows the process-wide peak working set is used.)

## Publish

```powershell
dotnet publish src/ParadisResize -p:PublishProfile=win-x64-self-contained
```

Output in `publish/win-x64/`:

```
ParadisResize.exe    the app, self-contained (includes .NET; nothing to install)
libvips-42.dll       the image library; must stay next to the exe
```

`libvips-42.dll` is deliberately not packed into the exe (see DECISIONS.md, section 1).

## Release ZIP

On Linux (or WSL) with `zip`:

```bash
dotnet publish src/ParadisResize -p:PublishProfile=win-x64-self-contained
packaging/package-windows.sh
```

This makes `out/ParadisResize-<version>-win-x64.zip` (exe, DLL, README.txt, `licenses/`), a source
ZIP and `SHA256SUMS.txt`. The version is defined once, in `Directory.Build.props`.
