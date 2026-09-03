using Microsoft.Terminal.Settings;

namespace WindowsTerminal.Actions;

public sealed class ActionDispatcher
{
    private readonly Dictionary<ShortcutAction, Registration> _registrations = [];

    public IReadOnlyCollection<ShortcutAction> RegisteredActions => _registrations.Keys;

    public void Register(
        ShortcutAction action,
        ActionScope scope,
        Func<ActionAndArgs, bool> canExecute,
        Func<ActionAndArgs, Task> execute)
    {
        ArgumentNullException.ThrowIfNull(canExecute);
        ArgumentNullException.ThrowIfNull(execute);
        _registrations[action] = new Registration(scope, canExecute, execute);
    }

    public void Register(
        ShortcutAction action,
        ActionScope scope,
        Func<ActionAndArgs, Task> execute) =>
        Register(action, scope, static _ => true, execute);

    public bool CanExecute(ActionAndArgs action) =>
        action.Action == ShortcutAction.MultipleActions && action.Args is MultipleActionsArgs multiple
            ? multiple.Actions.All(CanExecute)
            : _registrations.TryGetValue(action.Action, out var registration) &&
              registration.CanExecute(action);

    public async Task<ActionDispatchResult> DispatchAsync(ActionAndArgs action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Action == ShortcutAction.MultipleActions && action.Args is MultipleActionsArgs multiple)
        {
            foreach (var nested in multiple.Actions)
            {
                var result = await DispatchAsync(nested).ConfigureAwait(true);
                if (result.Status != ActionDispatchStatus.Executed)
                {
                    return result;
                }
            }

            return ActionDispatchResult.Executed(ActionScope.Application, action.ActionName);
        }

        if (!_registrations.TryGetValue(action.Action, out var registration))
        {
            return ActionDispatchResult.Unsupported(
                ActionScopeCatalog.GetScope(action.ActionName),
                action.ActionName);
        }

        try
        {
            if (!registration.CanExecute(action))
            {
                return ActionDispatchResult.Disabled(
                    registration.Scope,
                    action.ActionName,
                    $"Action '{action.ActionName}' is not currently available.");
            }

            await registration.Execute(action).ConfigureAwait(true);
            return ActionDispatchResult.Executed(registration.Scope, action.ActionName);
        }
        catch (Exception ex)
        {
            return ActionDispatchResult.Failed(registration.Scope, action.ActionName, ex.Message);
        }
    }

    private sealed record Registration(
        ActionScope Scope,
        Func<ActionAndArgs, bool> CanExecute,
        Func<ActionAndArgs, Task> Execute);
}
