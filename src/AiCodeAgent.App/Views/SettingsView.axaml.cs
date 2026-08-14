using Avalonia.Controls;
using Avalonia.Input;
using AiCodeAgent.App.ViewModels;

namespace AiCodeAgent.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// When the user clicks the model dropdown to choose a model, ensure the
    /// model list has been loaded for the current provider. The list is loaded
    /// lazily (only once per provider) so opening the Settings page does not
    /// trigger a network request every time.
    /// </summary>
    private void ModelComboBox_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            _ = vm.EnsureModelsLoadedCommand.ExecuteAsync(null);
        }
    }
}
