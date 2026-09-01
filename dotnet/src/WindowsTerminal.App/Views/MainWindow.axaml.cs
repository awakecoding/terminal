using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Terminal.Control;
using Microsoft.Terminal.Settings;
using WindowsTerminal.Actions;
using WindowsTerminal.Panes;
using WindowsTerminal.Routing;
using WindowsTerminal.Settings;

namespace WindowsTerminal.Views;

public partial class MainWindow : Window, ITerminalWindowActivationTarget
{
    private readonly AppSettings _settings;
    private readonly ActionDispatcher _actionDispatcher = new();
    private readonly List<TerminalTab> _tabs = [];
    private readonly List<PaletteItem> _paletteItems = [];
    private uint _nextPaneId;
    private TerminalTab? _activeTab;
    private ActionDispatchResult? _lastDispatchResult;
    private ProfileSettings? _initialProfile;
    private readonly TerminalWindowActivation? _initialActivation;
    private readonly Action<TerminalWindowActivation>? _newWindowRequested;
    private readonly TaskCompletionSource<TerminalWindowActivationResult> _initialActivationCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MainWindow() : this(0, string.Empty, null)
    {
    }

    public MainWindow(
        int windowId,
        string windowName,
        TerminalWindowActivation? initialActivation,
        Action<TerminalWindowActivation>? newWindowRequested = null)
    {
        WindowId = windowId;
        WindowName = windowName;
        _initialActivation = initialActivation;
        _newWindowRequested = newWindowRequested;
        InitializeComponent();
        _settings = SettingsService.Load();
        Width = Math.Max(640, _settings.InitialCols * 8);
        Height = Math.Max(400, _settings.InitialRows * 16 + 80);
        Opened += OnOpened;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
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
                _initialActivationCompletion.SetResult(
                    await ActivateAsync(_initialActivation).ConfigureAwait(true));
            }
            else
            {
                await CreateTabAsync(_initialProfile ?? _settings.GetDefaultProfile()).ConfigureAwait(true);
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

    private async void NewTab_OnClick(object? sender, RoutedEventArgs e) =>
        await CreateTabAsync(_settings.GetDefaultProfile()).ConfigureAwait(true);

    private void Menu_OnClick(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            ItemsSource = BuildProfileMenu(),
        };
        menu.Open(sender as Control);
    }

    private List<MenuItem> BuildProfileMenu()
    {
        var items = _settings.Profiles
            .Where(static profile => !profile.Hidden)
            .Select(profile => new MenuItem
            {
                Header = profile.Name,
                Command = new RelayCommand(() => _ = CreateTabAsync(profile)),
            })
            .ToList();

        items.Add(new MenuItem { Header = "-" });
        items.Add(new MenuItem
        {
            Header = "Split pane",
            Command = new RelayCommand(() => _ = SplitActivePaneAsync(PaneSplitOrientation.Vertical)),
        });
        items.Add(new MenuItem
        {
            Header = "Settings",
            Command = new RelayCommand(() => OpenSettings()),
        });
        return items;
    }

    private async Task CreateTabAsync(ProfileSettings profile)
    {
        var pane = CreatePane(profile);
        var tab = new TerminalTab(pane);
        _tabs.Add(tab);
        ActivateTab(tab);
        RebuildTabs();

        var (columns, rows) = InitialTerminalSize();
        await pane.Control.StartAsync(profile, columns, rows).ConfigureAwait(true);
        pane.Control.Focus();
    }

