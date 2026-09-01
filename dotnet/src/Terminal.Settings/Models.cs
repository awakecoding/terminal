using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Terminal.Core;

namespace Microsoft.Terminal.Settings;

public enum SettingsOrigin
{
    None,
    User,
    Inbox,
    Generated,
    Fragment,
    ProfilesDefaults,
}

public enum LaunchMode
{
    Default,
    Maximized,
    Fullscreen,
    Focus,
    MaximizedFocus,
}

public enum ConfirmOnClose
{
    Never,
    Automatic,
    Always,
}

public enum CloseOnExitMode
{
    Never,
    Graceful,
    Always,
    Automatic,
}

public enum TabWidthMode
{
    Equal,
    TitleLength,
    Compact,
}

[Flags]
public enum CopyFormat
{
    None = 0,
    Html = 1,
    Rtf = 2,
    All = Html | Rtf,
}

public enum NewTabMenuEntryType
{
    Invalid,
    Profile,
    Separator,
    Folder,
    RemainingProfiles,
    MatchProfiles,
    Action,
}

public sealed class ThemePair
{
    public string DarkName { get; set; } = "system";
    public string LightName { get; set; } = "system";

    public override string ToString() =>
        string.Equals(DarkName, LightName, StringComparison.Ordinal) ? DarkName : $"{DarkName}/{LightName}";
}

public sealed class MediaResource
{
    public string? Path { get; set; }
    public string? ResolvedPath { get; private set; }
    public bool IsValid { get; private set; } = true;

    public void Resolve(string value)
    {
        ResolvedPath = value;
        IsValid = true;
    }

    public void Reject()
    {
        ResolvedPath = null;
        IsValid = false;
    }

    public override string? ToString() => ResolvedPath ?? Path;
}

public sealed class NewTabMenuEntry
{
    public NewTabMenuEntryType Type { get; set; }
    public string? Profile { get; set; }
    public string? ActionId { get; set; }
    public string? Name { get; set; }
    public MediaResource? Icon { get; set; }
    public string Inlining { get; set; } = "never";
    public bool AllowEmpty { get; set; }
    public string? MatchName { get; set; }
    public string? MatchCommandline { get; set; }
    public string? MatchSource { get; set; }
    public List<NewTabMenuEntry> Entries { get; set; } = [];

    [JsonIgnore]
    internal JsonObject? SourceDocument { get; set; }
}

public sealed class AppSettings
{
    // App-global settings.
    public string? Language { get; set; }
    public bool InputServiceWarning { get; set; } = true;
    public string FirstWindowPreference { get; set; } = "defaultProfile";
    public bool DebugFeaturesEnabled { get; set; }
    public string WindowingBehavior { get; set; } = "useNew";
    public bool AlwaysShowNotificationIcon { get; set; }
    public List<string> DisabledProfileSources { get; set; } = [];
    public bool AllowHeadless { get; set; }
    public bool EnableColorSelection { get; set; }

