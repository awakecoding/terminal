using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Terminal.Core;

namespace Microsoft.Terminal.Settings;

public sealed class AppSettings
{
    public string? DefaultProfile { get; set; }
    public int InitialCols { get; set; } = 120;
    public int InitialRows { get; set; } = 30;
    public bool AlwaysShowTabs { get; set; } = true;
    public bool CopyOnSelect { get; set; }
    public string Theme { get; set; } = "dark";
    public string WordDelimiters { get; set; } = " /\\()\"'-.,:;<>~!@#$%^&*|+=[]{}~?";
    public List<ProfileSettings> Profiles { get; set; } = [];
    public List<SchemeSettings> Schemes { get; set; } = [];

    public ProfileSettings GetDefaultProfile()
    {
        if (!string.IsNullOrWhiteSpace(DefaultProfile))
        {
            var match = Profiles.Find(p => string.Equals(p.Guid, DefaultProfile, StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(p.Name, DefaultProfile, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return Profiles.Count > 0 ? Profiles[0] : ProfileSettings.PowerShell;
    }
}

public sealed class ProfileSettings
{
    public string Guid { get; set; } = System.Guid.NewGuid().ToString("B");
    public string Name { get; set; } = "Windows PowerShell";
    public string Commandline { get; set; } = @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe";
    public string ColorScheme { get; set; } = "Campbell";
    public string FontFace { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 12;
    public int HistorySize { get; set; } = 9001;
    public string StartingDirectory { get; set; } = "%USERPROFILE%";
    public string CursorShape { get; set; } = "bar";
    public string Padding { get; set; } = "8";

    public static ProfileSettings PowerShell { get; } = new()
    {
        Guid = "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}",
        Name = "Windows PowerShell",
        Commandline = @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe",
    };

    public static ProfileSettings Cmd { get; } = new()
    {
        Guid = "{0caa0dad-35be-5f56-a8ff-afceeeaa6101}",
        Name = "Command Prompt",
        Commandline = @"%SystemRoot%\System32\cmd.exe",
    };

    public static ProfileSettings Pwsh { get; } = new()
    {
        Guid = "{574e775e-4f2a-5b96-ac1e-a2962a157abf}",
        Name = "PowerShell",
        Commandline = "pwsh.exe",
    };

    public ColorScheme ResolveScheme() => Core.ColorScheme.FromName(ColorScheme);

    public string ExpandCommandline() => Environment.ExpandEnvironmentVariables(Commandline);

    public string ExpandStartingDirectory()
    {
        var directory = Environment.ExpandEnvironmentVariables(StartingDirectory);
        return Directory.Exists(directory) ? directory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}

public sealed class SchemeSettings
{
    public string Name { get; set; } = "Campbell";
    public string Foreground { get; set; } = "#CCCCCC";
    public string Background { get; set; } = "#0C0C0C";
    public string CursorColor { get; set; } = "#FFFFFF";
    public string SelectionBackground { get; set; } = "#FFFFFF";
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext;
