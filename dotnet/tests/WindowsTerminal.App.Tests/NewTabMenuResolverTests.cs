using Microsoft.Terminal.Settings;
using WindowsTerminal.Models;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class NewTabMenuResolverTests
{
    [Fact]
    public void ResolvesFoldersSeparatorsCollectionsAndActions()
    {
        var settings = new AppSettings
        {
            Profiles =
            [
                new() { Guid = "{11111111-1111-1111-1111-111111111111}", Name = "PowerShell", Source = "Windows.Terminal.PowershellCore" },
                new() { Guid = "{22222222-2222-2222-2222-222222222222}", Name = "Command Prompt" },
                new() { Name = "Hidden", Hidden = true },
            ],
            NewTabMenu =
            [
                new()
                {
                    Type = NewTabMenuEntryType.Folder,
                    Name = "Shells",
                    Entries =
                    [
                        new()
                        {
                            Type = NewTabMenuEntryType.MatchProfiles,
                            MatchSource = "Windows.Terminal.PowershellCore",
                        },
                    ],
                },
                new() { Type = NewTabMenuEntryType.Separator },
                new() { Type = NewTabMenuEntryType.Action, Name = "Palette", ActionId = "palette" },
                new() { Type = NewTabMenuEntryType.RemainingProfiles },
            ],
        };

        var menu = NewTabMenuResolver.Resolve(settings);

        Assert.Equal(
            [ResolvedNewTabMenuItemType.Folder, ResolvedNewTabMenuItemType.Separator,
             ResolvedNewTabMenuItemType.Action, ResolvedNewTabMenuItemType.Profile],
            menu.Select(static item => item.Type));
        Assert.Equal("PowerShell", Assert.Single(menu[0].Children!).Name);
        Assert.Equal("Command Prompt", menu[3].Name);
        Assert.Equal("palette", menu[2].ActionId);
    }

    [Fact]
    public void EmptyFoldersAreOnlyIncludedWhenAllowed()
    {
        var settings = new AppSettings
        {
            NewTabMenu =
            [
                new() { Type = NewTabMenuEntryType.Folder, Name = "hidden" },
                new() { Type = NewTabMenuEntryType.Folder, Name = "visible", AllowEmpty = true },
            ],
        };

        var menu = NewTabMenuResolver.Resolve(settings);

        Assert.Equal("visible", Assert.Single(menu).Name);
    }
}
