# libghostty-vt native assets

The optional Ghostty terminal engine uses `libghostty-vt` built from
[`ghostty-upstream.json`](ghostty-upstream.json):

- repository: `https://github.com/ghostty-org/ghostty`
- commit: `3c1ef5b32fc5ea6b93d28493fabf193f595139cf`
- Zig: `0.16.0`
- configuration: `ReleaseFast`, SIMD enabled
- license: MIT (see `LICENSE`)

Binaries are **not** committed. `dotnet build` / `dotnet test` restore the host
RID via [`../Restore-NativeLibraries.ps1`](../Restore-NativeLibraries.ps1),
which downloads Zig 0.16.0 if needed and compiles `libghostty-vt` into
`native/ghostty/<rid>/` (gitignored). ABI checks use the type manifest, not a
file hash.

Linux: glibc **2.31+**. macOS: **13.0+** (`aarch64-macos.13.0` /
`x86_64-macos.13.0`). Windows: MSVC.

## Rebuild locally

```powershell
# Host RID (also run automatically by MSBuild)
./native/Restore-NativeLibraries.ps1

# One RID
./native/ghostty/Build-Ghostty.ps1 -Rid win-x64
```

The script clones the pinned commit into `artifacts/ghostty-src`.

`build-ghostty.yml` still builds every RID and uploads `libghostty-vt` artifacts.
Optional fast path: download that artifact and run
`Restore-GhosttyNative.ps1 -SourceDirectory artifacts/libghostty-vt`.
