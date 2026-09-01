using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Terminal.Settings;
using Xunit;

namespace Terminal.Settings.Tests;

public sealed class DynamicProfileGeneratorTests
{
    [Fact]
    public async Task PowerShellProfilesAreStableAndBestVersionGetsLegacyGuid()
    {
        using var fixture = new DirectoryFixture();
        var stable = fixture.Touch("ProgramFiles", "PowerShell", "7", "pwsh.exe");
        fixture.Touch("ProgramFiles", "PowerShell", "7-preview", "pwsh.exe");
        fixture.Touch("ProgramFiles", "PowerShell", "6", "pwsh.exe");
        var environment = fixture.Environment();

        var result = await new PowerShellCoreProfileGenerator(environment).GenerateAsync(default);

        Assert.Equal(3, result.Profiles.Count);
        Assert.Equal("PowerShell", result.Profiles[0].Name);
        Assert.Equal("{574e775e-4f2a-5b96-ac1e-a2962a402336}", result.Profiles[0].Guid);
        Assert.Equal($"\"{stable}\"", result.Profiles[0].Commandline);
        Assert.Equal(DynamicProfileSource.PowerShellCore, result.Profiles[0].Source);
        Assert.Contains(result.Profiles, profile => profile.Name == "PowerShell 7 Preview");
        Assert.Equal(
            result.Profiles.Select(profile => profile.Guid),
            (await new PowerShellCoreProfileGenerator(environment).GenerateAsync(default))
                .Profiles.Select(profile => profile.Guid));
    }

    [Fact]
    public async Task X86PowerShellNameAndGuidMatchUpstreamSeed()
    {
        using var fixture = new DirectoryFixture();
        fixture.Touch("ProgramFilesX86", "PowerShell", "7", "pwsh.exe");

        var result = await new PowerShellCoreProfileGenerator(fixture.Environment()).GenerateAsync(default);

        var profile = Assert.Single(result.Profiles);
        Assert.Equal("PowerShell", profile.Name);
        Assert.Equal("{574e775e-4f2a-5b96-ac1e-a2962a402336}", profile.Guid);

        fixture.Touch("ProgramFiles", "PowerShell", "8", "pwsh.exe");
        result = await new PowerShellCoreProfileGenerator(fixture.Environment()).GenerateAsync(default);
        var x86 = Assert.Single(result.Profiles, item => item.Name == "PowerShell 7 (x86)");
        Assert.Equal(ProfileGuid.CreateDynamic("PowerShell 7 (x86)").ToString("B"), x86.Guid);
    }

