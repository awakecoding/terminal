# ADR 0004: Treat NativeAOT as a continuous compatibility requirement

## Status

Accepted.

## Context

Reflection-heavy serializers, dynamic XAML, runtime-generated interop, and
service-location patterns can build under the JIT and fail only during
NativeAOT publication or at runtime.

## Decision

- Set `IsAotCompatible` and trim analysis on every production assembly.
- Use `LibraryImport`, generated JSON/regex metadata, and compiled Avalonia XAML.
- Avoid reflection-based dependency injection and dynamic plugin loading.
- Publish x64 and ARM64 NativeAOT artifacts in CI.
- Require an AOT publish for PRs changing serialization, interop, XAML,
  rendering, or composition.

## Consequences

- Some libraries and extension designs are unavailable.
- Failures are detected when introduced instead of at release time.
- Unavoidable COM and packaged APIs must live behind narrow, AOT-tested
  adapters.
