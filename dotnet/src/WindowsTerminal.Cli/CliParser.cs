using System.CommandLine;
using System.Globalization;
using System.Text;
using Microsoft.Terminal.Settings;

namespace WindowsTerminal.Cli;

public sealed class CliParser
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "new-tab", "nt", "split-pane", "sp", "focus-tab", "ft", "move-focus", "mf",
        "move-pane", "mp", "swap-pane", "focus-pane", "fp", "save", "x-save",
    };

    private readonly RootCommand _schema = CommandLineSchema.Create();

    public CliParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (IsHelpRequest(args))
        {
            _ = _schema.Parse(["--help"]);
            return new(0, CommandLineSchema.HelpText, true, null);
        }

        if (args.Count == 1 && args[0] is "-v" or "--version")
        {
            return new(0, "Windows Terminal (.NET) 0.1.0", true, null);
        }

        try
        {
            return ParseCore(args);
        }
        catch (CliUsageException ex)
        {
            return new(2, $"wt: {ex.Message}", true, null);
        }
    }

    public static IReadOnlyList<IReadOnlyList<string>> SplitCommands(IReadOnlyList<string> args)
    {
        var commands = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        foreach (var argument in args)
        {
            var part = new StringBuilder();
            var hadDelimiter = false;
            for (var index = 0; index < argument.Length; index++)
            {
                var value = argument[index];
                if (value == '\\' && index + 1 < argument.Length && argument[index + 1] == ';')
                {
                    part.Append(';');
                    index++;
                }
                else if (value == ';')
                {
                    hadDelimiter = true;
                    if (part.Length > 0)
                    {
                        current.Add(part.ToString());
                        part.Clear();
                    }

                    commands.Add(current);
                    current = [];
                }
                else
                {
                    part.Append(value);
                }
            }

            if (part.Length > 0)
            {
                current.Add(part.ToString());
            }
            else if (!hadDelimiter && argument.Length == 0)
            {
                current.Add(string.Empty);
            }
        }

        commands.Add(current);
        return commands;
    }

    private static CliParseResult ParseCore(IReadOnlyList<string> args)
    {
        var segments = SplitCommands(args);
        var actions = new List<ActionAndArgs>();
        var targetWindow = string.Empty;
        int? positionX = null;
        int? positionY = null;
        int? columns = null;
        int? rows = null;
        int? savedLayout = null;
        var launchMode = CliLaunchMode.Default;
        CliSaveRequest? save = null;

        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            var index = 0;
            if (segmentIndex == 0)
            {
                ParseRootOptions(
                    segment,
                    ref index,
                    ref targetWindow,
                    ref positionX,
                    ref positionY,
                    ref columns,
                    ref rows,
                    ref savedLayout,
                    ref launchMode);
            }

            var command = index < segment.Count && Commands.Contains(segment[index])
                ? segment[index++].ToLowerInvariant()
                : "new-tab";
            var remaining = segment.Skip(index).ToArray();
            switch (command)
            {
                case "new-tab":
                case "nt":
                    actions.Add(new(ShortcutAction.NewTab, new NewTabArgs(ParseTerminalArgs(remaining))));
                    break;
                case "split-pane":
                case "sp":
                    actions.Add(ParseSplitPane(remaining));
                    break;
                case "focus-tab":
                case "ft":
                    actions.Add(ParseFocusTab(remaining));
                    break;
                case "move-focus":
                case "mf":
                    actions.Add(new(
                        ShortcutAction.MoveFocus,
                        new MoveFocusArgs(ParseFocusDirection(RequiredValue(remaining, 0, "direction")))));
                    EnsureCount(remaining, 1);
                    break;
                case "move-pane":
                case "mp":
                    actions.Add(new(
                        ShortcutAction.MovePane,
                        new MovePaneArgs((uint)RequiredIntOption(remaining, "-t", "--tab"), string.Empty)));
                    break;
                case "swap-pane":
                    actions.Add(new(
                        ShortcutAction.SwapPane,
                        new SwapPaneArgs(ParseFocusDirection(RequiredValue(remaining, 0, "direction")))));
                    EnsureCount(remaining, 1);
                    break;
                case "focus-pane":
                case "fp":
                    actions.Add(new(
                        ShortcutAction.FocusPane,
                        new FocusPaneArgs((uint)RequiredIntOption(remaining, "-t", "--target"))));
                    break;
                case "save":
                case "x-save":
                    if (segments.Count != 1)
                    {
                        throw new CliUsageException("save cannot be combined with other commands.");
                    }

                    save = ParseSave(remaining);
                    break;
                default:
                    throw new CliUsageException($"Unknown command '{command}'.");
            }
        }

        if (save is null && (actions.Count == 0 || actions[0].Action != ShortcutAction.NewTab))
        {
            actions.Insert(0, new(ShortcutAction.NewTab, new NewTabArgs()));
        }

        var invocation = new CliInvocation(
            targetWindow,
            positionX,
            positionY,
            columns,
            rows,
            launchMode,
            savedLayout,
            actions,
            save);
        return new(0, string.Empty, false, invocation);
    }

    private static void ParseRootOptions(
        IReadOnlyList<string> args,
        ref int index,
        ref string targetWindow,
        ref int? positionX,
        ref int? positionY,
        ref int? columns,
        ref int? rows,
        ref int? savedLayout,
        ref CliLaunchMode launchMode)
    {
        while (index < args.Count)
        {
            var option = args[index];
            if (Commands.Contains(option))
            {
                return;
            }

            switch (option)
            {
                case "-w":
                case "--window":
                    targetWindow = NextRoot(args, ref index, option);
                    break;
                case "--pos":
                    (positionX, positionY) = Pair(NextRoot(args, ref index, option), option, allowZero: true);
                    break;
                case "--size":
                    (columns, rows) = Pair(NextRoot(args, ref index, option), option, allowZero: false);
                    break;
                case "-s":
                case "--saved":
                    savedLayout = PositiveInt(NextRoot(args, ref index, option), option, allowZero: true);
                    break;
                case "-M":
                case "--maximized":
                    launchMode |= CliLaunchMode.Maximized;
                    index++;
                    break;
                case "-F":
                case "--fullscreen":
                    launchMode |= CliLaunchMode.Fullscreen;
                    index++;
                    break;
                case "-f":
                case "--focus":
                    launchMode |= CliLaunchMode.Focus;
                    index++;
                    break;
                default:
                    if (option.StartsWith('-') &&
                        option.Length > 2 &&
                        option.Skip(1).All(character => character is 'M' or 'F' or 'f'))
                    {
                        foreach (var character in option.Skip(1))
                        {
                            launchMode |= character switch
                            {
                                'M' => CliLaunchMode.Maximized,
                                'F' => CliLaunchMode.Fullscreen,
                                _ => CliLaunchMode.Focus,
                            };
                        }

                        index++;
                        break;
                    }

                    return;
            }
        }
    }

    private static ActionAndArgs ParseSplitPane(IReadOnlyList<string> args)
    {
        var direction = SplitDirection.Automatic;
        var splitMode = SplitType.Manual;
        var size = 0.5f;
        var terminalArgs = new List<string>();
        var commandStarted = false;
        for (var index = 0; index < args.Count; index++)
        {
            if (commandStarted)
            {
                terminalArgs.Add(args[index]);
                continue;
            }

            switch (args[index])
            {
                case "-H":
                case "--horizontal":
                    direction = SplitDirection.Down;
                    break;
                case "-V":
                case "--vertical":
                    direction = SplitDirection.Right;
                    break;
                case "-D":
                case "--duplicate":
                    splitMode = SplitType.Duplicate;
                    break;
                case "-s":
                case "--size":
                    var value = Next(args, ref index, args[index]);
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out size) ||
                        size is <= 0.01f or >= 0.99f)
                    {
                        throw new CliUsageException("split size must be between 0.01 and 0.99.");
                    }

                    break;
                default:
                    commandStarted = true;
                    terminalArgs.Add(args[index]);
                    break;
            }
        }

        return new(
            ShortcutAction.SplitPane,
            new SplitPaneArgs(direction, splitMode, size, ParseTerminalArgs(terminalArgs)));
    }

    private static ActionAndArgs ParseFocusTab(IReadOnlyList<string> args)
    {
        if (Has(args, "-n", "--next"))
        {
            return new(ShortcutAction.NextTab, new NextTabArgs(TabSwitcherMode.Disabled));
        }

        if (Has(args, "-p", "--previous"))
        {
            return new(ShortcutAction.PrevTab, new PrevTabArgs(TabSwitcherMode.Disabled));
        }

        return new(
            ShortcutAction.SwitchToTab,
            new SwitchToTabArgs((uint)RequiredIntOption(args, "-t", "--target")));
    }

    private static NewTerminalArgs ParseTerminalArgs(IReadOnlyList<string> args)
    {
        var profile = string.Empty;
        var directory = string.Empty;
        var title = string.Empty;
        string? tabColor = null;
        var colorScheme = string.Empty;
        var sessionId = Guid.Empty;
        var append = false;
        bool? suppressTitle = null;
        bool? reloadEnvironment = null;
        var command = new List<string>();
        var commandStarted = false;
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            if (commandStarted)
            {
                command.Add(option);
                continue;
            }

            switch (option)
            {
                case "--":
                    commandStarted = true;
                    break;
                case "-p":
                case "--profile":
                    profile = Next(args, ref index, option);
                    break;
                case "--sessionId":
                    var session = Next(args, ref index, option);
                    if (!Guid.TryParse(session, out sessionId))
                    {
                        throw new CliUsageException($"Invalid session ID '{session}'.");
                    }

                    break;
                case "-d":
                case "--startingDirectory":
                    directory = Next(args, ref index, option);
                    break;
                case "--title":
                    title = Next(args, ref index, option);
                    break;
                case "--tabColor":
                    var color = Next(args, ref index, option);
                    tabColor = ValidColor(color) ? color : null;
                    break;
                case "--suppressApplicationTitle":
                    suppressTitle = true;
                    break;
                case "--useApplicationTitle":
                    suppressTitle = false;
                    break;
                case "--colorScheme":
                    colorScheme = Next(args, ref index, option);
                    break;
                case "--appendCommandLine":
                    append = true;
                    break;
                case "--inheritEnvironment":
                    reloadEnvironment = false;
                    break;
                case "--reloadEnvironment":
                    reloadEnvironment = true;
                    break;
                default:
                    if (option.StartsWith('-'))
                    {
                        throw new CliUsageException($"Unknown command '{option}'.");
                    }

                    commandStarted = true;
                    command.Add(option);
                    break;
            }
        }

        if (command.Count > 0 && reloadEnvironment is null)
        {
            reloadEnvironment = false;
        }

        return new(
            JoinCommandline(command),
            directory,
            title,
            tabColor,
            Profile: profile,
            SessionId: sessionId,
            AppendCommandLine: append,
            SuppressApplicationTitle: suppressTitle,
            ColorScheme: colorScheme,
            ReloadEnvironmentVariables: reloadEnvironment);
    }

    private static CliSaveRequest ParseSave(IReadOnlyList<string> args)
    {
        var name = string.Empty;
        var keyChord = string.Empty;
        var command = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "-n":
                case "--name":
                    name = Next(args, ref index, args[index]);
                    break;
                case "-k":
                case "--keychord":
                    keyChord = Next(args, ref index, args[index]);
                    break;
                default:
                    command.Add(args[index]);
                    break;
            }
        }

        return new(name, keyChord, JoinCommandline(command));
    }

    private static FocusDirection ParseFocusDirection(string value) => value.ToLowerInvariant() switch
    {
        "left" => FocusDirection.Left,
        "right" => FocusDirection.Right,
        "up" => FocusDirection.Up,
        "down" => FocusDirection.Down,
        "previous" => FocusDirection.Previous,
        "previousinorder" => FocusDirection.PreviousInOrder,
        "nextinorder" => FocusDirection.NextInOrder,
        "first" => FocusDirection.First,
        _ => throw new CliUsageException($"Unknown focus direction '{value}'."),
    };

    private static int RequiredIntOption(IReadOnlyList<string> args, string shortName, string longName)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] == shortName || args[index] == longName)
            {
                return PositiveInt(Next(args, ref index, args[index]), args[index], allowZero: true);
            }
        }

        throw new CliUsageException($"Option '{longName}' is required.");
    }

    private static string RequiredValue(IReadOnlyList<string> args, int index, string name) =>
        index < args.Count ? args[index] : throw new CliUsageException($"{name} is required.");

    private static void EnsureCount(IReadOnlyList<string> args, int count)
    {
        if (args.Count != count)
        {
            throw new CliUsageException("Unexpected arguments.");
        }
    }

    private static bool Has(IReadOnlyList<string> args, string shortName, string longName) =>
        args.Contains(shortName, StringComparer.Ordinal) || args.Contains(longName, StringComparer.Ordinal);

    private static string Next(IReadOnlyList<string> args, ref int index, string option)
    {
        index++;
        if (index >= args.Count)
        {
            throw new CliUsageException($"Option '{option}' requires a value.");
        }

        return args[index];
    }

    private static string NextRoot(IReadOnlyList<string> args, ref int index, string option)
    {
        var value = Next(args, ref index, option);
        index++;
        return value;
    }

    private static (int First, int Second) Pair(string value, string option, bool allowZero)
    {
        var components = value.Split(',', StringSplitOptions.TrimEntries);
        if (components.Length != 2)
        {
            throw new CliUsageException($"Option '{option}' requires two comma-separated integers.");
        }

        return (
            PositiveInt(components[0], option, allowZero),
            PositiveInt(components[1], option, allowZero));
    }

    private static int PositiveInt(string value, string option, bool allowZero)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ||
            result < (allowZero ? 0 : 1))
        {
            throw new CliUsageException($"Option '{option}' requires a valid non-negative integer.");
        }

        return result;
    }

    private static bool ValidColor(string value) =>
        value.Length is 7 or 9 &&
        value[0] == '#' &&
        value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static string JoinCommandline(IEnumerable<string> args) =>
        string.Join(' ', args.Select(QuoteArgument));

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', (backslashes * 2) + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            result.Append(character);
            backslashes = 0;
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "-?" or "/?";

    private static bool IsHelpRequest(IReadOnlyList<string> args)
    {
        var commandSeen = false;
        var valueOptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "-w", "--window", "--pos", "--size", "-s", "--saved",
            "-p", "--profile", "--sessionId", "-d", "--startingDirectory",
            "--title", "--tabColor", "--colorScheme", "-t", "--tab",
            "--target", "--name", "-n", "--keychord", "-k",
        };
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument == "--")
            {
                return false;
            }

            if (IsHelp(argument))
            {
                return true;
            }

            if (valueOptions.Contains(argument))
            {
                index++;
                continue;
            }

            if (Commands.Contains(argument))
            {
                commandSeen = true;
                continue;
            }

            if (!commandSeen && !argument.StartsWith('-'))
            {
                return false;
            }

            if (commandSeen && !argument.StartsWith('-'))
            {
                return false;
            }
        }

        return false;
    }

    private sealed class CliUsageException(string message) : Exception(message);
}

