using Microsoft.Terminal.Settings;
using System.Security.Cryptography;

namespace WindowsTerminal.Settings;

public sealed record SettingsDiagnosticViewModel(string Severity, string Code, string Message, string? Source);

public sealed class SettingsNavigationItem
{
    public required SettingsPage Page { get; init; }
    public required string Icon { get; init; }
    public required string Title { get; init; }
    public required string Keywords { get; init; }
    public required object ViewModel { get; init; }

    public override string ToString() => Title;
}

public sealed class SettingsEditorViewModel : ObservableObject
{
    private readonly Func<AppSettings> _load;
    private readonly Action<AppSettings> _save;
    private readonly Func<AppSettings> _createDefault;
    private readonly Func<string?> _getRevision;
    private AppSettings _settings;
    private string? _loadedRevision;
    private IReadOnlyList<SettingsNavigationItem> _navigationItems = [];
    private IReadOnlyList<SettingsNavigationItem> _visibleNavigationItems = [];
    private IReadOnlyList<ProfileItemViewModel> _profiles = [];
    private SettingsNavigationItem? _selectedNavigationItem;
    private string _searchText = string.Empty;
    private bool _isDirty;
    private string _statusMessage = "Settings loaded.";
    private IReadOnlyList<SettingsDiagnosticViewModel> _diagnostics = [];
    private ActionsSettingsViewModel? _actions;
    private NewTabMenuSettingsViewModel? _newTabMenu;

    public SettingsEditorViewModel()
        : this(
            SettingsService.Load,
            SettingsService.Save,
            SettingsService.CreateDefault,
            ReadSettingsRevision)
    {
    }

