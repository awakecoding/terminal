# Advanced VT protocols

> [!NOTE]
> The public out-of-process Windows ConPTY may filter DCS payloads before they
> reach a terminal client on some Windows builds. The Core parser and renderer
> support Sixel when a connection transports DCS bytes unchanged (for example,
> remote/Azure transports); local ConPTY support is limited by the installed
> Windows pseudoconsole implementation.

`Devolutions.Terminal.Core` parses advanced string protocols without depending on Avalonia,
Skia, Win32, or an image codec. It exposes decoded Sixel pixels and bounded
encoded OSC 1337/ConEmu images as renderer-neutral overlay metadata.

## Supported sequences

| Sequence | Core behavior |
| --- | --- |
| `DCS ... q data ST` / `CSI ? 80 h/l` | Decodes Sixel with macro aspect ratio, repeat, color registers (HLS and RGB), raster attributes, graphics CR/LF, opaque or transparent backgrounds, DECSDM display/scroll behavior, and retained cell geometry. |
| `DCS $ q request ST` | Implements DECRQSS for SGR, DECSTBM, DECSLRM, DECSCUSR (default style), DECSCA (unprotected), and DECSACE (stream extent). Unknown settings return `DCS 0 $ r ST`. |
| `DCS + q names ST` | Implements practical XTGETTCAP reports for `TN`, `Co`, `RGB`, and `Tc`, with one response per hex-encoded capability name. |
| `DCS ... { Dscs data ST` | Implements bounded DECDLD soft-font download (one 94/96-character DRCS buffer, 16×32 maximum glyphs), SCS designation, SI/SO, LS2/LS3, LS1R/LS2R/LS3R, and SS2/SS3 invocation. Cells use `U+EF20`–`U+EF7F`; `DrcsGlyphs` exposes renderer-neutral alpha masks. |
| `DCS Pmid;Pdel;Penc ! z data ST` / `CSI Pmid * z` | Defines, deletes, and invokes DECDMAC macros in text or hex-pair encoding, including bounded hex repeats. |
| `CSI ? 2 l` / VT52 `ESC` commands | Implements VT52 cursor movement, home, reverse index, erase-to-end, direct cursor address, identify (`ESC / Z`), keypad mode, DEC graphics, and `ESC <` return to ANSI mode. CSI, OSC, and DCS entry are disabled while in VT52 mode. |
| `CSI ... $ r/t/v/x/z/{` / `CSI ... * y` | Implements DECCARA, DECRARA, DECCRA, DECFRA, DECERA, DECSERA, DECSACE stream/rectangle extent, and DECRQCRA checksums. |
| `CSI ? Ps J/K` / `CSI Ps " q` | Implements protected cells and DECSED/DECSEL selective erase. |
| `CSI 2;Pu $ u` / `DCS 2 $ p ... ST` | Reports and restores the 256-entry terminal color table in HLS or RGB percentage form. |
| `CSI Ps $ w` / `DCS Ps $ t ... ST` | Reports and restores cursor presentation state (`Ps=1`) and tab stops (`Ps=2`). |
| `OSC 1337 ; File=... : base64 ST` | Parses inline iTerm2 image name, declared size, width, height, aspect-ratio preference, and bounded encoded bytes. Non-inline file transfers are explicitly rejected without I/O. |
| `OSC 9 ; 4 ; st=0 ; sz=N ; base64 ST` | Parses bounded, single-part ConEmu encoded images. Multipart, malformed, size-mismatched, and oversized transfers are rejected. |

The DCS state machine handles 7-bit and C1 entry/termination, parameter and
intermediate collection, passthrough, CAN/SUB cancellation, and an `ESC`
terminator split across input chunks. BEL does not terminate DCS. An `ESC`
followed by anything other than `\` aborts the DCS and starts the new escape
sequence.

## Limits

Limits are public constants on `TerminalImageLimits` and `VtResourceLimits`.

| Resource | Limit |
| --- | ---: |
| Collected DCS payload | 4 MiB |
| Decoded OSC 1337 image | 768 KiB |
| Sixel width or height | 4096 pixels |
| Sixel pixel count | 16,777,216 |
| Sixel pixel writes per sequence | 67,108,864 |
| Retained overlays | 64 |
| Retained Core image data | 64 MiB |
| DRCS buffers | 1 |
| DRCS characters | 96 |
| DRCS glyph dimensions | 16×32 pixels |
| DECDMAC identifiers | 64 |
| Shared macro storage | 256 KiB |
| Macro invocation depth | 16 |
| Bytes expanded by one top-level macro invocation | 256 KiB |

Sequences that exceed a parser or decoder limit are discarded. Invalid DRCS
dimensions and malformed macro hex/repeat payloads do not publish partial state.
Macro definitions are rejected during macro invocation, and recursion plus total
expansion are independently bounded. A failed Sixel decode does not modify
persistent color registers. When the retained overlay budget is reached, Core
evicts the oldest overlay before publishing the new one.

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

Overlay anchors retain a logical-line identity and logical cell offset.
Snapshots resolve that anchor against the current scrollback/reflow layout, and
the overlay is removed deterministically when its owning line segment is
evicted. Main and alternate buffers retain independent identities.

## Intentional gaps

- DECTSR terminal-state format 1 (`CSI 1 $ u` / `DCS 1 $ p`) remains
  unsupported, matching upstream `AdaptDispatch`; color-table format 2 is
  supported.
- DRCS font numbers are accepted for compatibility but share one soft-font
  buffer, matching upstream. Multi-byte DRCS designators are accepted for the
  common intermediate-plus-final form.
- VT52 printer commands and host keyboard encoding are outside `Devolutions.Terminal.Core`;
  Core tracks cursor-key and keypad modes for input layers to consume.
- non-inline OSC 1337 file transfer and remote file access (explicitly rejected)
- multipart ConEmu image payloads (explicitly rejected)
- Ghostty image projection (the pinned C ABI exposes no image resources)

These gaps avoid remote I/O and renderer/input dependencies while keeping every
implemented parser and downloadable resource path bounded.
