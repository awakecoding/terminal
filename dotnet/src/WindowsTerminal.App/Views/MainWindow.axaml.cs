using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Microsoft.Terminal.Control;
using Microsoft.Terminal.Settings;

namespace WindowsTerminal.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<TerminalTab> _tabs = [];
    private TerminalTab? _active;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();
        Width = Math.Max(640, _settings.InitialCols * 8);
        Height = Math.Max(400, _settings.InitialRows * 16 + 80);
        Opened += async (_, _) => await OpenDefaultTabAsync().ConfigureAwait(true);
        KeyDown += OnWindowKeyDown;
    }

    private async Task OpenDefaultTabAsync()
    {
        await CreateTabAsync(_settings.GetDefaultProfile()).ConfigureAwait(true);
    }

    private async void NewTab_OnClick(object? sender, RoutedEventArgs e) =>
        await CreateTabAsync(_settings.GetDefaultProfile()).ConfigureAwait(true);

    private async void Menu_OnClick(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            ItemsSource = BuildProfileMenu(),
        };
        menu.Open(sender as Control);
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private List<MenuItem> BuildProfileMenu()
    {
        var items = _settings.Profiles.Select(profile => new MenuItem
        {
            Header = profile.Name,
            Command = new RelayCommand(() => _ = CreateTabAsync(profile)),
        }).ToList();

        items.Add(new MenuItem { Header = "-" });
        items.Add(new MenuItem
        {
            Header = "Open settings file",
            Command = new RelayCommand(OpenSettings),
        });
        return items;
    }

    private async Task CreateTabAsync(ProfileSettings profile)
    {
        var control = new TermControl();
        var tab = new TerminalTab(profile, control);
        control.TitleChanged += (_, title) =>
        {
            tab.Title = string.IsNullOrWhiteSpace(title) ? profile.Name : title;
            RebuildTabs();
            if (ReferenceEquals(_active, tab))
            {
                Title = tab.Title;
            }
        };
        control.CloseRequested += async (_, _) =>
        {
            await CloseTabAsync(tab).ConfigureAwait(true);
        };

        _tabs.Add(tab);
        Activate(tab);
        RebuildTabs();

        var cols = Math.Max(20, (int)((TerminalHost.Bounds.Width - 16) / 8));
        var rows = Math.Max(10, (int)((TerminalHost.Bounds.Height - 16) / 16));
        if (double.IsNaN(TerminalHost.Bounds.Width) || TerminalHost.Bounds.Width <= 0)
        {
            cols = _settings.InitialCols;
            rows = _settings.InitialRows;
        }

        await control.StartAsync(profile, cols, rows).ConfigureAwait(true);
        control.Focus();
    }

    private void Activate(TerminalTab tab)
    {
        _active = tab;
        TerminalHost.Children.Clear();
        TerminalHost.Children.Add(tab.Control);
        Title = tab.Title;
        RebuildTabs();
        tab.Control.Focus();
    }

    private async Task CloseTabAsync(TerminalTab tab)
    {
        _tabs.Remove(tab);
        await tab.Control.CloseAsync().ConfigureAwait(true);
        if (_tabs.Count == 0)
        {
            Close();
            return;
        }

        Activate(_tabs[^1]);
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
                            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        CreateCloseButton(tab),
                    },
                },
                Tag = tab,
            };
            if (ReferenceEquals(tab, _active))
            {
                button.Classes.Add("active");
            }

            button.Click += (_, _) => Activate(tab);
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
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.T:
                await CreateTabAsync(_settings.GetDefaultProfile()).ConfigureAwait(true);
                e.Handled = true;
                break;
            case Key.W when _active is not null:
                await CloseTabAsync(_active).ConfigureAwait(true);
                e.Handled = true;
                break;
            case Key.N:
                var window = new MainWindow();
                window.Show();
                e.Handled = true;
                break;
        }
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

    protected override async void OnClosed(EventArgs e)
    {
        foreach (var tab in _tabs.ToArray())
        {
            await tab.Control.CloseAsync().ConfigureAwait(true);
        }

        base.OnClosed(e);
    }
}

internal sealed class TerminalTab(ProfileSettings profile, TermControl control)
{
    public ProfileSettings Profile { get; } = profile;
    public TermControl Control { get; } = control;
    public string Title { get; set; } = profile.Name;
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