    public SettingsEditorViewModel(
        Func<AppSettings> load,
        Action<AppSettings> save,
        Func<AppSettings> createDefault,
        Func<string?>? getRevision = null)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _createDefault = createDefault ?? throw new ArgumentNullException(nameof(createDefault));
        _getRevision = getRevision ?? (() => null);
        _settings = _load();
        _loadedRevision = _getRevision();
        ApplyCommand = new(Apply, () => IsDirty);
        RevertCommand = new(Revert, () => IsDirty);
        ResetCommand = new(ResetToDefaults);
        BuildPages(SettingsPage.Startup);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                FilterNavigation();
            }
        }
    }

    public IReadOnlyList<SettingsNavigationItem> VisibleNavigationItems
    {
        get => _visibleNavigationItems;
        private set => SetProperty(ref _visibleNavigationItems, value);
    }

    public SettingsNavigationItem? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (SetProperty(ref _selectedNavigationItem, value))
            {
                OnPropertyChanged(nameof(CurrentPage));
            }
        }
    }

    public object? CurrentPage => SelectedNavigationItem?.ViewModel;
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                ApplyCommand.RaiseCanExecuteChanged();
                RevertCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public IReadOnlyList<SettingsDiagnosticViewModel> Diagnostics { get => _diagnostics; private set => SetProperty(ref _diagnostics, value); }
    public string SettingsPath => SettingsService.SettingsPath;
    public RelayCommand ApplyCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand ResetCommand { get; }

    public void SelectPage(SettingsPage page)
    {
        var item = _navigationItems.FirstOrDefault(candidate => candidate.Page == page);
        if (item is not null)
        {
            SelectedNavigationItem = item;
        }
    }

    public void Apply()
    {
        if (!string.Equals(_loadedRevision, _getRevision(), StringComparison.Ordinal))
        {
            StatusMessage = "Settings changed on disk after this editor loaded them. Revert to reload before applying.";
            return;
        }

        if (!TryCommitEditors(out var error))
        {
            StatusMessage = error!;
            return;
        }

        try
        {
            _save(_settings);
            _settings = _load();
            _loadedRevision = _getRevision();
            var selectedPage = SelectedNavigationItem?.Page ?? SettingsPage.Startup;
            BuildPages(selectedPage);
            IsDirty = false;
            StatusMessage = $"Saved atomically to {SettingsService.SettingsPath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    public void Revert()
    {
        _settings = _load();
        _loadedRevision = _getRevision();
        var selectedPage = SelectedNavigationItem?.Page ?? SettingsPage.Startup;
        BuildPages(selectedPage);
        IsDirty = false;
        StatusMessage = "Unsaved changes reverted.";
    }

    public void ResetToDefaults()
    {
        _settings = _createDefault();
        var selectedPage = SelectedNavigationItem?.Page ?? SettingsPage.Startup;
        BuildPages(selectedPage);
        MarkDirty();
        StatusMessage = "Factory defaults loaded. Apply to save them.";
    }

    private bool TryCommitEditors(out string? error)
    {
        if (!_actions!.TryCommit(out error) || !_newTabMenu!.TryCommit(out error))
        {
            return false;
        }

        foreach (var profile in _profiles)
        {
            if (!profile.TryCommitEnvironment(out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    private void BuildPages(SettingsPage selectedPage)
    {
        _profiles = _settings.Profiles
            .Select(profile => new ProfileItemViewModel(profile, MarkDirty))
            .ToArray();
        _actions = new(_settings, MarkDirty);
        _newTabMenu = new(_settings, MarkDirty);
        _navigationItems =
        [
            Item(SettingsPage.Startup, "Startup", "launch default profile window position startup actions", new StartupSettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.Interaction, "Interaction", "copy paste selection mouse urls focus", new InteractionSettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.Appearance, "Appearance", "global themes tabs acrylic mica visual", new AppearanceSettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.Profiles, "Profiles: base", "profile commandline directory icon tab title hidden", new ProfilesSettingsViewModel(_profiles)),
            Item(SettingsPage.ProfileAppearance, "Profiles: appearance", "profile font colors opacity background image", new ProfileAppearanceSettingsViewModel(_profiles)),
            Item(SettingsPage.ProfileTerminal, "Profiles: terminal", "profile scrollback cursor close antialiasing", new ProfileTerminalSettingsViewModel(_profiles)),
            Item(SettingsPage.ProfileAdvanced, "Profiles: advanced", "profile vt environment kitty osc compatibility", new ProfileAdvancedSettingsViewModel(_profiles)),
            Item(SettingsPage.ColorSchemes, "Color schemes", "palette ansi foreground background cursor selection", new ColorSchemesSettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.Actions, "Actions", "keybindings key chord command json", _actions),
            Item(SettingsPage.NewTabMenu, "New tab menu", "menu folder separator profile action json", _newTabMenu),
            Item(SettingsPage.Rendering, "Rendering", "graphics api software invalidation", new RenderingSettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.Compatibility, "Compatibility", "text measurement width input headless acrylic", new CompatibilitySettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.Extensions, "Extensions", "sources fragments experimental language notification", new ExtensionsSettingsViewModel(_settings, MarkDirty)),
        ];
        Diagnostics = _settings.Diagnostics
            .Select(diagnostic => new SettingsDiagnosticViewModel(
                diagnostic.Severity.ToString(),
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.Source))
            .ToArray();
        FilterNavigation();
        SelectedNavigationItem =
            _navigationItems.FirstOrDefault(item => item.Page == selectedPage) ??
            _navigationItems[0];
        OnPropertyChanged(nameof(SettingsPath));
    }

    private static SettingsNavigationItem Item(
        SettingsPage page,
        string title,
        string keywords,
        object viewModel) =>
        new()
        {
            Page = page,
            Icon = page switch
            {
                SettingsPage.Startup => "\uE7B5",
                SettingsPage.Interaction => "\uE8D4",
                SettingsPage.Appearance => "\uE790",
                SettingsPage.Profiles => "\uE77B",
                SettingsPage.ProfileAppearance => "\uE790",
                SettingsPage.ProfileTerminal => "\uE756",
                SettingsPage.ProfileAdvanced => "\uE90F",
                SettingsPage.ColorSchemes => "\uE2B1",
                SettingsPage.Actions => "\uE765",
                SettingsPage.NewTabMenu => "\uE8FD",
                SettingsPage.Rendering => "\uE7F8",
                SettingsPage.Compatibility => "\uE713",
                SettingsPage.Extensions => "\uE71B",
                _ => "\uE946",
            },
            Title = title,
            Keywords = keywords,
            ViewModel = viewModel,
        };

    private void FilterNavigation()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            VisibleNavigationItems = _navigationItems;
            return;
        }

        var terms = SearchText.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        VisibleNavigationItems = _navigationItems
            .Where(item => terms.All(term =>
                item.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Keywords.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (VisibleNavigationItems.Count > 0 &&
            (SelectedNavigationItem is null || !VisibleNavigationItems.Contains(SelectedNavigationItem)))
        {
            SelectedNavigationItem = VisibleNavigationItems[0];
        }
    }

    private void MarkDirty()
    {
        IsDirty = true;
        StatusMessage = "Unsaved changes.";
    }

    private static string? ReadSettingsRevision()
    {
        try
        {
            return File.Exists(SettingsService.SettingsPath)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SettingsService.SettingsPath)))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"unavailable:{ex.HResult}";
        }
    }
}
