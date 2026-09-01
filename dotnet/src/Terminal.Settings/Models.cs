using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Terminal.Core;

namespace Microsoft.Terminal.Settings;

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

public sealed class AppSettings
{
    public string? DefaultProfile { get; set; }
    public int InitialCols { get; set; } = 120;
    public int InitialRows { get; set; } = 30;
    public LaunchMode LaunchMode { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool AlwaysShowTabs { get; set; } = true;
    public bool ShowTabsInTitlebar { get; set; } = true;
    public bool ShowTerminalTitleInTitlebar { get; set; } = true;
    public bool ShowTabsFullscreen { get; set; }
    public bool CopyOnSelect { get; set; }
    public bool CopyFormatting { get; set; } = true;
    public bool TrimBlockSelection { get; set; } = true;
    public bool TrimPaste { get; set; } = true;
    public bool FocusFollowMouse { get; set; }
    public bool SnapToGridOnResize { get; set; } = true;
    public bool DisableAnimations { get; set; }
    public bool MinimizeToNotificationArea { get; set; }
    public bool AlwaysShowNotificationIcon { get; set; }
    public bool ShowAdminShield { get; set; } = true;
    public string Theme { get; set; } = "dark";
    public string StartupActions { get; set; } = string.Empty;
    public string WordDelimiters { get; set; } = " /\\()\"'-.,:;<>~!@#$%^&*|+=[]{}~?\u2502";
    public TabWidthMode TabWidthMode { get; set; }
    public ConfirmOnClose ConfirmOnClose { get; set; } = ConfirmOnClose.Automatic;
    public ProfileSettings ProfileDefaults { get; set; } = new();
    public List<ProfileSettings> Profiles { get; set; } = [];
    public List<SchemeSettings> Schemes { get; set; } = [];
    public List<ThemeSettings> Themes { get; set; } = [];
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
            var hasDefaultGuid = System.Guid.TryParse(DefaultProfile, out var defaultGuid);
            var match = Profiles.Find(p =>
                (hasDefaultGuid && System.Guid.TryParse(p.Guid, out var profileGuid) && profileGuid == defaultGuid) ||
                string.Equals(p.Name, DefaultProfile, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return Profiles.Count > 0 ? Profiles[0] : ProfileSettings.CreatePowerShell();
    }
}

public sealed class ProfileSettings
{
    public string? Guid { get; set; }
    public string Name { get; set; } = "Windows PowerShell";
    public string? Source { get; set; }
    public bool Hidden { get; set; }
    public string Commandline { get; set; } = @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe";
    public string StartingDirectory { get; set; } = "%USERPROFILE%";
    public string? Icon { get; set; }
    public string ColorScheme { get; set; } = "Campbell";
    public string FontFace { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 12;
    public int FontWeight { get; set; } = 400;
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
    public string? BackgroundImage { get; set; }
    public double BackgroundImageOpacity { get; set; } = 1;
    public string BackgroundImageStretchMode { get; set; } = "uniformToFill";
    public bool SnapOnInput { get; set; } = true;
    public bool AltGrAliasing { get; set; } = true;
    public bool Elevate { get; set; }
    public bool AutoMarkPrompts { get; set; } = true;
    public bool ShowMarksOnScrollbar { get; set; }
    public bool ReloadEnvironmentVariables { get; set; } = true;
    public bool AllowKittyKeyboardMode { get; set; } = true;
    public bool AllowVtClipboardWrite { get; set; } = true;
    public bool AllowOscNotifications { get; set; }
    public Dictionary<string, string?> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    internal JsonObject? SourceDocument { get; set; }

    public static ProfileSettings CreatePowerShell() => new()
    {
        Guid = "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}",
        Name = "Windows PowerShell",
        Commandline = @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe",
    };

    public static ProfileSettings CreateCmd() => new()
    {
        Guid = "{0caa0dad-35be-5f56-a8ff-afceeeaa6101}",
        Name = "Command Prompt",
        Commandline = @"%SystemRoot%\System32\cmd.exe",
    };

    public static ProfileSettings CreatePwsh(string commandline = "pwsh.exe") => new()
    {
        Guid = "{574e775e-4f2a-5b96-ac1e-a2962a157abf}",
        Name = "PowerShell",
        Commandline = commandline,
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

public sealed class SchemeSettings
{
    public string Name { get; set; } = "Campbell";
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

public sealed class ThemeSettings
{
    public string Name { get; set; } = "system";
    public string? WindowApplicationTheme { get; set; }
    public bool? UseMica { get; set; }
    public string? TabRowBackground { get; set; }

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
[JsonSerializable(typeof(SchemeSettings))]
[JsonSerializable(typeof(ThemeSettings))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
internal partial class SettingsJsonContext : JsonSerializerContext;
