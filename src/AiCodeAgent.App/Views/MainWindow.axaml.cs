using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using AiCodeAgent.App.ViewModels;
using ReactiveUI;

namespace AiCodeAgent.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            if (DataContext is MainViewModel mainVm && mainVm.CurrentViewModel is ChatViewModel chatVm)
            {
                if (chatVm.SendCommand.CanExecute(null))
                {
                    chatVm.SendCommand.Execute(null);
                }
            }
        }
    }
}