    private TerminalPane CreatePane(ProfileSettings profile)
    {
        var control = new TermControl();
        control.Cursor = new Cursor(StandardCursorType.Ibeam);
        var pane = new TerminalPane(_nextPaneId++, profile, control);
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

            if (ReferenceEquals(tab.Panes.ActiveContent, pane))
            {
                tab.Title = pane.Title;
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
        await newPane.Control.StartAsync(newPane.Profile, columns / 2, rows).ConfigureAwait(true);
        newPane.Control.Focus();
    }

    private void ActivateTab(TerminalTab tab)
    {
        if (tab.IsClosing)
        {
            return;
        }

        _activeTab = tab;
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
        if (tab.IsClosing || !tab.Panes.Close(pane))
        {
            return;
        }

        await pane.Control.CloseAsync().ConfigureAwait(true);
        if (tab.Panes.Count == 0)
        {
            await CloseTabAsync(tab).ConfigureAwait(true);
            return;
        }

        SynchronizeTitle(tab);
        if (ReferenceEquals(_activeTab, tab))
        {
            var activePane = tab.Panes.ActiveContent!;
            RebuildTerminalHost();
            activePane.Control.Focus();
        }
    }

    private async Task CloseTabAsync(TerminalTab tab)
    {
        if (tab.IsClosing)
        {
            return;
        }

        tab.IsClosing = true;
        var wasActive = ReferenceEquals(_activeTab, tab);
        DetachPaneControls(tab);
        _tabs.Remove(tab);
        if (wasActive)
        {
            _activeTab = null;
            TerminalHost.Children.Clear();
            var replacement = _tabs.LastOrDefault(static candidate => !candidate.IsClosing);
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
            var button = new Button
            {
                Classes = { "tab" },
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = tab.Title,
                            MaxWidth = 180,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        CreateCloseButton(tab),
                    },
                },
                Tag = tab,
            };
            if (ReferenceEquals(tab, _activeTab))
            {
                button.Classes.Add("active");
            }

            button.Click += (_, _) => ActivateTab(tab);
            TabStrip.Children.Add(button);
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
        Register(ShortcutAction.PasteText, ActionScope.Control, _ => ActiveControl is not null,
            async _ => await ActiveControl!.PasteAsync().ConfigureAwait(true));
        Register(ShortcutAction.SendInput, ActionScope.Control, action => ActiveControl is not null && action.Args is SendInputArgs,
            action =>
            {
                ActiveControl!.WriteInput(((SendInputArgs)action.Args!).Input);
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
            async _ => await CreateTabAsync(_activeTab!.Panes.ActiveContent!.Profile).ConfigureAwait(true));
        Register(ShortcutAction.CloseTab, ActionScope.Tab, action => ResolveTab((action.Args as CloseTabArgs)?.Index) is not null,
            async action => await CloseTabAsync(ResolveTab((action.Args as CloseTabArgs)?.Index)!).ConfigureAwait(true));
        Register(ShortcutAction.NextTab, ActionScope.Tab, _ => _tabs.Count > 1, _ =>
        {
            ActivateRelativeTab(1);
            return Task.CompletedTask;
        });
        Register(ShortcutAction.PrevTab, ActionScope.Tab, _ => _tabs.Count > 1, _ =>
        {
            ActivateRelativeTab(-1);
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
            action => ResolveTab((action.Args as CloseTabsAfterArgs)?.Index) is { } tab && _tabs.IndexOf(tab) < _tabs.Count - 1,
            async action => await CloseTabsAfterAsync((action.Args as CloseTabsAfterArgs)?.Index).ConfigureAwait(true));

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
            var profile = _settings.Profiles.FirstOrDefault(profile =>
                       profile.Name.Equals(terminal.Profile, StringComparison.OrdinalIgnoreCase) ||
                       profile.Guid?.ToString().Equals(
                           terminal.Profile.Trim('{', '}'),
                           StringComparison.OrdinalIgnoreCase) == true)
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

    private void ActivateRelativeTab(int delta)
    {
        if (_activeTab is null || _tabs.Count == 0)
        {
            return;
        }

        var index = (_tabs.IndexOf(_activeTab) + delta + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[index]);
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
        var keepIndex = keep is null ? -1 : _tabs.IndexOf(keep);
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
            _tabs.Remove(sourceTab);
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
        CommandPalette.IsVisible = true;
        CommandPaletteQuery.Text = string.Empty;
        RefreshCommandPalette();
        CommandPaletteQuery.Focus();
    }

    private void CloseCommandPalette()
    {
        CommandPalette.IsVisible = false;
        _activeTab?.Panes.ActiveContent?.Control.Focus();
    }

    private void RefreshCommandPalette()
    {
        var query = CommandPaletteQuery.Text;
        CommandPaletteList.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _paletteItems
            : _paletteItems
                .Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
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
            e.Handled ||
            !AvaloniaKeyChord.TryCreate(e, out var chord) ||
            _settings.ActionMap.ResolveAction(chord) is not { } action)
        {
            return;
        }

        // Claim executable bindings before an asynchronous clipboard/process action yields,
        // so the terminal control cannot also translate the same chord to VT input.
        e.Handled = true;
        var result = await DispatchActionAsync(action).ConfigureAwait(true);
        if (result.Status == ActionDispatchStatus.Disabled)
        {
            e.Handled = false;
        }
    }

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

    private TerminalTab? FindTab(TerminalPane pane) =>
        _tabs.FirstOrDefault(tab => !tab.IsClosing && tab.Panes.Leaves().Contains(pane));

    private void SynchronizeTitle(TerminalTab tab)
    {
        if (tab.Panes.ActiveContent is { } activePane)
        {
            tab.Title = activePane.Title;
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

internal sealed class TerminalPane(uint id, ProfileSettings profile, TermControl control)
{
    public uint Id { get; } = id;
    public ProfileSettings Profile { get; } = profile;
    public TermControl Control { get; } = control;
    public string Title { get; set; } = string.IsNullOrWhiteSpace(profile.TabTitle) ? profile.Name : profile.TabTitle;
}

internal sealed class TerminalTab(TerminalPane initialPane)
{
    public PaneTree<TerminalPane> Panes { get; } = new(initialPane);
    public string Title { get; set; } = initialPane.Title;
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
