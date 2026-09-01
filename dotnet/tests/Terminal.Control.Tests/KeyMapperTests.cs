using Avalonia.Input;
using Microsoft.Terminal.Control;
using Xunit;

namespace Terminal.Control.Tests;

public sealed class KeyMapperTests
{
    [Theory]
    [InlineData(Key.Return)]
    [InlineData(Key.LineFeed)]
    public void MapsEveryEnterKeyToCarriageReturn(Key key)
    {
        var sequence = KeyMapper.ToVt(
            key,
            KeyModifiers.None,
            PhysicalKey.None,
            null,
            applicationCursorKeys: false);

        Assert.Equal("\r", sequence);
    }

    [Theory]
    [InlineData(PhysicalKey.Enter)]
    [InlineData(PhysicalKey.NumPadEnter)]
    public void MapsPhysicalEnterWhenLogicalKeyIsUnavailable(PhysicalKey physicalKey)
    {
        var sequence = KeyMapper.ToVt(
            Key.None,
            KeyModifiers.None,
            physicalKey,
            null,
            applicationCursorKeys: false);

        Assert.Equal("\r", sequence);
    }

    [Fact]
    public void PhysicalAltEnterPreservesEscapePrefix()
    {
        var sequence = KeyMapper.ToVt(
            Key.None,
            KeyModifiers.Alt,
            PhysicalKey.Enter,
            null,
            applicationCursorKeys: false);

        Assert.Equal("\u001b\r", sequence);
    }

    [Fact]
    public void MapsApplicationCursorKey()
    {
        var sequence = KeyMapper.ToVt(
            Key.Up,
            KeyModifiers.None,
            PhysicalKey.ArrowUp,
            null,
            applicationCursorKeys: true);

        Assert.Equal("\u001bOA", sequence);
    }
}
