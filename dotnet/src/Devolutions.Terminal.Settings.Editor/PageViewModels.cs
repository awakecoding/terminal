using System.Text.Json;
using System.Text.Json.Nodes;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.Settings.Editor;

public abstract class SettingsPageViewModel(string title, string description) : ObservableObject
{
    public string Title { get; } = title;
    public string Description { get; } = description;
}

public sealed record StartupProfileOption(string Value, string Name)
{
    public override string ToString() => Name;
}

public sealed class StartupSettingsViewModel(AppSettings settings, Action changed)
    : SettingsPageViewModel("Startup", "Choose the default profile and initial window behavior.")
{
    public IReadOnlyList<LaunchMode> LaunchModes { get; } = Enum.GetValues<LaunchMode>();
    public IReadOnlyList<StartupProfileOption> Profiles { get; } = settings.Profiles
        .Where(static profile => !profile.Hidden)
        .Select(static profile => new StartupProfileOption(profile.Guid ?? profile.Name, profile.Name))
        .ToArray();

    public string? DefaultProfile { get => settings.DefaultProfile; set => Change(settings.DefaultProfile, value, v => settings.DefaultProfile = v); }
    public StartupProfileOption? SelectedProfile
    {
        get => Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Value, settings.DefaultProfile, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(profile.Name, settings.DefaultProfile, StringComparison.OrdinalIgnoreCase)) ??
            Profiles.FirstOrDefault();
        set
        {
            if (value is not null)
            {
                DefaultProfile = value.Value;
            }
        }
    }
    public int InitialColumns { get => settings.InitialCols; set => Change(settings.InitialCols, Math.Max(1, value), v => settings.InitialCols = v); }
    public int InitialRows { get => settings.InitialRows; set => Change(settings.InitialRows, Math.Max(1, value), v => settings.InitialRows = v); }
    public string? InitialPosition { get => settings.InitialPosition; set => Change(settings.InitialPosition, value, v => settings.InitialPosition = v); }
    public bool CenterOnLaunch
    {
        get => settings.CenterOnLaunch;
        set
        {
            Change(settings.CenterOnLaunch, value, v => settings.CenterOnLaunch = v);
            OnPropertyChanged(nameof(CenterOnLaunchState));
        }
    }
    public string CenterOnLaunchState => CenterOnLaunch ? "On" : "Off";
    public LaunchMode LaunchMode { get => settings.LaunchMode; set => Change(settings.LaunchMode, value, v => settings.LaunchMode = v); }
    public string FirstWindowPreference { get => settings.FirstWindowPreference; set => Change(settings.FirstWindowPreference, value, v => settings.FirstWindowPreference = v); }
    public string WindowingBehavior { get => settings.WindowingBehavior; set => Change(settings.WindowingBehavior, value, v => settings.WindowingBehavior = v); }
    public string StartupActions { get => settings.StartupActions; set => Change(settings.StartupActions, value, v => settings.StartupActions = v); }
    public bool AlwaysOnTop
    {
        get => settings.AlwaysOnTop;
        set
        {
            Change(settings.AlwaysOnTop, value, v => settings.AlwaysOnTop = v);
            OnPropertyChanged(nameof(AlwaysOnTopState));
        }
    }
    public string AlwaysOnTopState => AlwaysOnTop ? "On" : "Off";
    public bool AutoHideWindow
    {
        get => settings.AutoHideWindow;
        set
        {
            Change(settings.AutoHideWindow, value, v => settings.AutoHideWindow = v);
            OnPropertyChanged(nameof(AutoHideWindowState));
        }
    }
    public string AutoHideWindowState => AutoHideWindow ? "On" : "Off";

    private void Change<T>(T oldValue, T newValue, Action<T> update)
    {
        if (EqualityComparer<T>.Default.Equals(oldValue, newValue))
        {
            return;
        }

        update(newValue);
        changed();
    }
}

public sealed class InteractionSettingsViewModel(AppSettings settings, Action changed)
    : SettingsPageViewModel("Interaction", "Configure selection, paste, mouse, and URL behavior.")
{
    public bool CopyOnSelect { get => settings.CopyOnSelect; set => Change(settings.CopyOnSelect, value, v => settings.CopyOnSelect = v); }
    public bool CopyFormatting { get => settings.CopyFormatting; set => Change(settings.CopyFormatting, value, v => settings.CopyFormatting = v); }
    public bool TrimBlockSelection { get => settings.TrimBlockSelection; set => Change(settings.TrimBlockSelection, value, v => settings.TrimBlockSelection = v); }
    public bool TrimPaste { get => settings.TrimPaste; set => Change(settings.TrimPaste, value, v => settings.TrimPaste = v); }
    public bool FocusFollowMouse { get => settings.FocusFollowMouse; set => Change(settings.FocusFollowMouse, value, v => settings.FocusFollowMouse = v); }
    public bool ScrollToZoom { get => settings.ScrollToZoom; set => Change(settings.ScrollToZoom, value, v => settings.ScrollToZoom = v); }
    public bool ScrollToChangeOpacity { get => settings.ScrollToChangeOpacity; set => Change(settings.ScrollToChangeOpacity, value, v => settings.ScrollToChangeOpacity = v); }
    public bool DetectUrls { get => settings.DetectUrls; set => Change(settings.DetectUrls, value, v => settings.DetectUrls = v); }
    public bool WarnAboutLargePaste { get => settings.WarnAboutLargePaste; set => Change(settings.WarnAboutLargePaste, value, v => settings.WarnAboutLargePaste = v); }
    public string WarnAboutMultiLinePaste { get => settings.WarnAboutMultiLinePaste; set => Change(settings.WarnAboutMultiLinePaste, value, v => settings.WarnAboutMultiLinePaste = v); }
    public string WordDelimiters { get => settings.WordDelimiters; set => Change(settings.WordDelimiters, value, v => settings.WordDelimiters = v); }

    private void Change<T>(T oldValue, T newValue, Action<T> update)
    {
        if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
        {
            update(newValue);
            changed();
        }
    }
}

