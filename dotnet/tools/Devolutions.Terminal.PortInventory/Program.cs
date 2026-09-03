using System.Text.Json;
using System.Text.RegularExpressions;

namespace Devolutions.Terminal.PortInventory;

public sealed record CompatibilityInventory(
    IReadOnlyList<string> SettingsKeys,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> ActionsWithArgs,
    IReadOnlyList<string> VtDispatchMethods,
    IReadOnlyList<string> CliSubcommands,
    IReadOnlyList<string> CliOptions,
    IReadOnlyList<string> SettingsPages);

public static partial class InventoryGenerator
{
    public static CompatibilityInventory Generate(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var settingsModel = Path.Combine(repositoryRoot, "src", "cascadia", "TerminalSettingsModel");
        var terminalApp = Path.Combine(repositoryRoot, "src", "cascadia", "TerminalApp");
        var settingsEditor = Path.Combine(repositoryRoot, "src", "cascadia", "TerminalSettingsEditor");
        var terminalAdapter = Path.Combine(repositoryRoot, "src", "terminal", "adapter");

        var settingsText = File.ReadAllText(Path.Combine(settingsModel, "MTSMSettings.h"));
        var actionText = File.ReadAllText(Path.Combine(settingsModel, "AllShortcutActions.h"));
        var dispatchText = File.ReadAllText(Path.Combine(terminalAdapter, "ITermDispatch.hpp"));
        var commandlineText = File.ReadAllText(Path.Combine(terminalApp, "AppCommandlineArgs.cpp"));

        var actionsSection = Slice(
            actionText,
            "#define ALL_SHORTCUT_ACTIONS",
            "#define ALL_SHORTCUT_ACTIONS_WITH_ARGS");
        var actionsWithArgsSection = Slice(
            actionText,
            "#define ALL_SHORTCUT_ACTIONS_WITH_ARGS",
            "#define INTERNAL_SHORTCUT_ACTIONS");

        return new CompatibilityInventory(
            SortedMatches(SettingsKeyRegex(), settingsText),
            SortedMatches(ActionRegex(), actionsSection),
            SortedMatches(ActionWithArgsRegex(), actionsWithArgsSection),
            SortedMatches(VirtualMethodRegex(), dispatchText),
            SortedMatches(SubcommandRegex(), commandlineText),
            SortedMatches(CliOptionRegex(), commandlineText)
                .SelectMany(SplitOptionAliases)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            Directory.EnumerateFiles(settingsEditor, "*.xaml", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(static name => name is not null)
                .Cast<string>()
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    public static void Write(string repositoryRoot, string outputPath)
    {
        var inventory = Generate(repositoryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static IReadOnlyList<string> SortedMatches(Regex regex, string text) =>
        regex.Matches(text)
            .Select(static match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<string> SplitOptionAliases(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static option => option.TrimStart('!'))
            .Where(static option => option.StartsWith('-'));

    private static string Slice(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        var endIndex = text.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        if (startIndex < 0 || endIndex < 0)
        {
            throw new InvalidDataException($"Could not find inventory section '{start}' to '{end}'.");
        }

        return text[startIndex..endIndex];
    }

    [GeneratedRegex(@"X\([^\r\n]*?,\s*""([^""]+)")]
    private static partial Regex SettingsKeyRegex();

    [GeneratedRegex(@"ON_ALL_ACTIONS\((\w+)\)")]
    private static partial Regex ActionRegex();

    [GeneratedRegex(@"ON_ALL_ACTIONS_WITH_ARGS\((\w+)\)")]
    private static partial Regex ActionWithArgsRegex();

    [GeneratedRegex(@"virtual\s+[\w:<>,\s&*]+\s+(\w+)\s*\(")]
    private static partial Regex VirtualMethodRegex();

    [GeneratedRegex(@"add_subcommand\(""([^""]+)")]
    private static partial Regex SubcommandRegex();

    [GeneratedRegex(@"add_(?:option|flag)(?:_function<[^>]+>|_function)?\(\s*""([^""]+)")]
    private static partial Regex CliOptionRegex();
}

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: Devolutions.Terminal.PortInventory <repository-root> <output-json>");
            return 2;
        }

        InventoryGenerator.Write(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
        return 0;
    }
}
