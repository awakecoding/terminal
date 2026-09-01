# ADR 0005: Separate packaged and unpackaged capabilities

## Status

Accepted.

## Context

Terminal sessions and the Avalonia UI can run unpackaged, while notifications,
execution aliases, jumplists, default-terminal registration, and some shell
integrations depend on package identity.

## Decision

Define explicit capability checks in `WindowsTerminal.Interop` and package
metadata in `WindowsTerminal.Package`. Core terminal features must work
unpackaged. Identity-only actions return actionable diagnostics when unavailable.
Release artifacts use MSIX identity; developer builds remain directly runnable.

## Consequences

- Package-only APIs cannot leak into `Terminal.Core`, `Terminal.Control`, or
  settings resolution.
- Packaged and unpackaged smoke tests are both required.
- Shell integration can use an isolated native shim only when a manifest-based
  registration cannot satisfy the scenario.