public sealed class AppearanceSettingsViewModel(AppSettings settings, Action changed)
    : SettingsPageViewModel("Global appearance and themes", "Configure tabs, window themes, and visual effects.")
{
    private readonly IReadOnlyList<ThemeItemViewModel> _themes =
        settings.Themes.Select(theme => new ThemeItemViewModel(theme, changed)).ToArray();
    private ThemeItemViewModel? _selectedTheme;

    public IReadOnlyList<TabWidthMode> TabWidthModes { get; } = Enum.GetValues<TabWidthMode>();
    public IReadOnlyList<ThemeItemViewModel> Themes => _themes;
    public ThemeItemViewModel? SelectedTheme
    {
        get => _selectedTheme ??= _themes.FirstOrDefault();
        set => SetProperty(ref _selectedTheme, value);
    }

    public string DarkTheme { get => settings.Theme.DarkName; set => Change(settings.Theme.DarkName, value, v => settings.Theme.DarkName = v); }
    public string LightTheme { get => settings.Theme.LightName; set => Change(settings.Theme.LightName, value, v => settings.Theme.LightName = v); }
    public TabWidthMode TabWidthMode { get => settings.TabWidthMode; set => Change(settings.TabWidthMode, value, v => settings.TabWidthMode = v); }
    public bool AlwaysShowTabs { get => settings.AlwaysShowTabs; set => Change(settings.AlwaysShowTabs, value, v => settings.AlwaysShowTabs = v); }
    public bool ShowTabsInTitlebar { get => settings.ShowTabsInTitlebar; set => Change(settings.ShowTabsInTitlebar, value, v => settings.ShowTabsInTitlebar = v); }
    public bool ShowTerminalTitleInTitlebar { get => settings.ShowTerminalTitleInTitlebar; set => Change(settings.ShowTerminalTitleInTitlebar, value, v => settings.ShowTerminalTitleInTitlebar = v); }
    public bool ShowTabsFullscreen { get => settings.ShowTabsFullscreen; set => Change(settings.ShowTabsFullscreen, value, v => settings.ShowTabsFullscreen = v); }
    public bool UseAcrylicInTabRow { get => settings.UseAcrylicInTabRow; set => Change(settings.UseAcrylicInTabRow, value, v => settings.UseAcrylicInTabRow = v); }
    public bool DisableAnimations { get => settings.DisableAnimations; set => Change(settings.DisableAnimations, value, v => settings.DisableAnimations = v); }
    public string NewTabPosition { get => settings.NewTabPosition; set => Change(settings.NewTabPosition, value, v => settings.NewTabPosition = v); }

    private void Change<T>(T oldValue, T newValue, Action<T> update)
    {
        if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
        {
            update(newValue);
            changed();
        }
    }
}

public sealed class ThemeItemViewModel(ThemeSettings theme, Action changed)
{
    public string Name => theme.Name;
    public string Origin => theme.Origin.ToString();
    public string WindowApplicationTheme
    {
        get => theme.Window?.ApplicationTheme ?? "system";
        set
        {
            theme.Window ??= new();
            Change(theme.Window.ApplicationTheme, value, v => theme.Window.ApplicationTheme = v);
        }
    }
    public bool UseMica
    {
        get => theme.Window?.UseMica ?? false;
        set
        {
            theme.Window ??= new();
            Change(theme.Window.UseMica, value, v => theme.Window.UseMica = v);
        }
    }
    public bool RainbowFrame
    {
        get => theme.Window?.RainbowFrame ?? false;
        set
        {
            theme.Window ??= new();
            Change(theme.Window.RainbowFrame, value, v => theme.Window.RainbowFrame = v);
        }
    }
    public string TabIconStyle
    {
        get => theme.Tab?.IconStyle ?? "default";
        set
        {
            theme.Tab ??= new();
            Change(theme.Tab.IconStyle, value, v => theme.Tab.IconStyle = v);
        }
    }

