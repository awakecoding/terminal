# Windows shell integration helpers

This directory contains the architecture-matched native boundaries used by the
Windows package:

- `WindowsTerminalShellExt.dll` is an in-process `IExplorerCommand` COM server
  and an `INotificationActivationCallback` that validates and forwards toast
  activation to `WindowsTerminal.exe`.
- `wt-shell-integration.exe` is a one-shot, versioned process boundary for jump
  lists and system toast publication.

The helpers are built from source with the Visual C++ and Windows SDK toolchain
already present on `windows-latest`. They do not use managed COM interop and do
not change the NativeAOT host's `BuiltInComInteropSupport=false` policy.

`wt-shell-integration.exe` accepts its request on standard input. Every request
contains protocol version 1 and a random authentication token that must match
the child-only `WT_SHELL_HELPER_AUTH_TOKEN` environment variable. Values are
base64url-encoded UTF-8 and responses use the same bounded line protocol.

The Windows console default-terminal handoff interfaces require the OpenConsole
handoff proxy/stub and host implementation, which this port does not yet build.
The helper exposes the versioned `default-terminal-delegation.v1` boundary but
returns an explicit `unsupported` diagnostic. The package deliberately does not
advertise the corresponding app extensions until that implementation exists.
