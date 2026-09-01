using System.Text;
using Microsoft.Terminal.Settings;
using Xunit;

namespace Terminal.Settings.Tests;

public sealed class SettingsLoaderTests
{
    private const string Defaults = """
        {
            "defaultProfile": "{11111111-1111-1111-1111-111111111111}",
            "initialCols": 120,
            "profiles": {
                "defaults": {
                    "font": { "face": "Cascadia Mono", "size": 12 },
                    "historySize": 9001,
                    "colorScheme": "Campbell",
                    "commandline": "must-not-inherit.exe"
                },
                "list": [
                    {
                        "guid": "{11111111-1111-1111-1111-111111111111}",
                        "name": "PowerShell",
                        "commandline": "pwsh.exe"
                    }
                ]
            },
            "schemes": [
                {
                    "name": "Campbell",
                    "foreground": "#CCCCCC",
                    "background": "#0C0C0C"
                }
            ]
        }
        """;

    [Fact]
    public void EmbeddedDefaultsContainProfilesAndSchemes()
    {
        var settings = SettingsService.CreateDefault();

        Assert.NotEmpty(settings.Profiles);
        Assert.NotEmpty(settings.Schemes);
        Assert.Equal(120, settings.InitialCols);
        Assert.DoesNotContain(settings.Diagnostics, diagnostic =>
            diagnostic.Severity == SettingsDiagnosticSeverity.Error);
    }

    [Fact]
    public void AcceptsCommentsAndTrailingCommas()
    {
        const string user = """
            {
                // Windows Terminal permits comments.
                "initialCols": 132,
                "profiles": { "list": [], },
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);

        Assert.Equal(132, settings.InitialCols);
        Assert.DoesNotContain(settings.Diagnostics, diagnostic =>
            diagnostic.Code == "InvalidJson");
    }

    [Fact]
    public void AppliesProfileDefaultsButNotProhibitedFields()
    {
        var settings = SettingsLoader.Load(Defaults);
        var profile = Assert.Single(settings.Profiles);

        Assert.Equal("Cascadia Mono", profile.FontFace);
        Assert.Equal(12, profile.FontSize);
        Assert.Equal(9001, profile.HistorySize);
        Assert.Equal("pwsh.exe", profile.Commandline);
    }

    [Fact]
    public void UserProfileDefaultsOverrideInboxProfileValues()
    {
        const string user = """
            {
                "profiles": {
                    "defaults": { "historySize": 42 },
                    "list": []
                }
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);

        Assert.Equal(42, Assert.Single(settings.Profiles).HistorySize);
    }

