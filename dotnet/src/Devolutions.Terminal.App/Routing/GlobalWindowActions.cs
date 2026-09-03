using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Routing;

public readonly record struct WindowPixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

public readonly record struct MonitorGeometry(string Id, WindowPixelRect WorkArea);

public enum DesktopPresence
{
    Unknown,
    Current,
    Other,
}

public sealed record WindowActionResult(bool Succeeded, string Message)
{
    public static WindowActionResult Success(string message) => new(true, message);
    public static WindowActionResult Unsupported(string message) => new(false, message);
}

public interface IWindowSummonOperations
{
    bool IsWindowVisible { get; }
    bool IsWindowActive { get; }
    bool IsWindowMinimized { get; }
    DesktopPresence DesktopPresence { get; }
    WindowPixelRect CurrentBounds { get; }

    MonitorGeometry GetMonitor(MonitorBehavior behavior);
    WindowActionResult MoveToCurrentDesktop();
    void HideWindow();
    ValueTask ShowWindowAsync(
        WindowPixelRect bounds,
        uint dropdownDuration,
        CancellationToken cancellationToken);
    void ActivateWindow();
}

public static class WindowSummonGeometry
{
    public static WindowPixelRect Place(
        WindowPixelRect current,
        MonitorGeometry monitor,
        bool quake)
    {
        var workArea = monitor.WorkArea;
        if (quake)
        {
            var quakeHeight = Math.Clamp(
                (int)Math.Round(workArea.Height * 0.5, MidpointRounding.AwayFromZero),
                Math.Min(200, workArea.Height),
                workArea.Height);
            return new(workArea.X, workArea.Y, workArea.Width, quakeHeight);
        }

        var width = Math.Clamp(current.Width, 1, workArea.Width);
        var height = Math.Clamp(current.Height, 1, workArea.Height);
        var x = Math.Clamp(current.X, workArea.X, workArea.Right - width);
        var y = Math.Clamp(current.Y, workArea.Y, workArea.Bottom - height);
        return new(x, y, width, height);
    }
}

public sealed class WindowSummonController(IWindowSummonOperations operations)
{
    private readonly IWindowSummonOperations _operations =
        operations ?? throw new ArgumentNullException(nameof(operations));

    public async ValueTask<WindowActionResult> SummonAsync(
        GlobalSummonArgs args,
        bool quake,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.ToggleVisibility &&
            _operations.IsWindowVisible &&
            _operations.IsWindowActive &&
            !_operations.IsWindowMinimized)
        {
            _operations.HideWindow();
            return WindowActionResult.Success("The terminal window was hidden.");
        }

        var desktopDiagnostic = string.Empty;
        if (args.Desktop == DesktopBehavior.OnCurrent &&
            _operations.DesktopPresence == DesktopPresence.Other)
        {
            return WindowActionResult.Unsupported(
                "The requested terminal window is on another desktop and desktop behavior is 'onCurrent'.");
        }
        if (args.Desktop == DesktopBehavior.ToCurrent &&
            _operations.DesktopPresence != DesktopPresence.Current)
        {
            var moved = _operations.MoveToCurrentDesktop();
            if (!moved.Succeeded)
            {
                desktopDiagnostic = $" {moved.Message}";
            }
        }

        var monitor = _operations.GetMonitor(args.Monitor);
        var bounds = WindowSummonGeometry.Place(_operations.CurrentBounds, monitor, quake);
        await _operations.ShowWindowAsync(
            bounds,
            Math.Min(args.DropdownDuration, 2000),
            cancellationToken).ConfigureAwait(true);
        _operations.ActivateWindow();
        return WindowActionResult.Success(
            quake
                ? $"The quake window was shown on monitor '{monitor.Id}'.{desktopDiagnostic}"
                : $"The terminal window was shown on monitor '{monitor.Id}'.{desktopDiagnostic}");
    }
}

public interface IGlobalWindowActionTarget
{
    int WindowId { get; }
    string WindowName { get; }

    ValueTask<WindowActionResult> ApplySummonAsync(
        GlobalSummonArgs args,
        bool quake,
        CancellationToken cancellationToken);
}

public sealed class GlobalWindowActionRouter(
    Func<string, IGlobalWindowActionTarget> createWindow)
{
    private readonly Func<string, IGlobalWindowActionTarget> _createWindow =
        createWindow ?? throw new ArgumentNullException(nameof(createWindow));
    private readonly List<IGlobalWindowActionTarget> _windows = [];

    public void Add(IGlobalWindowActionTarget window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!_windows.Contains(window))
        {
            _windows.Add(window);
        }
    }

    public void Remove(IGlobalWindowActionTarget window) => _windows.Remove(window);

    public async ValueTask<WindowActionResult> SummonAsync(
        IGlobalWindowActionTarget? requester,
        GlobalSummonArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var quake = string.Equals(args.Name, "_quake", StringComparison.OrdinalIgnoreCase);
        IGlobalWindowActionTarget? target;
        if (string.IsNullOrWhiteSpace(args.Name))
        {
            target = requester ?? _windows.LastOrDefault();
        }
        else
        {
            target = _windows.LastOrDefault(window =>
                window.WindowName.Equals(args.Name, StringComparison.OrdinalIgnoreCase));
        }

        if (target is null)
        {
            target = _createWindow(args.Name);
            Add(target);
        }

        return await target.ApplySummonAsync(
            args,
            quake,
            cancellationToken).ConfigureAwait(false);
    }
}
