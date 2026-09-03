namespace Devolutions.Terminal.Connection;

public sealed record TerminalLaunchOptions
{
    public required string CommandLine { get; init; }

    public string? WorkingDirectory { get; init; }

    public int Columns { get; init; } = 80;

    public int Rows { get; init; } = 30;

    public bool InheritEnvironment { get; init; } = true;

    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } =
        new Dictionary<string, string?>();

    public TerminalCloseOnExitPolicy CloseOnExit { get; init; } =
        TerminalCloseOnExitPolicy.Automatic;

    public bool IsDefaultTerminalSession { get; init; }
}
