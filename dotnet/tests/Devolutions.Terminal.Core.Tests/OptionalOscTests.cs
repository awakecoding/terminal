using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class OptionalOscTests
{
    [Fact]
    public void Osc52ClipboardWriteRequiresPolicy()
    {
        var engine = new TerminalEngine();
        string? clipboard = null;
        engine.ClipboardWriteRequested += (_, text) => clipboard = text;
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello Ω"));

        engine.Feed($"\u001b]52;c;{payload}\u0007");
        Assert.Null(clipboard);

        engine.ConfigureOptionalFeatures(allowClipboardWrite: true, allowNotifications: false);
        engine.Feed($"\u001b]52;c;{payload}\u0007");
        Assert.Equal("hello Ω", clipboard);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("/w==")]
    public void Osc52RejectsMalformedBase64AndInvalidUtf8(string payload)
    {
        var engine = new TerminalEngine();
        var writes = 0;
        engine.ConfigureOptionalFeatures(allowClipboardWrite: true, allowNotifications: false);
        engine.ClipboardWriteRequested += (_, _) => writes++;

        engine.Feed($"\u001b]52;c;{payload}\u0007");

        Assert.Equal(0, writes);
    }

    [Fact]
    public void EmptyOsc52PayloadClearsClipboard()
    {
        var engine = new TerminalEngine();
        string? value = null;
        engine.ConfigureOptionalFeatures(allowClipboardWrite: true, allowNotifications: false);
        engine.ClipboardWriteRequested += (_, text) => value = text;

        engine.Feed("\u001b]52;c;\u0007");

        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void Osc52RejectsWhitespaceInsideBase64()
    {
        var engine = new TerminalEngine();
        var writes = 0;
        engine.ConfigureOptionalFeatures(allowClipboardWrite: true, allowNotifications: false);
        engine.ClipboardWriteRequested += (_, _) => writes++;

        engine.Feed("\u001b]52;c;aG VsbG8=\u0007");

        Assert.Equal(0, writes);
    }

    [Fact]
    public void WindowsNotificationRequiresPolicy()
    {
        var engine = new TerminalEngine();
        TerminalNotification? notification = null;
        engine.NotificationRequested += (_, value) => notification = value;

        engine.Feed("\u001b]9;Build complete\u0007");
        Assert.Null(notification);

        engine.ConfigureOptionalFeatures(allowClipboardWrite: false, allowNotifications: true);
        engine.Feed("\u001b]9;Build complete\u0007");
        Assert.Equal("Build complete", notification?.Body);
        Assert.Null(notification?.Title);
    }

    [Fact]
    public void RxvtNotificationParsesTitleAndBody()
    {
        var engine = new TerminalEngine();
        TerminalNotification? notification = null;
        engine.ConfigureOptionalFeatures(allowClipboardWrite: false, allowNotifications: true);
        engine.NotificationRequested += (_, value) => notification = value;

        engine.Feed("\u001b]777;notify;Deploy;Completed\u0007");

        Assert.Equal("Deploy", notification?.Title);
        Assert.Equal("Completed", notification?.Body);
    }

    [Fact]
    public void ConEmuOsc9SubcommandUpdatesWorkingDirectory()
    {
        var engine = new TerminalEngine();
        string? workingDirectory = null;
        engine.WorkingDirectoryChanged += (_, value) => workingDirectory = value;

        engine.Feed("\u001b]9;9;C:\\src\u0007");

        Assert.Equal(@"C:\src", workingDirectory);
    }

    [Fact]
    public void RxvtNotificationRequiresTitleBodySeparatorAndAllowsEmptyBody()
    {
        var engine = new TerminalEngine();
        var notifications = new List<TerminalNotification>();
        engine.ConfigureOptionalFeatures(allowClipboardWrite: false, allowNotifications: true);
        engine.NotificationRequested += (_, value) => notifications.Add(value);

        engine.Feed("\u001b]777;notify;Title\u0007");
        engine.Feed("\u001b]777;notify;Title;\u0007");

        var notification = Assert.Single(notifications);
        Assert.Equal("Title", notification.Title);
        Assert.Equal(string.Empty, notification.Body);
    }
}
