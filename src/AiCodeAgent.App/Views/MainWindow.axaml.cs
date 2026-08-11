using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.ReactiveUI;
using AiCodeAgent.App.CommandPalette;
using AiCodeAgent.App.ViewModels;
using ReactiveUI;
using System;
using System.Linq;
using System.Threading.Tasks;

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

    // Helper to access the command palette view model
    private CommandPaletteViewModel? GetCommandPalette()
    {
        if (DataContext is MainViewModel mainVm)
            return mainVm.CommandPalette;
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

    private async void CopyMessageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is ChatMessage chatMessage)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null && !string.IsNullOrEmpty(chatMessage.Content))
            {
                await clipboard.SetTextAsync(chatMessage.Content);
            }
        }
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        var palette = GetCommandPalette();
        if (palette == null)
            return;

        switch (e.Key)
        {
            case Key.Down:
                palette.SelectNext();
                e.Handled = true;
                break;

            case Key.Up:
                palette.SelectPrevious();
                e.Handled = true;
                break;

            case Key.Enter:
                palette.ExecuteSelected();
                e.Handled = true;
                break;

            case Key.Escape:
                palette.Close();
                e.Handled = true;
                break;
        }
        _ = HandlePaletteFocusAsync(palette);
    }

    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        var palette = GetCommandPalette();
        if (palette == null)
            return;

        // Only dismiss when clicking the backdrop itself (not the inner card)
        if (e.Source is Border border && ReferenceEquals(border, sender))
        {
            palette.Close();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Opens a folder picker to change the current working directory.
    /// </summary>
    private async void OnWorkingDirectoryPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel mainVm)
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null)
                return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Working Directory",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                var path = folders[0].TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                {
                    await mainVm.SetWorkingDirectoryAsync(path);
                }
            }
        }

        e.Handled = true;
    }

    /// <summary>
    /// Accepts a drag if the payload contains at least one `.agentsession` file.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p));
            if (files != null && files.Any(p => p != null && p.EndsWith(".agentsession", StringComparison.OrdinalIgnoreCase)))
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }

        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Imports the first dropped `.agentsession` file into a new session.
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel mainVm && e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p));
            var bundlePath = files?.FirstOrDefault(p => p != null && p.EndsWith(".agentsession", StringComparison.OrdinalIgnoreCase));
            if (bundlePath != null)
            {
                e.Handled = true;
                try
                {
                    await mainVm.SessionManager.ImportSessionFromPathAsync(bundlePath);
                    mainVm.ToggleSessionHistoryCommand.Execute(null);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Session import via drop failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Keeps keyboard focus on the palette search box while the palette is open.
    /// Called after each key event and when the palette opens/closes.
    /// </summary>
    private async Task HandlePaletteFocusAsync(CommandPaletteViewModel? palette = null)
    {
        palette ??= GetCommandPalette();
        if (palette == null)
            return;

        if (palette.IsOpen)
        {
            // Defer so the palette is visible and layout has run.
            await Task.Delay(1);
            PaletteSearchBox?.Focus();
        }
        else
        {
            // Return focus to the main input box.
            InputBox?.Focus();
        }
    }
}