    // Per-window settings.
    public string? DefaultProfile { get; set; }
    public int InitialCols { get; set; } = 80;
    public int InitialRows { get; set; } = 30;
    public string? InitialPosition { get; set; }
    public bool CenterOnLaunch { get; set; }
    public LaunchMode LaunchMode { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool AutoHideWindow { get; set; }
    public bool AlwaysShowTabs { get; set; } = true;
    public bool ShowTabsInTitlebar { get; set; } = true;
    public bool ShowTerminalTitleInTitlebar { get; set; } = true;
    public bool ShowTabsFullscreen { get; set; }
    public bool CopyOnSelect { get; set; }
    public bool CopyFormatting { get; set; }
    public CopyFormat CopyFormatFormats { get; set; }
    public bool TrimBlockSelection { get; set; } = true;
    public bool TrimPaste { get; set; } = true;
    public bool FocusFollowMouse { get; set; }
    public bool ScrollToZoom { get; set; } = true;
    public bool ScrollToChangeOpacity { get; set; } = true;
    public string GraphicsApi { get; set; } = "automatic";
    public bool DisablePartialInvalidation { get; set; }
    public bool SoftwareRendering { get; set; }
    public string TextMeasurement { get; set; } = "graphemes";
    public string AmbiguousWidth { get; set; } = "narrow";
    public string DefaultInputScope { get; set; } = "default";
    public bool UseBackgroundImageForWindow { get; set; }
    public bool DetectUrls { get; set; } = true;
    public string NewTabPosition { get; set; } = "afterLastTab";
    public ConfirmOnClose ConfirmOnClose { get; set; } = ConfirmOnClose.Automatic;
    public ThemePair Theme { get; set; } = new();
    public TabWidthMode TabWidthMode { get; set; }
    public bool UseAcrylicInTabRow { get; set; }
    public bool WarnAboutLargePaste { get; set; } = true;
    public string WarnAboutMultiLinePaste { get; set; } = "automatic";
    public bool SnapToGridOnResize { get; set; } = true;
    public string TabSwitcherMode { get; set; } = "inOrder";
    public bool DisableAnimations { get; set; }
    public string StartupActions { get; set; } = string.Empty;
    public bool MinimizeToNotificationArea { get; set; }
    public List<string> SafeUriSchemes { get; set; } = [];
    public bool ShowAdminShield { get; set; } = true;
    public bool EnableShellCompletionMenu { get; set; }
    public bool EnableUnfocusedAcrylic { get; set; } = true;
    public List<NewTabMenuEntry> NewTabMenu { get; set; } =
    [
        new() { Type = NewTabMenuEntryType.RemainingProfiles },
    ];
    public string SearchWebDefaultQueryUrl { get; set; } = "https://www.bing.com/search?q=%22%s%22";
    public string WordDelimiters { get; set; } = " /\\()\"'-.,:;<>~!@#$%^&*|+=[]{}~?\u2502";

    // Raw arrays are retained for lossless settings.json round-tripping. Runtime
    // consumers should use ActionMap.
    public ProfileSettings ProfileDefaults { get; set; } = new() { Origin = SettingsOrigin.ProfilesDefaults };
    public List<ProfileSettings> Profiles { get; set; } = [];
    public List<SchemeSettings> Schemes { get; set; } = [];
    public List<ThemeSettings> Themes { get; set; } = [];
    public JsonArray Actions { get; set; } = [];
    public JsonArray Keybindings { get; set; } = [];
    [JsonIgnore]
    public ActionMap ActionMap { get; internal set; } = new();
    public List<SettingsDiagnostic> Diagnostics { get; } = [];

    [JsonIgnore]
    internal JsonObject? UserDocument { get; set; }

    [JsonIgnore]
    internal JsonObject? ResolvedSnapshot { get; set; }

    [JsonIgnore]
    internal HashSet<string> InheritedProfileIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ProfileSettings GetDefaultProfile()
    {
        if (!string.IsNullOrWhiteSpace(DefaultProfile))
        {
            var hasDefaultGuid = Guid.TryParse(DefaultProfile, out var defaultGuid);
            var match = Profiles.Find(p =>
                (hasDefaultGuid && Guid.TryParse(p.Guid, out var profileGuid) && profileGuid == defaultGuid) ||
                string.Equals(p.Name, DefaultProfile, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return Profiles.Count > 0 ? Profiles[0] : ProfileSettings.CreatePowerShell();
    }
}

public sealed class FontSettings
{
    public string Face { get; set; } = "Cascadia Mono";
    public double Size { get; set; } = 12;
    public int Weight { get; set; } = 400;
    public Dictionary<string, double> Axes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> Features { get; set; } = new(StringComparer.Ordinal);
    public bool BuiltinGlyphs { get; set; } = true;
    public bool ColorGlyphs { get; set; } = true;
    public string? CellWidth { get; set; }
    public string? CellHeight { get; set; }
}

public sealed class AppearanceSettings
{
    public string CursorShape { get; set; } = "bar";
    public int CursorHeight { get; set; } = 25;
    public string? Foreground { get; set; }
    public string? Background { get; set; }
    public string? SelectionBackground { get; set; }
    public string? CursorColor { get; set; }
    public MediaResource? BackgroundImage { get; set; }
    public double BackgroundImageOpacity { get; set; } = 1;
    public string BackgroundImageStretchMode { get; set; } = "uniformToFill";
    public string BackgroundImageAlignment { get; set; } = "center";
    public bool RetroTerminalEffect { get; set; }
    public MediaResource? PixelShaderPath { get; set; }
    public MediaResource? PixelShaderImagePath { get; set; }
    public string IntenseTextStyle { get; set; } = "bright";
    public string AdjustIndistinguishableColors { get; set; } = "automatic";
    public bool UseAcrylic { get; set; }
    public int Opacity { get; set; } = 100;
    public string DarkColorScheme { get; set; } = "Campbell";
    public string LightColorScheme { get; set; } = "Campbell";
}

public sealed class ProfileSettings
{
    public string? Guid { get; set; }
    public string Name { get; set; } = "Windows PowerShell";
    public string? Source { get; set; }
    public SettingsOrigin Origin { get; set; }
    public string? SourcePath { get; set; }
    public bool Hidden { get; set; }
    public string Commandline { get; set; } = @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe";
    public string StartingDirectory { get; set; } = "%USERPROFILE%";
    public MediaResource? IconResource { get; set; }
    public string? Icon
    {
        get => IconResource?.Path;
        set => IconResource = value is null ? null : new MediaResource { Path = value };
    }

    public string? ConnectionType { get; set; }
    public string DarkColorScheme { get; set; } = "Campbell";
    public string LightColorScheme { get; set; } = "Campbell";
    public string ColorScheme
    {
        get => DarkColorScheme;
        set
        {
            DarkColorScheme = value;
            LightColorScheme = value;
        }
    }
    public FontSettings Font { get; set; } = new();
    public string FontFace { get => Font.Face; set => Font.Face = value; }
    public double FontSize { get => Font.Size; set => Font.Size = value; }
    public int FontWeight { get => Font.Weight; set => Font.Weight = value; }
    public int HistorySize { get; set; } = 9001;
    public string Padding { get; set; } = "8";
    public string CursorShape { get; set; } = "bar";
    public int CursorHeight { get; set; } = 25;
    public CloseOnExitMode CloseOnExit { get; set; } = CloseOnExitMode.Automatic;
    public string? TabTitle { get; set; }
    public string? TabColor { get; set; }
    public bool SuppressApplicationTitle { get; set; }
    public bool UseAcrylic { get; set; }
    public int Opacity { get; set; } = 100;
    public string? Foreground { get; set; }
    public string? Background { get; set; }
    public string? SelectionBackground { get; set; }
    public string? CursorColor { get; set; }
    public MediaResource? BackgroundImageResource { get; set; }
    public string? BackgroundImage
    {
        get => BackgroundImageResource?.Path;
        set => BackgroundImageResource = value is null ? null : new MediaResource { Path = value };
    }

    public double BackgroundImageOpacity { get; set; } = 1;
    public string BackgroundImageStretchMode { get; set; } = "uniformToFill";
    public string BackgroundImageAlignment { get; set; } = "center";
    public bool RetroTerminalEffect { get; set; }
    public MediaResource? PixelShaderPath { get; set; }
    public MediaResource? PixelShaderImagePath { get; set; }
    public string IntenseTextStyle { get; set; } = "bright";
    public string AdjustIndistinguishableColors { get; set; } = "automatic";
    public AppearanceSettings? UnfocusedAppearance { get; set; }
    public bool SnapOnInput { get; set; } = true;
    public bool AltGrAliasing { get; set; } = true;
    public string? AnswerbackMessage { get; set; }
    public string ScrollbarState { get; set; } = "visible";
    public string AntialiasingMode { get; set; } = "grayscale";
    public BellStyle BellStyle { get; set; } = BellStyle.Audible;
    public List<MediaResource> BellSound { get; set; } = [];
    public bool RightClickContextMenu { get; set; }
    public bool Elevate { get; set; }
    public bool AutoMarkPrompts { get; set; } = true;
    public bool ShowMarksOnScrollbar { get; set; }
    public bool RepositionCursorWithMouse { get; set; }
    public bool ReloadEnvironmentVariables { get; set; } = true;
    public bool RainbowSuggestions { get; set; }
    public bool ForceVtInput { get; set; }
    public bool AllowKittyKeyboardMode { get; set; } = true;
    public bool AllowVtChecksumReport { get; set; }
    public bool AllowVtClipboardWrite { get; set; } = true;
    public bool AllowOscNotifications { get; set; }
    public bool AllowKeypadMode { get; set; }
    public string DragDropDelimiter { get; set; } = " ";
    public string PathTranslationStyle { get; set; } = "none";
    public Dictionary<string, string?> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    internal JsonObject? SourceDocument { get; set; }

    public static ProfileSettings CreatePowerShell() => new()
    {
        Guid = "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}",
        Name = "Windows PowerShell",
        Commandline = @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe",
        Origin = SettingsOrigin.Inbox,
    };

    public static ProfileSettings CreateCmd() => new()
    {
        Guid = "{0caa0dad-35be-5f56-a8ff-afceeeaa6101}",
        Name = "Command Prompt",
        Commandline = @"%SystemRoot%\System32\cmd.exe",
        Origin = SettingsOrigin.Inbox,
    };

    public static ProfileSettings CreatePwsh(string commandline = "pwsh.exe") => new()
    {
        Guid = "{574e775e-4f2a-5b96-ac1e-a2962a157abf}",
        Name = "PowerShell",
        Commandline = commandline,
        Origin = SettingsOrigin.Generated,
    };

    public ColorScheme ResolveScheme() => Core.ColorScheme.FromName(ColorScheme);
    public string ExpandCommandline() => System.Environment.ExpandEnvironmentVariables(Commandline);

    public string ExpandStartingDirectory()
    {
        var directory = System.Environment.ExpandEnvironmentVariables(StartingDirectory);
        return Directory.Exists(directory)
            ? directory
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
    }
}

[Flags]
public enum BellStyle
{
    None = 0,
    Audible = 1,
    Window = 2,
    Taskbar = 4,
    Notification = 8,
    All = Audible | Window | Taskbar | Notification,
}

public sealed class SchemeSettings
{
    public string Name { get; set; } = "Campbell";
    public SettingsOrigin Origin { get; set; }
    public string? SourcePath { get; set; }
    public string Foreground { get; set; } = "#CCCCCC";
    public string Background { get; set; } = "#0C0C0C";
    public string CursorColor { get; set; } = "#FFFFFF";
    public string SelectionBackground { get; set; } = "#FFFFFF";
    public string Black { get; set; } = "#0C0C0C";
    public string Red { get; set; } = "#C50F1F";
    public string Green { get; set; } = "#13A10E";
    public string Yellow { get; set; } = "#C19C00";
    public string Blue { get; set; } = "#0037DA";
    public string Purple { get; set; } = "#881798";
    public string Cyan { get; set; } = "#3A96DD";
    public string White { get; set; } = "#CCCCCC";
    public string BrightBlack { get; set; } = "#767676";
    public string BrightRed { get; set; } = "#E74856";
    public string BrightGreen { get; set; } = "#16C60C";
    public string BrightYellow { get; set; } = "#F9F1A5";
    public string BrightBlue { get; set; } = "#3B78FF";
    public string BrightPurple { get; set; } = "#B4009E";
    public string BrightCyan { get; set; } = "#61D6D6";
    public string BrightWhite { get; set; } = "#F2F2F2";

    [JsonIgnore]
    internal JsonObject? SourceDocument { get; set; }
}

public sealed class ThemeColor
{
    public string Value { get; set; } = "accent";
}

public sealed class WindowThemeSettings
{
    public string ApplicationTheme { get; set; } = "system";
    public ThemeColor? Frame { get; set; }
    public ThemeColor? UnfocusedFrame { get; set; }
    public bool RainbowFrame { get; set; }
    public bool UseMica { get; set; }
    public bool ShowWorkspacesButton { get; set; } = true;
}

public sealed class SettingsThemeSettings
{
    public string Theme { get; set; } = "system";
}

public sealed class TabRowThemeSettings
{
    public ThemeColor? Background { get; set; }
    public ThemeColor? UnfocusedBackground { get; set; }
}

public sealed class TabThemeSettings
{
    public ThemeColor? Background { get; set; }
    public ThemeColor? UnfocusedBackground { get; set; }
    public string IconStyle { get; set; } = "default";
    public string ShowCloseButton { get; set; } = "always";
}

public sealed class ThemeSettings
{
    public string Name { get; set; } = "system";
    public SettingsOrigin Origin { get; set; }
    public string? SourcePath { get; set; }
    public WindowThemeSettings? Window { get; set; }
    public SettingsThemeSettings? Settings { get; set; }
    public TabRowThemeSettings? TabRow { get; set; }
    public TabThemeSettings? Tab { get; set; }

    // Compatibility accessors retained for the initial app-shell implementation.
    public string? WindowApplicationTheme
    {
        get => Window?.ApplicationTheme;
        set
        {
            if (value is null && Window is null)
            {
                return;
            }

            Window ??= new();
            Window.ApplicationTheme = value ?? "system";
        }
    }

    public bool? UseMica
    {
        get => Window?.UseMica;
        set
        {
            if (value is null && Window is null)
            {
                return;
            }

            Window ??= new();
            Window.UseMica = value ?? false;
        }
    }

    public string? TabRowBackground
    {
        get => TabRow?.Background?.Value;
        set
        {
            if (value is null && TabRow is null)
            {
                return;
            }

            TabRow ??= new();
            TabRow.Background = value is null ? null : new ThemeColor { Value = value };
        }
    }

    [JsonIgnore]
    internal JsonObject? SourceDocument { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ProfileSettings))]
[JsonSerializable(typeof(FontSettings))]
[JsonSerializable(typeof(AppearanceSettings))]
[JsonSerializable(typeof(SchemeSettings))]
[JsonSerializable(typeof(ThemeSettings))]
[JsonSerializable(typeof(MediaResource))]
[JsonSerializable(typeof(NewTabMenuEntry))]
[JsonSerializable(typeof(ApplicationStateData))]
[JsonSerializable(typeof(WindowLayoutState))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(Dictionary<string, WindowLayoutState>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<Guid>))]
internal partial class SettingsJsonContext : JsonSerializerContext;
