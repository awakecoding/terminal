using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(Terminal.Control.Tests.TestApplication))]

namespace Terminal.Control.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
