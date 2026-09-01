# ADR 0003: Use an authenticated named-pipe window broker

## Status

Accepted.

## Context

Windows Terminal routes `wt` invocations to existing named windows and supports
cross-window tab operations. The existing implementation uses Windows-specific
remoting and C++/WinRT application infrastructure.

## Decision

Use one NativeAOT process with an application broker and authenticated,
current-user-only named-pipe IPC. Keep protocol messages versioned and
source-generated. The broker owns windows and transferable terminal sessions;
Avalonia owns UI dispatch.

## Consequences

- `wt -w`, global summon, and tab tear-off do not depend on WinRT remoting.
- IPC authorization, protocol versioning, crash recovery, and stale endpoint
  handling become explicit test requirements.
- The initial app can continue using one process with multiple windows until
  the broker phase lands.