    [Fact]
    public async Task InboxShellsUseUpstreamGuidsAndCommands()
    {
        using var fixture = new DirectoryFixture();
        fixture.Touch("System32", "cmd.exe");
        fixture.Touch("System32", "WindowsPowerShell", "v1.0", "powershell.exe");

        var result = await new InboxShellProfileGenerator(fixture.Environment()).GenerateAsync(default);

        Assert.Collection(
            result.Profiles,
            profile =>
            {
                Assert.Equal("{61c54bbd-c2c6-5271-96e7-009a87ff44bf}", profile.Guid);
                Assert.Equal(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe", profile.Commandline);
            },
            profile =>
            {
                Assert.Equal("{0caa0dad-35be-5f56-a8ff-afceeeaa6101}", profile.Guid);
                Assert.Equal(@"%SystemRoot%\System32\cmd.exe", profile.Commandline);
            });
    }

    [Fact]
    public async Task WslProfilesAreSortedFilteredAndUseSystemWsl()
    {
        using var fixture = new DirectoryFixture();
        fixture.Touch("System32", "wsl.exe");
        var runner = new StubRunner(new DynamicProfileCommandResult(
            0,
            "Ubuntu\0\r\nDocker-Desktop\0\r\nDebian\0\r\nubuntu\0",
            string.Empty,
            false));

        var result = await new WslDistroProfileGenerator(
            runner,
            fixture.Environment(),
            TimeSpan.FromMilliseconds(50)).GenerateAsync(default);

        Assert.Equal(["Debian", "Ubuntu"], result.Profiles.Select(profile => profile.Name));
        Assert.Equal(
            "{2c4de342-38b7-51cf-b940-2309a097f518}",
            result.Profiles.Single(profile => profile.Name == "Ubuntu").Guid);
        Assert.All(result.Profiles, profile =>
        {
            Assert.Equal("~", profile.StartingDirectory);
            Assert.Equal("wsl", profile.PathTranslationStyle);
            Assert.StartsWith($"\"{fixture.PathOf("System32", "wsl.exe")}\" -d ", profile.Commandline);
        });
        Assert.Equal(["--list", "--quiet"], runner.LastCommand!.Arguments);
        Assert.Equal(Encoding.Unicode.WebName, runner.LastCommand.StandardOutputEncoding!.WebName);
    }

    [Fact]
    public async Task WslTimeoutProducesExplicitDiagnostic()
    {
        using var fixture = new DirectoryFixture();
        fixture.Touch("System32", "wsl.exe");
        var runner = new StubRunner(new DynamicProfileCommandResult(-1, string.Empty, string.Empty, true));

        var result = await new WslDistroProfileGenerator(runner, fixture.Environment()).GenerateAsync(default);

        Assert.Empty(result.Profiles);
        Assert.Contains(result.Diagnostics, item => item.Code == "DynamicProfileCommandTimedOut");
    }

    [Fact]
    public async Task SshConfigRequiresHostNameAndIgnoresPatterns()
    {
        using var fixture = new DirectoryFixture();
        fixture.Touch("System32", "OpenSSH", "ssh.exe");
        var config = fixture.Write(
            "home",
            ".ssh",
            "config",
            """
            Host wildcard-*
                HostName ignored.example
            Host work work-alias
                User dev
                HostName work.example
            Host no-host-name
                User dev
            Host WORK
                HostName duplicate.example
            """);
        var environment = fixture.Environment(readFiles: true);

        var result = await new SshHostProfileGenerator(environment, [config]).GenerateAsync(default);

        Assert.Equal(["SSH - work", "SSH - work-alias"], result.Profiles.Select(profile => profile.Name));
        Assert.All(result.Profiles, profile =>
        {
            Assert.Equal(DynamicProfileSource.Ssh, profile.Source);
            Assert.Contains("ssh.exe\"", profile.Commandline, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task VisualStudioProfilesUseVswhereWithoutComAndHideOlderInstances()
    {
        using var fixture = new DirectoryFixture();
        var vswhere = fixture.Touch("vswhere.exe");
        var current = fixture.Directory("VS", "2022");
        var older = fixture.Directory("VS", "2019");
        fixture.TouchAt(current, "Common7", "Tools", "VsDevCmd.bat");
        fixture.TouchAt(current, "Common7", "Tools", "Microsoft.VisualStudio.DevShell.dll");
        fixture.TouchAt(older, "Common7", "Tools", "VsDevCmd.bat");
        fixture.TouchAt(older, "Common7", "Tools", "Microsoft.VisualStudio.DevShell.dll");
        var json = $$"""
            [
              {
                "instanceId": "old",
                "installationPath": {{Json(older)}},
                "installationVersion": "16.11.0",
                "installDate": "2021-01-01T00:00:00Z",
                "catalog": { "productLineVersion": "2019" },
                "properties": { "channelId": "VisualStudio.16.Release" }
              },
              {
                "instanceId": "new",
                "installationPath": {{Json(current)}},
                "installationVersion": "17.10.1",
                "installDate": "2024-01-01T00:00:00Z",
                "catalog": { "productLineVersion": "2022" },
                "properties": { "nickname": "Main" }
              }
            ]
            """;
        var runner = new StubRunner(new DynamicProfileCommandResult(0, json, string.Empty, false));

        var result = await new VisualStudioProfileGenerator(
            runner,
            fixture.Environment(architecture: Architecture.X64),
            vswherePath: vswhere).GenerateAsync(default);

        Assert.Equal(4, result.Profiles.Count);
        Assert.Equal("Developer Command Prompt for VS 2022 (Main)", result.Profiles[0].Name);
        Assert.Equal("Developer PowerShell for VS 2022 (Main)", result.Profiles[1].Name);
        Assert.False(result.Profiles[0].Hidden);
        Assert.False(result.Profiles[1].Hidden);
        Assert.True(result.Profiles[2].Hidden);
        Assert.True(result.Profiles[3].Hidden);
        Assert.Contains("-arch=x64 -host_arch=x64", result.Profiles[0].Commandline);
        Assert.Equal(ProfileGuid.CreateDynamic("VsDevCmdnew").ToString("B"), result.Profiles[0].Guid);
        Assert.Contains("-format", runner.LastCommand!.Arguments);
    }

    [Fact]
    public async Task ManagerHonorsDisabledSourcesAndReconcilesOrphans()
    {
        var active = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var enabled = new StubGenerator(
            "Enabled",
            [new ProfileSettings { Guid = active.ToString("B"), Name = "Active", Source = "Enabled" }]);
        var disabled = new StubGenerator(
            "Disabled",
            [new ProfileSettings { Guid = Guid.NewGuid().ToString("B"), Name = "Disabled" }]);

        var result = await new DynamicProfileManager([enabled, disabled]).GenerateAsync(
            ["Disabled"],
            [active, orphan]);
        var settings = new AppSettings
        {
            Profiles =
            [
                new ProfileSettings { Guid = active.ToString("B"), Name = "Active" },
                new ProfileSettings { Guid = orphan.ToString("B"), Name = "Orphan" },
            ],
        };
        result.Reconcile(settings);

        Assert.Single(result.Profiles);
        Assert.Equal([orphan], result.OrphanedProfileIds);
        Assert.False(settings.Profiles[0].Orphaned);
        Assert.True(settings.Profiles[1].Orphaned);
        Assert.True(settings.Profiles[1].Hidden);
        Assert.Contains(result.Diagnostics, item => item.Code == "DynamicProfileSourceDisabled");

        ApplicationStateData state = new() { GeneratedProfiles = [active, orphan] };
        result.UpdateState(state);
        Assert.Equal(2, state.GeneratedProfiles.Count);
    }

    [Fact]
    public async Task DynamicLayerReceivesUserOverridesAndGeneratedOrigin()
    {
        var id = Guid.NewGuid();
        var generator = new StubGenerator(
            "Example.Source",
            [
                new ProfileSettings
                {
                    Guid = id.ToString("B"),
                    Name = "Generated",
                    Source = "Example.Source",
                    Commandline = "generated.exe",
                },
            ]);
        const string defaults = """
            { "profiles": { "defaults": {}, "list": [] }, "schemes": [{ "name": "Campbell" }] }
            """;
        var user = $$"""
            { "profiles": { "list": [{ "guid": "{{id:B}}", "startingDirectory": "C:\\src" }] } }
            """;

        var loaded = await DynamicSettingsLoader.LoadAsync(
            defaults,
            user,
            [],
            new DynamicProfileManager([generator]));

        var profile = Assert.Single(loaded.Settings.Profiles);
        Assert.Equal(SettingsOrigin.Generated, profile.Origin);
        Assert.Equal("generated.exe", profile.Commandline);
        Assert.Equal(@"C:\src", profile.StartingDirectory);
    }

    [Fact]
    public async Task OrphanIsHiddenWithoutPersistingAUserHiddenOverride()
    {
        var orphan = Guid.NewGuid();
        const string defaults = """
            { "profiles": { "defaults": {}, "list": [] }, "schemes": [{ "name": "Campbell" }] }
            """;
        var user = $$"""
            {
              "profiles": {
                "list": [{
                  "guid": "{{orphan:B}}",
                  "name": "Removed generator profile",
                  "source": "Example.Source"
                }]
              }
            }
            """;

        var loaded = await DynamicSettingsLoader.LoadAsync(
            defaults,
            user,
            [],
            new DynamicProfileManager([]),
            [orphan]);
        var profile = Assert.Single(loaded.Settings.Profiles);

        Assert.True(profile.Orphaned);
        Assert.True(profile.Hidden);
        Assert.DoesNotContain(
            "\"hidden\"",
            SettingsLoader.SerializeUserDocument(loaded.Settings),
            StringComparison.Ordinal);
        Assert.Equal(
            "Windows PowerShell",
            loaded.Settings.GetDefaultProfile().Name);
    }

    [Fact]
    public async Task CancellationPropagatesBetweenGenerators()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var manager = new DynamicProfileManager([new StubGenerator("source", [])]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await manager.GenerateAsync(cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task MachineDependentSmokeTestIsExplicitlyGuarded()
    {
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(
                Environment.GetEnvironmentVariable("WT_RUN_MACHINE_PROFILE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var manager = new DynamicProfileManager(
        [
            new InboxShellProfileGenerator(),
            new PowerShellCoreProfileGenerator(),
            new WslDistroProfileGenerator(),
            new SshHostProfileGenerator(),
            new VisualStudioProfileGenerator(),
        ]);
        var result = await manager.GenerateAsync();
        Assert.DoesNotContain(result.Diagnostics, item =>
            item.Severity == SettingsDiagnosticSeverity.Error);
    }

    private static string Json(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private sealed class StubRunner(DynamicProfileCommandResult result) : IDynamicProfileCommandRunner
    {
        public DynamicProfileCommand? LastCommand { get; private set; }

        public ValueTask<DynamicProfileCommandResult> RunAsync(
            DynamicProfileCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCommand = command;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StubGenerator(string source, IReadOnlyList<ProfileSettings> profiles)
        : IDynamicProfileGenerator
    {
        public string Source => source;
        public string DisplayName => source;
        public string Icon => string.Empty;

        public ValueTask<DynamicProfileGeneratorResult> GenerateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new DynamicProfileGeneratorResult(profiles, []));
        }
    }

    private sealed class DirectoryFixture : IDisposable
    {
        public DirectoryFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"terminal-profiles-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Directory(params string[] parts)
        {
            var path = PathOf(parts);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string Touch(params string[] parts) => TouchAt(Root, parts);

        public string TouchAt(string root, params string[] parts)
        {
            var path = Path.Combine([root, .. parts]);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return path;
        }

        public string Write(params string[] parts)
        {
            var content = parts[^1];
            var pathParts = parts[..^1];
            var path = PathOf(pathParts);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

        public DynamicProfileEnvironment Environment(
            bool readFiles = false,
            Architecture architecture = Architecture.X64) => new()
        {
            ProgramFiles = PathOf("ProgramFiles"),
            ProgramFilesX86 = PathOf("ProgramFilesX86"),
            UserProfile = PathOf("home"),
            ProgramData = PathOf("ProgramData"),
            LocalApplicationData = PathOf("LocalAppData"),
            SystemDirectory = PathOf("System32"),
            ProcessArchitecture = architecture,
            FileExists = File.Exists,
            EnumerateDirectories = path =>
                System.IO.Directory.Exists(path) ? System.IO.Directory.EnumerateDirectories(path) : [],
            ReadLines = readFiles ? File.ReadLines : _ => [],
            ResolveExecutable = executable =>
            {
                var candidate = PathOf("path", executable);
                return File.Exists(candidate) ? candidate : null;
            },
        };

        public void Dispose()
        {
            System.IO.Directory.Delete(Root, recursive: true);
        }
    }
}
