using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Terminal.Settings;
using WindowsTerminal.Settings.Controls;
using Xunit;

namespace WindowsTerminal.Settings.Tests;

public sealed class SettingsEditorViewModelTests
{
    private const string Defaults = """
        {
          "initialCols": 80,
          "initialRows": 30,
          "profiles": {
            "defaults": { "font": { "face": "Cascadia Mono", "size": 12 } },
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
          ],
          "newTabMenu": [ { "type": "remainingProfiles" } ],
          "actions": [],
          "keybindings": []
        }
        """;

    [Fact]
    public void SearchFiltersToMatchingSettingsPages()
    {
        var viewModel = CreateEditor();

        viewModel.SearchText = "kitty";

        var page = Assert.Single(viewModel.VisibleNavigationItems);
        Assert.Equal(SettingsPage.ProfileAdvanced, page.Page);
        Assert.Same(page, viewModel.SelectedNavigationItem);
    }

    [Fact]
    public void ApplyPersistsTypedChangeAndUnknownUserProperties()
    {
        var persisted = """
            {
              "futureSetting": { "preserve": true },
              "initialCols": 90,
              "profiles": { "list": [] }
            }
            """;
        var saveCount = 0;
        var viewModel = CreateEditor(
            () => SettingsLoader.Load(Defaults, persisted),
            settings =>
            {
                saveCount++;
                persisted = SettingsLoader.SerializeUserDocument(settings);
            });
        var startup = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);

        startup.InitialColumns = 132;
        viewModel.Apply();

        Assert.Equal(1, saveCount);
        Assert.False(viewModel.IsDirty);
        var saved = Assert.IsType<JsonObject>(JsonNode.Parse(persisted));
        Assert.Equal(132, saved["initialCols"]!.GetValue<int>());
        Assert.True(saved["futureSetting"]!["preserve"]!.GetValue<bool>());
    }

    [Fact]
    public void RevertReloadsPersistedValues()
    {
        var persisted = """{ "initialCols": 91, "profiles": { "list": [] } }""";
        var viewModel = CreateEditor(() => SettingsLoader.Load(Defaults, persisted));
        var startup = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);
        startup.InitialColumns = 150;

        viewModel.Revert();

        var reverted = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);
        Assert.Equal(91, reverted.InitialColumns);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void DefaultResetLoadsDefaultsAndRemainsDirtyUntilApply()
    {
        var viewModel = CreateEditor(
            () => SettingsLoader.Load(Defaults, """{ "initialCols": 140 }"""));

        viewModel.ResetToDefaults();

        var startup = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);
        Assert.Equal(80, startup.InitialColumns);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void InvalidActionJsonBlocksSave()
    {
        var saveCount = 0;
        var viewModel = CreateEditor(save: _ => saveCount++);
        viewModel.SelectPage(SettingsPage.Actions);
        var actions = Assert.IsType<ActionsSettingsViewModel>(viewModel.CurrentPage);
        actions.ActionsJson = "{ invalid";

        viewModel.Apply();

        Assert.Equal(0, saveCount);
        Assert.True(viewModel.IsDirty);
        Assert.Contains("invalid JSON", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfileSelectionRaisesPropertyChanged()
    {
        var profiles = new[]
        {
            new ProfileItemViewModel(ProfileSettings.CreatePowerShell(), () => { }),
            new ProfileItemViewModel(ProfileSettings.CreateCmd(), () => { }),
        };
        var page = new ProfilesSettingsViewModel(profiles);
        var changes = new List<string?>();
        page.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        page.SelectedProfile = profiles[1];

        Assert.Contains(nameof(ProfilesSettingsViewModel.SelectedProfile), changes);
    }

    [Fact]
    public void ExternalRevisionChangeBlocksSave()
    {
        var revision = "one";
        var saveCount = 0;
        var viewModel = new SettingsEditorViewModel(
            () => SettingsLoader.Load(Defaults),
            _ => saveCount++,
            () => SettingsLoader.Load(Defaults),
            () => revision);
        var startup = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);
        startup.InitialColumns = 100;
        revision = "two";

        viewModel.Apply();

        Assert.Equal(0, saveCount);
        Assert.Contains("changed on disk", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownNewTabEntryIsPreservedWhenOtherSettingsChange()
    {
        var persisted = """
            {
              "newTabMenu": [
                { "type": "futureProviderEntry", "payload": { "keep": 42 } }
              ],
              "profiles": { "list": [] }
            }
            """;
        var viewModel = CreateEditor(
            () => SettingsLoader.Load(Defaults, persisted),
            settings => persisted = SettingsLoader.SerializeUserDocument(settings));
        var startup = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);
        startup.InitialColumns = 123;

        viewModel.Apply();

        var saved = Assert.IsType<JsonObject>(JsonNode.Parse(persisted));
        Assert.Equal(
            42,
            saved["newTabMenu"]![0]!["payload"]!["keep"]!.GetValue<int>());
    }

    [Fact]
    public void EditingUnsupportedNewTabEntryIsRejectedInsteadOfDropped()
    {
        var saveCount = 0;
        var viewModel = CreateEditor(
            () => SettingsLoader.Load(
                Defaults,
                """{ "newTabMenu": [ { "type": "futureProviderEntry", "payload": 1 } ] }"""),
            _ => saveCount++);
        viewModel.SelectPage(SettingsPage.NewTabMenu);
        var menu = Assert.IsType<NewTabMenuSettingsViewModel>(viewModel.CurrentPage);
        menu.Json = menu.Json.Replace("\"payload\": 1", "\"payload\": 2", StringComparison.Ordinal);

        viewModel.Apply();

        Assert.Equal(0, saveCount);
        Assert.Contains("unsupported type", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedFolderPayloadIsRejectedInsteadOfErased()
    {
        var saveCount = 0;
        var viewModel = CreateEditor(save: _ => saveCount++);
        viewModel.SelectPage(SettingsPage.NewTabMenu);
        var menu = Assert.IsType<NewTabMenuSettingsViewModel>(viewModel.CurrentPage);
        menu.Json = """[ { "type": "folder", "name": "Broken", "entries": { "future": true } } ]""";

        viewModel.Apply();

        Assert.Equal(0, saveCount);
        Assert.Contains("must be an array", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void WindowConstructsWithCompiledXaml()
    {
        var viewModel = CreateEditor();

        var window = new SettingsWindow(viewModel);

        Assert.Same(viewModel, window.DataContext);
    }

    [AvaloniaFact]
    public void SettingsRowKeepsLabelsAndRightSideValue()
    {
        var value = new TextBox();
        var row = new SettingsRow
        {
            Header = "Command line",
            Description = "Executable used when this profile starts.",
            Value = value,
        };

        Assert.Equal("Command line", AutomationProperties.GetName(row));
        Assert.Equal("Command line", AutomationProperties.GetName(value));
        Assert.NotNull(AutomationProperties.GetLabeledBy(value));
        Assert.Same(value, row.Value);
    }

    private static SettingsEditorViewModel CreateEditor(
        Func<AppSettings>? load = null,
        Action<AppSettings>? save = null) =>
        new(
            load ?? (() => SettingsLoader.Load(Defaults)),
            save ?? (_ => { }),
            () => SettingsLoader.Load(Defaults));
}
