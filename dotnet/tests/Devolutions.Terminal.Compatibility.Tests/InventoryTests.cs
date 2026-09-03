using System.Text.Json;
using Devolutions.Terminal.PortInventory;
using Xunit;

namespace Devolutions.Terminal.Compatibility.Tests;

public sealed class InventoryTests
{
    [Fact]
    public void CheckedInInventoryMatchesCppSourcesWhenPresent()
    {
        if (!TryFindCppOracleRoot(out var repositoryRoot))
        {
            return;
        }

        var expected = LoadCheckedInInventory();
        var actual = InventoryGenerator.Generate(repositoryRoot);

        Assert.Equal(expected.SettingsKeys, actual.SettingsKeys);
        Assert.Equal(expected.Actions, actual.Actions);
        Assert.Equal(expected.ActionsWithArgs, actual.ActionsWithArgs);
        Assert.Equal(expected.VtDispatchMethods, actual.VtDispatchMethods);
        Assert.Equal(expected.CliSubcommands, actual.CliSubcommands);
        Assert.Equal(expected.CliOptions, actual.CliOptions);
        Assert.Equal(expected.SettingsPages, actual.SettingsPages);
    }

    [Fact]
    public void CheckedInInventoryCoversMajorCompatibilitySurfaces()
    {
        var inventory = LoadCheckedInInventory();

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

    private static CompatibilityInventory LoadCheckedInInventory()
    {
        var expectedPath = Path.Combine(AppContext.BaseDirectory, "windows-terminal.json");
        return JsonSerializer.Deserialize<CompatibilityInventory>(File.ReadAllText(expectedPath))
            ?? throw new InvalidOperationException($"Could not deserialize '{expectedPath}'.");
    }

    private static bool TryFindCppOracleRoot(out string repositoryRoot)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (InventoryGenerator.LooksLikeWindowsTerminalOracle(directory.FullName))
            {
                repositoryRoot = directory.FullName;
                return true;
            }

            directory = directory.Parent;
        }

        repositoryRoot = string.Empty;
        return false;
    }
}
