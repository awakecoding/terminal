using Microsoft.Terminal.Settings;

namespace WindowsTerminal.Models;

public enum ResolvedNewTabMenuItemType
{
    Profile,
    Separator,
    Folder,
    Action,
}

public sealed record ResolvedNewTabMenuItem(
    ResolvedNewTabMenuItemType Type,
    string Name,
    ProfileSettings? Profile = null,
    string? ActionId = null,
    string? Icon = null,
    IReadOnlyList<ResolvedNewTabMenuItem>? Children = null);

public static class NewTabMenuResolver
{
    public static IReadOnlyList<ResolvedNewTabMenuItem> Resolve(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var visible = settings.Profiles.Where(static profile => !profile.Hidden && !profile.Orphaned).ToArray();
        var emitted = new HashSet<ProfileSettings>(ReferenceEqualityComparer.Instance);
        return ResolveEntries(settings.NewTabMenu, visible, emitted);
    }

    private static IReadOnlyList<ResolvedNewTabMenuItem> ResolveEntries(
        IEnumerable<NewTabMenuEntry> entries,
        IReadOnlyList<ProfileSettings> profiles,
        ISet<ProfileSettings> emitted)
    {
        var result = new List<ResolvedNewTabMenuItem>();
        foreach (var entry in entries)
        {
            switch (entry.Type)
            {
                case NewTabMenuEntryType.Profile:
                    if (FindProfile(profiles, entry.Profile) is { } profile)
                    {
                        emitted.Add(profile);
                        result.Add(ProfileItem(profile));
                    }
                    break;
                case NewTabMenuEntryType.Separator:
                    result.Add(new(ResolvedNewTabMenuItemType.Separator, "-"));
                    break;
                case NewTabMenuEntryType.Folder:
                    var children = ResolveEntries(entry.Entries, profiles, emitted);
                    if (children.Count > 0 || entry.AllowEmpty)
                    {
                        result.Add(new(
                            ResolvedNewTabMenuItemType.Folder,
                            entry.Name ?? string.Empty,
                            Icon: entry.Icon?.ToString(),
                            Children: children));
                    }
                    break;
                case NewTabMenuEntryType.RemainingProfiles:
                    foreach (var remaining in profiles.Where(profile => !emitted.Contains(profile)))
                    {
                        emitted.Add(remaining);
                        result.Add(ProfileItem(remaining));
                    }
                    break;
                case NewTabMenuEntryType.MatchProfiles:
                    foreach (var match in profiles.Where(profile => Matches(profile, entry)))
                    {
                        emitted.Add(match);
                        result.Add(ProfileItem(match));
                    }
                    break;
                case NewTabMenuEntryType.Action:
                    if (!string.IsNullOrWhiteSpace(entry.ActionId))
                    {
                        result.Add(new(
                            ResolvedNewTabMenuItemType.Action,
                            entry.Name ?? entry.ActionId,
                            ActionId: entry.ActionId,
                            Icon: entry.Icon?.ToString()));
                    }
                    break;
            }
        }

        return result;
    }

    private static ProfileSettings? FindProfile(
        IEnumerable<ProfileSettings> profiles,
        string? identity) =>
        string.IsNullOrWhiteSpace(identity)
            ? null
            : profiles.FirstOrDefault(profile =>
                profile.Name.Equals(identity, StringComparison.OrdinalIgnoreCase) ||
                profile.Guid?.Trim('{', '}').Equals(
                    identity.Trim('{', '}'),
                    StringComparison.OrdinalIgnoreCase) == true);

    private static bool Matches(ProfileSettings profile, NewTabMenuEntry entry) =>
        (string.IsNullOrWhiteSpace(entry.MatchName) ||
         profile.Name.Contains(entry.MatchName, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(entry.MatchCommandline) ||
         profile.Commandline.Contains(entry.MatchCommandline, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(entry.MatchSource) ||
         string.Equals(profile.Source, entry.MatchSource, StringComparison.OrdinalIgnoreCase));

    private static ResolvedNewTabMenuItem ProfileItem(ProfileSettings profile) =>
        new(
            ResolvedNewTabMenuItemType.Profile,
            profile.Name,
            profile,
            Icon: profile.IconResource?.ToString());
}
