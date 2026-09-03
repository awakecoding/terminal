using System.Text.Json.Nodes;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Settings.Tests;

public sealed class SettingsSnippetStoreTests
{
    [Fact]
    public void AddsSendInputActionAndOptionalBindingToUserDocument()
    {
        var settings = SettingsLoader.Load(
            SettingsLoader.ReadEmbeddedDefaults(),
            """{ "actions": [], "keybindings": [] }""");

        var id = SettingsSnippetStore.Add(
            settings,
            "Build",
            "ctrl+b",
            "dotnet build");
        var document = JsonNode.Parse(SettingsLoader.SerializeUserDocument(settings))!.AsObject();
        var action = Assert.IsType<JsonObject>(Assert.Single(document["actions"]!.AsArray()));
        var binding = Assert.IsType<JsonObject>(Assert.Single(document["keybindings"]!.AsArray()));

        Assert.Equal(id, action["id"]!.GetValue<string>());
        Assert.Equal("Build", action["name"]!.GetValue<string>());
        Assert.Equal("sendInput", action["command"]!["action"]!.GetValue<string>());
        Assert.Equal("dotnet build", action["command"]!["input"]!.GetValue<string>());
        Assert.Equal(id, binding["id"]!.GetValue<string>());
        Assert.Equal("ctrl+b", binding["keys"]!.GetValue<string>());
    }

    [Fact]
    public void RejectsInvalidKeyChordWithoutChangingSettings()
    {
        var settings = SettingsLoader.Load(
            SettingsLoader.ReadEmbeddedDefaults(),
            """{ "actions": [] }""");

        Assert.Throws<ArgumentException>(() =>
            SettingsSnippetStore.Add(settings, "", "ctrl+not-a-key", "echo test"));

        var document = JsonNode.Parse(SettingsLoader.SerializeUserDocument(settings))!.AsObject();
        Assert.Empty(document["actions"]!.AsArray());
    }
}