    public override string ToString() => $"{Name} ({Origin})";

    private void Change<T>(T oldValue, T newValue, Action<T> update)
    {
        if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
        {
            update(newValue);
            changed();
        }
    }
}

public sealed class ProfileItemViewModel(ProfileSettings profile, Action changed) : ObservableObject
{
    public static IReadOnlyList<string> TerminalEngineChoices { get; } =
        ["Inherit", "Built-in", "Ghostty"];
    private string _environmentJson = SerializeEnvironment(profile.Environment);

    public string DisplayName => $"{profile.Name} ({profile.Origin})";
    public string Name { get => profile.Name; set => Change(profile.Name, value, v => profile.Name = v); }
    public string? Guid => profile.Guid;
    public string? Source => profile.Source;
    public string TerminalEngine
    {
        get => profile.TerminalEngine switch
        {
            TerminalEngineKind.BuiltIn => "Built-in",
            TerminalEngineKind.Ghostty => "Ghostty",
            _ => "Inherit",
        };
        set => Change(
            profile.TerminalEngine,
            value.ToLowerInvariant() switch
            {
                "built-in" or "builtin" => TerminalEngineKind.BuiltIn,
                "ghostty" => TerminalEngineKind.Ghostty,
                _ => null,
            },
            v => profile.TerminalEngine = v);
    }
    public bool Hidden { get => profile.Hidden; set => Change(profile.Hidden, value, v => profile.Hidden = v); }
    public string Commandline { get => profile.Commandline; set => Change(profile.Commandline, value, v => profile.Commandline = v); }
    public string StartingDirectory { get => profile.StartingDirectory; set => Change(profile.StartingDirectory, value, v => profile.StartingDirectory = v); }
    public string? Icon { get => profile.Icon; set => Change(profile.Icon, value, v => profile.Icon = v); }
    public string? TabTitle { get => profile.TabTitle; set => Change(profile.TabTitle, value, v => profile.TabTitle = v); }
    public string? TabColor { get => profile.TabColor; set => Change(profile.TabColor, value, v => profile.TabColor = v); }
    public bool SuppressApplicationTitle { get => profile.SuppressApplicationTitle; set => Change(profile.SuppressApplicationTitle, value, v => profile.SuppressApplicationTitle = v); }
    public bool Elevate { get => profile.Elevate; set => Change(profile.Elevate, value, v => profile.Elevate = v); }
    public string DarkColorScheme { get => profile.DarkColorScheme; set => Change(profile.DarkColorScheme, value, v => profile.DarkColorScheme = v); }
    public string LightColorScheme { get => profile.LightColorScheme; set => Change(profile.LightColorScheme, value, v => profile.LightColorScheme = v); }
    public string FontFace { get => profile.FontFace; set => Change(profile.FontFace, value, v => profile.FontFace = v); }
    public double FontSize { get => profile.FontSize; set => Change(profile.FontSize, Math.Max(1, value), v => profile.FontSize = v); }
    public int FontWeight { get => profile.FontWeight; set => Change(profile.FontWeight, value, v => profile.FontWeight = v); }
    public bool UseAcrylic { get => profile.UseAcrylic; set => Change(profile.UseAcrylic, value, v => profile.UseAcrylic = v); }
    public int Opacity { get => profile.Opacity; set => Change(profile.Opacity, Math.Clamp(value, 0, 100), v => profile.Opacity = v); }
    public string? Foreground { get => profile.Foreground; set => Change(profile.Foreground, value, v => profile.Foreground = v); }
    public string? Background { get => profile.Background; set => Change(profile.Background, value, v => profile.Background = v); }
    public string? CursorColor { get => profile.CursorColor; set => Change(profile.CursorColor, value, v => profile.CursorColor = v); }
    public string? SelectionBackground { get => profile.SelectionBackground; set => Change(profile.SelectionBackground, value, v => profile.SelectionBackground = v); }
    public string? BackgroundImage { get => profile.BackgroundImage; set => Change(profile.BackgroundImage, value, v => profile.BackgroundImage = v); }
    public double BackgroundImageOpacity { get => profile.BackgroundImageOpacity; set => Change(profile.BackgroundImageOpacity, Math.Clamp(value, 0, 1), v => profile.BackgroundImageOpacity = v); }
    public string BackgroundImageStretchMode { get => profile.BackgroundImageStretchMode; set => Change(profile.BackgroundImageStretchMode, value, v => profile.BackgroundImageStretchMode = v); }
    public string BackgroundImageAlignment { get => profile.BackgroundImageAlignment; set => Change(profile.BackgroundImageAlignment, value, v => profile.BackgroundImageAlignment = v); }
    public bool RetroTerminalEffect { get => profile.RetroTerminalEffect; set => Change(profile.RetroTerminalEffect, value, v => profile.RetroTerminalEffect = v); }
    public int HistorySize { get => profile.HistorySize; set => Change(profile.HistorySize, Math.Max(0, value), v => profile.HistorySize = v); }
    public string Padding { get => profile.Padding; set => Change(profile.Padding, value, v => profile.Padding = v); }
    public string CursorShape { get => profile.CursorShape; set => Change(profile.CursorShape, value, v => profile.CursorShape = v); }
    public int CursorHeight { get => profile.CursorHeight; set => Change(profile.CursorHeight, Math.Clamp(value, 1, 100), v => profile.CursorHeight = v); }
    public CloseOnExitMode CloseOnExit { get => profile.CloseOnExit; set => Change(profile.CloseOnExit, value, v => profile.CloseOnExit = v); }
    public string ScrollbarState { get => profile.ScrollbarState; set => Change(profile.ScrollbarState, value, v => profile.ScrollbarState = v); }
    public string AntialiasingMode { get => profile.AntialiasingMode; set => Change(profile.AntialiasingMode, value, v => profile.AntialiasingMode = v); }
    public bool SnapOnInput { get => profile.SnapOnInput; set => Change(profile.SnapOnInput, value, v => profile.SnapOnInput = v); }
    public bool AltGrAliasing { get => profile.AltGrAliasing; set => Change(profile.AltGrAliasing, value, v => profile.AltGrAliasing = v); }
    public bool RightClickContextMenu { get => profile.RightClickContextMenu; set => Change(profile.RightClickContextMenu, value, v => profile.RightClickContextMenu = v); }
    public bool AutoMarkPrompts { get => profile.AutoMarkPrompts; set => Change(profile.AutoMarkPrompts, value, v => profile.AutoMarkPrompts = v); }
    public bool ShowMarksOnScrollbar { get => profile.ShowMarksOnScrollbar; set => Change(profile.ShowMarksOnScrollbar, value, v => profile.ShowMarksOnScrollbar = v); }
    public bool ReloadEnvironmentVariables { get => profile.ReloadEnvironmentVariables; set => Change(profile.ReloadEnvironmentVariables, value, v => profile.ReloadEnvironmentVariables = v); }
    public bool ForceVtInput { get => profile.ForceVtInput; set => Change(profile.ForceVtInput, value, v => profile.ForceVtInput = v); }
    public bool AllowKittyKeyboardMode { get => profile.AllowKittyKeyboardMode; set => Change(profile.AllowKittyKeyboardMode, value, v => profile.AllowKittyKeyboardMode = v); }
    public bool AllowVtClipboardWrite { get => profile.AllowVtClipboardWrite; set => Change(profile.AllowVtClipboardWrite, value, v => profile.AllowVtClipboardWrite = v); }
    public bool AllowOscNotifications { get => profile.AllowOscNotifications; set => Change(profile.AllowOscNotifications, value, v => profile.AllowOscNotifications = v); }
    public string DragDropDelimiter { get => profile.DragDropDelimiter; set => Change(profile.DragDropDelimiter, value, v => profile.DragDropDelimiter = v); }
    public string PathTranslationStyle { get => profile.PathTranslationStyle; set => Change(profile.PathTranslationStyle, value, v => profile.PathTranslationStyle = v); }
    public string EnvironmentJson
    {
        get => _environmentJson;
        set
        {
            if (SetProperty(ref _environmentJson, value))
            {
                changed();
            }
        }
    }

