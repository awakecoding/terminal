using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Settings.Tests;

public sealed class ExtensionFragmentTests
{
    private const string Defaults = """
        {
          "profiles": { "defaults": {}, "list": [] },
          "schemes": [{ "name": "Campbell" }]
        }
        """;

    [Fact]
    public void DiscoversFragmentsDeterministicallyAndScopesProviders()
    {
        using var fixture = new FragmentFixture();
        fixture.Write("Zulu.Provider", "z.json", """{ "profiles": [{ "name": "Zulu" }] }""");
        fixture.Write("Alpha.Provider", "b.json", """{ "profiles": [{ "name": "Beta" }] }""");
        fixture.Write("Alpha.Provider", "a.json", """{ "profiles": [{ "name": "Alpha" }] }""");
        fixture.Write("Alpha.Provider", "ignored.txt", "{}");

        var discovered = ExtensionFragmentDiscovery.Discover([fixture.Root]);
        var settings = SettingsLoader.Load(Defaults, fragments: discovered.Fragments);

        Assert.Empty(discovered.Diagnostics);
        Assert.Equal(
            ["a.json", "b.json", "z.json"],
            discovered.Fragments.Select(layer => Path.GetFileName(layer.Source)));
        Assert.Equal(
            ["Alpha", "Beta", "Zulu"],
            settings.Profiles.Select(profile => profile.Name));
        Assert.Equal(
            ["Alpha.Provider", "Alpha.Provider", "Zulu.Provider"],
            settings.Profiles.Select(profile => profile.Source));
        Assert.All(settings.Profiles, profile => Assert.Equal(SettingsOrigin.Fragment, profile.Origin));
    }

    [Fact]
    public void InvalidFragmentReportsSourceAndDoesNotBlockValidFragments()
    {
        using var fixture = new FragmentFixture();
        var invalid = fixture.Write("Provider", "bad.json", "{ invalid");
        fixture.Write("Provider", "good.json", """{ "profiles": [{ "name": "Good" }] }""");

        var discovered = ExtensionFragmentDiscovery.Discover([fixture.Root]);
        var settings = SettingsLoader.Load(Defaults, fragments: discovered.Fragments);

        Assert.Equal("Good", Assert.Single(settings.Profiles).Name);
        Assert.Contains(settings.Diagnostics, diagnostic =>
            diagnostic.Code == "InvalidJson" &&
            string.Equals(diagnostic.Source, invalid, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FragmentUpdatesGeneratedProfileAfterIdentityIsKnown()
    {
        using var fixture = new FragmentFixture();
        var id = ProfileGuid.CreateDynamic("Generated");
        fixture.Write(
            "Provider",
            "update.json",
            $$"""
            { "profiles": [{ "updates": "{{id:B}}", "historySize": 123 }] }
            """);
        var generated = new SettingsLayer(
            "dynamic-profiles",
            $$"""
            {
              "profiles": {
                "list": [{
                  "guid": "{{id:B}}",
                  "name": "Generated",
                  "source": "Example.Source",
                  "commandline": "generated.exe"
                }]
              }
            }
            """,
            SettingsLayerKind.Generated);

        var fragments = ExtensionFragmentDiscovery.Discover([fixture.Root]).Fragments;
        var settings = SettingsLoader.Load(Defaults, fragments: [generated, .. fragments]);

        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(123, profile.HistorySize);
        Assert.Equal(SettingsOrigin.Generated, profile.Origin);
    }

    private sealed class FragmentFixture : IDisposable
    {
        public FragmentFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"terminal-fragments-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Write(string provider, string name, string content)
        {
            var directory = Path.Combine(Root, provider);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