    [Fact]
    public void LayersProfilesByGuidAndSchemesByName()
    {
        const string user = """
            {
                "profiles": {
                    "defaults": { "font": { "size": 15 } },
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "startingDirectory": "C:\\src"
                        }
                    ]
                },
                "schemes": [
                    { "name": "Campbell", "background": "#010203" }
                ]
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);
        var profile = Assert.Single(settings.Profiles);
        var scheme = settings.Schemes.Single(item => item.Name == "Campbell (modified)");

        Assert.Equal("PowerShell", profile.Name);
        Assert.Equal("pwsh.exe", profile.Commandline);
        Assert.Equal(@"C:\src", profile.StartingDirectory);
        Assert.Equal(15, profile.FontSize);
        Assert.Equal("Campbell (modified)", profile.DarkColorScheme);
        Assert.Equal("Campbell (modified)", profile.LightColorScheme);
        Assert.Equal("#010203", scheme.Background);
        Assert.Equal("#CCCCCC", scheme.Foreground);
        Assert.Contains(settings.Diagnostics, diagnostic => diagnostic.Code == "ColorSchemeRenamed");
    }

    [Fact]
    public void AcceptsLegacyProfilesArray()
    {
        const string legacy = """
            {
                "profiles": [
                    {
                        "guid": "{22222222-2222-2222-2222-222222222222}",
                        "name": "Legacy",
                        "commandline": "cmd.exe"
                    }
                ]
            }
            """;

        var settings = SettingsLoader.Load(Defaults, legacy);

        Assert.Equal(2, settings.Profiles.Count);
        Assert.Contains(settings.Profiles, profile => profile.Name == "Legacy");
    }

    [Fact]
    public void FragmentProfilesAreScopedByProvider()
    {
        const string fragment = """
            { "profiles": [{ "name": "Shell", "commandline": "shell.exe" }] }
            """;
        var fragments = new[]
        {
            new SettingsLayer(@"C:\Fragments\ProviderA\profiles.json", fragment, SettingsLayerKind.Fragment),
            new SettingsLayer(@"C:\Fragments\ProviderB\profiles.json", fragment, SettingsLayerKind.Fragment),
        };

        var settings = SettingsLoader.Load(Defaults, fragments: fragments);
        var shells = settings.Profiles.Where(profile => profile.Name == "Shell").ToArray();

        Assert.Equal(2, shells.Length);
        Assert.Contains(shells, profile => profile.Source == "ProviderA");
        Assert.Contains(shells, profile => profile.Source == "ProviderB");
        Assert.NotEqual(shells[0].Guid, shells[1].Guid);
    }

    [Fact]
    public void FragmentCannotSpoofProviderSource()
    {
        const string fragment = """
            {
                "profiles": [
                    {
                        "name": "Shell",
                        "source": "Spoofed.Provider",
                        "commandline": "shell.exe"
                    }
                ]
            }
            """;

        var settings = SettingsLoader.Load(
            Defaults,
            fragments:
            [
                new SettingsLayer(
                    @"C:\Fragments\Real.Provider\profiles.json",
                    fragment,
                    SettingsLayerKind.Fragment),
            ]);

        Assert.Equal("Real.Provider", settings.Profiles.Single(profile => profile.Name == "Shell").Source);
    }

    [Fact]
    public void FragmentUpdateLayersTargetWithoutAddingProfile()
    {
        const string fragment = """
            {
                "profiles": [
                    {
                        "updates": "{11111111-1111-1111-1111-111111111111}",
                        "historySize": 123
                    }
                ]
            }
            """;

        var settings = SettingsLoader.Load(
            Defaults,
            fragments:
            [
                new SettingsLayer("Provider", fragment, SettingsLayerKind.Fragment),
            ]);

        var profile = Assert.Single(settings.Profiles);
        Assert.Equal("PowerShell", profile.Name);
        Assert.Equal(123, profile.HistorySize);
    }

    [Fact]
    public void FragmentUpdateCanTargetUserCreatedProfile()
    {
        const string emptyDefaults = """
            {
                "profiles": { "defaults": {}, "list": [] },
                "schemes": [{ "name": "Campbell" }]
            }
            """;
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{33333333-3333-3333-3333-333333333333}",
                            "name": "User profile"
                        }
                    ]
                }
            }
            """;
        const string fragment = """
            {
                "profiles": [
                    {
                        "updates": "{33333333-3333-3333-3333-333333333333}",
                        "historySize": 123
                    }
                ]
            }
            """;

        var settings = SettingsLoader.Load(
            emptyDefaults,
            user,
            [new SettingsLayer("Provider", fragment, SettingsLayerKind.Fragment)]);

        var profile = Assert.Single(settings.Profiles);
        Assert.Equal("User profile", profile.Name);
        Assert.Equal(123, profile.HistorySize);
    }

