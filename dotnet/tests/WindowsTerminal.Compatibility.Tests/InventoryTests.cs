using System.Text.Json;
using Terminal.PortInventory;
using Xunit;

namespace WindowsTerminal.Compatibility.Tests;

public sealed class InventoryTests
{
    [Fact]
    public void CheckedInInventoryMatchesCppSources()
    {
        var repositoryRoot = FindRepositoryRoot();
        var expectedPath = Path.Combine(AppContext.BaseDirectory, "windows-terminal.json");
        var expected = JsonSerializer.Deserialize<CompatibilityInventory>(File.ReadAllText(expectedPath));
        var actual = InventoryGenerator.Generate(repositoryRoot);

        Assert.NotNull(expected);
        Assert.Equal(expected.SettingsKeys, actual.SettingsKeys);
        Assert.Equal(expected.Actions, actual.Actions);
        Assert.Equal(expected.ActionsWithArgs, actual.ActionsWithArgs);
        Assert.Equal(expected.VtDispatchMethods, actual.VtDispatchMethods);
        Assert.Equal(expected.CliSubcommands, actual.CliSubcommands);
        Assert.Equal(expected.CliOptions, actual.CliOptions);
        Assert.Equal(expected.SettingsPages, actual.SettingsPages);
    }

    [Fact]
    public void InventoryCoversMajorCompatibilitySurfaces()
    {
        var inventory = InventoryGenerator.Generate(FindRepositoryRoot());

        Assert.True(inventory.SettingsKeys.Count > 50);
        Assert.True(inventory.Actions.Count > 80);
        Assert.True(inventory.VtDispatchMethods.Count > 100);
        Assert.Contains("new-tab", inventory.CliSubcommands);
        Assert.Contains("--suppressApplicationTitle", inventory.CliOptions);
        Assert.Contains("--useApplicationTitle", inventory.CliOptions);
        Assert.Contains("--inheritEnvironment", inventory.CliOptions);
        Assert.Contains("--reloadEnvironment", inventory.CliOptions);
        Assert.Contains("Profiles", inventory.SettingsPages);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenConsole.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the terminal repository root.");
    }
}
