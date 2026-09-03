using Devolutions.Terminal.App.Platform;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class Win32ParentWindowTests
{
    [Theory]
    [InlineData("1234", 1234)]
    [InlineData("0x4d2", 0x4D2)]
    [InlineData("0X10", 16)]
    public void ParsesDecimalAndHexHandles(string value, long expected)
    {
        Assert.True(Win32ParentWindow.TryParseHandle(value, out var handle));
        Assert.Equal((nint)expected, handle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("not-a-handle")]
    public void RejectsInvalidHandles(string? value)
    {
        Assert.False(Win32ParentWindow.TryParseHandle(value, out _));
    }
}
