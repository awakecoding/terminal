using Devolutions.Terminal.App.Platform;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class WindowsGlobalHotkeyBackendTests
{
    [Fact]
    public async Task CommandsRunOnTheWindowMessageLoopThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var backend = new WindowsGlobalHotkeyBackend();
        var callerThread = Environment.CurrentManagedThreadId;

        var commandThread = await Task.Run(backend.InvokeOnMessageLoopThreadForTesting);

        Assert.Equal(backend.MessageLoopThreadId, commandThread);
        Assert.NotEqual(callerThread, commandThread);
    }

    [Fact]
    public void DisposalIsSafeOnTheWindowMessageLoopThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new WindowsGlobalHotkeyBackend();

        backend.DisposeOnMessageLoopThreadForTesting();
        backend.Dispose();
    }
}
