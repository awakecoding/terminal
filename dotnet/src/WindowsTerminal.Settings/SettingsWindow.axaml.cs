using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WindowsTerminal.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
        : this(new SettingsEditorViewModel())
    {
    }

    public SettingsWindow(SettingsEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
    }
}
