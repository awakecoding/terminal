using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Terminal.Control;
using Microsoft.Terminal.Settings;
using WindowsTerminal.Panes;

namespace WindowsTerminal.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<TerminalTab> _tabs = [];
    private TerminalTab? _activeTab;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();
        Width = Math.Max(640, _settings.InitialCols * 8);
        Height = Math.Max(400, _settings.InitialRows * 16 + 80);
        Opened += async (_, _) => await OpenDefaultTabAsync().ConfigureAwait(true);
        KeyDown += OnWindowKeyDown;
    }

    private async Task OpenDefaultTabAsync() =>
        await CreateTabAsync(_settings.GetDefaultProfile()).ConfigureAwait(true);

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
            Header = "Open settings file",
            Command = new RelayCommand(OpenSettings),
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
        var pane = new TerminalPane(profile, control);
        control.TitleChanged += (_, title) =>
        {
            pane.Title = string.IsNullOrWhiteSpace(title) ? profile.Name : title;
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
        control.ProcessExited += async (_, _) =>
        {
            var tab = FindTab(pane);
            if (tab is not null)
            {
                await ClosePaneAsync(tab, pane).ConfigureAwait(true);
            }
        };
        return pane;
    }

    private async Task SplitActivePaneAsync(PaneSplitOrientation orientation)
    {
        var tab = _activeTab;
        var activePane = tab?.Panes.ActiveContent;
        if (tab is null || activePane is null || tab.IsClosing)
        {
            return;
        }

        var newPane = CreatePane(activePane.Profile);
        if (!tab.Panes.SplitActive(newPane, orientation))
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

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var modifiers = e.KeyModifiers;
        if (modifiers == (KeyModifiers.Alt | KeyModifiers.Shift) && e.Key == Key.D)
        {
            await SplitActivePaneAsync(PaneSplitOrientation.Vertical).ConfigureAwait(true);
            e.Handled = true;
            return;
        }

        if (modifiers == KeyModifiers.Alt && TryDirection(e.Key, out var direction) && _activeTab is not null)
        {
            if (_activeTab.Panes.MoveFocus(direction))
            {
                var pane = _activeTab.Panes.ActiveContent!;
                SynchronizeTitle(_activeTab);
                RebuildTerminalHost();
                pane.Control.Focus();
            }

            e.Handled = true;
            return;
        }

        if (!modifiers.HasFlag(KeyModifiers.Control) || !modifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.T:
                await CreateTabAsync(_settings.GetDefaultProfile()).ConfigureAwait(true);
                e.Handled = true;
                break;
            case Key.W when _activeTab?.Panes.ActiveContent is { } pane:
                await ClosePaneAsync(_activeTab, pane).ConfigureAwait(true);
                e.Handled = true;
                break;
            case Key.N:
                new MainWindow().Show();
                e.Handled = true;
                break;
        }
    }

    private static bool TryDirection(Key key, out PaneDirection direction)
    {
        direction = key switch
        {
            Key.Left => PaneDirection.Left,
            Key.Right => PaneDirection.Right,
            Key.Up => PaneDirection.Up,
            Key.Down => PaneDirection.Down,
            _ => default,
        };
        return key is Key.Left or Key.Right or Key.Up or Key.Down;
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private static void OpenSettings()
    {
        SettingsService.Save(SettingsService.Load());
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = SettingsService.SettingsPath,
            UseShellExecute = true,
        });
    }

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

internal sealed class TerminalPane(ProfileSettings profile, TermControl control)
{
    public ProfileSettings Profile { get; } = profile;
    public TermControl Control { get; } = control;
    public string Title { get; set; } = profile.Name;
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
