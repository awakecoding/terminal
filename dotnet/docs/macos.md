# macOS support

macOS is a partial port. The managed app, built-in VT engine, settings, and
Unix PTY protocol are wired. NativeAOT publish and live UI have not been run
on a Mac in this tree.

## What should work

- Avalonia desktop host (`osx-arm64` / `osx-x64`)
- Local shells through `dt-pty-host` (`forkpty`, same framing as Linux)
- Settings at `~/Library/Application Support/Devolutions/Terminal/`
- Generated zsh/bash/fish/pwsh/sh profiles (`Devolutions.Terminal.macOS`)
- Hidden Windows inbox profiles that use `%SystemRoot%`
- Opening files and URIs with `open(1)`
- Notifications through `osascript` `display notification`
- `dterm:` URL scheme declared in `macos/Info.plist`

## Not bundled yet

- `libghostty-vt.dylib` (built by `build-ghostty.yml` for `osx-arm64` /
  `osx-x64`; not tracked until a CI hash is pinned)
- App bundle / notarization / DMG / Homebrew cask
- Global hotkeys (broker / `dt -w` still work)
- Default-terminal registration

## Build on a Mac

```bash
# Unix PTY host (also cross-compilable with Zig)
# native/linux-pty/Build-LinuxPtyHost.ps1 targets osx-arm64 and osx-x64

dotnet publish src/Devolutions.Terminal -c Release -r osx-arm64 --self-contained
```

Copy `macos/Info.plist` into the `.app` bundle when packaging. Until a
`dt-pty-host` binary exists under `native/linux-pty/osx-arm64/`, publish
succeeds but local shells fail with a missing Unix PTY host.
