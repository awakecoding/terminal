# libghostty-vt native assets

The optional Ghostty terminal engine uses `libghostty-vt` built from
[`ghostty-upstream.json`](ghostty-upstream.json):

- repository: `https://github.com/ghostty-org/ghostty`
- commit: `3c1ef5b32fc5ea6b93d28493fabf193f595139cf`
- Zig: `0.16.0`
- configuration: `ReleaseFast`, SIMD enabled
- license: MIT (see `LICENSE`)

CI (`build-ghostty.yml`) is the producer. It builds every RID, uploads
`libghostty-vt-<rid>` artifacts plus a combined `libghostty-vt` bundle, and
prints SHA-256 sums. Tracked `win-*` / `linux-*` binaries in this folder are a
cache so `dotnet test` works offline; replace them from a green Ghostty workflow
run when bumping the pin. macOS dylibs are not tracked.

| RID | SHA-256 |
| --- | --- |
| `win-x64` | `DCB3274F9D8C945AC765A11903614C5DA4BC0CC2EF4EBC23E8CD70C130B7B458` |
| `win-arm64` | `691A331E92D0CE17B8407DD370D26394090B14AB8A7C398DF497442293D4ED72` |
| `linux-x64` | `46AC64A83F91542D38D60BC0DC169157E9475958566349D7D0B4EEE621C5F929` |
| `linux-arm64` | `0D2CB6B391592CF772A166D9393DA859884C46614B219B64E3792B4BC989DADC` |
| `osx-arm64` | *(CI artifact; pin after the first green macOS build)* |
| `osx-x64` | *(CI artifact; pin after the first green macOS build)* |

## Rebuild locally

```powershell
# One RID, download Zig if needed, skip hash check while iterating
./native/ghostty/Build-Ghostty.ps1 -Rid win-x64 -InstallZig -SkipHashCheck

# All RIDs the current host can target (needs Zig 0.16.0 on PATH)
./native/ghostty/Build-Ghostty.ps1
```

The script clones the pinned commit into `artifacts/ghostty-src` and copies
libraries into `native/ghostty/<rid>/`. Linux hashes require `llvm-strip`.

## Restore CI artifacts

Download the `libghostty-vt` artifact from a `build-ghostty` run, then:

```powershell
./native/ghostty/Restore-GhosttyNative.ps1 -SourceDirectory artifacts/libghostty-vt
```

After a CI hash is stable, copy it into `ghostty-upstream.json` and
`GhosttyAbi.cs`, then drop `-SkipHashCheck`.
