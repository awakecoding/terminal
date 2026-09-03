using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal;
using Devolutions.Terminal.Core;
using Devolutions.Terminal.Settings;
using Devolutions.Terminal.App.Actions;
using Devolutions.Terminal.App.Connections;
using Devolutions.Terminal.App.Models;
using Devolutions.Terminal.App.Panes;
using Devolutions.Terminal.App.Platform;
using Devolutions.Terminal.App.Routing;
using Devolutions.Terminal.Settings.Editor;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Devolutions.Terminal.App.Views;

public partial class MainWindow :
    Window,
    ITerminalWindowActivationTarget,
    IGlobalWindowActionTarget,
    IWindowSummonOperations
{
    private static readonly SemaphoreSlim JumpListRefreshGate = new(1, 1);
    private static string? _lastJumpListFingerprint;
    private static long _lastSystemToastTick;
    private AppSettings _settings;
    private readonly ApplicationStateStore _stateStore;
    private readonly TerminalConnectionFactory _connectionFactory;
    private readonly DynamicProfileManager _dynamicProfileManager;
    private readonly IPlatformLauncher _platformLauncher;
    private readonly ActionDispatcher _actionDispatcher = new();
    private readonly TabCollection<TerminalTab, TabLayoutDescriptor> _tabCollection = new();
    private readonly List<PaletteItem> _paletteItems = [];
    private readonly Dictionary<string, Bitmap> _tabIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<TerminalPane, PaneScrollBar> _paneScrollBars = new();
    private IReadOnlyList<TerminalTab> _tabs => _tabCollection.Items;
    private uint _nextPaneId;
    private TerminalTab? _activeTab;
    private TerminalTab? _draggedTab;
    private Point _dragStart;
    private PaletteMode _paletteMode;
    private bool _layoutPersisted;
    private bool _focusMode;
    private bool _persistenceBlockedByInvalidLayout;
    private ActionDispatchResult? _lastDispatchResult;
    private ProfileSettings? _initialProfile;
    private IInputElement? _aboutPreviousFocus;
    private readonly TerminalWindowActivation? _initialActivation;
    private readonly Action<TerminalWindowActivation>? _newWindowRequested;
    private readonly Action<TabTearOffRequest>? _tabTearOffRequested;
    private readonly Func<string, TerminalCommandLineParseResult>? _commandLineParser;
    private readonly Action<string>? _workspaceRequested;
    private readonly Func<string, bool>? _windowNameValidator;
    private readonly Func<IReadOnlyList<string>>? _windowIdentityProvider;
    private readonly Func<GlobalSummonArgs, ValueTask<WindowActionResult>>? _summonRequested;
    private readonly Action<AppSettings>? _settingsChanged;
    private readonly ISystemMenuService _systemMenuService;
    private readonly TaskCompletionSource<TerminalWindowActivationResult> _initialActivationCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _notificationTimer;
    private PixelPoint? _normalPosition;
    private WindowSizeState _normalSize = new();

    public MainWindow() : this(0, string.Empty, null)
    {
    }

    public MainWindow(
        int windowId,
        string windowName,
        TerminalWindowActivation? initialActivation,
        Action<TerminalWindowActivation>? newWindowRequested = null,
        Action<TabTearOffRequest>? tabTearOffRequested = null,
        Func<string, TerminalCommandLineParseResult>? commandLineParser = null,
        IPlatformLauncher? platformLauncher = null,
        Func<string, bool>? windowNameValidator = null,
        Func<IReadOnlyList<string>>? windowIdentityProvider = null,
        ApplicationStateStore? stateStore = null,
        Action<string>? workspaceRequested = null,
        Func<GlobalSummonArgs, ValueTask<WindowActionResult>>? summonRequested = null,
        Action<AppSettings>? settingsChanged = null,
        ISystemMenuService? systemMenuService = null)
    {
        WindowId = windowId;
        WindowName = windowName;
        _initialActivation = initialActivation;
        _newWindowRequested = newWindowRequested;
        _tabTearOffRequested = tabTearOffRequested;
        _commandLineParser = commandLineParser;
        _workspaceRequested = workspaceRequested;
        _summonRequested = summonRequested;
        _settingsChanged = settingsChanged;
        _systemMenuService = systemMenuService ?? SystemMenuService.CreateDefault();
        _platformLauncher = platformLauncher ?? new PlatformLauncher();
        _windowNameValidator = windowNameValidator;
        _windowIdentityProvider = windowIdentityProvider;
        InitializeComponent();
        if (OperatingSystem.IsWindows())
        {
            Win32ParentWindow.Attach(this);
        }

        AutomationProperties.SetName(AboutOverlay, "About Devolutions Terminal dialog");
        AutomationProperties.SetControlTypeOverride(AboutOverlay, AutomationControlType.Window);
        _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            NotificationToast.IsVisible = false;
        };
        _connectionFactory = new TerminalConnectionFactory(
            new AzureCloudShellAuthenticationCallbacks
            {
                ShowDeviceCodeAsync = ShowAzureDeviceCodeAsync,
                SelectTenantAsync = SelectAzureTenantAsync,
            });
        _dynamicProfileManager = TerminalConnectionFactory.IsAzureConfigured
            ? DynamicProfileManager.CreateDefaultWithAzure(
                AzureCloudShellConnection.ConnectionTypeGuid)
            : DynamicProfileManager.CreateDefault();
        _settings = SettingsService.LoadWithDynamicProfiles(_dynamicProfileManager);
        _settingsChanged?.Invoke(_settings);
        ApplyWindowChrome();
        RefreshJumpList();
        _stateStore = stateStore ?? SettingsService.LoadApplicationState();
        var defaultProfile = _settings.GetDefaultProfile();
        var defaultCell = TermControl.MeasureCell(defaultProfile, DisplayScale());
        Width = Math.Max(
            640,
            (_settings.InitialCols * defaultCell.Width) + 16 + ScrollbarWidth(defaultProfile));
        Height = Math.Max(400, (_settings.InitialRows * defaultCell.Height) + 56);
        _normalSize = new WindowSizeState { Width = Width, Height = Height };
        Opened += OnOpened;
        PositionChanged += (_, _) =>
            Dispatcher.UIThread.Post(CaptureNormalWindowBounds, DispatcherPriority.Background);
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty)
            {
                UpdateFullscreenChrome();
            }
        };
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnWindowTextInput, RoutingStrategies.Tunnel);
        ConfigureActionDispatcher();
        PopulateCommandPalette();
    }

    private MainWindow(ProfileSettings initialProfile) : this()
    {
        _initialProfile = initialProfile;
    }

    public int WindowId { get; }
    public string WindowName { get; private set; }
    public Task<TerminalWindowActivationResult> InitialActivation => _initialActivationCompletion.Task;
    public IReadOnlyList<TerminalTab> Tabs => _tabCollection.Items;
    public IReadOnlyCollection<ShortcutAction> RegisteredActions =>
        _actionDispatcher.RegisteredActions;
    public TerminalTab? ActiveTab => _activeTab;
    public IReadOnlyList<string> WorkspaceNames => _stateStore.GetWorkspaceNames();
    public bool AlwaysShowNotificationIcon => _settings.AlwaysShowNotificationIcon;
    public bool MinimizeToNotificationArea => _settings.MinimizeToNotificationArea;
    public string? LastPersistenceError { get; private set; }
    public event Action<TabTearOffRequest>? TabTearOffRequested;

    public async ValueTask<TerminalWindowActivationResult> ActivateAsync(
        TerminalWindowActivation activation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyLaunchOptions(activation);
        if (activation.SaveRequest is { } saveRequest)
        {
            var saveResult = SaveSnippet(saveRequest);
            if (!saveResult.Succeeded)
            {
                return new(false, saveResult.Message, []);
            }
        }

        var results = new List<ActionDispatchResult>(activation.Actions.Count);
        foreach (var action in activation.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await DispatchActionAsync(action).ConfigureAwait(true));
        }

        if (!activation.Actions.Any(ManagesWindowVisibility))
        {
            Show();
            Activate();
        }
        var failure = results.FirstOrDefault(result => result.Status != ActionDispatchStatus.Executed);
        return failure is null
            ? new(true, "Activation completed.", results)
            : new(false, failure.Message ?? $"Action '{failure.Action}' failed.", results);
    }

    private static bool ManagesWindowVisibility(ActionAndArgs action) =>
        action.Action is ShortcutAction.GlobalSummon or ShortcutAction.QuakeMode ||
        action.Action == ShortcutAction.MultipleActions &&
        action.Args is MultipleActionsArgs multiple &&
        multiple.Actions.Any(ManagesWindowVisibility);

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            if (_initialActivation is not null)
            {
                if (!string.IsNullOrWhiteSpace(_initialActivation.PersistedLayoutDiagnostic))
                {
                    LastPersistenceError = _initialActivation.PersistedLayoutDiagnostic;
                    _persistenceBlockedByInvalidLayout = true;
                    var fallback = await ActivateAsync(
                        _initialActivation with { PersistedLayoutDiagnostic = null }).ConfigureAwait(true);
                    _initialActivationCompletion.SetResult(
                        fallback with
                        {
                            Succeeded = false,
                            Message = LastPersistenceError,
                        });
                }
                else if (_initialActivation.PersistedLayout is { } requestedLayout &&
                    await TryRestoreLayoutAsync(requestedLayout).ConfigureAwait(true))
                {
                    ApplyLaunchOptions(_initialActivation);
                    _initialActivationCompletion.SetResult(
                        new(true, "Saved layout restored.", []));
                }
                else if (IsDefaultStartupActivation(_initialActivation) &&
                    await TryRestorePersistedLayoutAsync().ConfigureAwait(true))
                {
                    ApplyLaunchOptions(_initialActivation);
                    _initialActivationCompletion.SetResult(
                        new(true, "Persisted layout restored.", []));
                }
                else
                {
                    _initialActivationCompletion.SetResult(
                        await ActivateAsync(_initialActivation).ConfigureAwait(true));
                }
            }
            else
            {
                if (_initialProfile is not null ||
                    !await TryRestorePersistedLayoutAsync().ConfigureAwait(true))
                {
                    await CreateTabAsync(_initialProfile ?? _settings.GetDefaultProfile()).ConfigureAwait(true);
                }
                _initialActivationCompletion.SetResult(
                    new(true, "Activation completed.", []));
            }
        }
        catch (Exception ex)
        {
            _initialActivationCompletion.SetException(ex);
        }
    }

    private void ApplyLaunchOptions(TerminalWindowActivation activation)
    {
        if (activation.PositionX is { } x && activation.PositionY is { } y)
        {
            Position = new PixelPoint(x, y);
        }

        var profile = ResolveLaunchProfile(activation) ??
                      _activeTab?.Panes.ActiveContent?.Profile ??
                      _settings.GetDefaultProfile();
        var cell = TermControl.MeasureCell(profile, DisplayScale());
        if (activation.Columns is { } columns)
        {
            Width = Math.Max(320, (columns * cell.Width) + 16 + ScrollbarWidth(profile));
        }

        if (activation.Rows is { } rows)
        {
            var titleBarHeight = activation.LaunchMode.HasFlag(TerminalWindowLaunchMode.Focus)
                ? 0
                : 40;
            Height = Math.Max(240, (rows * cell.Height) + 16 + titleBarHeight);
        }

        if (activation.LaunchMode.HasFlag(TerminalWindowLaunchMode.Fullscreen))
        {
            WindowState = WindowState.FullScreen;
        }
        else if (activation.LaunchMode.HasFlag(TerminalWindowLaunchMode.Maximized))
        {
            WindowState = WindowState.Maximized;
        }

        _focusMode = activation.LaunchMode.HasFlag(TerminalWindowLaunchMode.Focus);
        ApplyWindowChrome();
    }

    private ProfileSettings? ResolveLaunchProfile(TerminalWindowActivation activation)
    {
        foreach (var action in activation.Actions)
        {
            if (action.Action == ShortcutAction.NewTab &&
                action.Args is NewTabArgs args)
            {
                return ResolveProfile(args.ContentArgs);
            }
        }

        return _initialProfile;
    }

    private double DisplayScale() =>
        Screens.ScreenFromPoint(Position)?.Scaling ??
        Screens.Primary?.Scaling ??
        1;

    private static bool IsDefaultStartupActivation(TerminalWindowActivation activation) =>
        activation.SaveRequest is null &&
        activation.PersistedLayout is null &&
        activation.WorkspaceName is null &&
        (activation.Actions.Count == 0 ||
         (activation.Actions.Count == 1 &&
          activation.Actions[0] is
          {
              Action: ShortcutAction.NewTab,
              Args: NewTabArgs { ContentArgs: NewTerminalArgs terminal },
          } &&
          terminal == new NewTerminalArgs()));

    private async void NewTab_OnClick(object? sender, RoutedEventArgs e) =>
        await CreateTabAsync(_settings.GetDefaultProfile()).ConfigureAwait(true);

    private void Menu_OnClick(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            ItemsSource = BuildNewTabMenu(),
        };
        menu.Open(sender as Control);
    }

    private List<MenuItem> BuildNewTabMenu()
    {
        var resolvedItems = NewTabMenuResolver.Resolve(_settings);
        var items = resolvedItems
            .Select(CreateMenuItem)
            .ToList();
        if (items.Count > 0)
        {
            items.Add(new MenuItem { Header = "-" });
        }
        var splitPane = new MenuItem
        {
            Header = "Split pane",
            Command = new RelayCommand(() => _ = SplitActivePaneAsync(PaneSplitOrientation.Vertical)),
            Icon = FluentMenuIcon("\uE7C2"),
        };
        AutomationProperties.SetName(splitPane, "Split pane");
        AutomationProperties.SetAutomationId(splitPane, "SplitPaneMenuItem");
        items.Add(splitPane);
        items.Add(new MenuItem { Header = "-" });
        var settings = new MenuItem
        {
            Header = "Settings",
            Command = new RelayCommand(() => OpenSettings()),
            Icon = FluentMenuIcon("\uE713"),
            InputGesture = EffectiveDefaultGesture(
                "ctrl+comma",
                ShortcutAction.OpenSettings,
                new KeyGesture(Key.OemComma, KeyModifiers.Control)),
        };
        AutomationProperties.SetName(settings, "Settings");
        AutomationProperties.SetAutomationId(settings, "SettingsMenuItem");
        items.Add(settings);
        items.Add(new MenuItem
        {
            Header = "Command palette",
            Command = new RelayCommand(() => ShowCommandPalette()),
            Icon = FluentMenuIcon("\uE945"),
            InputGesture = EffectiveDefaultGesture(
                "ctrl+shift+p",
                ShortcutAction.ToggleCommandPalette,
                new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift)),
        });
        items.Add(new MenuItem
        {
            Header = "About",
            Command = new RelayCommand(ShowAbout),
            Icon = FluentMenuIcon("\uE897"),
        });
        return items;
    }

    private KeyGesture? EffectiveDefaultGesture(
        string chord,
        ShortcutAction expectedAction,
        KeyGesture gesture) =>
        _settings.ActionMap.ResolveAction(chord)?.Action == expectedAction ? gesture : null;

    private KeyGesture? ProfileMenuGesture(ProfileSettings profile)
    {
        for (var number = 1; number <= 9; number++)
        {
            var action = _settings.ActionMap.ResolveAction($"ctrl+shift+{number}");
            if (action?.Action != ShortcutAction.NewTab ||
                action.Args is not NewTabArgs newTab)
            {
                continue;
            }

            var target = ResolveProfile(newTab.ContentArgs);
            var matches = !string.IsNullOrWhiteSpace(profile.Guid) &&
                          !string.IsNullOrWhiteSpace(target.Guid)
                ? profile.Guid.Equals(target.Guid, StringComparison.OrdinalIgnoreCase)
                : profile.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase);
            if (matches)
            {
                return new KeyGesture(
                    (Key)((int)Key.D0 + number),
                    KeyModifiers.Control | KeyModifiers.Shift);
            }
        }

        return null;
    }

    private static TextBlock FluentMenuIcon(string glyph) =>
        new()
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
            Width = 20,
            TextAlignment = TextAlignment.Center,
        };

    private MenuItem CreateMenuItem(ResolvedNewTabMenuItem item)
    {
        if (item.Type == ResolvedNewTabMenuItemType.Separator)
        {
            return new MenuItem { Header = "-" };
        }

        var menu = new MenuItem { Header = item.Name };
        AutomationProperties.SetName(menu, item.Name);
        var menuIdentity = item.Profile?.Guid ?? item.ActionId ?? item.Name;
        AutomationProperties.SetAutomationId(
            menu,
            $"NewTabMenuItem_{item.Type}_{menuIdentity.Replace(' ', '_')}");
        if (item.Type == ResolvedNewTabMenuItemType.Folder)
        {
            menu.ItemsSource = item.Children?.Select(CreateMenuItem).ToArray() ?? [];
        }
        else if (item.Profile is not null)
        {
            menu.Icon = CreateTabIcon(ProfileVisualDefaults.Icon(item.Profile));
            menu.InputGesture = ProfileMenuGesture(item.Profile);
            menu.Command = new RelayCommand(() => _ = CreateTabAsync(item.Profile));
        }
        else if (item.ActionId is { } actionId &&
                 _settings.ActionMap.AvailableActions.TryGetValue(actionId, out var action))
        {
            menu.Command = new RelayCommand(() => _ = DispatchActionAsync(action));
        }
        else
        {
            menu.IsEnabled = false;
        }

        return menu;
    }

    private async Task CreateTabAsync(ProfileSettings profile)
    {
        TerminalPane? pane = null;
        TerminalTab? tab = null;
        try
        {
            pane = CreatePane(profile);
            tab = new TerminalTab(pane);
            _tabCollection.Add(tab);
            ActivateTab(tab);
            RebuildTabs();

            var (columns, rows) = InitialTerminalSize();
            await pane.Control.StartAsync(profile, columns, rows).ConfigureAwait(true);
            pane.Control.Focus();
        }
        catch (Exception ex) when (IsLaunchFailure(ex))
        {
            if (pane is not null && tab is not null)
            {
                await RemoveFailedPaneAsync(tab, pane).ConfigureAwait(true);
            }

            await ShowLaunchErrorAsync(profile, ex).ConfigureAwait(true);
        }
    }

    private TerminalPane CreatePane(
        ProfileSettings profile,
        TerminalSessionDescriptor? session = null,
        PanePresentationState? presentation = null)
    {
        var control = new TermControl(TerminalEngineFactory.Create(_settings, profile));
        control.ConnectionFactory = CreateConnection;
        control.InteractionOptions = TerminalInteractionOptions.FromSettings(_settings);
        control.Cursor = new Cursor(StandardCursorType.Ibeam);
        control.NotificationRequested += (_, notification) => ShowNotification(notification);
        control.InteractionError += (_, error) => ShowNotification(new TerminalNotification(
            error.Operation,
            error.Exception.Message));
        var pane = new TerminalPane(
            _nextPaneId++,
            session ?? CreateSessionDescriptor(profile),
            profile,
            control,
            presentation);
        control.TitleChanged += (_, title) =>
        {
            pane.Title = profile.SuppressApplicationTitle ||
                         string.IsNullOrWhiteSpace(title) ||
                         IsExecutableTitle(profile, title)
                ? (string.IsNullOrWhiteSpace(profile.TabTitle) ? profile.Name : profile.TabTitle)
                : title;
            var tab = FindTab(pane);
            if (tab is null)
            {
                return;
            }

            if (ReferenceEquals(tab.Panes.ActiveContent, pane) &&
                string.IsNullOrWhiteSpace(tab.CustomTitle))
            {
                tab.Title = pane.Title;
            }
            else
            {
                pane.Presentation.HasUnseenActivity = true;
            }

            RebuildTabs();
            if (ReferenceEquals(_activeTab, tab))
            {
                SetNativeWindowTitle(tab.Title);
            }
        };
        control.CloseRequested += async (_, _) =>
        {
            var tab = FindTab(pane);
            if (tab is not null)
            {
                await ClosePaneAsync(tab, pane).ConfigureAwait(true);
            }
        };
        return pane;
    }

    private static bool IsExecutableTitle(ProfileSettings profile, string title)
    {
        var commandLine = profile.ExpandCommandline().Trim();
        var normalizedTitle = title.Trim().Trim('"');
        if (normalizedTitle.Equals(commandLine.Trim('"'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var closingQuote = commandLine.StartsWith('"') ? commandLine.IndexOf('"', 1) : -1;
        var executable = closingQuote > 1
            ? commandLine[1..closingQuote]
            : commandLine.Split(' ', 2)[0];
        executable = executable.Trim().Trim('"');
        return normalizedTitle.Equals(executable, StringComparison.OrdinalIgnoreCase) ||
               Path.GetFileName(normalizedTitle).Equals(
                   Path.GetFileName(executable),
                   StringComparison.OrdinalIgnoreCase);
    }

    private IRestartableTerminalConnection CreateConnection(ProfileSettings profile)
    {
        return _connectionFactory.Create(profile);
    }

    private async Task SplitActivePaneAsync(
        PaneSplitOrientation orientation,
        ProfileSettings? profile = null,
        double splitSize = 0.5,
        bool newPaneFirst = false)
    {
        var tab = _activeTab;
        var activePane = tab?.Panes.ActiveContent;
        if (tab is null || activePane is null || tab.IsClosing)
        {
            return;
        }

        var paneProfile = profile ?? activePane.Profile;
        TerminalPane? newPane = null;
        try
        {
            newPane = CreatePane(paneProfile);
            var normalizedSize = Math.Clamp(splitSize, 0.1, 0.9);
            var firstPaneRatio = newPaneFirst ? normalizedSize : 1 - normalizedSize;
            if (!tab.Panes.SplitActive(newPane, orientation, firstPaneRatio, newPaneFirst))
            {
                await newPane.Control.CloseAsync().ConfigureAwait(true);
                return;
            }

            RebuildTerminalHost();
            var (columns, rows) = InitialTerminalSize();
            await newPane.Control.StartAsync(newPane.Profile, columns / 2, rows).ConfigureAwait(true);
            newPane.Control.Focus();
        }
        catch (Exception ex) when (IsLaunchFailure(ex))
        {
            if (newPane is not null)
            {
                await RemoveFailedPaneAsync(tab, newPane).ConfigureAwait(true);
            }

            await ShowLaunchErrorAsync(paneProfile, ex).ConfigureAwait(true);
        }
    }

    private void ActivateTab(TerminalTab tab)
    {
        if (tab.IsClosing)
        {
            return;
        }

        _activeTab = tab;
        _tabCollection.Activate(tab);
        tab.Panes.ActiveContent!.Presentation.HasUnseenActivity = false;
        tab.Panes.ActiveContent.Presentation.HasBellIndicator = false;
        SynchronizeTitle(tab);
        RebuildTabs();
        RebuildTerminalHost();
        tab.Panes.ActiveContent?.Control.Focus();
    }

    private void ActivatePane(TerminalTab tab, TerminalPane pane)
    {
        if (tab.IsClosing || !tab.Panes.Activate(pane))
        {
            return;
        }

        SynchronizeTitle(tab);
        RebuildTerminalHost();
        pane.Control.Focus();
    }

    private async Task ClosePaneAsync(TerminalTab tab, TerminalPane pane)
    {
        if (tab.IsClosing)
        {
            return;
        }

        if (tab.Panes.Count == 1)
        {
            await CloseTabAsync(tab).ConfigureAwait(true);
            return;
        }

        if (!tab.Panes.Close(pane))
        {
            return;
        }

        await pane.Control.CloseAsync().ConfigureAwait(true);

        SynchronizeTitle(tab);
        if (ReferenceEquals(_activeTab, tab))
        {
            var activePane = tab.Panes.ActiveContent!;
            RebuildTerminalHost();
            activePane.Control.Focus();
        }
    }

    private async Task CloseTabAsync(TerminalTab tab, bool remember = true)
    {
        if (tab.IsClosing)
        {
            return;
        }

        var finalLayout = _tabs.Count == 1 ? CaptureLayout() : null;
        tab.IsClosing = true;
        var wasActive = ReferenceEquals(_activeTab, tab);
        DetachPaneControls(tab);
        _tabCollection.Close(tab, CaptureTab, remember);
        if (wasActive)
        {
            _activeTab = null;
            TerminalHost.Children.Clear();
            var replacement = _tabCollection.ActiveTab ??
                              _tabs.LastOrDefault(static candidate => !candidate.IsClosing);
            if (replacement is not null)
            {
                ActivateTab(replacement);
            }
        }
        else
        {
            RebuildTabs();
        }

        foreach (var pane in tab.Panes.Leaves())
        {
            await pane.Control.CloseAsync().ConfigureAwait(true);
        }

        if (_tabs.Count == 0)
        {
            if (finalLayout is not null)
            {
                TryPersistCurrentLayout(finalLayout);
            }

            Close();
            return;
        }

    }

    private void RebuildTerminalHost()
    {
        foreach (var tabToDetach in _tabs)
        {
            DetachPaneControls(tabToDetach);
        }

        TerminalHost.Children.Clear();
        var tab = _activeTab;
        if (tab?.Panes.Root is null)
        {
            return;
        }

        var visual = tab.Panes.ZoomedContent is { } zoomed
            ? BuildPaneLeaf(tab, zoomed)
            : BuildPaneNode(tab, tab.Panes.Root);
        TerminalHost.Children.Add(visual);
    }

    private Control BuildPaneNode(TerminalTab tab, PaneNode<TerminalPane> node)
    {
        if (node is PaneLeaf<TerminalPane> leaf)
        {
            return BuildPaneLeaf(tab, leaf.Content);
        }

        var split = (PaneSplit<TerminalPane>)node;
        var grid = new Grid();
        var first = BuildPaneNode(tab, split.First);
        var second = BuildPaneNode(tab, split.Second);
        var splitter = new GridSplitter
        {
            Background = Brushes.Transparent,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        };

        if (split.Orientation == PaneSplitOrientation.Vertical)
        {
            grid.ColumnDefinitions =
            [
                new ColumnDefinition(new GridLength(split.Ratio, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(4)),
                new ColumnDefinition(new GridLength(1 - split.Ratio, GridUnitType.Star)),
            ];
            Grid.SetColumn(first, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(second, 2);
            splitter.ResizeDirection = GridResizeDirection.Columns;
            splitter.Cursor = new Cursor(StandardCursorType.SizeWestEast);
            splitter.PointerReleased += (_, _) =>
            {
                var total = grid.ColumnDefinitions[0].ActualWidth + grid.ColumnDefinitions[2].ActualWidth;
                if (total > 0)
                {
                    tab.Panes.SetSplitRatio(split, grid.ColumnDefinitions[0].ActualWidth / total);
                }
            };
        }
        else
        {
            grid.RowDefinitions =
            [
                new RowDefinition(new GridLength(split.Ratio, GridUnitType.Star)),
                new RowDefinition(new GridLength(4)),
                new RowDefinition(new GridLength(1 - split.Ratio, GridUnitType.Star)),
            ];
            Grid.SetRow(first, 0);
            Grid.SetRow(splitter, 1);
            Grid.SetRow(second, 2);
            splitter.ResizeDirection = GridResizeDirection.Rows;
            splitter.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            splitter.PointerReleased += (_, _) =>
            {
                var total = grid.RowDefinitions[0].ActualHeight + grid.RowDefinitions[2].ActualHeight;
                if (total > 0)
                {
                    tab.Panes.SetSplitRatio(split, grid.RowDefinitions[0].ActualHeight / total);
                }
            };
        }

        grid.Children.Add(first);
        grid.Children.Add(splitter);
        grid.Children.Add(second);
        return grid;
    }

    private Border BuildPaneLeaf(TerminalTab tab, TerminalPane pane)
    {
        var active = tab.Panes.Count > 1 && ReferenceEquals(tab.Panes.ActiveContent, pane);
        var scrollBar = _paneScrollBars.GetValue(pane, CreatePaneScrollBar);

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        content.Children.Add(pane.Control);
        Grid.SetColumn(scrollBar, 1);
        content.Children.Add(scrollBar);
        var border = new Border
        {
            BorderBrush = active ? new SolidColorBrush(Color.Parse("#3A96DD")) : Brushes.Transparent,
            BorderThickness = new Thickness(active ? 1 : 0),
            Child = content,
            MinWidth = 80,
            MinHeight = 40,
        };
        border.PointerPressed += (_, _) => ActivatePane(tab, pane);
        return border;
    }

    private static PaneScrollBar CreatePaneScrollBar(TerminalPane pane) => new(pane);

    private void RebuildTabs()
    {
        TabStrip.Children.Clear();
        Button? activeButton = null;
        foreach (var tab in _tabs)
        {
            var presentation = tab.Panes.ActiveContent?.Presentation ?? new PanePresentationState();
            var tabWidth = TabWidth();
            var compact = tabWidth < 160;
            var content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                ColumnSpacing = 6,
                ClipToBounds = true,
            };
            var prefix = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (!string.IsNullOrWhiteSpace(presentation.Icon))
            {
                prefix.Children.Add(CreateTabIcon(presentation.Icon));
            }

            if (presentation.IsAdministrator && !compact)
            {
                prefix.Children.Add(new TextBlock { Text = "◆" });
            }

            content.Children.Add(prefix);
            var title = new TextBlock
            {
                Text = tab.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = presentation.HasUnseenActivity ? FontWeight.Bold : FontWeight.Normal,
            };
            Grid.SetColumn(title, 1);
            content.Children.Add(title);

            var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (compact)
            {
                if (presentation.ProgressState != TerminalProgressState.None)
                {
                    status.Children.Add(new ProgressBar
                    {
                        Width = 20,
                        Height = 3,
                        IsIndeterminate = presentation.ProgressState == TerminalProgressState.Indeterminate,
                        Value = presentation.Progress * 100,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                else if (presentation.IsReadOnly)
                {
                    status.Children.Add(new TextBlock { Text = "🔒" });
                }
                else if (presentation.HasBellIndicator)
                {
                    status.Children.Add(new TextBlock { Text = "●" });
                }
            }
            else
            {
                if (presentation.IsReadOnly)
                {
                    status.Children.Add(new TextBlock { Text = "🔒" });
                }

                if (presentation.HasBellIndicator)
                {
                    status.Children.Add(new TextBlock { Text = "●" });
                }

                if (presentation.ProgressState != TerminalProgressState.None)
                {
                    status.Children.Add(new ProgressBar
                    {
                        Width = 34,
                        Height = 3,
                        IsIndeterminate = presentation.ProgressState == TerminalProgressState.Indeterminate,
                        Value = presentation.Progress * 100,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
            }

            Grid.SetColumn(status, 2);
            content.Children.Add(status);
            var closeButton = CreateCloseButton(tab);
            Grid.SetColumn(closeButton, 3);
            content.Children.Add(closeButton);
            var button = new Button
            {
                Classes = { "tab" },
                Content = content,
                Tag = tab,
                ContextMenu = CreateTabContextMenu(tab),
                Width = tabWidth,
            };
            if (TryParseColor(tab.Color ?? presentation.Color, out var tabColor))
            {
                button.Background = new SolidColorBrush(tabColor);
            }
            if (ReferenceEquals(tab, _activeTab))
            {
                button.Classes.Add("active");
                activeButton = button;
            }

            button.Click += (_, _) => ActivateTab(tab);
            button.PointerPressed += (_, e) => BeginTabDrag(tab, button, e);
            button.PointerReleased += (_, e) => EndTabDrag(tab, button, e);
            TabStrip.Children.Add(button);
        }

        if (activeButton is not null)
        {
            Dispatcher.UIThread.Post(activeButton.BringIntoView, DispatcherPriority.Loaded);
        }

        ApplyWindowChrome();
    }

    private double TabWidth()
    {
        var available = double.IsFinite(TabScrollViewer.MaxWidth)
            ? TabScrollViewer.MaxWidth
            : 720;
        return Math.Clamp(
            (available - (_tabs.Count * 2)) / Math.Max(1, _tabs.Count),
            120,
            240);
    }

    private Control CreateTabIcon(string icon)
    {
        if (TryResolveTabIcon(icon, out var key, out var uri, out var filePath))
        {
            if (!_tabIconCache.TryGetValue(key, out var bitmap))
            {
                try
                {
                    using var stream = filePath is null
                        ? AssetLoader.Open(uri!)
                        : File.OpenRead(filePath);
                    bitmap = new Bitmap(stream);
                }
                catch (Exception ex) when (ex is
                    IOException or
                    UnauthorizedAccessException or
                    ArgumentException or
                    InvalidOperationException)
                {
                    return CreateTabIcon("ms-appx:///ProfileIcons/terminal.png");
                }

                _tabIconCache.Add(key, bitmap);
            }

            return new Image
            {
                Source = bitmap,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        if (!icon.Contains('/') && !icon.Contains('\\') && icon.Length <= 4)
        {
            return new TextBlock
            {
                Text = icon,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return CreateTabIcon("ms-appx:///ProfileIcons/terminal.png");
    }

    private static bool TryResolveTabIcon(
        string icon,
        out string key,
        out Uri? uri,
        out string? filePath)
    {
        const string profilePrefix = "ms-appx:///ProfileIcons/";
        const string generatorPrefix = "ms-appx:///ProfileGeneratorIcons/";
        filePath = null;
        if (icon.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(icon[profilePrefix.Length..]);
            var resourceName = fileName.Equals("terminal.png", StringComparison.OrdinalIgnoreCase)
                ? fileName
                : $"{Path.GetFileNameWithoutExtension(fileName)}.scale-100.png";
            key = $"profile:{resourceName}";
            uri = new Uri($"avares://Devolutions.Terminal.App/Assets/ProfileIcons/{resourceName}");
            return AssetLoader.Exists(uri);
        }

        if (icon.StartsWith(generatorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(icon[generatorPrefix.Length..]);
            key = $"generator:{fileName}";
            uri = new Uri($"avares://Devolutions.Terminal.App/Assets/ProfileGeneratorIcons/{fileName}");
            return AssetLoader.Exists(uri);
        }

        var expanded = Environment.ExpandEnvironmentVariables(icon);
        if (Uri.TryCreate(expanded, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            filePath = parsed.LocalPath;
        }
        else if (Path.IsPathRooted(expanded))
        {
            filePath = expanded;
        }

        if (filePath is not null && File.Exists(filePath))
        {
            key = $"file:{Path.GetFullPath(filePath)}";
            uri = null;
            return true;
        }

        key = string.Empty;
        uri = null;
        filePath = null;
        return false;
    }

    private ContextMenu CreateTabContextMenu(TerminalTab tab) =>
        new()
        {
            ItemsSource = new[]
            {
                new MenuItem
                {
                    Header = "Duplicate",
                    Command = new RelayCommand(() => _ = RestoreTabAsync(CaptureTab(tab), regenerateIdentities: true)),
                },
                new MenuItem
                {
                    Header = "Move left",
                    IsEnabled = TabIndexOf(tab) > 0,
                    Command = new RelayCommand(() =>
                    {
                        _tabCollection.MoveRelative(tab, -1);
                        RebuildTabs();
                    }),
                },
                new MenuItem
                {
                    Header = "Move right",
                    IsEnabled = TabIndexOf(tab) < _tabs.Count - 1,
                    Command = new RelayCommand(() =>
                    {
                        _tabCollection.MoveRelative(tab, 1);
                        RebuildTabs();
                    }),
                },
                new MenuItem { Header = "-" },
                new MenuItem
                {
                    Header = "Close other tabs",
                    IsEnabled = _tabs.Count > 1,
                    Command = new RelayCommand(() => _ = CloseOtherTabsAsync((uint)TabIndexOf(tab))),
                },
                new MenuItem
                {
                    Header = "Close tabs after",
                    IsEnabled = TabIndexOf(tab) < _tabs.Count - 1,
                    Command = new RelayCommand(() => _ = CloseTabsAfterAsync((uint)TabIndexOf(tab))),
                },
                new MenuItem
                {
                    Header = "Close",
                    Command = new RelayCommand(() => _ = CloseTabAsync(tab)),
                },
            },
        };

    private void BeginTabDrag(TerminalTab tab, Control control, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _draggedTab = tab;
        _dragStart = e.GetPosition(TabStrip);
        e.Pointer.Capture(control);
    }

    private void EndTabDrag(TerminalTab tab, Control control, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (!ReferenceEquals(_draggedTab, tab))
        {
            return;
        }

        _draggedTab = null;
        var position = e.GetPosition(TabStrip);
        if (Math.Abs(position.X - _dragStart.X) < 4 &&
            Math.Abs(position.Y - _dragStart.Y) < 4)
        {
            return;
        }

        if (position.Y < -24 || position.Y > TabStrip.Bounds.Height + 24)
        {
            var local = e.GetPosition(this);
            var screen = new PixelPoint(Position.X + (int)local.X, Position.Y + (int)local.Y);
            var request = new TabTearOffRequest(
                Guid.NewGuid(),
                WindowId,
                CaptureTab(tab),
                new PixelPosition(screen.X, screen.Y));
            _tabTearOffRequested?.Invoke(request);
            TabTearOffRequested?.Invoke(request);
            return;
        }

        var targetIndex = _tabs.Count;
        for (var index = 0; index < TabStrip.Children.Count; index++)
        {
            var child = TabStrip.Children[index];
            if (position.X < child.Bounds.Center.X)
            {
                targetIndex = index;
                break;
            }
        }

        var sourceIndex = TabIndexOf(tab);
        if (sourceIndex >= 0 && sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        if (_tabCollection.Move(tab, targetIndex))
        {
            RebuildTabs();
        }
    }

    private Button CreateCloseButton(TerminalTab tab)
    {
        var close = new Button
        {
            Classes = { "icon" },
            Content = "×",
            Width = 22,
            Height = 22,
            FontSize = 14,
        };
        close.Click += async (_, e) =>
        {
            e.Handled = true;
            await CloseTabAsync(tab).ConfigureAwait(true);
        };
        return close;
    }

    private TermControl? ActiveControl => _activeTab?.Panes.ActiveContent?.Control;

    private void ConfigureActionDispatcher()
    {
        Register(ShortcutAction.CopyText, ActionScope.Control, action => ActiveControl?.HasSelection == true, async action =>
        {
            var args = action.Args as CopyTextArgs ?? new CopyTextArgs();
            await ActiveControl!.CopyAsync(args.SingleLine).ConfigureAwait(true);
            if (args.DismissSelection)
            {
                ActiveControl.ClearSelection();
            }
        });
        Register(ShortcutAction.PasteText, ActionScope.Control,
            _ => CanPaste(),
            async _ => await PasteCoordinatedAsync().ConfigureAwait(true));
        Register(ShortcutAction.SendInput, ActionScope.Control, action => ActiveControl is not null && action.Args is SendInputArgs,
            action =>
            {
                WriteCoordinatedInput(((SendInputArgs)action.Args!).Input);
                return Task.CompletedTask;
            });
        Register(ShortcutAction.SelectAll, ActionScope.Control, _ => ActiveControl is not null, _ =>
        {
            ActiveControl!.SelectAll();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ClearBuffer, ActionScope.Control, _ => ActiveControl is not null, _ =>
        {
            ActiveControl!.ClearBuffer();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.AdjustFontSize, ActionScope.Control,
            action => ActiveControl is not null && action.Args is AdjustFontSizeArgs,
            action =>
            {
                ActiveControl!.AdjustFontSize(((AdjustFontSizeArgs)action.Args!).Delta);
                return Task.CompletedTask;
            });
        Register(ShortcutAction.ResetFontSize, ActionScope.Control, _ => ActiveControl is not null, _ =>
        {
            ActiveControl!.ResetFontSize();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ScrollUp, ActionScope.Control, _ => ActiveControl is not null,
            action =>
            {
                ActiveControl!.ScrollBy(-(int)((action.Args as ScrollUpArgs)?.RowsToScroll ?? 1));
                return Task.CompletedTask;
            });
        Register(ShortcutAction.ScrollDown, ActionScope.Control, _ => ActiveControl is not null,
            action =>
            {
                ActiveControl!.ScrollBy((int)((action.Args as ScrollDownArgs)?.RowsToScroll ?? 1));
                return Task.CompletedTask;
            });
        Register(ShortcutAction.ScrollUpPage, ActionScope.Control, _ => ActiveControl is not null, _ =>
        {
            ActiveControl!.ScrollPage(-1);
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ScrollDownPage, ActionScope.Control, _ => ActiveControl is not null, _ =>
        {
            ActiveControl!.ScrollPage(1);
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ScrollToTop, ActionScope.Control, _ => ActiveControl is not null, _ =>
        {
            ActiveControl!.ScrollToTop();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ScrollToBottom, ActionScope.Control, _ => ActiveControl is not null, _ =>
        {
            ActiveControl!.ScrollToBottom();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ScrollToMark, ActionScope.Control, _ => ActiveControl is not null, action =>
        {
            ActiveControl!.ScrollToMark(
                (action.Args as ScrollToMarkArgs)?.Direction ?? ScrollToMarkDirection.Previous);
            return Task.CompletedTask;
        });
        Register(ShortcutAction.AddMark, ActionScope.Control, _ => ActiveControl is not null, action =>
        {
            ActiveControl!.AddMark((action.Args as AddMarkArgs)?.Color);
            ShowNotification(new TerminalNotification("Mark added", "A scroll mark was added at the current line."));
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ClearMark, ActionScope.Control, _ => ActiveControl is not null, _ =>
        {
            ActiveControl!.ClearMark();
            ShowNotification(new TerminalNotification("Mark cleared", "Marks at the current line or selection were cleared."));
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ClearAllMarks, ActionScope.Control, _ => ActiveControl is not null, _ =>
        {
            ActiveControl!.ClearAllMarks();
            ShowNotification(new TerminalNotification("Marks cleared", "All terminal scroll marks were cleared."));
            return Task.CompletedTask;
        });
        Register(ShortcutAction.Find, ActionScope.Control, _ => ActiveControl is not null, _ =>
        {
            ShowFind();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.FindMatch, ActionScope.Control,
            action => ActiveControl is not null && action.Args is FindMatchArgs && !string.IsNullOrWhiteSpace(FindBox.Text),
            action =>
            {
                Find(((FindMatchArgs)action.Args!).Direction == FindMatchDirection.Previous);
                return Task.CompletedTask;
            });

        Register(ShortcutAction.NewTab, ActionScope.Tab, _ => true,
            async action => await CreateTabAsync(ResolveProfile((action.Args as NewTabArgs)?.ContentArgs)).ConfigureAwait(true));
        Register(ShortcutAction.DuplicateTab, ActionScope.Tab, _ => _activeTab?.Panes.ActiveContent is not null,
            async _ => await RestoreTabAsync(CaptureTab(_activeTab!), regenerateIdentities: true).ConfigureAwait(true));
        Register(ShortcutAction.CloseTab, ActionScope.Tab, action => ResolveTab((action.Args as CloseTabArgs)?.Index) is not null,
            async action => await CloseTabAsync(ResolveTab((action.Args as CloseTabArgs)?.Index)!).ConfigureAwait(true));
        Register(ShortcutAction.NextTab, ActionScope.Tab, _ => _tabs.Count > 1, action =>
        {
            ActivateRelativeTab(
                1,
                (action.Args as NextTabArgs)?.SwitcherMode == TabSwitcherMode.MostRecentlyUsed);
            return Task.CompletedTask;
        });
        Register(ShortcutAction.PrevTab, ActionScope.Tab, _ => _tabs.Count > 1, action =>
        {
            ActivateRelativeTab(
                -1,
                (action.Args as PrevTabArgs)?.SwitcherMode == TabSwitcherMode.MostRecentlyUsed);
            return Task.CompletedTask;
        });
        Register(ShortcutAction.SwitchToTab, ActionScope.Tab,
            action => action.Args is SwitchToTabArgs args && ResolveTab(args.TabIndex) is not null,
            action =>
            {
                ActivateTab(ResolveTab(((SwitchToTabArgs)action.Args!).TabIndex)!);
                return Task.CompletedTask;
            });
        Register(ShortcutAction.CloseOtherTabs, ActionScope.Tab, _ => _tabs.Count > 1,
            async action => await CloseOtherTabsAsync((action.Args as CloseOtherTabsArgs)?.Index).ConfigureAwait(true));
        Register(ShortcutAction.CloseTabsAfter, ActionScope.Tab,
            action => ResolveTab((action.Args as CloseTabsAfterArgs)?.Index) is { } tab && TabIndexOf(tab) < _tabs.Count - 1,
            async action => await CloseTabsAfterAsync((action.Args as CloseTabsAfterArgs)?.Index).ConfigureAwait(true));
        Register(ShortcutAction.MoveTab, ActionScope.Tab,
            action => _activeTab is not null &&
                      action.Args is MoveTabArgs { Window.Length: 0, Direction: not MoveTabDirection.None },
            action =>
            {
                var delta = ((MoveTabArgs)action.Args!).Direction == MoveTabDirection.Forward ? 1 : -1;
                _tabCollection.MoveRelative(_activeTab!, delta);
                RebuildTabs();
                return Task.CompletedTask;
            });
        Register(ShortcutAction.RestoreLastClosed, ActionScope.Tab, _ => _tabCollection.ClosedCount > 0,
            async _ =>
            {
                if (_tabCollection.TryTakeLastClosed(out var closed) && closed is not null)
                {
                    await RestoreTabAsync(closed).ConfigureAwait(true);
                }
            });
        Register(ShortcutAction.RenameTab, ActionScope.Tab,
            action => _activeTab is not null && action.Args is RenameTabArgs,
            action =>
            {
                _activeTab!.CustomTitle = ((RenameTabArgs)action.Args!).Title;
                SynchronizeTitle(_activeTab);
                return Task.CompletedTask;
            });
        Register(ShortcutAction.OpenTabRenamer, ActionScope.Tab, _ => _activeTab is not null,
            async _ =>
            {
                var title = await PromptForTextAsync(
                    "Rename tab",
                    "Tab title",
                    _activeTab!.CustomTitle ?? _activeTab.Title).ConfigureAwait(true);
                if (title is not null)
                {
                    _activeTab.CustomTitle = title;
                    SynchronizeTitle(_activeTab);
                }
            });
        Register(ShortcutAction.SetTabColor, ActionScope.Tab,
            action => _activeTab is not null && action.Args is SetTabColorArgs,
            action =>
            {
                _activeTab!.Color = ((SetTabColorArgs)action.Args!).TabColor;
                RebuildTabs();
                return Task.CompletedTask;
            });
        Register(ShortcutAction.OpenTabColorPicker, ActionScope.Tab, _ => _activeTab is not null,
            async _ =>
            {
                var color = await PromptForTextAsync(
                    "Set tab color",
                    "Hex color, or blank to reset",
                    _activeTab!.Color ?? string.Empty).ConfigureAwait(true);
                if (color is null)
                {
                    return;
                }

                if (color.Length == 0 || TryParseColor(color, out var parsedColor))
                {
                    _activeTab.Color = color.Length == 0 ? null : color;
                    RebuildTabs();
                }
                else
                {
                    ShowNotification(new TerminalNotification(
                        "Invalid tab color",
                        $"'{color}' is not a valid color."));
                }
            });
        Register(ShortcutAction.SetColorScheme, ActionScope.Control,
            action => ActiveControl is not null &&
                      action.Args is SetColorSchemeArgs args &&
                      FindColorScheme(args.SchemeName) is not null,
            action =>
            {
                ActiveControl!.Engine.Scheme =
                    FindColorScheme(((SetColorSchemeArgs)action.Args!).SchemeName)!;
                ActiveControl.InvalidateVisual();
                return Task.CompletedTask;
            });
        Register(ShortcutAction.ColorSelection, ActionScope.Control,
            action => _settings.EnableColorSelection &&
                      ActiveControl?.HasSelection == true &&
                      action.Args is ColorSelectionArgs,
            action =>
            {
                var args = (ColorSelectionArgs)action.Args!;
                ActiveControl!.ColorSelection(
                    args.Foreground?.Value,
                    args.Background?.Value,
                    args.MatchMode);
                return Task.CompletedTask;
            });
        Register(ShortcutAction.ToggleShaderEffects, ActionScope.Control,
            _ => ActiveControl?.HasSupportedShaderEffects == true,
            _ =>
            {
                ActiveControl!.ToggleShaderEffects();
                return Task.CompletedTask;
            });
        Register(ShortcutAction.TabSearch, ActionScope.Window, _ => _tabs.Count > 0, _ =>
        {
            ShowTabSearch();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.OpenNewTabDropdown, ActionScope.Window, _ => true, _ =>
        {
            Menu_OnClick(TitleBar, new RoutedEventArgs());
            return Task.CompletedTask;
        });

        Register(ShortcutAction.SplitPane, ActionScope.Pane, _ => ActiveControl is not null,
            async action =>
            {
                var args = action.Args as SplitPaneArgs;
                await SplitActivePaneAsync(
                    ResolveSplitOrientation(args?.SplitDirection),
                    ResolveSplitProfile(args),
                    args?.SplitSize ?? 0.5,
                    args?.SplitDirection is SplitDirection.Left or SplitDirection.Up).ConfigureAwait(true);
            });
        Register(ShortcutAction.ClosePane, ActionScope.Pane, _ => _activeTab?.Panes.ActiveContent is not null,
            async _ => await ClosePaneAsync(_activeTab!, _activeTab!.Panes.ActiveContent!).ConfigureAwait(true));
        Register(ShortcutAction.CloseOtherPanes, ActionScope.Pane, _ => _activeTab?.Panes.Count > 1,
            async _ => await CloseOtherPanesAsync().ConfigureAwait(true));
        Register(ShortcutAction.TogglePaneZoom, ActionScope.Pane, _ => _activeTab?.Panes.ActiveContent is not null, _ =>
        {
            _activeTab!.Panes.ToggleZoom();
            RebuildTerminalHost();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ToggleSplitOrientation, ActionScope.Pane, _ => _activeTab?.Panes.Count > 1, _ =>
        {
            _activeTab!.Panes.ToggleActiveSplitOrientation();
            RebuildTerminalHost();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.MoveFocus, ActionScope.Pane, action => CanMoveFocus(action.Args as MoveFocusArgs),
            action =>
            {
                MoveFocus((MoveFocusArgs)action.Args!);
                return Task.CompletedTask;
            });
        Register(ShortcutAction.ResizePane, ActionScope.Pane,
            action => _activeTab?.Panes.ActiveContent is not null && action.Args is ResizePaneArgs,
            action =>
            {
                var direction = ToPaneDirection(((ResizePaneArgs)action.Args!).ResizeDirection);
                if (direction is { } paneDirection)
                {
                    _activeTab!.Panes.ResizeActive(paneDirection, 0.05);
                    RebuildTerminalHost();
                }

                return Task.CompletedTask;
            });
        Register(ShortcutAction.MovePane, ActionScope.Pane, CanMovePane,
            action =>
            {
                MovePane((MovePaneArgs)action.Args!);
                return Task.CompletedTask;
            });
        Register(ShortcutAction.SwapPane, ActionScope.Pane,
            action => _activeTab?.Panes.Count > 1 &&
                      action.Args is SwapPaneArgs args &&
                      ToPaneDirection(args.Direction) is not null,
            action =>
            {
                var direction = ToPaneDirection(((SwapPaneArgs)action.Args!).Direction)!.Value;
                _activeTab!.Panes.SwapActive(direction);
                RebuildTerminalHost();
                ActiveControl?.Focus();
                return Task.CompletedTask;
            });
        Register(ShortcutAction.FocusPane, ActionScope.Pane,
            action => action.Args is FocusPaneArgs args &&
                      _activeTab?.Panes.Leaves().Any(pane => pane.Id == args.Id) == true,
            action =>
            {
                var pane = _activeTab!.Panes.Leaves()
                    .First(candidate => candidate.Id == ((FocusPaneArgs)action.Args!).Id);
                ActivatePane(_activeTab, pane);
                return Task.CompletedTask;
            });
        Register(ShortcutAction.RestartConnection, ActionScope.Pane, _ => ActiveControl is not null,
            async _ => await ActiveControl!.RestartAsync().ConfigureAwait(true));
        Register(ShortcutAction.TogglePaneReadOnly, ActionScope.Pane, _ => _activeTab?.Panes.ActiveContent is not null, _ =>
        {
            var state = _activeTab!.Panes.ActiveContent!.Presentation;
            state.IsReadOnly = !state.IsReadOnly;
            RebuildTabs();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.EnablePaneReadOnly, ActionScope.Pane,
            _ => _activeTab?.Panes.ActiveContent?.Presentation.IsReadOnly == false, _ =>
            {
                _activeTab!.Panes.ActiveContent!.Presentation.IsReadOnly = true;
                RebuildTabs();
                return Task.CompletedTask;
            });
        Register(ShortcutAction.DisablePaneReadOnly, ActionScope.Pane,
            _ => _activeTab?.Panes.ActiveContent?.Presentation.IsReadOnly == true, _ =>
            {
                _activeTab!.Panes.ActiveContent!.Presentation.IsReadOnly = false;
                RebuildTabs();
                return Task.CompletedTask;
            });
        Register(ShortcutAction.ToggleBroadcastInput, ActionScope.Pane,
            _ => _activeTab?.Panes.Count > 1, _ =>
            {
                _activeTab!.BroadcastInput.Toggle();
                return Task.CompletedTask;
            });
        Register(ShortcutAction.MarkMode, ActionScope.Control, _ => ActiveControl is not null, _ =>
        {
            ActiveControl!.EnterMarkMode();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ToggleBlockSelection, ActionScope.Control,
            _ => ActiveControl?.HasSelection == true,
            _ =>
            {
                ActiveControl!.ToggleBlockSelection();
                return Task.CompletedTask;
            });
        Register(ShortcutAction.SwitchSelectionEndpoint, ActionScope.Control,
            _ => ActiveControl?.HasSelection == true,
            _ =>
            {
                ActiveControl!.SwitchSelectionEndpoint();
                return Task.CompletedTask;
            });
        Register(ShortcutAction.ExpandSelectionToWord, ActionScope.Control,
            _ => ActiveControl?.HasSelection == true,
            _ =>
            {
                ActiveControl!.ExpandSelectionToWord();
                return Task.CompletedTask;
            });

        Register(ShortcutAction.ToggleCommandPalette, ActionScope.Window, _ => true, action =>
        {
            ShowCommandPalette(
                (action.Args as ToggleCommandPaletteArgs)?.LaunchMode ==
                CommandPaletteLaunchMode.CommandLine
                    ? PaletteMode.CommandLine
                    : PaletteMode.Actions);
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ExecuteCommandline, ActionScope.Window,
            action => action.Args is ExecuteCommandlineArgs,
            action => ExecutePaletteCommandLineAsync(
                ((ExecuteCommandlineArgs)action.Args!).Commandline));
        Register(ShortcutAction.Suggestions, ActionScope.Control, _ => ActiveControl is not null, action =>
        {
            var source = (action.Args as SuggestionsArgs)?.Source ?? SuggestionsSource.Tasks;
            if ((source & SuggestionsSource.CommandHistory) != 0 ||
                source == SuggestionsSource.All)
            {
                ShowCommandPalette(PaletteMode.CommandHistory);
            }
            else
            {
                ShowNotification(new TerminalNotification(
                    "Suggestions unavailable",
                    "This shell did not provide completion or quick-fix suggestions."));
            }

            return Task.CompletedTask;
        });
        Register(ShortcutAction.ExportBuffer, ActionScope.Control, _ => ActiveControl is not null, action =>
        {
            var requestedPath = (action.Args as ExportBufferArgs)?.Path;
            var path = string.IsNullOrWhiteSpace(requestedPath)
                ? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(SettingsService.SettingsPath))!,
                    $"terminal-buffer-{DateTime.Now:yyyyMMdd-HHmmss}.txt")
                : Environment.ExpandEnvironmentVariables(requestedPath);
            var text = TerminalBufferExport.ToPlainText(
                ActiveControl!.Engine.CreateSnapshot(includeHistory: true).Buffer);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, text);
            ShowNotification(new TerminalNotification("Buffer exported", path));
            return Task.CompletedTask;
        });
        Register(ShortcutAction.SelectCommand, ActionScope.Control, _ => ActiveControl is not null, action =>
        {
            ActiveControl!.SelectCommand(
                (action.Args as SelectCommandArgs)?.Direction == SelectOutputDirection.Next
                    ? TerminalShellSelectionDirection.Next
                    : TerminalShellSelectionDirection.Previous);
            return Task.CompletedTask;
        });
        Register(ShortcutAction.SelectOutput, ActionScope.Control, _ => ActiveControl is not null, action =>
        {
            ActiveControl!.SelectOutput(
                (action.Args as SelectOutputArgs)?.Direction == SelectOutputDirection.Next
                    ? TerminalShellSelectionDirection.Next
                    : TerminalShellSelectionDirection.Previous);
            return Task.CompletedTask;
        });
        Register(ShortcutAction.SearchForText, ActionScope.Control,
            _ => ActiveControl?.BuildCopyPayload() is not null,
            action =>
            {
                var text = ActiveControl!.BuildCopyPayload()!.Text;
                var template = (action.Args as SearchForTextArgs)?.QueryUrl;
                if (string.IsNullOrWhiteSpace(template))
                {
                    template = _settings.SearchWebDefaultQueryUrl;
                }

                OpenWithShell(template.Replace(
                    "%s",
                    Uri.EscapeDataString(text),
                    StringComparison.Ordinal));
                return Task.CompletedTask;
            });
        Register(ShortcutAction.OpenCWD, ActionScope.Control,
            action => TryGetWorkingDirectory(out var workingDirectory),
            _ =>
            {
                TryGetWorkingDirectory(out var workingDirectory);
                OpenDirectoryWithShell(workingDirectory!);
                return Task.CompletedTask;
            });
        Register(ShortcutAction.QuickFix, ActionScope.Control, _ => ActiveControl is not null, _ =>
        {
            ShowNotification(new TerminalNotification(
                "Quick Fix unavailable",
                "This shell did not provide any quick fixes."));
            return Task.CompletedTask;
        });
        Register(ShortcutAction.DisplayWorkingDirectory, ActionScope.Control,
            _ => !string.IsNullOrWhiteSpace(ActiveControl?.Engine.WorkingDirectory),
            _ =>
            {
                ShowNotification(new TerminalNotification(
                    "Working directory",
                    ActiveControl!.Engine.WorkingDirectory!));
                return Task.CompletedTask;
            });
        Register(ShortcutAction.AdjustOpacity, ActionScope.Window,
            action => action.Args is AdjustOpacityArgs,
            action =>
            {
                var args = (AdjustOpacityArgs)action.Args!;
                var target = args.Relative ? (Opacity * 100) + args.Opacity : args.Opacity;
                Opacity = Math.Clamp(target / 100d, 0.1, 1);
                return Task.CompletedTask;
            });
        Register(ShortcutAction.IdentifyWindow, ActionScope.Window, _ => true, _ =>
        {
            ShowNotification(new TerminalNotification(
                "Window identity",
                string.IsNullOrWhiteSpace(WindowName)
                    ? $"Window {WindowId}"
                    : $"{WindowName} ({WindowId})"));
            return Task.CompletedTask;
        });
        Register(ShortcutAction.IdentifyWindows, ActionScope.Window, _ => true, _ =>
        {
            var identities = _windowIdentityProvider?.Invoke() ??
                             [string.IsNullOrWhiteSpace(WindowName)
                                 ? $"Window {WindowId}"
                                 : $"{WindowName} ({WindowId})"];
            ShowNotification(new TerminalNotification(
                "Terminal windows",
                string.Join(Environment.NewLine, identities)));
            return Task.CompletedTask;
        });
        Register(ShortcutAction.RenameWindow, ActionScope.Window,
            action => action.Args is RenameWindowArgs { Name.Length: > 0 },
            action =>
            {
                TryRenameWindow(((RenameWindowArgs)action.Args!).Name);
                return Task.CompletedTask;
            });
        Register(ShortcutAction.OpenWindowRenamer, ActionScope.Window, _ => true,
            async _ =>
            {
                var name = await PromptForTextAsync(
                    "Rename window",
                    "Window name",
                    WindowName).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    TryRenameWindow(name);
                }
            });
        Register(ShortcutAction.OpenWorkspace, ActionScope.Application,
            action => action.Args is OpenWorkspaceArgs { Name.Length: > 0 },
            action => OpenWorkspaceAsync(((OpenWorkspaceArgs)action.Args!).Name));
        Register(ShortcutAction.Workspaces, ActionScope.Application, _ => true, _ =>
        {
            ShowCommandPalette(PaletteMode.Workspaces);
            return Task.CompletedTask;
        });
        Register(ShortcutAction.GlobalSummon, ActionScope.Application,
            action => action.Args is GlobalSummonArgs,
            action => SummonAsync((GlobalSummonArgs)action.Args!, quake: false));
        Register(ShortcutAction.QuakeMode, ActionScope.Application, _ => true,
            action =>
            {
                var args = action.Args as GlobalSummonArgs ??
                           new GlobalSummonArgs(Name: "_quake", DropdownDuration: 200);
                return SummonAsync(
                    args with
                    {
                        Name = "_quake",
                        DropdownDuration = args.DropdownDuration == 0 ? 200u : args.DropdownDuration,
                    },
                    quake: true);
            });
        Register(ShortcutAction.OpenSystemMenu, ActionScope.Window, _ => true, _ =>
        {
            OpenSystemMenu();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ShowContextMenu, ActionScope.Control,
            _ => ActiveControl is not null && _activeTab is not null,
            _ =>
            {
                CreateTabContextMenu(_activeTab!).Open(ActiveControl);
                return Task.CompletedTask;
            });
        Register(ShortcutAction.BreakIntoDebugger, ActionScope.Application, _ => true, _ =>
        {
            if (System.Diagnostics.Debugger.IsAttached)
            {
                System.Diagnostics.Debugger.Break();
            }
            else
            {
                ShowNotification(new TerminalNotification(
                    "Debugger unavailable",
                    "No managed debugger is attached."));
            }

            return Task.CompletedTask;
        });
        Register(ShortcutAction.OpenScratchpad, ActionScope.Window, _ => true, _ =>
        {
            ShowScratchpad();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.OpenAbout, ActionScope.Application, _ => true, _ =>
        {
            ShowAbout();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.OpenSettings, ActionScope.Application, _ => true, action =>
        {
            OpenSettings((action.Args as OpenSettingsArgs)?.Target ?? SettingsTarget.SettingsFile);
            return Task.CompletedTask;
        });
        Register(ShortcutAction.NewWindow, ActionScope.Application, _ => true, action =>
        {
            var content = (action.Args as NewWindowArgs)?.ContentArgs ?? new NewTerminalArgs();
            if (_newWindowRequested is not null)
            {
                _newWindowRequested(new(
                    null,
                    null,
                    null,
                    null,
                    TerminalWindowLaunchMode.Default,
                    [new(ShortcutAction.NewTab, new NewTabArgs(content))]));
            }
            else
            {
                new MainWindow(ResolveProfile(content)).Show();
            }

            return Task.CompletedTask;
        });
        Register(ShortcutAction.CloseWindow, ActionScope.Window, _ => true, _ =>
        {
            Close();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.Quit, ActionScope.Application, _ => true, _ =>
        {
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ToggleFullscreen, ActionScope.Window, _ => true, _ =>
        {
            WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            return Task.CompletedTask;
        });
        Register(ShortcutAction.SetFullScreen, ActionScope.Window, action => action.Args is SetFullScreenArgs, action =>
        {
            WindowState = ((SetFullScreenArgs)action.Args!).IsFullScreen ? WindowState.FullScreen : WindowState.Normal;
            return Task.CompletedTask;
        });
        Register(ShortcutAction.SetMaximized, ActionScope.Window, action => action.Args is SetMaximizedArgs, action =>
        {
            WindowState = ((SetMaximizedArgs)action.Args!).IsMaximized ? WindowState.Maximized : WindowState.Normal;
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ToggleAlwaysOnTop, ActionScope.Window, _ => true, _ =>
        {
            Topmost = !Topmost;
            return Task.CompletedTask;
        });
        Register(ShortcutAction.ToggleFocusMode, ActionScope.Window, _ => true, _ =>
        {
            _focusMode = !_focusMode;
            ApplyWindowChrome();
            return Task.CompletedTask;
        });
        Register(ShortcutAction.SetFocusMode, ActionScope.Window, action => action.Args is SetFocusModeArgs, action =>
        {
            _focusMode = ((SetFocusModeArgs)action.Args!).IsFocusMode;
            ApplyWindowChrome();
            return Task.CompletedTask;
        });
    }

    private void Register(
        ShortcutAction action,
        ActionScope scope,
        Func<ActionAndArgs, bool> canExecute,
        Func<ActionAndArgs, Task> execute) =>
        _actionDispatcher.Register(action, scope, canExecute, execute);

    private ProfileSettings ResolveProfile(INewContentArgs? contentArgs)
    {
        if (contentArgs is not NewTerminalArgs terminal)
        {
            return _settings.GetDefaultProfile();
        }

        if (!string.IsNullOrWhiteSpace(terminal.Profile))
        {
            var hasRequestedGuid = Guid.TryParse(terminal.Profile, out var requestedGuid);
            var profile = _settings.Profiles.FirstOrDefault(profile =>
                       profile.Name.Equals(terminal.Profile, StringComparison.OrdinalIgnoreCase) ||
                       (hasRequestedGuid &&
                        Guid.TryParse(profile.Guid, out var profileGuid) &&
                        profileGuid == requestedGuid))
                   ?? _settings.GetDefaultProfile();
            return profile.WithOverrides(terminal);
        }

        var visibleProfiles = _settings.Profiles
            .Where(static profile => !profile.Hidden && !profile.Orphaned)
            .ToArray();
        var selected = terminal.ProfileIndex is { } selectedIndex &&
                       selectedIndex >= 0 &&
                       selectedIndex < visibleProfiles.Length
            ? visibleProfiles[selectedIndex]
            : _settings.GetDefaultProfile();
        return selected.WithOverrides(terminal);
    }

    private ColorScheme? FindColorScheme(string name)
    {
        var scheme = _settings.Schemes.FirstOrDefault(candidate =>
            candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (scheme is null ||
            !TryParseSchemeColor(scheme.Foreground, out var foreground) ||
            !TryParseSchemeColor(scheme.Background, out var background) ||
            !TryParseSchemeColor(scheme.CursorColor, out var cursor) ||
            !TryParseSchemeColor(scheme.SelectionBackground, out var selection) ||
            !TryParseSchemeColor(scheme.Black, out var black) ||
            !TryParseSchemeColor(scheme.Red, out var red) ||
            !TryParseSchemeColor(scheme.Green, out var green) ||
            !TryParseSchemeColor(scheme.Yellow, out var yellow) ||
            !TryParseSchemeColor(scheme.Blue, out var blue) ||
            !TryParseSchemeColor(scheme.Purple, out var purple) ||
            !TryParseSchemeColor(scheme.Cyan, out var cyan) ||
            !TryParseSchemeColor(scheme.White, out var white) ||
            !TryParseSchemeColor(scheme.BrightBlack, out var brightBlack) ||
            !TryParseSchemeColor(scheme.BrightRed, out var brightRed) ||
            !TryParseSchemeColor(scheme.BrightGreen, out var brightGreen) ||
            !TryParseSchemeColor(scheme.BrightYellow, out var brightYellow) ||
            !TryParseSchemeColor(scheme.BrightBlue, out var brightBlue) ||
            !TryParseSchemeColor(scheme.BrightPurple, out var brightPurple) ||
            !TryParseSchemeColor(scheme.BrightCyan, out var brightCyan) ||
            !TryParseSchemeColor(scheme.BrightWhite, out var brightWhite))
        {
            return null;
        }

        return new ColorScheme
        {
            Name = scheme.Name,
            Foreground = foreground,
            Background = background,
            Cursor = cursor,
            SelectionBackground = selection,
            Table =
            [
                black, red, green, yellow, blue, purple, cyan, white,
                brightBlack, brightRed, brightGreen, brightYellow,
                brightBlue, brightPurple, brightCyan, brightWhite,
            ],
        };
    }

    private static bool TryParseSchemeColor(string value, out uint color) =>
        ColorScheme.TryParseXtermColor(value, out color);

    private ProfileSettings ResolveSplitProfile(SplitPaneArgs? args) =>
        args?.SplitMode == SplitType.Duplicate && _activeTab?.Panes.ActiveContent is { } activePane
            ? activePane.Profile
            : ResolveProfile(args?.ContentArgs);

    private TerminalTab? ResolveTab(uint? index)
    {
        if (index is null)
        {
            return _activeTab;
        }

        return index == uint.MaxValue
            ? _tabs.LastOrDefault()
            : index < _tabs.Count ? _tabs[(int)index] : null;
    }

    private int TabIndexOf(TerminalTab tab)
    {
        for (var index = 0; index < _tabs.Count; index++)
        {
            if (ReferenceEquals(_tabs[index], tab))
            {
                return index;
            }
        }

        return -1;
    }

    private void ActivateRelativeTab(int delta, bool mostRecentlyUsed = false)
    {
        if (_activeTab is null || _tabs.Count == 0)
        {
            return;
        }

        if (_tabCollection.SelectRelative(delta, mostRecentlyUsed) &&
            _tabCollection.ActiveTab is { } selected)
        {
            ActivateTab(selected);
        }
    }

    private async Task CloseOtherTabsAsync(uint? index)
    {
        var keep = ResolveTab(index) ?? _activeTab;
        foreach (var tab in _tabs.Where(tab => !ReferenceEquals(tab, keep)).ToArray())
        {
            await CloseTabAsync(tab).ConfigureAwait(true);
        }

        if (keep is not null)
        {
            ActivateTab(keep);
        }
    }

    private async Task CloseTabsAfterAsync(uint? index)
    {
        var keep = ResolveTab(index) ?? _activeTab;
        var keepIndex = keep is null ? -1 : TabIndexOf(keep);
        foreach (var tab in _tabs.Skip(keepIndex + 1).ToArray())
        {
            await CloseTabAsync(tab).ConfigureAwait(true);
        }
    }

    private async Task CloseOtherPanesAsync()
    {
        var closed = _activeTab!.Panes.CloseOthers();
        foreach (var pane in closed)
        {
            await pane.Control.CloseAsync().ConfigureAwait(true);
        }

        SynchronizeTitle(_activeTab);
        RebuildTerminalHost();
        ActiveControl?.Focus();
    }

    private bool CanMoveFocus(MoveFocusArgs? args)
    {
        if (_activeTab?.Panes.ActiveContent is null || args is null)
        {
            return false;
        }

        return args.FocusDirection switch
        {
            FocusDirection.First => true,
            FocusDirection.NextInOrder or FocusDirection.Previous or FocusDirection.PreviousInOrder =>
                _activeTab.Panes.Count > 1,
            _ => ToPaneDirection(args.FocusDirection) is not null && _activeTab.Panes.Count > 1,
        };
    }

    private void MoveFocus(MoveFocusArgs args)
    {
        var moved = args.FocusDirection switch
        {
            FocusDirection.First => _activeTab!.Panes.FocusFirst(),
            FocusDirection.NextInOrder => _activeTab!.Panes.MoveFocusInOrder(1),
            FocusDirection.Previous or FocusDirection.PreviousInOrder => _activeTab!.Panes.MoveFocusInOrder(-1),
            _ => ToPaneDirection(args.FocusDirection) is { } direction &&
                 _activeTab!.Panes.MoveFocus(direction),
        };
        if (moved)
        {
            SynchronizeTitle(_activeTab!);
            RebuildTerminalHost();
            ActiveControl?.Focus();
        }
    }

    private bool CanMovePane(ActionAndArgs action) =>
        action.Args is MovePaneArgs args &&
        string.IsNullOrEmpty(args.Window) &&
        _activeTab?.Panes.ActiveContent is not null &&
        ResolveTab(args.TabIndex) is { } target &&
        !ReferenceEquals(target, _activeTab);

    private void MovePane(MovePaneArgs args)
    {
        var sourceTab = _activeTab!;
        var pane = sourceTab.Panes.ActiveContent!;
        var targetTab = ResolveTab(args.TabIndex)!;
        sourceTab.Panes.Close(pane);
        targetTab.Panes.SplitActive(pane, PaneSplitOrientation.Vertical);
        if (sourceTab.Panes.Count == 0)
        {
            _tabCollection.Remove(sourceTab);
        }

        ActivateTab(targetTab);
        RebuildTabs();
    }

    private void PopulateCommandPalette()
    {
        _paletteItems.Clear();
        foreach (var command in _settings.ActionMap.AllCommands.Where(static command => command.ActionAndArgs is not null))
        {
            var action = command.ActionAndArgs!;
            var shortcut = _settings.ActionMap.GetKeyBindingForAction(command.Id)?.ToString();
            _paletteItems.Add(new PaletteItem(command.Name, async () =>
            {
                await DispatchActionAsync(action).ConfigureAwait(true);
            }, shortcut));
        }

        foreach (var profile in _settings.Profiles.Where(static profile => !profile.Hidden))
        {
            _paletteItems.Add(new PaletteItem($"New tab: {profile.Name}", () => CreateTabAsync(profile)));
        }
    }

    private async Task<ActionDispatchResult> DispatchActionAsync(ActionAndArgs action)
    {
        _lastDispatchResult = await _actionDispatcher.DispatchAsync(action).ConfigureAwait(true);
        return _lastDispatchResult;
    }

    private static PaneSplitOrientation ResolveSplitOrientation(SplitDirection? direction) =>
        direction is SplitDirection.Up or SplitDirection.Down
            ? PaneSplitOrientation.Horizontal
            : PaneSplitOrientation.Vertical;

    private static PaneDirection? ToPaneDirection(FocusDirection direction) => direction switch
    {
        FocusDirection.Left => PaneDirection.Left,
        FocusDirection.Right => PaneDirection.Right,
        FocusDirection.Up => PaneDirection.Up,
        FocusDirection.Down => PaneDirection.Down,
        _ => null,
    };

    private static PaneDirection? ToPaneDirection(ResizeDirection direction) => direction switch
    {
        ResizeDirection.Left => PaneDirection.Left,
        ResizeDirection.Right => PaneDirection.Right,
        ResizeDirection.Up => PaneDirection.Up,
        ResizeDirection.Down => PaneDirection.Down,
        _ => null,
    };

    private void ShowFind()
    {
        FindBar.IsVisible = true;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void CloseFind()
    {
        FindBar.IsVisible = false;
        _activeTab?.Panes.ActiveContent?.Control.Focus();
    }

    private void Find(bool previous)
    {
        if (!string.IsNullOrWhiteSpace(FindBox.Text))
        {
            _activeTab?.Panes.ActiveContent?.Control.Find(FindBox.Text, previous);
        }
    }

    private void FindBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Find(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
    }

    private void FindPrevious_OnClick(object? sender, RoutedEventArgs e) => Find(previous: true);

    private void FindNext_OnClick(object? sender, RoutedEventArgs e) => Find(previous: false);

    private void CloseFind_OnClick(object? sender, RoutedEventArgs e) => CloseFind();

    private void ShowCommandPalette(PaletteMode mode = PaletteMode.Actions)
    {
        _paletteMode = mode;
        CommandPalette.IsVisible = true;
        CommandPaletteQuery.Text = string.Empty;
        CommandPaletteQuery.PlaceholderText = mode switch
        {
            PaletteMode.CommandHistory => "Search command history",
            PaletteMode.CommandLine => "Enter a wt command line",
            PaletteMode.Workspaces => "Search saved workspaces",
            _ => "Search actions",
        };
        RefreshCommandPalette();
        CommandPaletteQuery.Focus();
    }

    private void ShowTabSearch()
    {
        _paletteMode = PaletteMode.Tabs;
        CommandPalette.IsVisible = true;
        CommandPaletteQuery.Text = string.Empty;
        CommandPaletteQuery.PlaceholderText = "Search tabs";
        RefreshCommandPalette();
        CommandPaletteQuery.Focus();
    }

    private void CloseCommandPalette()
    {
        CommandPalette.IsVisible = false;
        CommandPaletteQuery.PlaceholderText = "Search actions";
        _activeTab?.Panes.ActiveContent?.Control.Focus();
    }

    private void RefreshCommandPalette()
    {
        var query = CommandPaletteQuery.Text;
        CommandPaletteList.ItemsSource = _paletteMode switch
        {
            PaletteMode.Tabs => _tabCollection.Search(query, static tab => tab.Title)
                .Select(tab => new PaletteItem(tab.Title, () =>
                {
                    ActivateTab(tab);
                    return Task.CompletedTask;
                }))
                .ToArray(),
            PaletteMode.CommandHistory => CommandHistoryItems(query),
            PaletteMode.CommandLine => CommandLineItems(query),
            PaletteMode.Workspaces => WorkspaceItems(query),
            _ => string.IsNullOrWhiteSpace(query)
                ? _paletteItems
                : FuzzyMatcher.Rank(_paletteItems, query, static item => item.Name),
        };
        CommandPaletteList.SelectedIndex = CommandPaletteList.ItemCount > 0 ? 0 : -1;
    }

    private IReadOnlyList<PaletteItem> CommandHistoryItems(string? query)
    {
        if (ActiveControl is null)
        {
            return [];
        }

        var commands = TerminalBufferExport.GetCommandHistory(
                ActiveControl.Engine.CreateSnapshot(includeHistory: true).Buffer)
            .Reverse()
            .Distinct(StringComparer.Ordinal)
            .Select(command => new PaletteItem(command, () =>
            {
                WriteCoordinatedInput(command);
                return Task.CompletedTask;
            }))
            .ToArray();
        return string.IsNullOrWhiteSpace(query)
            ? commands
            : FuzzyMatcher.Rank(commands, query, static item => item.Name);
    }

    private IReadOnlyList<PaletteItem> CommandLineItems(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : [new PaletteItem($"Run: {query}", () => ExecutePaletteCommandLineAsync(query))];

    private IReadOnlyList<PaletteItem> WorkspaceItems(string? query)
    {
        var items = WorkspaceNames
            .Select(name => new PaletteItem(
                name,
                () => OpenWorkspaceAsync(name)))
            .ToArray();
        return string.IsNullOrWhiteSpace(query)
            ? items
            : FuzzyMatcher.Rank(items, query, static item => item.Name);
    }

    private async Task ExecutePaletteCommandLineAsync(string commandLine)
    {
        if (_commandLineParser is null)
        {
            ShowNotification(new TerminalNotification(
                "Command line unavailable",
                "No command-line parser was registered for this window."));
            return;
        }

        var parsed = _commandLineParser(commandLine);
        if (!parsed.Succeeded)
        {
            ShowNotification(new TerminalNotification(
                "Invalid command line",
                parsed.Message));
            return;
        }

        if (parsed.SaveRequest is { } saveRequest)
        {
            var saveResult = SaveSnippet(saveRequest);
            if (!saveResult.Succeeded)
            {
                ShowNotification(new TerminalNotification(
                    "Unable to save command",
                    saveResult.Message));
                return;
            }
        }

        foreach (var action in parsed.Actions)
        {
            var result = await DispatchActionAsync(action).ConfigureAwait(true);
            if (result.Status != ActionDispatchStatus.Executed)
            {
                ShowNotification(new TerminalNotification(
                    "Command line action failed",
                    result.Message ?? $"Action '{result.Action}' was not executed."));
                return;
            }
        }
    }

    private void WriteCoordinatedInput(string input)
    {
        if (_activeTab?.Panes.ActiveContent is not { } activePane)
        {
            return;
        }

        _activeTab.BroadcastInput.WriteInput(
            activePane,
            _activeTab.Panes.Leaves(),
            input);
    }

    private bool TryGetWorkingDirectory(out string? workingDirectory)
    {
        workingDirectory = null;
        if (!Uri.TryCreate(
                ActiveControl?.Engine.WorkingDirectory,
                UriKind.Absolute,
                out var uri) ||
            !uri.IsFile ||
            !Directory.Exists(uri.LocalPath))
        {
            return false;
        }

        workingDirectory = uri.LocalPath;
        return true;
    }

    private void CommandPaletteQuery_OnTextChanged(object? sender, TextChangedEventArgs e) =>
        RefreshCommandPalette();

    private async void CommandPaletteQuery_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseCommandPalette();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && CommandPaletteList.ItemCount > 0)
        {
            CommandPaletteList.SelectedIndex = Math.Min(
                CommandPaletteList.SelectedIndex + 1,
                CommandPaletteList.ItemCount - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && CommandPaletteList.ItemCount > 0)
        {
            CommandPaletteList.SelectedIndex = Math.Max(CommandPaletteList.SelectedIndex - 1, 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            await ExecuteSelectedPaletteAsync().ConfigureAwait(true);
            e.Handled = true;
        }
    }

    private async void CommandPaletteList_OnDoubleTapped(object? sender, TappedEventArgs e) =>
        await ExecuteSelectedPaletteAsync().ConfigureAwait(true);

    private async Task ExecuteSelectedPaletteAsync()
    {
        if (CommandPaletteList.SelectedItem is PaletteItem item)
        {
            CloseCommandPalette();
            await item.Execute().ConfigureAwait(true);
        }
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (AboutOverlay.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                CloseAbout();
                e.Handled = true;
            }
            return;
        }

        if ((FindBar.IsVisible && FindBox.IsKeyboardFocusWithin) ||
            (CommandPalette.IsVisible && CommandPaletteQuery.IsKeyboardFocusWithin) ||
            e.Handled)
        {
            return;
        }

        if (AvaloniaKeyChord.TryCreate(e, out var chord) &&
            _settings.ActionMap.ResolveAction(chord) is { } action)
        {
            if (!_actionDispatcher.CanExecute(action))
            {
                e.Handled = TryRouteCoordinatedKey(e);
                return;
            }

            // Claim executable bindings before an asynchronous clipboard/process action yields,
            // so the terminal control cannot also translate the same chord to VT input.
            e.Handled = true;
            var result = await DispatchActionAsync(action).ConfigureAwait(true);
            if (result.Status == ActionDispatchStatus.Disabled)
            {
                RouteTerminalKey(e);
            }
        }
        else
        {
            e.Handled = TryRouteCoordinatedKey(e);
        }
    }

    private bool TryRouteCoordinatedKey(KeyEventArgs e)
    {
        if (_activeTab is not { } activeTab ||
            activeTab.Panes.ActiveContent is not { } activePane ||
            (!activePane.Presentation.IsReadOnly && !activeTab.BroadcastInput.IsEnabled))
        {
            return false;
        }

        var input = KeyMapper.ToVt(
            e.Key,
            e.KeyModifiers,
            e.PhysicalKey,
            e.KeySymbol,
            activePane.Control.Engine.InputMode);
        if (input is null)
        {
            return activePane.Presentation.IsReadOnly;
        }

        activeTab.BroadcastInput.WriteInput(activePane, activeTab.Panes.Leaves(), input);
        return true;
    }

    private bool RouteTerminalKey(KeyEventArgs e)
    {
        if (_activeTab is not { } activeTab ||
            activeTab.Panes.ActiveContent is not { } activePane)
        {
            return false;
        }

        var input = KeyMapper.ToVt(
            e.Key,
            e.KeyModifiers,
            e.PhysicalKey,
            e.KeySymbol,
            activePane.Control.Engine.InputMode);
        if (input is null)
        {
            return activePane.Presentation.IsReadOnly;
        }

        return activeTab.BroadcastInput
            .WriteInput(activePane, activeTab.Panes.Leaves(), input)
            .Count > 0;
    }

    private void OnWindowTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled ||
            string.IsNullOrEmpty(e.Text) ||
            e.Text is "\r" or "\n" or "\t" ||
            _activeTab is not { } activeTab ||
            activeTab.Panes.ActiveContent is not { } activePane ||
            (!activePane.Presentation.IsReadOnly && !activeTab.BroadcastInput.IsEnabled))
        {
            return;
        }

        activeTab.BroadcastInput.WriteInput(activePane, activeTab.Panes.Leaves(), e.Text);
        e.Handled = true;
    }

    private async Task PasteCoordinatedAsync()
    {
        var tab = _activeTab;
        var activePane = tab?.Panes.ActiveContent;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard is null ? null : await clipboard.TryGetTextAsync().ConfigureAwait(true);
        if (tab is null ||
            activePane is null ||
            !_tabs.Contains(tab) ||
            string.IsNullOrEmpty(text))
        {
            return;
        }

        text = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        foreach (var target in tab.BroadcastInput
                     .ResolveTargets(activePane, tab.Panes.Leaves())
                     .Cast<TerminalPane>())
        {
            target.WriteInput(target.Control.Engine.WrapPaste(text));
        }
    }

    private bool TryRenameWindow(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return false;
        }

        if (WindowName.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            WindowName = name;
            return true;
        }

        if (_stateStore.GetWorkspaceNames().Contains(name, StringComparer.OrdinalIgnoreCase) ||
            _windowNameValidator?.Invoke(name) == false)
        {
            ShowNotification(new TerminalNotification(
                "Window name unavailable",
                $"Another terminal window or saved workspace is already named '{name}'."));
            return false;
        }

        WindowName = name;
        ShowNotification(new TerminalNotification("Window renamed", name));
        return true;
    }

    private async Task<string?> PromptForTextAsync(
        string title,
        string prompt,
        string initialValue)
    {
        var textBox = new TextBox
        {
            Text = initialValue,
            MinWidth = 320,
        };
        var accept = new Button
        {
            Content = "Save",
            IsDefault = true,
            MinWidth = 80,
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 80,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, accept },
        };
        var dialog = new Window
        {
            Title = title,
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = prompt },
                    textBox,
                    buttons,
                },
            },
        };
        accept.Click += (_, _) => dialog.Close(textBox.Text ?? string.Empty);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Opened += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };
        return await dialog.ShowDialog<string?>(this).ConfigureAwait(true);
    }

    private bool CanPaste() =>
        _activeTab?.Panes.ActiveContent is { } activePane &&
        _activeTab.BroadcastInput.ResolveTargets(activePane, _activeTab.Panes.Leaves()).Count > 0;

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void ExitFullscreen_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Normal;

    private void UpdateFullscreenChrome()
    {
        var isFullscreen = WindowState == WindowState.FullScreen;
        ExitFullscreenButton.IsVisible = isFullscreen && !Win32ParentWindow.IsRequested;
        TitleBarLayout.Margin = isFullscreen
            ? new Thickness(8, 0, 8, 0)
            : new Thickness(8, 0, 138, 0);
        ApplyWindowChrome();
    }

    private void ApplyWindowChrome()
    {
        var embedded = OperatingSystem.IsWindows() && Win32ParentWindow.IsRequested;
        var fullscreen = WindowState == WindowState.FullScreen;
        var showTabs = !_focusMode &&
                       WindowChrome.ShouldShowTabRow(_settings, _tabs.Count, fullscreen);
        TabScrollViewer.IsVisible = showTabs;
        NewTabButton.IsVisible = showTabs;
        MenuButton.IsVisible = showTabs;

        if (embedded)
        {
            TitleBar.IsVisible = showTabs;
            Win32ParentWindow.ApplyEmbeddedChrome(this);
            Topmost = false;
            return;
        }

        var customTitlebar = WindowChrome.ShouldUseCustomTitlebar(_settings, embedded: false);
        ExtendClientAreaToDecorationsHint = customTitlebar && !_focusMode;
        TitleBar.IsVisible = !_focusMode && (showTabs || customTitlebar);
    }

    private async Task SummonAsync(GlobalSummonArgs args, bool quake)
    {
        var effective = quake
            ? args with { Name = "_quake" }
            : args;
        var result = _summonRequested is not null
            ? await _summonRequested(effective).ConfigureAwait(true)
            : await ((IGlobalWindowActionTarget)this).ApplySummonAsync(
                effective,
                quake,
                CancellationToken.None).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            ShowNotification(new TerminalNotification(
                "Window summon unavailable",
                result.Message));
        }
    }

    async ValueTask<WindowActionResult> IGlobalWindowActionTarget.ApplySummonAsync(
        GlobalSummonArgs args,
        bool quake,
        CancellationToken cancellationToken) =>
        await new WindowSummonController(this)
            .SummonAsync(args, quake, cancellationToken)
            .ConfigureAwait(true);

    bool IWindowSummonOperations.IsWindowVisible => IsVisible;
    bool IWindowSummonOperations.IsWindowActive => IsActive;
    bool IWindowSummonOperations.IsWindowMinimized => WindowState == WindowState.Minimized;
    DesktopPresence IWindowSummonOperations.DesktopPresence => DesktopPresence.Unknown;

    WindowPixelRect IWindowSummonOperations.CurrentBounds
    {
        get
        {
            var scale = RenderScaling;
            return new(
                Position.X,
                Position.Y,
                Math.Max(1, (int)Math.Round(Bounds.Width * scale)),
                Math.Max(1, (int)Math.Round(Bounds.Height * scale)));
        }
    }

    MonitorGeometry IWindowSummonOperations.GetMonitor(MonitorBehavior behavior)
    {
        PixelPoint point;
        if (behavior == MonitorBehavior.ToMouse &&
            OperatingSystem.IsWindows() &&
            GetCursorPosition(out var cursor))
        {
            point = new PixelPoint(cursor.X, cursor.Y);
        }
        else
        {
            var current = ((IWindowSummonOperations)this).CurrentBounds;
            point = new PixelPoint(
                current.X + (current.Width / 2),
                current.Y + (current.Height / 2));
        }

        var screen = Screens.ScreenFromPoint(point) ?? Screens.Primary;
        if (screen is null)
        {
            var current = ((IWindowSummonOperations)this).CurrentBounds;
            return new MonitorGeometry("current", current);
        }
        var workArea = screen.WorkingArea;
        return new MonitorGeometry(
            screen.DisplayName ?? $"{workArea.X},{workArea.Y}",
            new WindowPixelRect(
                workArea.X,
                workArea.Y,
                workArea.Width,
                workArea.Height));
    }

    WindowActionResult IWindowSummonOperations.MoveToCurrentDesktop() =>
        WindowActionResult.Unsupported(
            OperatingSystem.IsLinux()
                ? "Moving a window between Linux desktops is compositor-specific; the window was summoned without changing desktops."
                : "The platform has no stable public API for moving this window to the current virtual desktop; the window was summoned in place.");

    void IWindowSummonOperations.HideWindow() => Hide();

    async ValueTask IWindowSummonOperations.ShowWindowAsync(
        WindowPixelRect bounds,
        uint dropdownDuration,
        CancellationToken cancellationToken)
    {
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        var scale = RenderScaling;
        Width = bounds.Width / scale;
        Height = bounds.Height / scale;
        var destination = new PixelPoint(bounds.X, bounds.Y);
        if (dropdownDuration == 0)
        {
            Position = destination;
            Show();
            return;
        }

        var start = new PixelPoint(bounds.X, bounds.Y - bounds.Height);
        Position = start;
        Show();
        var duration = TimeSpan.FromMilliseconds(dropdownDuration);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            var progress = Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            Position = new PixelPoint(
                destination.X,
                start.Y + (int)Math.Round((destination.Y - start.Y) * eased));
            if (progress >= 1)
            {
                break;
            }
            await Task.Delay(16, cancellationToken).ConfigureAwait(true);
        }
        Position = destination;
    }

    void IWindowSummonOperations.ActivateWindow()
    {
        Activate();
        ActiveControl?.Focus();
    }

    private void OpenSystemMenu()
    {
        var result = _systemMenuService.Open(
            TryGetPlatformHandle()?.Handle ?? 0,
            Position.X + 12,
            Position.Y + 12);
        if (result.Succeeded)
        {
            return;
        }

        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                MenuCommand("Restore", () => WindowState = WindowState.Normal),
                MenuCommand("Minimize", () => WindowState = WindowState.Minimized),
                MenuCommand("Maximize", () => WindowState = WindowState.Maximized),
                new Separator(),
                MenuCommand("Close", Close),
            },
        };
        menu.Open(TitleBar);
    }

    private static MenuItem MenuCommand(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void MainWindow_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(CaptureNormalWindowBounds, DispatcherPriority.Background);

        TabScrollViewer.MaxWidth = Math.Max(120, e.NewSize.Width - 253);
        RebuildTabs();
    }

    private void CaptureNormalWindowBounds()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        _normalPosition = Position;
        _normalSize = new WindowSizeState
        {
            Width = Bounds.Width,
            Height = Bounds.Height,
        };
    }

    private void OpenSettings(SettingsTarget target = SettingsTarget.SettingsUI)
    {
        switch (target)
        {
            case SettingsTarget.SettingsUI:
            case SettingsTarget.AllFiles:
                SettingsViewFactory.CreateWindow(
                    () => SettingsService.LoadWithDynamicProfiles(_dynamicProfileManager),
                    SaveSettingsAndRefresh,
                    SettingsService.CreateDefault).Show(this);
                break;
            case SettingsTarget.SettingsFile:
                SaveSettingsAndRefresh(SettingsService.LoadWithDynamicProfiles(_dynamicProfileManager));
                OpenWithShell(SettingsService.SettingsPath);
                break;
            case SettingsTarget.DefaultsFile:
                var settingsDirectory = Path.GetDirectoryName(Path.GetFullPath(SettingsService.SettingsPath))
                    ?? SettingsService.SettingsDirectory;
                Directory.CreateDirectory(settingsDirectory);
                var defaultsPath = Path.Combine(settingsDirectory, "defaults.json");
                File.WriteAllText(defaultsPath, SettingsLoader.ReadEmbeddedDefaults());
                OpenWithShell(defaultsPath);
                break;
            case SettingsTarget.Directory:
                var directory = Path.GetDirectoryName(Path.GetFullPath(SettingsService.SettingsPath))
                    ?? SettingsService.SettingsDirectory;
                Directory.CreateDirectory(directory);
                OpenDirectoryWithShell(directory);
                break;
        }
    }

    private void OpenDirectoryWithShell(string path)
    {
        try
        {
            _platformLauncher.OpenDirectory(path);
        }
        catch (Exception ex) when (ex is
            System.ComponentModel.Win32Exception or
            InvalidOperationException or
            DirectoryNotFoundException)
        {
            ShowNotification(new TerminalNotification(
                "Unable to open directory",
                ex.Message));
        }
    }

    private void OpenWithShell(string target)
    {
        try
        {
            _platformLauncher.Open(target);
        }
        catch (Exception ex) when (ex is
            System.ComponentModel.Win32Exception or
            InvalidOperationException or
            DirectoryNotFoundException)
        {
            ShowNotification(new TerminalNotification(
                "Unable to open",
                ex.Message));
        }
    }

    private (bool Succeeded, string Message) SaveSnippet(TerminalSaveRequest request)
    {
        var commandline = request.Commandline;
        if (string.IsNullOrWhiteSpace(commandline) &&
            ActiveControl?.BuildCopyPayload() is { Text.Length: > 0 } selection)
        {
            commandline = selection.Text;
        }

        if (string.IsNullOrWhiteSpace(commandline))
        {
            return (false, "A command line or terminal selection is required.");
        }

        try
        {
            var current = SettingsService.LoadWithDynamicProfiles(_dynamicProfileManager);
            SettingsSnippetStore.Add(
                current,
                request.Name,
                request.KeyChord,
                commandline);
            SaveSettingsAndRefresh(current);
            return (true, $"Saved command '{commandline}'.");
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            IOException or
            UnauthorizedAccessException)
        {
            return (false, ex.Message);
        }
    }

    private async Task OpenWorkspaceAsync(string name)
    {
        if (_workspaceRequested is not null)
        {
            _workspaceRequested(name);
            return;
        }

        var saved = _stateStore.GetWorkspace(name);
        if (saved is null)
        {
            ShowNotification(new TerminalNotification(
                "Workspace unavailable",
                $"Workspace '{name}' was not found."));
            return;
        }

        if (!TerminalLayoutStateStore.TryRead(saved, out _, out var diagnostic))
        {
            LastPersistenceError = diagnostic;
            _persistenceBlockedByInvalidLayout = true;
            ShowNotification(new TerminalNotification(
                "Workspace unavailable",
                diagnostic ?? $"Workspace '{name}' has an invalid layout."));
            return;
        }

        var claimed = _stateStore.TakeWorkspace(
            name,
            state => TerminalLayoutStateStore.TryRead(state, out _, out _));
        if (claimed is not null)
        {
            await TryRestoreLayoutAsync(claimed).ConfigureAwait(true);
        }
    }

    private (int Columns, int Rows) InitialTerminalSize()
    {
        var profile = _activeTab?.Panes.ActiveContent?.Profile ?? _settings.GetDefaultProfile();
        var cell = ActiveControl?.CellSize ?? TermControl.MeasureCell(profile, DisplayScale());
        var columns = Math.Max(
            20,
            (int)((TerminalHost.Bounds.Width - 16 - ScrollbarWidth(profile)) / cell.Width));
        var rows = Math.Max(10, (int)((TerminalHost.Bounds.Height - 16) / cell.Height));
        return double.IsNaN(TerminalHost.Bounds.Width) || TerminalHost.Bounds.Width <= 0
            ? (_settings.InitialCols, _settings.InitialRows)
            : (columns, rows);
    }

    private static int ScrollbarWidth(ProfileSettings profile) =>
        profile.ScrollbarState.Equals("hidden", StringComparison.OrdinalIgnoreCase) ? 0 : 16;

    public TerminalWindowLayoutDescriptor CaptureLayout() =>
        new()
        {
            ActiveTabId = _activeTab?.Id,
            Tabs = _tabs.Select(CaptureTab).ToList(),
        };

    private TabLayoutDescriptor CaptureTab(TerminalTab tab) =>
        new()
        {
            TabId = tab.Id,
            ActiveSessionId = tab.Panes.ActiveContent?.Session.SessionId ?? Guid.Empty,
            ZoomedSessionId = tab.Panes.ZoomedContent?.Session.SessionId,
            Title = tab.Title,
            CustomTitle = tab.CustomTitle,
            Color = tab.Color,
            Root = CapturePaneNode(tab.Panes.Root ??
                throw new InvalidOperationException("Cannot capture an empty tab.")),
        };

    private static PaneLayoutDescriptor CapturePaneNode(PaneNode<TerminalPane> node) =>
        node switch
        {
            PaneLeaf<TerminalPane> leaf => new()
            {
                Session = CloneSession(leaf.Content.Session),
                Presentation = ClonePresentation(leaf.Content.Presentation),
            },
            PaneSplit<TerminalPane> split => new()
            {
                Orientation = split.Orientation,
                Ratio = split.Ratio,
                First = CapturePaneNode(split.First),
                Second = CapturePaneNode(split.Second),
            },
            _ => throw new InvalidOperationException("Unknown pane node."),
        };

    private async Task<bool> TryRestorePersistedLayoutAsync()
    {
        if (!UsesPersistedLayout)
        {
            return false;
        }

        var windowState = TerminalLayoutStateStore.ReadWindowState(_stateStore, WindowId);
        if (windowState is null)
        {
            return false;
        }

        var restored = await TryRestoreLayoutAsync(windowState).ConfigureAwait(true);
        if (!restored)
        {
            // Keep this slot intact so the fallback tab cannot replace data that may
            // be recoverable by a newer version of the application.
            _persistenceBlockedByInvalidLayout = true;
        }

        return restored;
    }

    private async Task<bool> TryRestoreLayoutAsync(WindowLayoutState windowState)
    {
        if (!TerminalLayoutStateStore.TryRead(
                windowState,
                out var layout,
                out var diagnostic) ||
            layout is null ||
            layout.Tabs.Count == 0)
        {
            LastPersistenceError = diagnostic;
            ShowNotification(new TerminalNotification(
                "Saved layout unavailable",
                diagnostic ?? "The saved terminal layout is invalid."));
            return false;
        }

        ApplyPersistedWindowState(windowState);
        foreach (var descriptor in layout.Tabs)
        {
            await RestoreTabAsync(descriptor).ConfigureAwait(true);
        }

        if (layout.ActiveTabId is { } activeId &&
            _tabs.FirstOrDefault(tab => tab.Id == activeId) is { } active)
        {
            ActivateTab(active);
        }

        LastPersistenceError = null;
        return true;
    }

    private void ApplyPersistedWindowState(WindowLayoutState? state)
    {
        if (state?.InitialPosition?.Split(',') is [var xText, var yText] &&
            int.TryParse(xText, out var x) &&
            int.TryParse(yText, out var y))
        {
            _normalPosition = new PixelPoint(x, y);
            Position = _normalPosition.Value;
        }

        if (state?.InitialSize is { Width: > 0, Height: > 0 } size)
        {
            _normalSize = new WindowSizeState
            {
                Width = size.Width,
                Height = size.Height,
            };
            Width = _normalSize.Width;
            Height = _normalSize.Height;
        }

        switch (state?.LaunchMode)
        {
            case LaunchMode.Maximized:
                WindowState = WindowState.Maximized;
                _focusMode = false;
                break;
            case LaunchMode.Fullscreen:
                WindowState = WindowState.FullScreen;
                _focusMode = false;
                break;
            case LaunchMode.Focus:
                WindowState = WindowState.Normal;
                _focusMode = true;
                break;
            case LaunchMode.MaximizedFocus:
                WindowState = WindowState.Maximized;
                _focusMode = true;
                break;
            default:
                WindowState = WindowState.Normal;
                _focusMode = false;
                break;
        }

        ApplyWindowChrome();
    }

    private async Task<TerminalTab> RestoreTabAsync(
        TabLayoutDescriptor descriptor,
        bool regenerateIdentities = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var sessions = new Dictionary<Guid, TerminalPane>();
        var root = RestorePaneNode(descriptor.Root, sessions, regenerateIdentities);
        var activePane = sessions.GetValueOrDefault(descriptor.ActiveSessionId) ?? sessions.Values.First();
        var zoomedPane = descriptor.ZoomedSessionId is { } zoomedId
            ? sessions.GetValueOrDefault(zoomedId)
            : null;
        var tree = PaneTree<TerminalPane>.Restore(root, activePane, zoomedPane);
        var tab = new TerminalTab(
            regenerateIdentities ? Guid.NewGuid() : descriptor.TabId,
            tree)
        {
            Title = string.IsNullOrWhiteSpace(descriptor.CustomTitle)
                ? activePane.Title
                : descriptor.CustomTitle,
            CustomTitle = descriptor.CustomTitle,
            Color = descriptor.Color,
        };
        _tabCollection.Add(tab);
        ActivateTab(tab);
        RebuildTabs();

        var (columns, rows) = InitialTerminalSize();
        foreach (var pane in sessions.Values)
        {
            await pane.Control.StartAsync(pane.Profile, columns, rows).ConfigureAwait(true);
        }

        activePane.Control.Focus();
        return tab;
    }

    private PaneNode<TerminalPane> RestorePaneNode(
        PaneLayoutDescriptor descriptor,
        IDictionary<Guid, TerminalPane> sessions,
        bool regenerateIdentities)
    {
        if (descriptor.Session is { } savedSession)
        {
            var session = CloneSession(savedSession);
            if (regenerateIdentities)
            {
                session.SessionId = Guid.NewGuid();
            }

            var profile = ResolveProfile(new NewTerminalArgs(
                Commandline: session.Commandline,
                StartingDirectory: session.StartingDirectory,
                TabTitle: session.TabTitle ?? string.Empty,
                TabColor: session.TabColor,
                Profile: session.ProfileId ?? session.ProfileName,
                SessionId: session.SessionId,
                SuppressApplicationTitle: session.SuppressApplicationTitle,
                Elevate: session.Elevate,
                ReloadEnvironmentVariables: session.ReloadEnvironmentVariables));
            var pane = CreatePane(profile, session, ClonePresentation(descriptor.Presentation));
            sessions.Add(savedSession.SessionId, pane);

            return new PaneLeaf<TerminalPane>(pane);
        }

        if (descriptor.First is null || descriptor.Second is null || descriptor.Orientation is null)
        {
            throw new InvalidOperationException("Invalid persisted pane split.");
        }

        return new PaneSplit<TerminalPane>(
            descriptor.Orientation.Value,
            descriptor.Ratio,
            RestorePaneNode(descriptor.First, sessions, regenerateIdentities),
            RestorePaneNode(descriptor.Second, sessions, regenerateIdentities));
    }

    private static TerminalSessionDescriptor CreateSessionDescriptor(ProfileSettings profile) =>
        new()
        {
            ProfileId = profile.Guid,
            ProfileName = profile.Name,
            Commandline = profile.Commandline,
            StartingDirectory = profile.StartingDirectory,
            TabTitle = profile.TabTitle,
            TabColor = profile.TabColor,
            Icon = ProfileVisualDefaults.Icon(profile),
            Elevate = profile.Elevate,
            SuppressApplicationTitle = profile.SuppressApplicationTitle,
            ReloadEnvironmentVariables = profile.ReloadEnvironmentVariables,
        };

    private static TerminalSessionDescriptor CloneSession(TerminalSessionDescriptor session) =>
        new()
        {
            SessionId = session.SessionId,
            ProfileId = session.ProfileId,
            ProfileName = session.ProfileName,
            Commandline = session.Commandline,
            StartingDirectory = session.StartingDirectory,
            TabTitle = session.TabTitle,
            TabColor = session.TabColor,
            Icon = session.Icon,
            Elevate = session.Elevate,
            SuppressApplicationTitle = session.SuppressApplicationTitle,
            ReloadEnvironmentVariables = session.ReloadEnvironmentVariables,
        };

    private static PanePresentationState ClonePresentation(PanePresentationState presentation) =>
        new()
        {
            Title = presentation.Title,
            Icon = presentation.Icon,
            Color = presentation.Color,
            ProgressState = presentation.ProgressState,
            Progress = presentation.Progress,
            IsAdministrator = presentation.IsAdministrator,
            IsReadOnly = presentation.IsReadOnly,
            HasBellIndicator = presentation.HasBellIndicator,
            HasUnseenActivity = presentation.HasUnseenActivity,
        };

    private void PersistLayout(TerminalWindowLayoutDescriptor layout)
    {
        if (!TerminalLayoutStateStore.TrySaveWindow(
            _stateStore,
            WindowId,
            layout,
            _normalPosition is { } position ? $"{position.X},{position.Y}" : null,
            _normalSize,
            CurrentLaunchMode(),
            _persistenceBlockedByInvalidLayout))
        {
            return;
        }
        _layoutPersisted = true;
    }

    private void PersistWorkspace(TerminalWindowLayoutDescriptor layout)
    {
        _stateStore.SaveWorkspace(
            WindowName,
            TerminalLayoutSerializer.ToApplicationState(
                layout,
                _normalPosition is { } position ? $"{position.X},{position.Y}" : null,
                _normalSize,
                CurrentLaunchMode()));
        _layoutPersisted = true;
    }

    private LaunchMode CurrentLaunchMode() => (WindowState, TitleBar.IsVisible) switch
    {
        (WindowState.FullScreen, _) => LaunchMode.Fullscreen,
        (WindowState.Maximized, false) => LaunchMode.MaximizedFocus,
        (WindowState.Maximized, true) => LaunchMode.Maximized,
        (_, false) => LaunchMode.Focus,
        _ => LaunchMode.Default,
    };

    private bool UsesPersistedLayout =>
        TerminalLayoutStateStore.IsPersistedLayoutPreference(
            _settings.FirstWindowPreference);

    private void TryPersistCurrentLayout(TerminalWindowLayoutDescriptor layout)
    {
        if (!string.IsNullOrWhiteSpace(WindowName))
        {
            if (!_persistenceBlockedByInvalidLayout)
            {
                TryPersistWorkspace(layout);
            }
        }
        else if (UsesPersistedLayout)
        {
            TryPersistLayout(layout);
        }
    }

    private void TryPersistLayout(TerminalWindowLayoutDescriptor layout)
    {
        try
        {
            PersistLayout(layout);
            LastPersistenceError = null;
        }
        catch (IOException ex)
        {
            LastPersistenceError = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            LastPersistenceError = ex.Message;
        }
    }

    private void TryPersistWorkspace(TerminalWindowLayoutDescriptor layout)
    {
        try
        {
            PersistWorkspace(layout);
            LastPersistenceError = null;
        }
        catch (IOException ex)
        {
            LastPersistenceError = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            LastPersistenceError = ex.Message;
        }
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            color = default;
            return false;
        }

        return Color.TryParse(value, out color);
    }

    private TerminalTab? FindTab(TerminalPane pane) =>
        _tabs.FirstOrDefault(tab => !tab.IsClosing && tab.Panes.Leaves().Contains(pane));

    private async Task RemoveFailedPaneAsync(TerminalTab tab, TerminalPane pane)
    {
        tab.Panes.Close(pane);
        await pane.Control.CloseAsync().ConfigureAwait(true);
        if (tab.Panes.Count > 0)
        {
            SynchronizeTitle(tab);
            RebuildTerminalHost();
            tab.Panes.ActiveContent?.Control.Focus();
            return;
        }

        tab.IsClosing = true;
        _tabCollection.Remove(tab);
        if (ReferenceEquals(_activeTab, tab))
        {
            _activeTab = null;
            TerminalHost.Children.Clear();
            var replacement = _tabs.LastOrDefault(static candidate => !candidate.IsClosing);
            if (replacement is not null)
            {
                ActivateTab(replacement);
            }
            else
            {
                Title = "Devolutions Terminal";
                RebuildTabs();
            }
        }
    }

    private void ShowScratchpad()
    {
        var editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(editor, "Scratchpad editor");
        var scratchpad = new Window
        {
            Title = "Scratchpad",
            Width = 720,
            Height = 520,
            Content = editor,
        };
        scratchpad.Show(this);
        editor.Focus();
    }

    private void ShowAbout()
    {
        _aboutPreviousFocus = FocusManager?.GetFocusedElement();
        AboutVersion.Text =
            $"Version: {typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "Development build"}";
        TitleBar.IsEnabled = false;
        TerminalHost.IsEnabled = false;
        FindBar.IsEnabled = false;
        CommandPalette.IsEnabled = false;
        AboutOverlay.IsVisible = true;
        AboutOkButton.Focus();
    }

    private void CloseAbout()
    {
        AboutOverlay.IsVisible = false;
        TitleBar.IsEnabled = true;
        TerminalHost.IsEnabled = true;
        FindBar.IsEnabled = true;
        CommandPalette.IsEnabled = true;
        var previousFocus = _aboutPreviousFocus;
        _aboutPreviousFocus = null;
        if (previousFocus?.Focus() != true &&
            !MenuButton.Focus())
        {
            ActiveControl?.Focus();
        }
    }

    private void AboutClose_OnClick(object? sender, RoutedEventArgs e) => CloseAbout();

    private void AboutFeedback_OnClick(object? sender, RoutedEventArgs e) =>
        OpenWithShell("https://github.com/Devolutions/devolutions-terminal/issues");

    private void AboutLink_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uri })
        {
            OpenWithShell(uri);
        }
    }

    private ValueTask ShowAzureDeviceCodeAsync(
        AzureDeviceCodePrompt prompt,
        CancellationToken cancellationToken) =>
        RunOnUiThreadAsync(
            () => ShowAzureDeviceCodeDialogAsync(prompt, cancellationToken),
            cancellationToken);

    private ValueTask<AzureCloudShellTenant> SelectAzureTenantAsync(
        IReadOnlyList<AzureCloudShellTenant> tenants,
        CancellationToken cancellationToken) =>
        RunOnUiThreadAsync(
            () => ShowAzureTenantDialogAsync(tenants, cancellationToken),
            cancellationToken);

    private async ValueTask ShowAzureDeviceCodeDialogAsync(
        AzureDeviceCodePrompt prompt,
        CancellationToken cancellationToken)
    {
        var openBrowser = new Button
        {
            Content = "Open browser",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var continueButton = new Button { Content = "Continue" };
        var cancelButton = new Button { Content = "Cancel" };
        var dialog = new Window
        {
            Title = "Sign in to Azure Cloud Shell",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Sign in to Azure Cloud Shell",
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = prompt.Message,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock { Text = "Device code" },
                    new TextBox
                    {
                        Text = prompt.UserCode ?? string.Empty,
                        IsReadOnly = true,
                    },
                    new TextBlock
                    {
                        Text = $"This code expires {prompt.ExpiresAt.LocalDateTime:g}.",
                    },
                    openBrowser,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, continueButton },
                    },
                },
            },
        };
        openBrowser.Click += (_, _) => OpenAzureVerificationUri(prompt.VerificationUri);
        continueButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);
        using var registration = cancellationToken.Register(
            () => Dispatcher.UIThread.Post(() => dialog.Close(false)));

        var accepted = await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        if (!accepted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AzureCloudShellException(
                AzureCloudShellStage.Authentication,
                "AuthenticationCanceled",
                "Azure Cloud Shell sign-in was canceled.");
        }
    }

    private async ValueTask<AzureCloudShellTenant> ShowAzureTenantDialogAsync(
        IReadOnlyList<AzureCloudShellTenant> tenants,
        CancellationToken cancellationToken)
    {
        if (tenants.Count == 0)
        {
            throw new InvalidOperationException("Azure returned no accessible tenants.");
        }

        var list = new ListBox
        {
            ItemsSource = tenants.Select(static tenant =>
                string.IsNullOrWhiteSpace(tenant.DisplayName)
                    ? tenant.DefaultDomain ?? tenant.TenantId
                    : $"{tenant.DisplayName} ({tenant.DefaultDomain ?? tenant.TenantId})"),
            SelectedIndex = 0,
            MinHeight = 160,
        };
        var connectButton = new Button { Content = "Connect" };
        var cancelButton = new Button { Content = "Cancel" };
        var dialog = new Window
        {
            Title = "Choose an Azure tenant",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Choose an Azure tenant",
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold,
                    },
                    list,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, connectButton },
                    },
                },
            },
        };
        connectButton.Click += (_, _) =>
        {
            if (list.SelectedIndex >= 0 && list.SelectedIndex < tenants.Count)
            {
                dialog.Close(tenants[list.SelectedIndex]);
            }
        };
        cancelButton.Click += (_, _) => dialog.Close(null);
        using var registration = cancellationToken.Register(
            () => Dispatcher.UIThread.Post(() => dialog.Close(null)));

        var selected = await dialog.ShowDialog<AzureCloudShellTenant?>(this).ConfigureAwait(true);
        if (selected is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AzureCloudShellException(
                AzureCloudShellStage.Authentication,
                "TenantSelectionCanceled",
                "Azure tenant selection was canceled.");
        }

        return selected;
    }

    private void OpenAzureVerificationUri(Uri? verificationUri)
    {
        if (verificationUri is null)
        {
            ShowNotification(new TerminalNotification(
                "Azure sign-in",
                "Azure did not provide a verification address."));
            return;
        }

        try
        {
            OpenWithShell(verificationUri.AbsoluteUri);
        }
        catch (Exception ex) when (ex is
            System.ComponentModel.Win32Exception or
            InvalidOperationException)
        {
            ShowNotification(new TerminalNotification(
                "Unable to open browser",
                ex.Message));
        }
    }

    private static ValueTask RunOnUiThreadAsync(
        Func<ValueTask> operation,
        CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return operation();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await operation().ConfigureAwait(true);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return new ValueTask(completion.Task);
    }

    private static ValueTask<T> RunOnUiThreadAsync<T>(
        Func<ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return operation();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(await operation().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return new ValueTask<T>(completion.Task);
    }

    private async Task ShowLaunchErrorAsync(ProfileSettings profile, Exception error)
    {
        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var dialog = new Window
        {
            Title = "Unable to launch profile",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Devolutions Terminal could not launch '{profile.Name}'.",
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = error.Message,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    close,
                },
            },
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }

    private void ShowNotification(TerminalNotification notification)
    {
        var title = string.IsNullOrWhiteSpace(notification.Title)
            ? "Devolutions Terminal"
            : notification.Title;
        NotificationTitle.Text = title;
        NotificationBody.Text = notification.Body;
        NotificationToast.IsVisible = true;
        _notificationTimer.Stop();
        _notificationTimer.Start();

        QueueSystemToast(title, notification.Body);
    }

    private void QueueSystemToast(string title, string body)
    {
        var now = Environment.TickCount64;
        while (true)
        {
            var previous = Interlocked.Read(ref _lastSystemToastTick);
            if (now - previous < 1000)
            {
                System.Diagnostics.Trace.WriteLine("System toast publication was rate-limited.");
                return;
            }
            if (Interlocked.CompareExchange(ref _lastSystemToastTick, now, previous) == previous)
            {
                break;
            }
        }

        _ = PublishSystemToastAsync(title, body);
    }

    private async Task PublishSystemToastAsync(string title, string body)
    {
        try
        {
            var desktopResult = await Task.Run(
                () => _platformLauncher.ShowNotification(title, body)).ConfigureAwait(false);
            if (!desktopResult.Succeeded &&
                !string.IsNullOrWhiteSpace(desktopResult.Diagnostic))
            {
                System.Diagnostics.Trace.TraceWarning(desktopResult.Diagnostic);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"System toast publication failed: {ex.Message}");
        }
    }

    private void SaveSettingsAndRefresh(AppSettings settings)
    {
        SettingsService.Save(settings);
        _settings = SettingsService.LoadWithDynamicProfiles(_dynamicProfileManager);
        _settingsChanged?.Invoke(_settings);
        RefreshJumpList(_settings);
        PopulateCommandPalette();
    }

    private void RefreshJumpList(AppSettings? settings = null)
    {
        var snapshot = settings ?? _settings;
        var fingerprint = string.Join(
            '\u001f',
            snapshot.Profiles
                .Where(static profile => !profile.Hidden && !profile.Orphaned)
                .Select(static profile => $"{profile.Guid}\u001e{profile.Name}\u001e{profile.Icon}"));
        if (string.Equals(fingerprint, _lastJumpListFingerprint, StringComparison.Ordinal))
        {
            return;
        }
        _ = RefreshJumpListAsync(snapshot, fingerprint);
    }

    private async Task RefreshJumpListAsync(AppSettings settings, string fingerprint)
    {
        await JumpListRefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (string.Equals(fingerprint, _lastJumpListFingerprint, StringComparison.Ordinal))
            {
                return;
            }

            var result = await Task.Run(
                () => _platformLauncher.RefreshJumpList(settings)).ConfigureAwait(false);
            if (result.Succeeded)
            {
                _lastJumpListFingerprint = fingerprint;
            }
            else
            {
                System.Diagnostics.Trace.TraceWarning(result.Diagnostic);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Jump-list refresh failed: {ex.Message}");
        }
        finally
        {
            JumpListRefreshGate.Release();
        }
    }

    private static bool IsLaunchFailure(Exception error) =>
        error is
            System.ComponentModel.Win32Exception or
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            PlatformNotSupportedException or
            AzureCloudShellException or
            System.Runtime.InteropServices.COMException;

    private void SynchronizeTitle(TerminalTab tab)
    {
        if (tab.Panes.ActiveContent is { } activePane)
        {
            tab.Title = string.IsNullOrWhiteSpace(tab.CustomTitle)
                ? activePane.Title
                : tab.CustomTitle;
        }

        RebuildTabs();
        if (ReferenceEquals(_activeTab, tab))
        {
            SetNativeWindowTitle(tab.Title);
        }
    }

    private void SetNativeWindowTitle(string title)
    {
        if (!OperatingSystem.IsWindows())
        {
            Title = title;
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? 0;
        if (handle != 0)
        {
            _ = SetWindowText(handle, title);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowText(nint windowHandle, string title);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPosition(out CursorPoint point);

    private void DetachPaneControls(TerminalTab tab)
    {
        foreach (var pane in tab.Panes.Leaves())
        {
            DetachControl(pane.Control);
            if (_paneScrollBars.TryGetValue(pane, out var scrollBar))
            {
                DetachControl(scrollBar);
            }
        }
    }

    private static void DetachControl(Control control)
    {
        if (control.Parent is Decorator decorator)
        {
            decorator.Child = null;
        }
        else if (control.Parent is Panel panel)
        {
            panel.Children.Remove(control);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (AboutOverlay.IsVisible)
        {
            e.Cancel = true;
            CloseAbout();
            return;
        }

        base.OnClosing(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (!_layoutPersisted && _tabs.Count > 0)
        {
            TryPersistCurrentLayout(CaptureLayout());
        }

        foreach (var tab in _tabs.ToArray())
        {
            foreach (var pane in tab.Panes.Leaves())
            {
                await pane.Control.CloseAsync().ConfigureAwait(true);
            }
        }

        foreach (var bitmap in _tabIconCache.Values)
        {
            bitmap.Dispose();
        }

        _tabIconCache.Clear();

        base.OnClosed(e);
    }

    private sealed class PaneScrollBar : Grid
    {
        private readonly TerminalPane _pane;
        private readonly ScrollBar _scrollBar;
        private readonly Canvas _marks;
        private bool _updating;

        public PaneScrollBar(TerminalPane pane)
        {
            _pane = pane;
            Width = 12;
            _scrollBar = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                Width = 12,
                Minimum = 0,
                SmallChange = 1,
                AllowAutoHide = !pane.Profile.ScrollbarState.Equals(
                    "always",
                    StringComparison.OrdinalIgnoreCase),
            };
            _marks = new Canvas
            {
                Width = 12,
                IsHitTestVisible = false,
            };
            Children.Add(_scrollBar);
            Children.Add(_marks);
            _scrollBar.ValueChanged += (_, _) =>
            {
                if (!_updating)
                {
                    pane.Control.SetScrollOffset(
                        pane.Control.Engine.HistoryCount - (int)Math.Round(_scrollBar.Value));
                }
            };
            pane.Control.ViewportChanged += (_, _) => Update();
            pane.Control.ScrollMarksChanged += (_, _) => Update();
            PropertyChanged += (_, args) =>
            {
                if (args.Property == BoundsProperty)
                {
                    UpdateMarks();
                }
            };
            Update();
        }

        private void Update()
        {
            _updating = true;
            var history = _pane.Control.Engine.HistoryCount;
            _scrollBar.Maximum = history;
            _scrollBar.ViewportSize = _pane.Control.Engine.Rows;
            _scrollBar.LargeChange = Math.Max(1, _pane.Control.Engine.Rows - 1);
            _scrollBar.Value = history - _pane.Control.Engine.ScrollOffset;
            IsVisible = !_pane.Profile.ScrollbarState.Equals(
                "hidden",
                StringComparison.OrdinalIgnoreCase);
            _updating = false;
            UpdateMarks();
        }

        private void UpdateMarks()
        {
            _marks.Children.Clear();
            if (!_pane.Profile.ShowMarksOnScrollbar || Bounds.Height <= 0)
            {
                return;
            }

            foreach (var mark in _pane.Control.GetScrollMarks())
            {
                var color = mark.Color is { Length: > 0 } value &&
                            TryParseColor(value, out var parsed)
                    ? parsed
                    : mark.Kind switch
                    {
                        TerminalScrollMarkKind.CommandError => Color.Parse("#E74856"),
                        TerminalScrollMarkKind.CommandSuccess => Color.Parse("#16C60C"),
                        TerminalScrollMarkKind.CurrentSearchMatch => Color.Parse("#F9F1A5"),
                        TerminalScrollMarkKind.SearchMatch => Color.Parse("#C19C00"),
                        _ => Color.Parse("#3A96DD"),
                    };
                var tick = new Border
                {
                    Width = 10,
                    Height = 3,
                    Background = new SolidColorBrush(color),
                };
                Canvas.SetLeft(tick, 1);
                Canvas.SetTop(tick, Math.Clamp(mark.Position * Math.Max(0, Bounds.Height - 3), 0, Bounds.Height));
                _marks.Children.Add(tick);
            }
        }
    }
}

internal static class ProfileVisualDefaults
{
    public static string Icon(ProfileSettings profile)
    {
        if (profile.IconResource?.ToString() is { Length: > 0 } icon)
        {
            return icon;
        }

        if (profile.Commandline.Contains("pwsh.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-appx:///ProfileIcons/pwsh.png";
        }

        if (profile.Commandline.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-appx:///ProfileIcons/{61c54bbd-c2c6-5271-96e7-009a87ff44bf}.png";
        }

        if (profile.Commandline.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-appx:///ProfileIcons/{0caa0dad-35be-5f56-a8ff-afceeeaa6101}.png";
        }

        if (profile.Commandline.Contains("wsl.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-appx:///ProfileGeneratorIcons/WSL.png";
        }

        if (profile.Commandline.Contains("ssh", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-appx:///ProfileGeneratorIcons/SSH.png";
        }

        if (profile.Name.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase) ||
            profile.Name.Contains("Developer", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-appx:///ProfileGeneratorIcons/VisualStudio.png";
        }

        return "ms-appx:///ProfileIcons/terminal.png";
    }
}

public sealed class TerminalPane : ITerminalInputTarget
{
    public TerminalPane(
        uint id,
        TerminalSessionDescriptor session,
        ProfileSettings profile,
        TermControl control,
        PanePresentationState? presentation = null)
    {
        Id = id;
        Session = session;
        Profile = profile;
        Control = control;
        Presentation = presentation ?? new PanePresentationState
        {
            Title = string.IsNullOrWhiteSpace(profile.TabTitle) ? profile.Name : profile.TabTitle,
            Icon = profile.IconResource?.ToString(),
            Color = profile.TabColor,
            IsAdministrator = profile.Elevate,
        };
        Presentation.Title = string.IsNullOrWhiteSpace(profile.TabTitle)
            ? profile.Name
            : profile.TabTitle;
        Presentation.Icon = string.IsNullOrWhiteSpace(Presentation.Icon)
            ? ProfileVisualDefaults.Icon(profile)
            : Presentation.Icon;
    }

    public uint Id { get; }
    public TerminalSessionDescriptor Session { get; }
    public ProfileSettings Profile { get; }
    public TermControl Control { get; }
    public PanePresentationState Presentation { get; }
    public string Title
    {
        get => Presentation.Title;
        set => Presentation.Title = value;
    }

    public bool IsReadOnly => Presentation.IsReadOnly;

    public void WriteInput(string input)
    {
        if (!IsReadOnly)
        {
            Control.WriteInput(input);
        }
    }
}

public sealed class TerminalTab
{
    public TerminalTab(TerminalPane initialPane)
        : this(Guid.NewGuid(), new PaneTree<TerminalPane>(initialPane))
    {
        Title = initialPane.Title;
        Color = initialPane.Presentation.Color;
    }

    public TerminalTab(Guid id, PaneTree<TerminalPane> panes)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Panes = panes;
        Title = panes.ActiveContent?.Title ?? string.Empty;
    }

    public Guid Id { get; }
    public PaneTree<TerminalPane> Panes { get; }
    public BroadcastInputCoordinator BroadcastInput { get; } = new();
    public string Title { get; set; }
    public string? CustomTitle { get; set; }
    public string? Color { get; set; }
    public bool IsClosing { get; set; }
}

internal sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

internal enum PaletteMode
{
    Actions,
    Tabs,
    CommandHistory,
    CommandLine,
    Workspaces,
}

internal sealed record PaletteItem(string Name, Func<Task> Execute, string? Shortcut = null)
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Shortcut) ? Name : $"{Name}    {Shortcut}";
}
