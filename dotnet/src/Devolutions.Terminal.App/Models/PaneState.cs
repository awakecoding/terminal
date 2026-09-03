namespace Devolutions.Terminal.App.Models;

public enum TerminalProgressState
{
    None,
    Indeterminate,
    Normal,
    Error,
    Paused,
}

public sealed class PanePresentationState
{
    public string Title { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public TerminalProgressState ProgressState { get; set; }
    public double Progress { get; set; }
    public bool IsAdministrator { get; set; }
    public bool IsReadOnly { get; set; }
    public bool HasBellIndicator { get; set; }
    public bool HasUnseenActivity { get; set; }

    public void SetProgress(TerminalProgressState state, double value = 0)
    {
        ProgressState = state;
        Progress = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }
}

public interface ITerminalInputTarget
{
    bool IsReadOnly { get; }
    void WriteInput(string input);
}

public sealed class BroadcastInputCoordinator
{
    public bool IsEnabled { get; private set; }

    public bool Toggle()
    {
        IsEnabled = !IsEnabled;
        return IsEnabled;
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    public IReadOnlyList<ITerminalInputTarget> WriteInput(
        ITerminalInputTarget source,
        IEnumerable<ITerminalInputTarget> panes,
        string input)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(panes);
        ArgumentNullException.ThrowIfNull(input);

        var targets = ResolveTargets(source, panes);
        foreach (var pane in targets)
        {
            pane.WriteInput(input);
        }

        return targets;
    }

    public IReadOnlyList<ITerminalInputTarget> ResolveTargets(
        ITerminalInputTarget source,
        IEnumerable<ITerminalInputTarget> panes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(panes);
        return (IsEnabled ? panes : [source])
            .Where(static pane => !pane.IsReadOnly)
            .Distinct(InputTargetReferenceComparer.Instance)
            .ToArray();
    }

    private sealed class InputTargetReferenceComparer : IEqualityComparer<ITerminalInputTarget>
    {
        public static InputTargetReferenceComparer Instance { get; } = new();

        public bool Equals(ITerminalInputTarget? x, ITerminalInputTarget? y) => ReferenceEquals(x, y);

        public int GetHashCode(ITerminalInputTarget obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
