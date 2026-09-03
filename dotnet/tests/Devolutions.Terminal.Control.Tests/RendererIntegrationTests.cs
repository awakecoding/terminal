using Devolutions.Terminal;
using Devolutions.Terminal.Render;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

public sealed class RendererIntegrationTests
{
    [Theory]
    [InlineData("bar", TerminalCursorStyle.Bar)]
    [InlineData("underscore", TerminalCursorStyle.Underscore)]
    [InlineData("doubleUnderscore", TerminalCursorStyle.DoubleUnderscore)]
    [InlineData("vintage", TerminalCursorStyle.Vintage)]
    [InlineData("filledBox", TerminalCursorStyle.FilledBox)]
    [InlineData("emptyBox", TerminalCursorStyle.EmptyBox)]
    public void MapsEveryWindowsTerminalCursorShape(string value, TerminalCursorStyle expected)
    {
        Assert.Equal(expected, TermControl.ParseCursorStyle(value));
    }
}
