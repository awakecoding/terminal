using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.Settings.Editor;

public static class SettingsViewFactory
{
    public static SettingsWindow CreateWindow() => new(new SettingsEditorViewModel());

    public static SettingsWindow CreateWindow(
        Func<AppSettings> load,
        Action<AppSettings> save,
        Func<AppSettings> createDefault,
        Func<string?>? getRevision = null) =>
        new(new SettingsEditorViewModel(load, save, createDefault, getRevision));
}
