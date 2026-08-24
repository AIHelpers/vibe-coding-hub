using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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
        DataContextChanged += OnDataContextChanged;
        KeyDown += OnWindowKeyDown;
    }

    /// <summary>
    /// Wires up editor services that require access to the host window, such as
    /// the Save-As file picker and the VS Code-style unsaved-changes prompt.
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel mainVm)
        {
            var editor = mainVm.EditorPane;

            // Save As: open a file picker seeded with the tab's current path.
            editor.SaveAsPathPicker = async () =>
            {
                var topLevel = GetTopLevel(this);
                if (topLevel == null)
                    return null;

                var currentFile = editor.ActiveTab?.FilePath;
                IStorageFolder? suggestedStart = null;
                if (!string.IsNullOrEmpty(currentFile))
                {
                    try
                    {
                        suggestedStart = await topLevel.StorageProvider
                            .TryGetFolderFromPathAsync(Path.GetDirectoryName(currentFile)!);
                    }
                    catch { /* ignore seeding errors */ }
                }

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save File As",
                    SuggestedFileName = currentFile != null ? Path.GetFileName(currentFile) : "untitled.txt",
                    SuggestedStartLocation = suggestedStart,
                    DefaultExtension = currentFile != null ? Path.GetExtension(currentFile).TrimStart('.') : "txt",
                    ShowOverwritePrompt = true
                });

                return file?.TryGetLocalPath();
            };

            // Close with unsaved changes: prompt Save / Discard / Cancel (VS Code style).
            editor.ConfirmSaveChangesAsync = async tab =>
            {
                var topLevel = GetTopLevel(this);
                if (topLevel == null)
                    return true;

                var dialog = new Window
                {
                    Width = 420,
                    Height = 160,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    SystemDecorations = SystemDecorations.BorderOnly,
                    Title = "Unsaved Changes"
                };

                var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
                panel.Children.Add(new TextBlock
                {
                    Text = $"Do you want to save the changes you made to {System.IO.Path.GetFileName(tab.FilePath)}?",
                    TextWrapping = TextWrapping.Wrap
                });

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right
                };

                var saveButton = new Button { Content = "Save", IsDefault = true };
                var discardButton = new Button { Content = "Don't Save" };
                var cancelButton = new Button { Content = "Cancel", IsCancel = true };

                buttons.Children.Add(saveButton);
                buttons.Children.Add(discardButton);
                buttons.Children.Add(cancelButton);
                panel.Children.Add(buttons);
                dialog.Content = panel;

                var result = await dialog.ShowDialog<DialogResult>(this);
                switch (result)
                {
                    case DialogResult.Save:
                        return await tab.SaveAsync();
                    case DialogResult.Discard:
                        return true;
                    default:
                        return false;
                }
            };
        }
    }

    /// <summary>
    /// Global editor shortcuts: Save, Save As, Close Tab, Next/Previous Tab.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is MainViewModel mainVm)
        {
            var editor = mainVm.EditorPane;
            switch (e.Key)
            {
                case Key.S when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    _ = editor.SaveActiveAsAsync();
                    e.Handled = true;
                    break;
                case Key.S:
                    _ = editor.SaveActiveAsync();
                    e.Handled = true;
                    break;
                case Key.W:
                    _ = editor.CloseActiveTabAsync();
                    e.Handled = true;
                    break;
                case Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    editor.PreviousTab();
                    e.Handled = true;
                    break;
                case Key.Tab:
                    editor.NextTab();
                    e.Handled = true;
                    break;
            }
        }
    }

    /// <summary>Outcome of the unsaved-changes dialog.</summary>
    private enum DialogResult { Save, Discard, Cancel }

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

    private void OnFileExplorerItemDoubleTapped(object? sender, RoutedEventArgs e)
    {
        // Resolve the double-clicked item from the event source rather than
        // relying on TreeView.SelectedItem, which may not be set yet when the
        // DoubleTapped event fires, leaving the editor stuck on "No file open".
        var item = ResolveFileExplorerItem(e.Source);
        if (item != null &&
            !item.IsDirectory &&
            DataContext is MainViewModel mainVm)
        {
            // Open the file in the editor pane (editable by default).
            _ = mainVm.EditorPane.OpenFileAsync(item.FullPath);
            // Collapse the checkpoint browser to give the editor focus.
            mainVm.IsCheckpointBrowserOpen = false;
        }
    }

    /// <summary>
    /// Handles TreeView selection changes so a single click on a file in the
    /// explorer opens it in the editor pane. Without this handler the editor
    /// can remain stuck on "No file open" because the SelectedItem binding
    /// alone does not reliably push the selection into FileExplorerViewModel
    /// on every selection change (Avalonia TreeView TwoWay binding quirk).
    /// </summary>
    private void OnFileExplorerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is TreeView treeView &&
            treeView.SelectedItem is FileExplorerItem item &&
            !item.IsDirectory &&
            DataContext is MainViewModel mainVm)
        {
            // Open as preview on single-click selection
            _ = mainVm.EditorPane.OpenFileAsync(item.FullPath, isPreview: true);
        }
    }

    /// <summary>
    /// Walks up the visual tree from the event source to find the
    /// <see cref="TreeViewItem"/> that was double-clicked and returns its
    /// <see cref="FileExplorerItem"/> data context (or null if not found).
    /// </summary>
    private static FileExplorerItem? ResolveFileExplorerItem(object? source)
    {
        var current = source as StyledElement;
        while (current != null)
        {
            if (current is TreeViewItem tvi && tvi.DataContext is FileExplorerItem item)
                return item;
            current = current.Parent as StyledElement;
        }
        return null;
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

    private async void CopyMessageButton_ClickAsync(object? sender, RoutedEventArgs e)
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
    private void OnWorkingDirectoryPressedAsync(object? sender, PointerPressedEventArgs e)
    {
        _ = OnWorkingDirectoryPressedCoreAsync(sender, e);
        e.Handled = true;
    }

    private async Task OnWorkingDirectoryPressedCoreAsync(object? sender, PointerPressedEventArgs e)
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
    private async void OnDropAsync(object? sender, DragEventArgs e)
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