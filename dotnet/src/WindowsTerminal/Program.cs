using Avalonia;
using Avalonia.Media;

namespace WindowsTerminal;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://WindowsTerminal/Assets/Fonts/CascadiaMono.ttf#Cascadia Mono",
            })
            .LogToTrace();
}