    [Fact]
    public void EquivalentGuidFormatsLayerAsOneProfile()
    {
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "11111111-1111-1111-1111-111111111111",
                            "startingDirectory": "C:\\src"
                        }
                    ]
                }
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);

        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(@"{11111111-1111-1111-1111-111111111111}", profile.Guid);
        Assert.Equal(@"C:\src", profile.StartingDirectory);
    }

    [Fact]
    public void EquivalentDefaultProfileGuidDoesNotWarn()
    {
        const string user = """
            {
                "defaultProfile": "11111111-1111-1111-1111-111111111111"
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);

        Assert.DoesNotContain(settings.Diagnostics, diagnostic => diagnostic.Code == "MissingDefaultProfile");
        Assert.Equal("PowerShell", settings.GetDefaultProfile().Name);
    }

    [Fact]
    public void InvalidUserJsonProducesDiagnosticAndKeepsDefaults()
    {
        var settings = SettingsLoader.Load(Defaults, "{ invalid");

        Assert.Single(settings.Profiles);
        Assert.Contains(settings.Diagnostics, diagnostic =>
            diagnostic.Code == "InvalidJson" &&
            diagnostic.Severity == SettingsDiagnosticSeverity.Error);
    }

    [Fact]
    public void MissingDefaultProfileProducesWarning()
    {
        const string user = """
            { "defaultProfile": "{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}" }
            """;

        var settings = SettingsLoader.Load(Defaults, user);

        Assert.Contains(settings.Diagnostics, diagnostic =>
            diagnostic.Code == "MissingDefaultProfile" &&
            diagnostic.Severity == SettingsDiagnosticSeverity.Warning);
        Assert.Equal("PowerShell", settings.GetDefaultProfile().Name);
    }

    [Fact]
    public void DuplicateProfileGuidKeepsFirstAndWarns()
    {
        const string defaults = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "name": "First"
                        },
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "name": "Second"
                        }
                    ]
                },
                "schemes": [{ "name": "Campbell" }]
            }
            """;

        var settings = SettingsLoader.Load(defaults);

        Assert.Equal("First", Assert.Single(settings.Profiles).Name);
        Assert.Contains(settings.Diagnostics, diagnostic => diagnostic.Code == "DuplicateProfile");
    }

    [Fact]
    public void UnknownColorSchemeFallsBackAndWarns()
    {
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "colorScheme": "Missing"
                        }
                    ]
                }
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);

        Assert.Equal("Campbell", Assert.Single(settings.Profiles).ColorScheme);
        Assert.Contains(settings.Diagnostics, diagnostic => diagnostic.Code == "UnknownColorScheme");
    }

    [Fact]
    public void AllHiddenProfilesProducesError()
    {
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "hidden": true
                        }
                    ]
                }
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);

        Assert.Contains(settings.Diagnostics, diagnostic =>
            diagnostic.Code == "AllProfilesHidden" &&
            diagnostic.Severity == SettingsDiagnosticSeverity.Error);
    }

    [Fact]
    public void PreservesUnknownUserPropertiesWhenSerialized()
    {
        const string user = """
            {
                "$schema": "https://aka.ms/terminal-profiles-schema",
                "futureSetting": { "enabled": true }
            }
            """;
        var settings = SettingsLoader.Load(Defaults, user);

        var output = SettingsLoader.SerializeUserDocument(settings);

        Assert.Contains("\"futureSetting\"", output, StringComparison.Ordinal);
        Assert.Contains("\"enabled\": true", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializesTypedChanges()
    {
        var settings = SettingsLoader.Load(Defaults, """{ "futureSetting": true }""");
        settings.InitialCols = 222;
        settings.Profiles[0].FontSize = 18;

        var output = SettingsLoader.SerializeUserDocument(settings);
        var roundTrip = SettingsLoader.Load(Defaults, output);

        Assert.Equal(222, roundTrip.InitialCols);
        Assert.Equal(18, Assert.Single(roundTrip.Profiles).FontSize);
        Assert.Contains("\"futureSetting\": true", output, StringComparison.Ordinal);
    }

    [Fact]
    public void UnchangedSaveDoesNotFlattenInheritedProfileValues()
    {
        const string user = """
            {
                "profiles": {
                    "defaults": { "historySize": 42 },
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "startingDirectory": "C:\\src",
                            "futureProfileSetting": true
                        }
                    ]
                }
            }
            """;
        var settings = SettingsLoader.Load(Defaults, user);

        var output = SettingsLoader.SerializeUserDocument(settings);

        Assert.Contains("\"futureProfileSetting\": true", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"commandline\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"font\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileEditWritesOnlyChangedOverride()
    {
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "futureProfileSetting": true
                        }
                    ]
                }
            }
            """;
        var settings = SettingsLoader.Load(Defaults, user);
        settings.Profiles[0].FontSize = 18;

        var output = SettingsLoader.SerializeUserDocument(settings);

        Assert.Contains("\"futureProfileSetting\": true", output, StringComparison.Ordinal);
        Assert.Contains("\"size\": 18", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"commandline\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingInheritedProfileWritesHiddenOverride()
    {
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "startingDirectory": "C:\\src"
                        }
                    ]
                }
            }
            """;
        var settings = SettingsLoader.Load(Defaults, user);
        settings.Profiles.Clear();

        var output = SettingsLoader.SerializeUserDocument(settings);
        var roundTrip = SettingsLoader.Load(Defaults, output);

        Assert.True(Assert.Single(roundTrip.Profiles).Hidden);
    }

    [Fact]
    public void RemovingEnvironmentVariableWritesNullOverride()
    {
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "environment": {
                                "REMOVE_ME": "old",
                                "KEEP_ME": "value"
                            }
                        }
                    ]
                }
            }
            """;
        var settings = SettingsLoader.Load(Defaults, user);
        settings.Profiles[0].Environment.Remove("REMOVE_ME");

        var output = SettingsLoader.SerializeUserDocument(settings);
        var roundTrip = SettingsLoader.Load(Defaults, output);

        Assert.True(roundTrip.Profiles[0].Environment.ContainsKey("REMOVE_ME"));
        Assert.Null(roundTrip.Profiles[0].Environment["REMOVE_ME"]);
        Assert.Equal("value", roundTrip.Profiles[0].Environment["KEEP_ME"]);
    }

    [Fact]
    public void ThemeEditIsSerialized()
    {
        const string user = """
            {
                "themes": [
                    {
                        "name": "custom",
                        "window": { "applicationTheme": "dark" }
                    }
                ]
            }
            """;
        var settings = SettingsLoader.Load(Defaults, user);
        var theme = Assert.Single(settings.Themes);
        theme.UseMica = true;

        var output = SettingsLoader.SerializeUserDocument(settings);

        Assert.Contains("\"useMica\": true", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", CloseOnExitMode.Graceful)]
    [InlineData("false", CloseOnExitMode.Never)]
    [InlineData("\"always\"", CloseOnExitMode.Always)]
    [InlineData("\"automatic\"", CloseOnExitMode.Automatic)]
    public void ParsesCloseOnExitCompatibilityForms(string value, CloseOnExitMode expected)
    {
        var user = $$"""
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "closeOnExit": {{value}}
                        }
                    ]
                }
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);

        Assert.Equal(expected, Assert.Single(settings.Profiles).CloseOnExit);
    }

    [Fact]
    public void GeneratedProfileGuidMatchesUpstreamUuidV5()
    {
        var namespaceId = new Guid("ad56de9e-5167-41b6-80eb-fb19f7927d1a");

        var actual = ProfileGuid.CreateV5(namespaceId, Encoding.Unicode.GetBytes("testing"));

        Assert.Equal(new Guid("e04fb1f7-739d-5d63-bb18-e0ea00b19ee8"), actual);
    }

    [Fact]
    public void GeneratedProfileGuidsAreStableAndSourceScoped()
    {
        var first = ProfileGuid.Create("Ubuntu", "Windows.Terminal.Wsl");
        var second = ProfileGuid.Create("Ubuntu", "Windows.Terminal.Wsl");
        var otherSource = ProfileGuid.Create("Ubuntu", "Example.Source");

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherSource);
    }

    [Fact]
    public void DynamicProfileGuidMatchesUpstreamNamespace()
    {
        Assert.Equal(
            new Guid("2c4de342-38b7-51cf-b940-2309a097f518"),
            ProfileGuid.CreateDynamic("Ubuntu"));
    }
}
