using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Terminal.Control;
using Microsoft.Terminal.Core;
using Microsoft.Terminal.Settings;
using WindowsTerminal.Actions;
using WindowsTerminal.Models;
using WindowsTerminal.Panes;
using WindowsTerminal.Routing;
using WindowsTerminal.Settings;

namespace WindowsTerminal.Views;

public partial class MainWindow : Window, ITerminalWindowActivationTarget
{
    private readonly AppSettings _settings;
    private readonly ApplicationStateStore _stateStore;
    private readonly ActionDispatcher _actionDispatcher = new();
    private readonly TabCollection<TerminalTab, TabLayoutDescriptor> _tabCollection = new();
    private readonly List<PaletteItem> _paletteItems = [];
    private IReadOnlyList<TerminalTab> _tabs => _tabCollection.Items;
    private uint _nextPaneId;
    private TerminalTab? _activeTab;
    private TerminalTab? _draggedTab;
    private Point _dragStart;
    private bool _tabSearchMode;
    private bool _layoutPersisted;
    private ActionDispatchResult? _lastDispatchResult;
    private ProfileSettings? _initialProfile;
    private readonly TerminalWindowActivation? _initialActivation;
    private readonly Action<TerminalWindowActivation>? _newWindowRequested;
    private readonly Action<TabTearOffRequest>? _tabTearOffRequested;
    private readonly TaskCompletionSource<TerminalWindowActivationResult> _initialActivationCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _notificationTimer;

    public MainWindow() : this(0, string.Empty, null)
    {
    }

    public MainWindow(
        int windowId,
        string windowName,
        TerminalWindowActivation? initialActivation,
        Action<TerminalWindowActivation>? newWindowRequested = null,
        Action<TabTearOffRequest>? tabTearOffRequested = null)
    {
        WindowId = windowId;
        WindowName = windowName;
        _initialActivation = initialActivation;
        _newWindowRequested = newWindowRequested;
        _tabTearOffRequested = tabTearOffRequested;
        InitializeComponent();
        _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            NotificationToast.IsVisible = false;
        };
        _settings = SettingsService.Load();
        _stateStore = SettingsService.LoadApplicationState();
        Width = Math.Max(640, _settings.InitialCols * 8);
        Height = Math.Max(400, _settings.InitialRows * 16 + 80);
        Opened += OnOpened;
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
    public string WindowName { get; }
    public Task<TerminalWindowActivationResult> InitialActivation => _initialActivationCompletion.Task;
    public IReadOnlyList<TerminalTab> Tabs => _tabCollection.Items;
    public TerminalTab? ActiveTab => _activeTab;
    public string? LastPersistenceError { get; private set; }
    public event Action<TabTearOffRequest>? TabTearOffRequested;

    public async ValueTask<TerminalWindowActivationResult> ActivateAsync(
        TerminalWindowActivation activation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyLaunchOptions(activation);
        var results = new List<ActionDispatchResult>(activation.Actions.Count);
        foreach (var action in activation.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await DispatchActionAsync(action).ConfigureAwait(true));
        }

