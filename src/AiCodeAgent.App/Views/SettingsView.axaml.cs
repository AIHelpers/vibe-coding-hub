using Avalonia.Controls;
using Avalonia.Input;
using AiCodeAgent.App.ViewModels;

namespace AiCodeAgent.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // Esc leaves the settings page (unsaved edits are discarded; press Save first).
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is SettingsViewModel vm && vm.CloseCommand.CanExecute(null))
            {
                vm.CloseCommand.Execute(null);
                e.Handled = true;
            }
        };
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
