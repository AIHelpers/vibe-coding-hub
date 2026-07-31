using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    // Helper to access ChatViewModel from the data context
    private ChatViewModel? GetChatViewModel()
    {
        if (DataContext is MainViewModel mainVm && mainVm.CurrentViewModel is ChatViewModel chatVm)
            return chatVm;
        return null;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && sender is TextBox)
        {
            var chatVm = GetChatViewModel();
            if (chatVm != null && chatVm.SendCommand.CanExecute(null))
            {
                chatVm.SendCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            var chatVm = GetChatViewModel();
            if (chatVm != null && chatVm.IsCancellable)
            {
                chatVm.CancelCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Down)
        {
            var chatVm = GetChatViewModel();
            if (chatVm?.ShowMentionPopup == true || chatVm?.ShowSlashCommands == true)
            {
                e.Handled = true;
            }
        }
    }

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            var chatVm = GetChatViewModel();
            if (chatVm != null)
            {
                chatVm.OnTextChanged(textBox.Text ?? string.Empty, textBox.CaretIndex);
            }
        }
    }

    private void OnMentionSelected(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is MentionItem mentionItem)
        {
            var chatVm = GetChatViewModel();
            if (chatVm != null)
            {
                chatVm.InsertMention(mentionItem);
                e.Handled = true;
            }
        }
    }

    private void OnSlashCommandSelected(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is SlashCommandItem slashCmd)
        {
            var chatVm = GetChatViewModel();
            if (chatVm != null)
            {
                chatVm.InsertSlashCommand(slashCmd);
                e.Handled = true;
            }
        }
    }

    private void OnToolCardExpandToggle(object? sender, PointerPressedEventArgs e)
    {
        // Find the ToolCallCardViewModel from the data context
        if (sender is Button button)
        {
            // Navigate up the visual tree to find the parent Border with the DataContext
            var parent = button.Parent;
            while (parent != null)
            {
                if (parent is Border border && border.DataContext is ToolCallCardViewModel card)
                {
                    card.ToggleExpand();
                    e.Handled = true;
                    return;
                }
                parent = parent.Parent;
            }
        }
    }
}