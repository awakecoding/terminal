using Avalonia.Input;
using Microsoft.Terminal.Control;
using Xunit;

namespace Terminal.Control.Tests;

public sealed class KeyMapperTests
{
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
