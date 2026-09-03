# Noto Color Emoji

`NotoColorEmoji.ttf` is restored at build time (not committed). It is the
deterministic emoji/ZWJ fallback on systems without a color emoji font.

Pin is [`noto-emoji.json`](noto-emoji.json):

- Upstream: `googlefonts/noto-emoji`
- Commit: `8998f5dd683424a73e2314a8c1f1e359c19e8742`
- SHA-256: `72A635CB3D2F3524C51620CDDE406B217204E8A6A06C6A096FF8ED4B5FD6E27B`
- License: SIL Open Font License 1.1 (`LICENSE`)

`dotnet build` downloads the TTF via `Restore-NotoEmoji.ps1`. Pass
`-p:SkipNativeRestore=true` to skip.