    public override string ToString() => DisplayName;

    public bool TryCommitEnvironment(out string? error)
    {
        try
        {
            var node = JsonNode.Parse(_environmentJson);
            if (node is not JsonObject map)
            {
                error = $"Environment for '{profile.Name}' must be a JSON object.";
                return false;
            }

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in map)
            {
                if (pair.Value is null)
                {
                    values[pair.Key] = null;
                }
                else if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    values[pair.Key] = text;
                }
                else
                {
                    error = $"Environment value '{pair.Key}' must be a string or null.";
                    return false;
                }
            }

            profile.Environment = values;
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Environment for '{profile.Name}' is invalid JSON: {ex.Message}";
            return false;
        }
    }

    private void Change<T>(T oldValue, T newValue, Action<T> update, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(oldValue, newValue))
        {
            return;
        }

        update(newValue);
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(Name))
        {
            OnPropertyChanged(nameof(DisplayName));
        }

        changed();
    }

    private static string SerializeEnvironment(Dictionary<string, string?> environment)
    {
        var node = new JsonObject(environment.Select(static pair =>
            KeyValuePair.Create<string, JsonNode?>(
                pair.Key,
                pair.Value is null ? null : JsonValue.Create(pair.Value))));
        return node.ToJsonString(JsonFormatting.Options);
    }
}

public sealed class ProfilesSettingsViewModel : SettingsPageViewModel
{
    private ProfileItemViewModel? _selectedProfile;

    public ProfilesSettingsViewModel(IReadOnlyList<ProfileItemViewModel> profiles)
        : base("Profile base settings", "Edit profile identity, launch command, directory, and tab metadata.")
    {
        Profiles = profiles;
        _selectedProfile = profiles.FirstOrDefault();
    }

