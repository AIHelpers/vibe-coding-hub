using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using AiCodeAgent.App.Editor;
using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace AiCodeAgent.App.Views;

public partial class EditorView : UserControl
{
    private readonly SyntaxHighlightingResolver _highlightingResolver = new();
    private readonly DiffOverlayRenderer _diffOverlayRenderer = new();
    private readonly DiagnosticsRenderer _diagnosticsRenderer = new();
    private LspDocumentService? _lspService;
    private EditorPaneViewModel? _viewModel;
    private EditorTabViewModel? _activeTab;
    private TextEditor? _codeEditor;

    public EditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;

        _lspService = (AiCodeAgent.App.App.Services?.GetService(typeof(LspDocumentService))) as LspDocumentService;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Locate the code editor control from the XAML template once the view is in the visual tree
        if (_codeEditor == null)
        {
            _codeEditor = this.FindControl<TextEditor>("CodeEditor");

            // Set up the diff overlay renderer
            if (_codeEditor?.TextArea?.TextView != null)
            {
                _codeEditor.TextArea.TextView.BackgroundRenderers.Add(_diffOverlayRenderer);
            }

            // Set up the diagnostics squiggly renderer
            if (_codeEditor?.TextArea?.TextView != null)
            {
                _codeEditor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticsRenderer);
            }

            if (_codeEditor != null)
            {
                _codeEditor.PointerHover += OnEditorPointerHover;
                _codeEditor.PointerHoverStopped += OnEditorPointerHoverStopped;
                _codeEditor.PointerPressed += OnEditorPointerPressed;
            }

            // If a tab was already active before we attached, update the editor now
            if (_codeEditor != null && _activeTab != null)
            {
                UpdateEditorDocument();
            }
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        // Stop tracking Document replacements on the previously adopted tab
        if (_activeTab != null)
        {
            _activeTab.PropertyChanged -= OnActiveTabPropertyChanged;
        }

        _viewModel = DataContext as EditorPaneViewModel;
        _activeTab = _viewModel?.ActiveTab;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        // The adopted tab must also be tracked: LoadAsync/ReloadAsync replace the
        // TextDocument instance, and setting ActiveTab to the same tab instance
        // (preview reuse) raises no PropertyChanged the view could react to.
        if (_activeTab != null)
        {
            _activeTab.PropertyChanged += OnActiveTabPropertyChanged;
        }

