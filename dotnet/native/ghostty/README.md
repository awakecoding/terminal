# libghostty-vt native assets

The optional Ghostty terminal engine uses `libghostty-vt` built from:

- repository: `https://github.com/ghostty-org/ghostty`
- commit: `3c1ef5b32fc5ea6b93d28493fabf193f595139cf`
- Zig: `0.16.0`
- configuration: `ReleaseFast`, SIMD enabled
- license: MIT (see `LICENSE`)

Pinned SHA-256 hashes:

| RID | SHA-256 |
| --- | --- |
| `win-x64` | `DCB3274F9D8C945AC765A11903614C5DA4BC0CC2EF4EBC23E8CD70C130B7B458` |
| `win-arm64` | `691A331E92D0CE17B8407DD370D26394090B14AB8A7C398DF497442293D4ED72` |
| `linux-x64` | `46AC64A83F91542D38D60BC0DC169157E9475958566349D7D0B4EEE621C5F929` |
| `linux-arm64` | `0D2CB6B391592CF772A166D9393DA859884C46614B219B64E3792B4BC989DADC` |

Rebuild both binaries with `Build-Ghostty.ps1`. The script checks out the
pinned commit in an ignored artifacts directory and verifies the resulting
hashes before replacing the tracked runtime assets.