        Show();
        Activate();
        var failure = results.FirstOrDefault(result => result.Status != ActionDispatchStatus.Executed);
        return failure is null
            ? new(true, "Activation completed.", results)
            : new(false, failure.Message ?? $"Action '{failure.Action}' failed.", results);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            if (_initialActivation is not null)
            {
                if (IsDefaultStartupActivation(_initialActivation) &&
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

        if (activation.Columns is { } columns)
        {
            Width = Math.Max(320, columns * 8);
        }

        if (activation.Rows is { } rows)
        {
            Height = Math.Max(240, rows * 16 + 80);
        }

        if (activation.LaunchMode.HasFlag(TerminalWindowLaunchMode.Fullscreen))
        {
            WindowState = WindowState.FullScreen;
        }
        else if (activation.LaunchMode.HasFlag(TerminalWindowLaunchMode.Maximized))
        {
            WindowState = WindowState.Maximized;
        }

        TitleBar.IsVisible = !activation.LaunchMode.HasFlag(TerminalWindowLaunchMode.Focus);
    }

    private static bool IsDefaultStartupActivation(TerminalWindowActivation activation) =>
        activation.Actions.Count == 0 ||
        (activation.Actions.Count == 1 &&
         activation.Actions[0] is
         {
             Action: ShortcutAction.NewTab,
             Args: NewTabArgs { ContentArgs: NewTerminalArgs terminal },
         } &&
         terminal == new NewTerminalArgs());

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
        var items = NewTabMenuResolver.Resolve(_settings)
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
        };
        AutomationProperties.SetName(splitPane, "Split pane");
        AutomationProperties.SetAutomationId(splitPane, "SplitPaneMenuItem");
        items.Add(splitPane);
        var settings = new MenuItem
        {
            Header = "Settings",
            Command = new RelayCommand(() => OpenSettings()),
        };
        AutomationProperties.SetName(settings, "Settings");
        AutomationProperties.SetAutomationId(settings, "SettingsMenuItem");
        items.Add(settings);
        return items;
    }

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
        var pane = CreatePane(profile);
        var tab = new TerminalTab(pane);
        _tabCollection.Add(tab);
        ActivateTab(tab);
        RebuildTabs();

        var (columns, rows) = InitialTerminalSize();
        try
        {
            await pane.Control.StartAsync(profile, columns, rows).ConfigureAwait(true);
            pane.Control.Focus();
        }
        catch (Exception ex) when (IsLaunchFailure(ex))
        {
            await RemoveFailedPaneAsync(tab, pane).ConfigureAwait(true);
            await ShowLaunchErrorAsync(profile, ex).ConfigureAwait(true);
        }
    }

    private TerminalPane CreatePane(
        ProfileSettings profile,
        TerminalSessionDescriptor? session = null,
        PanePresentationState? presentation = null)
    {
        var control = new TermControl();
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
            pane.Title = profile.SuppressApplicationTitle || string.IsNullOrWhiteSpace(title)
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
                Title = tab.Title;
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

        var newPane = CreatePane(profile ?? activePane.Profile);
        var normalizedSize = Math.Clamp(splitSize, 0.1, 0.9);
        var firstPaneRatio = newPaneFirst ? normalizedSize : 1 - normalizedSize;
        if (!tab.Panes.SplitActive(newPane, orientation, firstPaneRatio, newPaneFirst))
        {
            return;
        }

        RebuildTerminalHost();
        var (columns, rows) = InitialTerminalSize();
        try
        {
            await newPane.Control.StartAsync(newPane.Profile, columns / 2, rows).ConfigureAwait(true);
            newPane.Control.Focus();
        }
        catch (Exception ex) when (IsLaunchFailure(ex))
        {
            await RemoveFailedPaneAsync(tab, newPane).ConfigureAwait(true);
            await ShowLaunchErrorAsync(newPane.Profile, ex).ConfigureAwait(true);
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
                TryPersistLayout(finalLayout);
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
        var active = ReferenceEquals(tab.Panes.ActiveContent, pane);
        var border = new Border
        {
            BorderBrush = active ? new SolidColorBrush(Color.Parse("#3A96DD")) : Brushes.Transparent,
            BorderThickness = new Thickness(active ? 1 : 0),
            Child = pane.Control,
            MinWidth = 80,
            MinHeight = 40,
        };
        border.PointerPressed += (_, _) => ActivatePane(tab, pane);
        return border;
    }

    private void RebuildTabs()
    {
        TabStrip.Children.Clear();
        foreach (var tab in _tabs)
        {
            var presentation = tab.Panes.ActiveContent?.Presentation ?? new PanePresentationState();
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
            };
            if (!string.IsNullOrWhiteSpace(presentation.Icon))
            {
                content.Children.Add(new TextBlock
                {
                    Text = presentation.Icon,
                    MaxWidth = 18,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            if (presentation.IsAdministrator)
            {
                content.Children.Add(new TextBlock { Text = "◆" });
            }

            content.Children.Add(new TextBlock
            {
                Text = tab.Title,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = presentation.HasUnseenActivity ? FontWeight.Bold : FontWeight.Normal,
            });
            if (presentation.IsReadOnly)
            {
                content.Children.Add(new TextBlock { Text = "🔒" });
            }

            if (presentation.HasBellIndicator)
            {
                content.Children.Add(new TextBlock { Text = "●" });
            }

            if (presentation.ProgressState != TerminalProgressState.None)
            {
                content.Children.Add(new ProgressBar
                {
                    Width = 34,
                    Height = 3,
                    IsIndeterminate = presentation.ProgressState == TerminalProgressState.Indeterminate,
                    Value = presentation.Progress * 100,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            content.Children.Add(CreateCloseButton(tab));
            var button = new Button
            {
                Classes = { "tab" },
                Content = content,
                Tag = tab,
                ContextMenu = CreateTabContextMenu(tab),
            };
            if (TryParseColor(tab.Color ?? presentation.Color, out var tabColor))
            {
                button.Background = new SolidColorBrush(tabColor);
            }
            if (ReferenceEquals(tab, _activeTab))
            {
                button.Classes.Add("active");
            }

            button.Click += (_, _) => ActivateTab(tab);
            button.PointerPressed += (_, e) => BeginTabDrag(tab, button, e);
            button.PointerReleased += (_, e) => EndTabDrag(tab, button, e);
            TabStrip.Children.Add(button);
        }
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
                var activePane = _activeTab!.Panes.ActiveContent!;
                _activeTab.BroadcastInput.WriteInput(
                    activePane,
                    _activeTab.Panes.Leaves(),
                    ((SendInputArgs)action.Args!).Input);
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
        Register(ShortcutAction.SetTabColor, ActionScope.Tab,
            action => _activeTab is not null && action.Args is SetTabColorArgs,
            action =>
            {
                _activeTab!.Color = ((SetTabColorArgs)action.Args!).TabColor;
                RebuildTabs();
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

        Register(ShortcutAction.ToggleCommandPalette, ActionScope.Window, _ => true, _ =>
        {
            ShowCommandPalette();
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
            TitleBar.IsVisible = !TitleBar.IsVisible;
            return Task.CompletedTask;
        });
        Register(ShortcutAction.SetFocusMode, ActionScope.Window, action => action.Args is SetFocusModeArgs, action =>
        {
            TitleBar.IsVisible = !((SetFocusModeArgs)action.Args!).IsFocusMode;
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

        var selected = terminal.ProfileIndex is { } selectedIndex &&
                       selectedIndex >= 0 &&
                       selectedIndex < _settings.Profiles.Count
            ? _settings.Profiles[selectedIndex]
            : _settings.GetDefaultProfile();
        return selected.WithOverrides(terminal);
    }

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
            _paletteItems.Add(new PaletteItem(command.Name, async () =>
            {
                await DispatchActionAsync(action).ConfigureAwait(true);
            }));
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

    private void ShowCommandPalette()
    {
        _tabSearchMode = false;
        CommandPalette.IsVisible = true;
        CommandPaletteQuery.Text = string.Empty;
        RefreshCommandPalette();
        CommandPaletteQuery.Focus();
    }

    private void ShowTabSearch()
    {
        _tabSearchMode = true;
        CommandPalette.IsVisible = true;
        CommandPaletteQuery.Text = string.Empty;
        CommandPaletteQuery.Watermark = "Search tabs";
        RefreshCommandPalette();
        CommandPaletteQuery.Focus();
    }

    private void CloseCommandPalette()
    {
        CommandPalette.IsVisible = false;
        CommandPaletteQuery.Watermark = "Search actions";
        _activeTab?.Panes.ActiveContent?.Control.Focus();
    }

    private void RefreshCommandPalette()
    {
        var query = CommandPaletteQuery.Text;
        CommandPaletteList.ItemsSource = _tabSearchMode
            ? _tabCollection.Search(query, static tab => tab.Title)
                .Select(tab => new PaletteItem(tab.Title, () =>
                {
                    ActivateTab(tab);
                    return Task.CompletedTask;
                }))
                .ToArray()
            : string.IsNullOrWhiteSpace(query)
                ? _paletteItems
                : FuzzyMatcher.Rank(_paletteItems, query, static item => item.Name);
        CommandPaletteList.SelectedIndex = CommandPaletteList.ItemCount > 0 ? 0 : -1;
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
        if ((FindBar.IsVisible && FindBox.IsKeyboardFocusWithin) ||
            (CommandPalette.IsVisible && CommandPaletteQuery.IsKeyboardFocusWithin) ||
            e.Handled)
        {
            return;
        }

        if (AvaloniaKeyChord.TryCreate(e, out var chord) &&
            _settings.ActionMap.ResolveAction(chord) is { } action)
        {
            // Claim executable bindings before an asynchronous clipboard/process action yields,
            // so the terminal control cannot also translate the same chord to VT input.
            e.Handled = true;
            var result = await DispatchActionAsync(action).ConfigureAwait(true);
            if (result.Status == ActionDispatchStatus.Disabled)
            {
                e.Handled = TryRouteCoordinatedKey(e);
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
            activePane.Control.Engine.ApplicationCursorKeys);
        if (input is null)
        {
            return activePane.Presentation.IsReadOnly;
        }

        activeTab.BroadcastInput.WriteInput(activePane, activeTab.Panes.Leaves(), input);
        return true;
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
        var text = clipboard is null ? null : await clipboard.GetTextAsync().ConfigureAwait(true);
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

    private void OpenSettings(SettingsTarget target = SettingsTarget.SettingsUI)
    {
        switch (target)
        {
            case SettingsTarget.SettingsUI:
            case SettingsTarget.AllFiles:
                SettingsViewFactory.CreateWindow().Show(this);
                break;
            case SettingsTarget.SettingsFile:
                SettingsService.Save(SettingsService.Load());
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
                OpenWithShell(directory);
                break;
        }
    }

    private static void OpenWithShell(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });

    private (int Columns, int Rows) InitialTerminalSize()
    {
        var columns = Math.Max(20, (int)((TerminalHost.Bounds.Width - 16) / 8));
        var rows = Math.Max(10, (int)((TerminalHost.Bounds.Height - 16) / 16));
        return double.IsNaN(TerminalHost.Bounds.Width) || TerminalHost.Bounds.Width <= 0
            ? (_settings.InitialCols, _settings.InitialRows)
            : (columns, rows);
    }

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
        var windowState = TerminalLayoutStateStore.ReadWindowState(_stateStore, WindowId);
        var layout = TerminalLayoutStateStore.ReadWindow(_stateStore, WindowId);
        if (layout is null || layout.Tabs.Count == 0)
        {
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

        return true;
    }

    private void ApplyPersistedWindowState(WindowLayoutState? state)
    {
        if (state?.InitialPosition?.Split(',') is [var xText, var yText] &&
            int.TryParse(xText, out var x) &&
            int.TryParse(yText, out var y))
        {
            Position = new PixelPoint(x, y);
        }

        if (state?.InitialSize is { Width: > 0, Height: > 0 } size)
        {
            Width = size.Width;
            Height = size.Height;
        }

        WindowState = state?.LaunchMode switch
        {
            LaunchMode.Maximized or LaunchMode.MaximizedFocus => WindowState.Maximized,
            LaunchMode.Fullscreen => WindowState.FullScreen,
            _ => WindowState,
        };
        if (state?.LaunchMode is LaunchMode.Focus or LaunchMode.MaximizedFocus)
        {
            TitleBar.IsVisible = false;
        }
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
            Title = descriptor.Title,
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
            Icon = profile.IconResource?.ToString(),
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
        TerminalLayoutStateStore.SaveWindow(
            _stateStore,
            WindowId,
            layout,
            $"{Position.X},{Position.Y}",
            new WindowSizeState { Width = Width, Height = Height },
            WindowState switch
            {
                WindowState.Maximized => LaunchMode.Maximized,
                WindowState.FullScreen => LaunchMode.Fullscreen,
                _ => LaunchMode.Default,
            });
        _layoutPersisted = true;
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
                Title = "Windows Terminal";
                RebuildTabs();
            }
        }
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
                        Text = $"Windows Terminal could not launch '{profile.Name}'.",
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
        NotificationTitle.Text = string.IsNullOrWhiteSpace(notification.Title)
            ? "Windows Terminal"
            : notification.Title;
        NotificationBody.Text = notification.Body;
        NotificationToast.IsVisible = true;
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private static bool IsLaunchFailure(Exception error) =>
        error is
            System.ComponentModel.Win32Exception or
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            PlatformNotSupportedException or
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
            Title = tab.Title;
        }
    }

    private static void DetachPaneControls(TerminalTab tab)
    {
        foreach (var pane in tab.Panes.Leaves())
        {
            if (pane.Control.Parent is Decorator decorator)
            {
                decorator.Child = null;
            }
            else if (pane.Control.Parent is Panel panel)
            {
                panel.Children.Remove(pane.Control);
            }
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (!_layoutPersisted && _tabs.Count > 0)
        {
            TryPersistLayout(CaptureLayout());
        }

        foreach (var tab in _tabs.ToArray())
        {
            foreach (var pane in tab.Panes.Leaves())
            {
                await pane.Control.CloseAsync().ConfigureAwait(true);
            }
        }

        base.OnClosed(e);
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

internal sealed record PaletteItem(string Name, Func<Task> Execute)
{
    public override string ToString() => Name;
}
