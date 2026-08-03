using System;
using System.ComponentModel;
using AiCodeAgent.App.Editor;
using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Diffing;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using AvaloniaEdit;

namespace AiCodeAgent.App.Views;

public partial class EditorView : UserControl
{
    private readonly SyntaxHighlightingResolver _highlightingResolver = new();
    private readonly DiffOverlayRenderer _diffOverlayRenderer = new();
    private EditorPaneViewModel? _viewModel;
    private EditorTabViewModel? _activeTab;
    private TextEditor? _codeEditor;

    public EditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Locate the code editor control from the XAML template
        _codeEditor = this.FindControl<TextEditor>("CodeEditor");

        // Set up the diff overlay renderer
        if (_codeEditor?.TextArea?.TextView != null)
        {
            _codeEditor.TextArea.TextView.BackgroundRenderers.Add(_diffOverlayRenderer);
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

        _viewModel = DataContext as EditorPaneViewModel;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _activeTab = _viewModel.ActiveTab;
            UpdateEditorDocument();
        }
        else
        {
            _activeTab = null;
            UpdateEditorDocument();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorPaneViewModel.ActiveTab))
        {
            _activeTab = _viewModel?.ActiveTab;
            UpdateEditorDocument();
        }
        else if (e.PropertyName == nameof(EditorPaneViewModel.ActiveTabHunks))
        {
            UpdateDiffOverlay();
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
            if (_codeEditor?.TextArea?.TextView != null)
            {
                _codeEditor.TextArea.TextView.InvalidateVisual();
            }
            return;
        }

        if (_codeEditor != null)
        {
            _codeEditor.Document = _activeTab.Document;
            _codeEditor.IsReadOnly = _activeTab.IsReadOnly;

            // Apply syntax highlighting
            var highlighting = _highlightingResolver.Resolve(_activeTab.FilePath);
            _codeEditor.SyntaxHighlighting = highlighting;
        }

        // Update diff overlay with hunks for this file
        UpdateDiffOverlay();
    }
}
