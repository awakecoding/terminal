# ADR 0001: Use an Avalonia/Skia renderer

## Status

Accepted.

## Context

Windows Terminal's AtlasEngine uses DirectWrite, Direct2D, and Direct3D. The
.NET port already uses Avalonia, whose rendering stack is based on Skia.
Embedding Atlas would retain a large C++/WinRT boundary and duplicate graphics
device management.

## Decision

Implement a C# renderer in `Devolutions.Terminal.Render` using Avalonia/Skia and HarfBuzz.
Match Atlas behavior: cell placement, font fallback, glyph caching, terminal
attributes, dirty-region rendering, DPI changes, cursor, selection, and images.
Do not attempt a line-for-line port of Atlas internals or shaders.

## Consequences

- NativeAOT and cross-architecture publishing remain straightforward.
- Visual and performance parity require dedicated golden tests and benchmarks.
- Windows-specific effects are optional capabilities rather than core renderer
  dependencies.

## Implementation

`Devolutions.Terminal.Render` shapes one terminal cell cluster at a time with HarfBuzz while
supplying the complete same-style run as shaping context. This prevents
proportional-font advances from moving text across cell boundaries while
retaining contextual forms, combining text, and emoji joiner sequences.
Resolved Skia text blobs are held in a bounded LRU cache. Avalonia integration is
one `ICustomDrawOperation`; it leases the active Skia canvas and owns no graphics
device, so DPI changes and device recreation only invalidate CPU-side caches.

The supported fallback order is the profile face, Cascadia Mono, Consolas, Segoe
UI Emoji, then the platform font manager. Common Powerline separators have
geometry fallbacks when no font supplies them.