    public IReadOnlyList<ProfileItemViewModel> Profiles { get; }
    public ProfileItemViewModel? SelectedProfile { get => _selectedProfile; set => SetProperty(ref _selectedProfile, value); }
}

public sealed class ProfileAppearanceSettingsViewModel : SettingsPageViewModel
{
    private ProfileItemViewModel? _selectedProfile;

    public ProfileAppearanceSettingsViewModel(IReadOnlyList<ProfileItemViewModel> profiles)
        : base("Profile appearance", "Edit profile font, colors, opacity, and background image.")
    {
        Profiles = profiles;
        _selectedProfile = profiles.FirstOrDefault();
    }

    public IReadOnlyList<ProfileItemViewModel> Profiles { get; }
    public ProfileItemViewModel? SelectedProfile { get => _selectedProfile; set => SetProperty(ref _selectedProfile, value); }
}

public sealed class ProfileTerminalSettingsViewModel : SettingsPageViewModel
{
    private ProfileItemViewModel? _selectedProfile;

    public ProfileTerminalSettingsViewModel(IReadOnlyList<ProfileItemViewModel> profiles)
        : base("Profile terminal", "Configure scrollback, cursor, close, and terminal interaction.")
    {
        Profiles = profiles;
        _selectedProfile = profiles.FirstOrDefault();
    }

    public IReadOnlyList<CloseOnExitMode> CloseOnExitModes { get; } = Enum.GetValues<CloseOnExitMode>();
    public IReadOnlyList<ProfileItemViewModel> Profiles { get; }
    public ProfileItemViewModel? SelectedProfile { get => _selectedProfile; set => SetProperty(ref _selectedProfile, value); }
}

public sealed class ProfileAdvancedSettingsViewModel : SettingsPageViewModel
{
    private ProfileItemViewModel? _selectedProfile;

    public ProfileAdvancedSettingsViewModel(IReadOnlyList<ProfileItemViewModel> profiles)
        : base("Profile advanced", "Configure VT compatibility and JSON-backed environment variables.")
    {
        Profiles = profiles;
        _selectedProfile = profiles.FirstOrDefault();
    }

    public IReadOnlyList<ProfileItemViewModel> Profiles { get; }
    public ProfileItemViewModel? SelectedProfile { get => _selectedProfile; set => SetProperty(ref _selectedProfile, value); }
}

public sealed class SchemeItemViewModel(SchemeSettings scheme, Action changed)
{
    public string DisplayName => $"{scheme.Name} ({scheme.Origin})";
    public string Name { get => scheme.Name; set => Change(scheme.Name, value, v => scheme.Name = v); }
    public string Foreground { get => scheme.Foreground; set => Change(scheme.Foreground, value, v => scheme.Foreground = v); }
    public string Background { get => scheme.Background; set => Change(scheme.Background, value, v => scheme.Background = v); }
    public string CursorColor { get => scheme.CursorColor; set => Change(scheme.CursorColor, value, v => scheme.CursorColor = v); }
    public string SelectionBackground { get => scheme.SelectionBackground; set => Change(scheme.SelectionBackground, value, v => scheme.SelectionBackground = v); }
    public string Black { get => scheme.Black; set => Change(scheme.Black, value, v => scheme.Black = v); }
    public string Red { get => scheme.Red; set => Change(scheme.Red, value, v => scheme.Red = v); }
    public string Green { get => scheme.Green; set => Change(scheme.Green, value, v => scheme.Green = v); }
    public string Yellow { get => scheme.Yellow; set => Change(scheme.Yellow, value, v => scheme.Yellow = v); }
    public string Blue { get => scheme.Blue; set => Change(scheme.Blue, value, v => scheme.Blue = v); }
    public string Purple { get => scheme.Purple; set => Change(scheme.Purple, value, v => scheme.Purple = v); }
    public string Cyan { get => scheme.Cyan; set => Change(scheme.Cyan, value, v => scheme.Cyan = v); }
    public string White { get => scheme.White; set => Change(scheme.White, value, v => scheme.White = v); }
    public string BrightBlack { get => scheme.BrightBlack; set => Change(scheme.BrightBlack, value, v => scheme.BrightBlack = v); }
    public string BrightRed { get => scheme.BrightRed; set => Change(scheme.BrightRed, value, v => scheme.BrightRed = v); }
    public string BrightGreen { get => scheme.BrightGreen; set => Change(scheme.BrightGreen, value, v => scheme.BrightGreen = v); }
    public string BrightYellow { get => scheme.BrightYellow; set => Change(scheme.BrightYellow, value, v => scheme.BrightYellow = v); }
    public string BrightBlue { get => scheme.BrightBlue; set => Change(scheme.BrightBlue, value, v => scheme.BrightBlue = v); }
    public string BrightPurple { get => scheme.BrightPurple; set => Change(scheme.BrightPurple, value, v => scheme.BrightPurple = v); }
    public string BrightCyan { get => scheme.BrightCyan; set => Change(scheme.BrightCyan, value, v => scheme.BrightCyan = v); }
    public string BrightWhite { get => scheme.BrightWhite; set => Change(scheme.BrightWhite, value, v => scheme.BrightWhite = v); }

