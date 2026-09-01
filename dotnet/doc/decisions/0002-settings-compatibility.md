# ADR 0002: Preserve Windows Terminal settings compatibility

## Status

Accepted.

## Context

Users have existing JSON files with comments, trailing commas, legacy aliases,
extension fragments, generated profiles, and keys unknown to this port.
Deserializing directly into mutable CLR objects would lose syntax and unknown
data when the settings UI saves.

## Decision

Use two representations:

1. A syntax-aware ordered JSON document that retains comments and unknown data.
2. Typed immutable settings layers resolved into an effective runtime model.

Use source-generated `System.Text.Json` metadata for all typed serialization.
Implement defaults, fragments, generated profiles, user settings, and runtime
overrides as explicit layers.

## Consequences

- Existing settings can be consumed without a migration step.
- The editor must apply targeted syntax-tree updates rather than rewrite a
  serialized CLR object graph.
- Every new setting requires source metadata, resolution tests, and a mapping in
  the compatibility inventory.
