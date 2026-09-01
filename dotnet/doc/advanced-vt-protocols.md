# Advanced VT protocols

`Terminal.Core` parses advanced string protocols without depending on Avalonia,
Skia, Win32, or an image codec. It exposes decoded Sixel pixels and encoded
OSC 1337 images as renderer-neutral overlay metadata.

## Supported sequences

| Sequence | Core behavior |
| --- | --- |
| `DCS ... q data ST` | Decodes Sixel with macro aspect ratio, repeat, color registers (HLS and RGB), raster attributes, graphics CR/LF, and opaque or transparent backgrounds. |
| `DCS $ q request ST` | Implements DECRQSS for SGR, DECSTBM, DECSLRM, DECSCUSR (default style), DECSCA (unprotected), and DECSACE (stream extent). Unknown settings return `DCS 0 $ r ST`. |
| `DCS + q names ST` | Implements practical XTGETTCAP reports for `TN`, `Co`, `RGB`, and `Tc`, with one response per hex-encoded capability name. |
| `OSC 1337 ; File=... : base64 ST` | Parses inline iTerm2 image name, declared size, width, height, aspect-ratio preference, and bounded encoded bytes. Non-inline file transfers are ignored. |

The DCS state machine handles 7-bit and C1 entry/termination, parameter and
intermediate collection, passthrough, CAN/SUB cancellation, and an `ESC`
terminator split across input chunks. BEL does not terminate DCS. An `ESC`
followed by anything other than `\` aborts the DCS and starts the new escape
sequence.

## Limits

Limits are public constants on `TerminalImageLimits`.

| Resource | Limit |
| --- | ---: |
| Collected DCS payload | 4 MiB |
| Decoded OSC 1337 image | 768 KiB |
| Sixel width or height | 4096 pixels |
| Sixel pixel count | 16,777,216 |
| Sixel pixel writes per sequence | 67,108,864 |
| Retained overlays | 64 |
| Retained Core image data | 64 MiB |

Sequences that exceed a parser or decoder limit are discarded. A failed Sixel
decode does not modify persistent color registers. When the retained overlay
budget is reached, Core evicts the oldest overlay before publishing the new one.

## Renderer contract

Renderers can consume `TerminalEngine.Images`, the detached
`TerminalSnapshot.Images`, or subscribe to `TerminalEngine.ImageAdded`.
`TerminalImageOverlay` provides:

- a monotonic ID and protocol;
- the primary/alternate buffer identity;
- the cursor cell where the image was received;
- either a `SixelImage` or an `InlineImage`.

`SixelImage.PixelIndices` contains 16-bit palette indexes. Index 256 is
transparent; indexes 0 through 255 address `SixelImage.Palette`, whose entries
are packed `0xAARRGGBB`. `ToRgba32()` provides a detached packed RGBA/ARGB
surface for renderers that do not use indexed textures.

For OSC 1337, Core intentionally retains encoded PNG/JPEG/GIF bytes rather than
loading a codec. The renderer validates the encoded format, decodes it with its
own image stack, applies `Width`, `Height`, and `PreserveAspectRatio`, and clips
the result to its terminal viewport.

Overlay anchors currently remain at the viewport cell recorded when the
sequence completes. The renderer must not infer text-cell ownership from that
coordinate. Moving overlays with scrollback/reflow requires stable buffer-line
identities and is a later cross-layer change.

## Intentional gaps

- DRCS/downloadable character sets (`DECDLD`)
- user-defined macros (`DECDMAC`)
- VT52 mode
- Sixel scrolling/display-mode cursor movement and image slices tied to stable
  scrollback line identities
- non-inline OSC 1337 file transfer and remote file access
- ConEmu image payloads

These are deferred to keep the Core parser bounded and the Sixel/DECRQSS path
correct without introducing UI or renderer dependencies.