    public override string ToString() => DisplayName;

    private void Change(string oldValue, string newValue, Action<string> update)
    {
        if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            update(newValue);
            changed();
        }
    }
}

public sealed class ColorSchemesSettingsViewModel : SettingsPageViewModel
{
    private SchemeItemViewModel? _selectedScheme;

    public ColorSchemesSettingsViewModel(AppSettings settings, Action changed)
        : base("Color schemes", "Edit foreground, background, cursor, selection, and ANSI palette colors.")
    {
        Schemes = settings.Schemes.Select(scheme => new SchemeItemViewModel(scheme, changed)).ToArray();
        _selectedScheme = Schemes.FirstOrDefault();
    }

    public IReadOnlyList<SchemeItemViewModel> Schemes { get; }
    public SchemeItemViewModel? SelectedScheme { get => _selectedScheme; set => SetProperty(ref _selectedScheme, value); }
}

public sealed record ActionBindingSummary(string Action, string Chord, string Source);

public sealed class ActionsSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _changed;
    private string _actionsJson;
    private string _keybindingsJson;
    private string _validationMessage = "JSON has not been validated.";
    private IReadOnlyList<ActionBindingSummary> _bindings = [];

    public ActionsSettingsViewModel(AppSettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
        _actionsJson = settings.Actions.ToJsonString(JsonFormatting.Options);
        _keybindingsJson = settings.Keybindings.ToJsonString(JsonFormatting.Options);
        Validate();
    }

    public string Title => "Actions and key chords";
    public string Description => "Inspect key chord mappings and safely edit polymorphic action JSON.";
    public string ActionsJson { get => _actionsJson; set => SetJson(ref _actionsJson, value); }
    public string KeybindingsJson { get => _keybindingsJson; set => SetJson(ref _keybindingsJson, value); }
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }
    public IReadOnlyList<ActionBindingSummary> Bindings { get => _bindings; private set => SetProperty(ref _bindings, value); }
    public RelayCommand ValidateCommand => new(Validate);

    public bool TryCommit(out string? error)
    {
        if (!TryParseArray(_actionsJson, "Actions", out var actions, out error) ||
            !TryParseArray(_keybindingsJson, "Keybindings", out var keybindings, out error))
        {
            ValidationMessage = error!;
            return false;
        }

        _settings.Actions = actions!;
        _settings.Keybindings = keybindings!;
        ValidationMessage = "Action and keybinding JSON is valid.";
        RefreshBindings(actions!, keybindings!);
        return true;
    }

    public void Validate()
    {
        if (!TryParseArray(_actionsJson, "Actions", out var actions, out var error) ||
            !TryParseArray(_keybindingsJson, "Keybindings", out var keybindings, out error))
        {
            ValidationMessage = error!;
            Bindings = [];
            return;
        }

        ValidationMessage = "Action and keybinding JSON is valid.";
        RefreshBindings(actions!, keybindings!);
    }

    private void SetJson(ref string field, string value)
    {
        if (SetProperty(ref field, value))
        {
            ValidationMessage = "JSON changed; validate or Apply to check it.";
            _changed();
        }
    }

    private void RefreshBindings(JsonArray actions, JsonArray keybindings)
    {
        var rows = new List<ActionBindingSummary>();
        AddSummaries(rows, actions, "actions");
        AddSummaries(rows, keybindings, "keybindings");
        Bindings = rows;
    }

    private static void AddSummaries(List<ActionBindingSummary> rows, JsonArray array, string source)
    {
        foreach (var item in array.OfType<JsonObject>())
        {
            var chord = JsonText(item["keys"]) ?? JsonText(item["key"]) ?? "unbound";
            var actionNode = item["command"] ?? item["action"];
            var action = actionNode is JsonObject command
                ? JsonText(command["action"]) ?? JsonText(command["command"]) ?? command.ToJsonString()
                : JsonText(actionNode) ?? "unknown";
            rows.Add(new(action, chord, source));
        }
    }

    internal static bool TryParseArray(
        string json,
        string label,
        out JsonArray? array,
        out string? error)
    {
        try
        {
            array = JsonNode.Parse(json) as JsonArray;
            if (array is null)
            {
                error = $"{label} must be a JSON array.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            array = null;
            error = $"{label} contains invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static string? JsonText(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (node is JsonArray array)
        {
            return string.Join(", ", array.Select(JsonText).Where(static item => item is not null));
        }

        return null;
    }
}

public sealed class NewTabMenuSettingsViewModel : ObservableObject
{
    private static readonly HashSet<string> SupportedTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "profile",
            "separator",
            "folder",
            "remainingProfiles",
            "matchProfiles",
            "action",
        };

    private readonly AppSettings _settings;
    private readonly Action _changed;
    private string _json;
    private readonly string _initialJson;
    private string _validationMessage = "JSON has not been validated.";

    public NewTabMenuSettingsViewModel(AppSettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
        _json = ReadUserSection(settings, "newTabMenu") ??
            SerializeKnownEntries(settings.NewTabMenu).ToJsonString(JsonFormatting.Options);
        _initialJson = _json;
        Validate();
    }

    public string Title => "New tab menu";
    public string Description => "Edit the polymorphic new-tab menu without discarding unknown properties.";
    public string Json
    {
        get => _json;
        set
        {
            if (SetProperty(ref _json, value))
            {
                ValidationMessage = "JSON changed; validate or Apply to check it.";
                _changed();
            }
        }
    }
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }
    public RelayCommand ValidateCommand => new(Validate);

    public bool TryCommit(out string? error)
    {
        if (string.Equals(_json, _initialJson, StringComparison.Ordinal))
        {
            error = null;
            return true;
        }

        if (!TryParse(out var array, out error))
        {
            ValidationMessage = error!;
            return false;
        }

        var user = new JsonObject { ["newTabMenu"] = array!.DeepClone() };
        var parsed = SettingsLoader.Load(
            SettingsLoader.ReadEmbeddedDefaults(),
            user.ToJsonString());
        _settings.NewTabMenu = parsed.NewTabMenu;
        ValidationMessage = "New-tab menu JSON is valid.";
        return true;
    }

    public void Validate()
    {
        ValidationMessage = TryParse(out _, out var error)
            ? "New-tab menu JSON is valid."
            : error!;
    }

    private bool TryParse(out JsonArray? array, out string? error)
    {
        if (!ActionsSettingsViewModel.TryParseArray(_json, "New-tab menu", out array, out error))
        {
            return false;
        }

        return ValidateEntries(array!, "newTabMenu", out error);
    }

    private static bool ValidateEntries(JsonArray entries, string path, out string? error)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index] is not JsonObject entry)
            {
                error = $"{path}[{index}] must be an object.";
                return false;
            }

            var type = entry["type"] is JsonValue typeValue &&
                typeValue.TryGetValue<string>(out var typeText)
                ? typeText
                : null;
            if (type is null || !SupportedTypes.Contains(type))
            {
                error = $"{path}[{index}] has unsupported type '{type ?? "(missing)"}'; it was not changed to avoid data loss.";
                return false;
            }

            if (string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase))
            {
                if (entry["entries"] is not JsonArray children)
                {
                    error = $"{path}[{index}].entries must be an array.";
                    return false;
                }

                if (!ValidateEntries(children, $"{path}[{index}].entries", out error))
                {
                    return false;
                }
            }
        }

        error = null;
        return true;
    }

    private static string? ReadUserSection(AppSettings settings, string name)
    {
        var document = JsonNode.Parse(SettingsLoader.SerializeUserDocument(settings)) as JsonObject;
        return document?[name]?.ToJsonString(JsonFormatting.Options);
    }

    private static JsonArray SerializeKnownEntries(IEnumerable<NewTabMenuEntry> entries) =>
        new(entries.Select(SerializeKnownEntry).ToArray());

    private static JsonObject SerializeKnownEntry(NewTabMenuEntry entry)
    {
        var result = new JsonObject
        {
            ["type"] = entry.Type switch
            {
                NewTabMenuEntryType.Profile => "profile",
                NewTabMenuEntryType.Separator => "separator",
                NewTabMenuEntryType.Folder => "folder",
                NewTabMenuEntryType.RemainingProfiles => "remainingProfiles",
                NewTabMenuEntryType.MatchProfiles => "matchProfiles",
                NewTabMenuEntryType.Action => "action",
                _ => "invalid",
            },
        };
        if (entry.Type == NewTabMenuEntryType.Profile)
        {
            result["profile"] = entry.Profile;
        }
        else if (entry.Type == NewTabMenuEntryType.Action)
        {
            result["action"] = entry.ActionId;
        }
        else if (entry.Type == NewTabMenuEntryType.Folder)
        {
            result["name"] = entry.Name;
            result["icon"] = entry.Icon?.Path;
            result["inline"] = entry.Inlining;
            result["allowEmpty"] = entry.AllowEmpty;
            result["entries"] = SerializeKnownEntries(entry.Entries);
        }
        else if (entry.Type == NewTabMenuEntryType.MatchProfiles)
        {
            result["name"] = entry.MatchName;
            result["commandline"] = entry.MatchCommandline;
            result["source"] = entry.MatchSource;
        }

        return result;
    }
}

