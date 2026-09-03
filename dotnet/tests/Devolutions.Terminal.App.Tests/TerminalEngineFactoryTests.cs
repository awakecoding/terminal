using Devolutions.Terminal.Core;
using Devolutions.Terminal.Ghostty;
using Devolutions.Terminal.Settings;
using Devolutions.Terminal.App.Connections;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

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
