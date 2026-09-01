# ADR 0001: Use an Avalonia/Skia renderer

## Status

Accepted.

## Context

Windows Terminal's AtlasEngine uses DirectWrite, Direct2D, and Direct3D. The
.NET port already uses Avalonia, whose rendering stack is based on Skia.
Embedding Atlas would retain a large C++/WinRT boundary and duplicate graphics
device management.

## Decision

Implement a C# renderer in `Terminal.Render` using Avalonia/Skia and HarfBuzz.
Match Atlas behavior: cell placement, font fallback, glyph caching, terminal
attributes, dirty-region rendering, DPI changes, cursor, selection, and images.
Do not attempt a line-for-line port of Atlas internals or shaders.

## Consequences

- NativeAOT and cross-architecture publishing remain straightforward.
- Visual and performance parity require dedicated golden tests and benchmarks.
- Windows-specific effects are optional capabilities rather than core renderer
  dependencies.