        UpdateEditorDocument();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorPaneViewModel.ActiveTab))
        {
            if (_activeTab != null)
            {
                _activeTab.PropertyChanged -= OnActiveTabPropertyChanged;
            }

            _activeTab = _viewModel?.ActiveTab;

            if (_activeTab != null)
            {
                _activeTab.PropertyChanged += OnActiveTabPropertyChanged;
            }
            UpdateEditorDocument();
        }
        else if (e.PropertyName == nameof(EditorPaneViewModel.ActiveTabHunks))
        {
            UpdateDiffOverlay();
        }
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorTabViewModel.Document))
        {
            UpdateEditorDocument();
        }
    }

    private void OnDiagnosticsUpdated(object? sender, LspDiagnosticsUpdatedEventArgs e)
    {
        // Only refresh if the diagnostics belong to the active tab
        if (_activeTab == null || !string.Equals(
                Path.GetFullPath(e.FilePath),
                Path.GetFullPath(_activeTab.FilePath),
                StringComparison.OrdinalIgnoreCase))
            return;

        _diagnosticsRenderer.UpdateDiagnostics(e.Diagnostics);
        if (_codeEditor?.TextArea?.TextView != null)
        {
            _codeEditor.TextArea.TextView.InvalidateVisual();
        }
    }

    private void UpdateDiffOverlay()
    {
        _diffOverlayRenderer.UpdateHunks(_viewModel?.ActiveTabHunks ?? Array.Empty<DiffHunk>());
        if (_codeEditor?.TextArea?.TextView != null)
        {
            _codeEditor.TextArea.TextView.InvalidateVisual();
        }
    }

    private void UpdateEditorDocument()
    {
        if (_activeTab == null)
        {
            if (_codeEditor != null)
            {
                _codeEditor.Document = null;
            }
            _diffOverlayRenderer.UpdateHunks(Array.Empty<DiffHunk>());
            _diagnosticsRenderer.UpdateDiagnostics(Array.Empty<LspDiagnosticItem>());
            _codeEditor?.TextArea?.TextView?.InvalidateVisual();
            return;
        }

        if (_codeEditor != null && _activeTab != null)
        {
            // Assign the document directly from the ViewModel on the UI thread
            _codeEditor.Document = _activeTab.Document;
            _codeEditor.IsReadOnly = _activeTab.IsReadOnly;

            // Apply syntax highlighting
            var highlighting = _highlightingResolver.Resolve(_activeTab.FilePath);
            _codeEditor.SyntaxHighlighting = highlighting;

            // Force layout and visual refresh
            _codeEditor.TextArea?.TextView?.InvalidateVisual();
            _codeEditor.InvalidateMeasure();
            _codeEditor.InvalidateArrange();
        }

        // Update diff overlay with hunks for this file
        UpdateDiffOverlay();

        // Update diagnostics squiggles from the LSP cache
        var activeTab = _activeTab;
        if (_lspService != null && activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath))
        {
            var diagnostics = _lspService.GetCachedDiagnostics(activeTab.FilePath);
            _diagnosticsRenderer.UpdateDiagnostics(diagnostics);
            if (_codeEditor?.TextArea?.TextView != null)
            {
                _codeEditor.TextArea.TextView.InvalidateVisual();
            }
        }
        else
        {
            _diagnosticsRenderer.UpdateDiagnostics(Array.Empty<LspDiagnosticItem>());
        }
    }

    // ===== LSP hover =====

    private async void OnEditorPointerHover(object? sender, PointerEventArgs e)
    {
        if (_codeEditor == null || _activeTab == null || _lspService == null)
            return;

        var position = _codeEditor.GetPositionFromPoint(e.GetPosition(_codeEditor.TextArea.TextView));
        if (position == null)
            return;

        var location = position.Value.Location;
        var hover = await _lspService.HoverAsync(
            _activeTab.FilePath,
            Math.Max(0, location.Line - 1),
            Math.Max(0, location.Column - 1));

        if (hover?.Contents?.Value == null)
        {
            CloseHoverToolTip();
            return;
        }

        // Strip markdown code fences for plain-text tooltip display
        var text = hover.Contents.Value
            .Replace("```csharp", "")
            .Replace("```typescript", "")
            .Replace("```python", "")
            .Replace("```", "")
            .Trim();

        if (string.IsNullOrEmpty(text))
        {
            CloseHoverToolTip();
            return;
        }

        var editor = _codeEditor;
        ToolTip.SetPlacement(editor, PlacementMode.Pointer);
        ToolTip.SetShowDelay(editor, 0);
        ToolTip.SetTip(editor, text);
        ToolTip.SetIsOpen(editor, true);
    }

    private void OnEditorPointerHoverStopped(object? sender, PointerEventArgs e)
    {
        CloseHoverToolTip();
    }

    private void CloseHoverToolTip()
    {
        if (_codeEditor != null)
        {
            ToolTip.SetIsOpen(_codeEditor, false);
            ToolTip.SetTip(_codeEditor, null);
        }
    }

    // ===== Go-to-definition (Ctrl+Click / F12) =====

    private async void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var keyModifiers = e.KeyModifiers;
        if ((keyModifiers & KeyModifiers.Control) == 0)
            return;

        if (_codeEditor == null || _activeTab == null || _lspService == null)
            return;

        var position = _codeEditor.GetPositionFromPoint(e.GetPosition(_codeEditor.TextArea.TextView));
        if (position == null)
            return;

        var location = position.Value.Location;
        var locations = await _lspService.DefinitionAsync(
            _activeTab.FilePath,
            Math.Max(0, location.Line - 1),
            Math.Max(0, location.Column - 1));

        if (locations.Count == 0)
            return;

        var loc = locations[0];
        var targetPath = UriToPath(loc.Uri);
        if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
            return;

        e.Handled = true;
        await OpenLocationAsync(targetPath, loc.Range?.Start.Line ?? 0);
    }

    /// <summary>
    /// Opens <paramref name="path"/> and scrolls/moves the caret to
    /// <paramref name="line"/> (0-based). Public so other UI flows — Quick
    /// Open's symbol results, in particular — can reuse the same navigation
    /// path as Ctrl+Click/F12 go-to-definition.
    /// </summary>
    public async Task OpenLocationAsync(string path, int line)
    {
        // Open the file in the editor
        if (_viewModel != null)
        {
            await _viewModel.OpenFileAsync(path);
        }

        // Scroll to the target line after the document is loaded
        if (_codeEditor != null && _codeEditor.TextArea != null)
        {
            await Task.Yield();
            var doc = _codeEditor.Document;
            if (doc != null && line >= 0 && line < doc.LineCount)
            {
                _codeEditor.ScrollToLine(line + 1);
                var docLine = doc.GetLineByNumber(line + 1);
                _codeEditor.TextArea.Caret.Offset = docLine.Offset;
            }
        }
    }

    // ===== Helpers =====

    private static string UriToPath(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return string.Empty;

        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var path = Uri.UnescapeDataString(uri[7..]);
            if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
                path = path[1..];
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        return uri;
    }
}