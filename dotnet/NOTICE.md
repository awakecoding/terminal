# Notices

Devolutions Terminal includes third-party material. Application code is MIT
licensed. Bundled fonts and some native runtimes use additional licenses.

## Cascadia Mono

**Source**: [https://github.com/microsoft/cascadia-code](https://github.com/microsoft/cascadia-code)

Vendored under `assets/fonts/`. Licensed under the SIL Open Font License 1.1.
See the license in the Cascadia Code repository.

## Noto Color Emoji

**Source**: Google Noto Emoji

Vendored under `native/noto-emoji/`. Licensed under the SIL Open Font License
1.1. See `native/noto-emoji/LICENSE`.

## Ghostty (`libghostty-vt`)

Pinned native VT engine binaries under `native/ghostty/`. See
`native/ghostty/LICENSE` and `docs/ghostty-engine.md`.

## AppImage type-2 runtime

Linux AppImage packages embed a pinned type-2 runtime. See
`linux/APPIMAGE-RUNTIME-LICENSE.txt`. Combined SPDX for those artifacts is
`(MIT AND OFL-1.1) AND LicenseRef-AppImage-Runtime`.

## Windows Terminal compatibility

Settings schema, action inventory, profile GUIDs, icons, and defaults are
derived from Microsoft Windows Terminal (MIT). The C++ implementation remains
the behavioral oracle; this project is a C#/Avalonia reimplementation.
