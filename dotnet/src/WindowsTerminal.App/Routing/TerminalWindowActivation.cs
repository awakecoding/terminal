using Microsoft.Terminal.Settings;
using WindowsTerminal.Actions;

namespace WindowsTerminal.Routing;

[Flags]
public enum TerminalWindowLaunchMode
{
    Default = 0,
    Maximized = 1,
    Fullscreen = 2,
    Focus = 4,
}

public sealed record TerminalWindowActivation(
    int? PositionX,
    int? PositionY,
    int? Columns,
    int? Rows,
    TerminalWindowLaunchMode LaunchMode,
    IReadOnlyList<ActionAndArgs> Actions,
    WindowLayoutState? PersistedLayout = null,
    string? PersistedLayoutDiagnostic = null,
    TerminalSaveRequest? SaveRequest = null,
    string? WorkspaceName = null);

public sealed record TerminalSaveRequest(
    string Name,
    string KeyChord,
    string Commandline);

public sealed record TerminalWindowActivationResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<ActionDispatchResult> Actions);

public sealed record TerminalCommandLineParseResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<ActionAndArgs> Actions,
    TerminalSaveRequest? SaveRequest = null);

public interface ITerminalWindowActivationTarget
{
    int WindowId { get; }
    string WindowName { get; }

    ValueTask<TerminalWindowActivationResult> ActivateAsync(
        TerminalWindowActivation activation,
        CancellationToken cancellationToken = default);
}
