using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WindowsTerminal.Settings.Controls;

public sealed class SettingsRow : UserControl
{
    private readonly TextBlock _header;
    private readonly TextBlock _description;
    private readonly ContentControl _value;

    public static readonly StyledProperty<object?> ValueProperty =
        AvaloniaProperty.Register<SettingsRow, object?>(nameof(Value));

    public SettingsRow()
    {
        _header = new TextBlock
        {
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _description = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _value = new ContentControl
        {
            Width = 248,
            MinWidth = 248,
            MaxWidth = 248,
            Margin = new Thickness(24, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var labels = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _header, _description },
        };
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MinHeight = 44,
            Margin = new Thickness(16, 6),
        };
        layout.Children.Add(labels);
        Grid.SetColumn(_value, 1);
        layout.Children.Add(_value);
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = layout,
        };
        border.Classes.Add("settings-row");
        base.Content = border;
    }

    public string Header
    {
        get => _header.Text ?? string.Empty;
        set
        {
            _header.Text = value;
            AutomationProperties.SetName(this, value);
            ApplyValueLabel();
        }
    }

    public string Description
    {
        get => _description.Text ?? string.Empty;
        set
        {
            _description.Text = value;
            _description.IsVisible = !string.IsNullOrWhiteSpace(value);
        }
    }

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            _value.Content = change.NewValue;
            ApplyValueLabel();
        }
    }

    private void ApplyValueLabel()
    {
        if (Value is Control control)
        {
            AutomationProperties.SetLabeledBy(control, _header);
            AutomationProperties.SetName(control, Header);
        }
    }
}
