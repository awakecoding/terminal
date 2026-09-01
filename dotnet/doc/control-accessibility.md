# Terminal control interaction and accessibility

`Terminal.Control` owns terminal-local input, selection, clipboard, hyperlink, IME,
and accessibility behavior. The app shell remains responsible only for presenting
paste warnings and optional context UI through the control events.

## Selection and clipboard

Selections use absolute buffer coordinates, so they remain stable while scrollback
moves. `TerminalSelectionMode` supports linear, rectangular block, word, logical
line, shell command, and shell output ranges. Mouse double-click selects a word,
triple-click selects a logical line, and Alt+drag makes a block selection.

Mark mode exposes caret movement, active-endpoint switching, select-all, and
word expansion without sending keys to the hosted shell. Public selection methods
also let the action dispatcher bind command/output selection without duplicating
buffer logic.

`BuildCopyPayload` and `CopyAsync(TerminalCopyOptions)` always produce Unicode plain
text and can add CF_HTML and RTF. Options cover single-line conversion, block-row
trimming, and stripping or preserving control characters. The strip policy retains
only printable text plus tab and line breaks.

Paste is normalized to terminal carriage returns and can trim outer whitespace.
`TerminalPasteRequest` reports large and multiline classifications before any input
is written. When a host subscribes to `PasteWarning`, it must set `Allow` after
confirmation or the paste is cancelled. Without a subscriber, paste retains the
pre-existing direct behavior; hosts that present warning UI opt into enforcement
by subscribing.

OSC 52 remains gated by `ProfileSettings.AllowVtClipboardWrite`. Clipboard failures
are raised through `InteractionError` with the `OSC 52 clipboard write` operation;
they are not converted into success.

## Hyperlinks, mouse, touch, and marks

Hyperlink hit testing returns the complete hyperlink run, display text, buffer
range, and a policy-derived `CanOpen` value. The default safe schemes are `http`,
`https`, and `mailto`; settings can replace this allow-list. Ctrl+click opens a safe
URI, while right-click raises `HyperlinkContextRequested`. Open and copy operations
are independently callable for host context menus. Executable `file:` targets remain
blocked even when a host adds `file` to the scheme allow-list.

DEC mouse tracking takes precedence over selection and emits SGR or legacy mouse
coordinates as negotiated by the terminal. Focus reporting emits CSI I/O. Outside
mouse mode, wheel input scrolls history, Ctrl+wheel zooms, touch drag scrolls, and
Alt+click cursor repositioning is enabled only by the profile setting.

`GetScrollMarks` merges OSC 133 prompt/exit-code marks with search matches and
returns normalized positions. Consumers can distinguish successful and failed
commands and the active search match.

## IME and automation

The control supplies an Avalonia `TextInputMethodClient` whenever focused. It
reports surrounding cursor-line text, selection, and the cell-aligned cursor
rectangle, and renders underlined preedit text with the IME cursor.

`TermControlAutomationPeer` exposes:

- the `Document` automation role and accessible name;
- read-only terminal text through Avalonia's value provider;
- immutable document, selection, and caret ranges;
- running/read-only state and line count through `CreateState`.

### Avalonia UIA limitation

Avalonia 11.3 does not expose public `ITextProvider` or `ITextRangeProvider`
interfaces from its automation layer. On Windows, the peer therefore reaches UIA
as a Document element with ValuePattern, name, focus, and state, while its full text
range model is available to managed automation clients and tests. Native UIA
TextPattern/TextPattern2 selection and caret navigation cannot be bridged without
an Avalonia framework change or a Windows-specific raw provider.

### Host integration boundary

`TerminalInteractionOptions.FromSettings` maps global copy, paste, word-delimiter,
and safe-scheme settings into the control contract. Pane creation must assign those
options and present warning/context events in the app shell; the control does not
read global settings directly.
