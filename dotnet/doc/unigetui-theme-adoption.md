# UniGetUI theme adoption

Research was performed against Devolutions/UniGetUI commit
`b2713c68d15647ba6009ce5fc9c4db93227d64fd`.

## Findings

UniGetUI's Windows appearance does not come from FluentAvalonia, Semi.Avalonia,
or another third-party Windows theme. It uses:

- Avalonia, Desktop, and FluentTheme 12.0.4;
- stock Avalonia controls with a custom WinUI Fluent v2 token layer in
  `UniGetUI.Avalonia/Assets/Styles/Styles.Common.axaml`;
- light/dark resources for card, text, subtle navigation, control, menu, and
  dialog surfaces;
- custom `SettingsCard` controls with 60-DIP rows, 8-DIP radii, 1-DIP borders,
  14-DIP labels, secondary descriptions, and right-aligned controls;
- a custom ListBox-based navigation pane with 6-DIP pills and a narrow accent
  selection indicator;
- conditional Windows 11 Mica resources and solid fallback surfaces;
- explicit page construction and compiled bindings compatible with NativeAOT.

UniGetUI's large DWM custom-chrome implementation, smooth-scroll internals,
settings persistence wrappers, and SVG assets are intentionally not copied.
The icons have separate Icons8 licensing requirements. UniGetUI itself is MIT
licensed; substantial copied code would require its notice.

## Adopted here

`WindowsTerminal.Settings/SettingsTheme.axaml` implements a focused,
independently maintained subset of the same visual grammar on Avalonia 11.3:

- theme-aware WinUI-like page, card, footer, navigation, control, and secondary
  text resources;
- Mica-first settings window with an opaque fallback;
- rounded settings cards and diagnostics surface;
- subtle navigation hover/selection with an accent pill;
- constrained right-side form controls and an accent Apply action;
- card-like standalone boolean settings.

The existing settings view models, compiled bindings, data-loss guards, and
atomic persistence remain unchanged.

## Avalonia 12 migration boundary

Moving the terminal to Avalonia 12 should be a separate compatibility change.
It requires upgrading all Avalonia packages together and addressing removed
Diagnostics APIs, `Watermark` to `PlaceholderText`, window-decoration changes,
and the new asynchronous clipboard data-transfer APIs. The migration must keep
reflection-free compiled bindings and pass both win-x64 and win-arm64 NativeAOT
publishes. The WinUI resource/card layer is deliberately compatible with the
current Avalonia version so visual progress does not depend on that migration.