public sealed class RenderingSettingsViewModel(AppSettings settings, Action changed)
    : SettingsPageViewModel("Rendering", "Configure the graphics API and rendering fallbacks.")
{
    public IReadOnlyList<string> TerminalEngines { get; } = ["Built-in", "Ghostty"];
    public string TerminalEngine
    {
        get => settings.TerminalEngine == TerminalEngineKind.Ghostty ? "Ghostty" : "Built-in";
        set => Change(
            settings.TerminalEngine,
            value.Equals("Ghostty", StringComparison.OrdinalIgnoreCase)
                ? TerminalEngineKind.Ghostty
                : TerminalEngineKind.BuiltIn,
            v => settings.TerminalEngine = v);
    }
    public string GraphicsApi { get => settings.GraphicsApi; set => Change(settings.GraphicsApi, value, v => settings.GraphicsApi = v); }
    public bool DisablePartialInvalidation { get => settings.DisablePartialInvalidation; set => Change(settings.DisablePartialInvalidation, value, v => settings.DisablePartialInvalidation = v); }
    public bool SoftwareRendering { get => settings.SoftwareRendering; set => Change(settings.SoftwareRendering, value, v => settings.SoftwareRendering = v); }
    public bool UseBackgroundImageForWindow { get => settings.UseBackgroundImageForWindow; set => Change(settings.UseBackgroundImageForWindow, value, v => settings.UseBackgroundImageForWindow = v); }

    private void Change<T>(T oldValue, T newValue, Action<T> update)
    {
        if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
        {
            update(newValue);
            changed();
        }
    }
}

