# Avalonia settings editor

`Devolutions.Terminal.Settings.Editor` is the NativeAOT-safe Avalonia editor for the layered
`Devolutions.Terminal.Settings` model. It intentionally has no dependency on the application
shell or action dispatcher.

## Integration

The host can open the editor after adding a project reference and rooting the
assembly for NativeAOT:

```xml
<ProjectReference Include="..\Devolutions.Terminal.Settings.Editor\Devolutions.Terminal.Settings.Editor.csproj" />
<TrimmerRootAssembly Include="Devolutions.Terminal.Settings.Editor" />
```

Create the window through the public factory and assign an owner if appropriate:

```csharp
var settingsWindow = SettingsViewFactory.CreateWindow();
settingsWindow.Show(owner);
```

`SettingsWindow` and `SettingsEditorViewModel` are public for hosts that need
custom lifetime or persistence composition. `SettingsViewFactory.CreateWindow`
also accepts explicit load, save, and default factories for tests or alternate
storage. No reflection-based DI or runtime view discovery is used.

## Editing behavior

- Pages and navigation are compiled Avalonia XAML with explicit view models.
- Search matches page titles and setting keywords.
- The loaded `AppSettings` instance remains the edit buffer. Applying calls
  `SettingsService.Save`, whose resolved-snapshot diff writes only user-layer
  changes atomically and retains unknown user keys and inherited fragment data.
- Revert reloads all layers. Reset loads embedded defaults but remains dirty
  until Apply.
- The default service composition fingerprints the loaded file and refuses to
  overwrite external changes; Revert reloads the newer file.
- Loader diagnostics remain visible in the editor.
- Actions, keybindings, new-tab menu entries, and profile environments use
  JSON-backed editors. JSON is validated before Apply.
- Recognized polymorphic new-tab entries retain unknown properties. An
  unsupported entry type may remain untouched, but editing it is rejected
  rather than silently dropping its payload.

## Current boundary

The editor does not dispatch actions, mutate the running terminal, select files
with native pickers, or provide visual color/font pickers yet. It exposes the
highest-value typed fields and lossless JSON hooks so those controls can be
added without changing persistence semantics.
