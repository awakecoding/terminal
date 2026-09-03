using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Devolutions.Terminal.Settings.Editor;

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
