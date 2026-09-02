using Microsoft.Terminal.Core;
using Microsoft.Terminal.Ghostty;
using Microsoft.Terminal.Settings;
using WindowsTerminal.Connections;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class TerminalEngineFactoryTests
{
    [Fact]
    public void BuiltInRemainsTheDefault()
    {
        using var engine = TerminalEngineFactory.Create(new AppSettings(), new ProfileSettings());

        Assert.IsType<TerminalEngine>(engine);
    }

    [Fact]
    public void GlobalGhosttySelectionCreatesGhosttyEngine()
    {
        using var engine = TerminalEngineFactory.Create(
            new AppSettings { TerminalEngine = TerminalEngineKind.Ghostty },
            new ProfileSettings());

        Assert.IsType<GhosttyTerminalEngine>(engine);
    }

    [Fact]
    public void ProfileSelectionOverridesGlobalEngine()
    {
        using var engine = TerminalEngineFactory.Create(
            new AppSettings { TerminalEngine = TerminalEngineKind.Ghostty },
            new ProfileSettings { TerminalEngine = TerminalEngineKind.BuiltIn });

        Assert.IsType<TerminalEngine>(engine);
    }

    [Fact]
    public void ProfileDefaultsOverrideGlobalEngineWhenProfileInherits()
    {
        using var engine = TerminalEngineFactory.Create(
            new AppSettings
            {
                TerminalEngine = TerminalEngineKind.BuiltIn,
                ProfileDefaults = new ProfileSettings
                {
                    TerminalEngine = TerminalEngineKind.Ghostty,
                },
            },
            new ProfileSettings());

        Assert.IsType<GhosttyTerminalEngine>(engine);
    }
}
