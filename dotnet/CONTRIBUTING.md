# Contributing

This repository is Devolutions Terminal. Run commands from the repository root.

## Build and test

```powershell
dotnet test Devolutions.Terminal.slnx
dotnet run --project src/Devolutions.Terminal
```

Warnings are errors. Prefer small, reviewable changes that keep NativeAOT publish green.

## NativeAOT

```powershell
dotnet publish src/Devolutions.Terminal -c Release -r win-x64 --self-contained
```

Linux packages:

```bash
scripts/Build-LinuxPackage.sh linux-x64 0.1.0 artifacts/packages all
```

Windows MSIX:

```powershell
.\src\Devolutions.Terminal.Package\Scripts\Build-Packages.ps1
```

Details are in [docs/release.md](docs/release.md).

## Compatibility inventory

[`compat/windows-terminal.json`](compat/windows-terminal.json) is the checked-in
Windows Terminal surface snapshot. Regenerating it requires a separate
[microsoft/terminal](https://github.com/microsoft/terminal) C++ checkout:

```powershell
dotnet run --project tools/Devolutions.Terminal.PortInventory -- <windows-terminal-checkout> compat/windows-terminal.json
```

Do not point that tool at this repository.

## Native helpers

- Linux/macOS PTY host: `native/linux-pty` (`dt-pty-host.c`, Zig `cc`)
- Ghostty VT library: `native/ghostty` (Zig build of pinned Ghostty)
- Windows Explorer/toast helpers: `native/windows-shell` (MSVC, gitignored `bin/`)

`dotnet build` restores Ghostty and `dt-pty-host` for the host RID (Zig is
downloaded into `artifacts/tools` on first use). See
[`native/ghostty/README.md`](native/ghostty/README.md). Pass
`-p:SkipNativeRestore=true` to skip.

## macOS

macOS is partial. See [docs/macos.md](docs/macos.md). The Unix PTY host source
cross-targets `osx-arm64` / `osx-x64`; Ghostty dylibs and app-bundle packaging
are not in this tree yet.