public sealed class CompatibilitySettingsViewModel(AppSettings settings, Action changed)
    : SettingsPageViewModel("Compatibility", "Configure text measurement, character width, and compatibility switches.")
{
    public string TextMeasurement { get => settings.TextMeasurement; set => Change(settings.TextMeasurement, value, v => settings.TextMeasurement = v); }
    public string AmbiguousWidth { get => settings.AmbiguousWidth; set => Change(settings.AmbiguousWidth, value, v => settings.AmbiguousWidth = v); }
    public string DefaultInputScope { get => settings.DefaultInputScope; set => Change(settings.DefaultInputScope, value, v => settings.DefaultInputScope = v); }
    public bool AllowHeadless { get => settings.AllowHeadless; set => Change(settings.AllowHeadless, value, v => settings.AllowHeadless = v); }
    public bool EnableUnfocusedAcrylic { get => settings.EnableUnfocusedAcrylic; set => Change(settings.EnableUnfocusedAcrylic, value, v => settings.EnableUnfocusedAcrylic = v); }
    public bool InputServiceWarning { get => settings.InputServiceWarning; set => Change(settings.InputServiceWarning, value, v => settings.InputServiceWarning = v); }

    private void Change<T>(T oldValue, T newValue, Action<T> update)
    {
        if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
        {
            update(newValue);
            changed();
        }
    }
}

public sealed class ExtensionsSettingsViewModel(AppSettings settings, Action changed)
    : SettingsPageViewModel("Extensions and experimental", "Manage profile sources, notifications, and experimental features.")
{
    public string? Language { get => settings.Language; set => Change(settings.Language, value, v => settings.Language = v); }
    public bool DebugFeaturesEnabled { get => settings.DebugFeaturesEnabled; set => Change(settings.DebugFeaturesEnabled, value, v => settings.DebugFeaturesEnabled = v); }
    public bool AlwaysShowNotificationIcon { get => settings.AlwaysShowNotificationIcon; set => Change(settings.AlwaysShowNotificationIcon, value, v => settings.AlwaysShowNotificationIcon = v); }
    public bool MinimizeToNotificationArea { get => settings.MinimizeToNotificationArea; set => Change(settings.MinimizeToNotificationArea, value, v => settings.MinimizeToNotificationArea = v); }
    public bool ShowAdminShield { get => settings.ShowAdminShield; set => Change(settings.ShowAdminShield, value, v => settings.ShowAdminShield = v); }
    public bool EnableColorSelection { get => settings.EnableColorSelection; set => Change(settings.EnableColorSelection, value, v => settings.EnableColorSelection = v); }
    public bool EnableShellCompletionMenu { get => settings.EnableShellCompletionMenu; set => Change(settings.EnableShellCompletionMenu, value, v => settings.EnableShellCompletionMenu = v); }
    public string SearchWebDefaultQueryUrl { get => settings.SearchWebDefaultQueryUrl; set => Change(settings.SearchWebDefaultQueryUrl, value, v => settings.SearchWebDefaultQueryUrl = v); }
    public string DisabledProfileSources
    {
        get => string.Join(Environment.NewLine, settings.DisabledProfileSources);
        set => ChangeList(settings.DisabledProfileSources, value);
    }
    public string SafeUriSchemes
    {
        get => string.Join(Environment.NewLine, settings.SafeUriSchemes);
        set => ChangeList(settings.SafeUriSchemes, value);
    }

    private void Change<T>(T oldValue, T newValue, Action<T> update)
    {
        if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
        {
            update(newValue);
            changed();
        }
    }

    private void ChangeList(List<string> target, string value)
    {
        var items = value
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (target.SequenceEqual(items, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        target.Clear();
        target.AddRange(items);
        changed();
    }
}

internal static class JsonFormatting
{
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = true };
}
