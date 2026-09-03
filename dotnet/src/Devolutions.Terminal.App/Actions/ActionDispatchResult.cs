namespace Devolutions.Terminal.App.Actions;

public enum ActionDispatchStatus
{
    Executed,
    Disabled,
    Unsupported,
    Failed,
}

public enum ActionScope
{
    Application,
    Window,
    Tab,
    Pane,
    Control,
}

public sealed record ActionDispatchResult(
    ActionDispatchStatus Status,
    ActionScope Scope,
    string Action,
    string? Message = null)
{
    public bool Handled => Status is ActionDispatchStatus.Executed or ActionDispatchStatus.Unsupported;

    public static ActionDispatchResult Executed(ActionScope scope, string action) =>
        new(ActionDispatchStatus.Executed, scope, action);

    public static ActionDispatchResult Disabled(ActionScope scope, string action, string message) =>
        new(ActionDispatchStatus.Disabled, scope, action, message);

    public static ActionDispatchResult Unsupported(ActionScope scope, string action) =>
        new(ActionDispatchStatus.Unsupported, scope, action, $"Action '{action}' is not implemented.");

    public static ActionDispatchResult Failed(ActionScope scope, string action, string message) =>
        new(ActionDispatchStatus.Failed, scope, action, message);
}