internal static class CommandLineSchema
{
    public const string HelpText =
        """
        wt - Windows Terminal

        Usage: wt [options] [command] [; command ...]

        Options:
          -w, --window <target>  use-new, use-any, use-existing, an ID, or a name
          --pos <x,y>            Window position
          --size <columns,rows>  Terminal size
          -M, --maximized        Launch maximized
          -F, --fullscreen       Launch fullscreen
          -f, --focus            Hide the title bar

        Commands:
          new-tab, nt   split-pane, sp   focus-tab, ft   move-focus, mf
          move-pane, mp   swap-pane   focus-pane, fp   save
        """;

    public static RootCommand Create()
    {
        var root = new RootCommand("Windows Terminal");
        root.Options.Add(new Option<string>("--window") { Description = "Target window." });
        root.Options.Add(new Option<string>("--pos") { Description = "Window position." });
        root.Options.Add(new Option<string>("--size") { Description = "Terminal size." });
        foreach (var (name, alias) in new[]
                 {
                     ("new-tab", "nt"),
                     ("split-pane", "sp"),
                     ("focus-tab", "ft"),
                     ("move-focus", "mf"),
                     ("move-pane", "mp"),
                     ("focus-pane", "fp"),
                 })
        {
            var command = new System.CommandLine.Command(name);
            command.Aliases.Add(alias);
            root.Subcommands.Add(command);
        }

        root.Subcommands.Add(new System.CommandLine.Command("swap-pane"));
        root.Subcommands.Add(new System.CommandLine.Command("save"));
        return root;
    }
}
