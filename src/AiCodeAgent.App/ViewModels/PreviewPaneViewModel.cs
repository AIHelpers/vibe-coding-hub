using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// Raised when the user commits a freehand annotation from the preview pane.
/// Subscribers (e.g. the chat view-model) can forward the annotation to the
/// agent.
/// </summary>
public delegate void AnnotationCommittedHandler(AnnotationMessage annotation);

/// <summary>
/// A minimal click-to-edit visual layer view-model. Hosts a mock preview of
/// the active file's rendered elements (each tagged with a
/// <c>data-source-id</c>), a selection model, a lightweight property panel, and
/// write-back of edits through <see cref="SharedChangeset"/> as
/// <see cref="DiffHunk"/>s.
/// </summary>
public partial class PreviewPaneViewModel : ObservableObject
{
    private readonly SharedChangeset? _changeset;
    private readonly SourceIdResolver _resolver;
    private readonly EditorPaneViewModel? _editorPane;

    /// <summary>Elements rendered in the preview pane (mock representation).</summary>
    public ObservableCollection<PreviewElement> Elements { get; } = new();

    /// <summary>
    /// The freehand annotation overlay view-model. May be null if annotation
    /// support is not wired up for this preview instance.
    /// </summary>
    public AnnotationViewModel? Annotation { get; private set; }

    /// <summary>Raised when an annotation is committed for send-to-chat.</summary>
    public event AnnotationCommittedHandler? AnnotationCommitted;

    /// <summary>Whether the annotation overlay is currently active.</summary>
    [ObservableProperty]
    private bool _isAnnotationActive;

    [ObservableProperty]
    private PreviewElement? _selectedElement;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private string _statusText = string.Empty;

    // ---- Property panel fields (bound to the selected element) ----
    [ObservableProperty]
    private string _propertyText = string.Empty;

    [ObservableProperty]
    private string _propertyColor = string.Empty;

    [ObservableProperty]
    private string _propertyFontSize = string.Empty;

    [ObservableProperty]
    private string _propertyPadding = string.Empty;

    public PreviewPaneViewModel(
        SourceIdResolver resolver,
        SharedChangeset? changeset = null,
        EditorPaneViewModel? editorPane = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _changeset = changeset;
        _editorPane = editorPane;

        Annotation = new AnnotationViewModel(new AnnotationMapper());
        Annotation.AnnotationCommitted += OnAnnotationCommitted;
    }

    /// <summary>Activate/deactivate the freehand annotation overlay.</summary>
    [RelayCommand]
    private void ToggleAnnotation()
    {
        if (Annotation == null) return;
        Annotation.IsActive = !Annotation.IsActive;
        IsAnnotationActive = Annotation.IsActive;
        StatusText = Annotation.IsActive ? "Annotation mode on" : "Annotation mode off";
    }

    /// <summary>Send a committed annotation to the preview's write-back path.</summary>
    [RelayCommand]
    private void AttachAnnotationToChat()
    {
        if (Annotation == null) return;
        Annotation.CommitCommand.Execute(null);
    }

    private void OnAnnotationCommitted(object? sender, AnnotationMessage annotation)
    {
        StatusText = $"Annotation captured ({annotation.Strokes.Count} strokes)";
        AnnotationCommitted?.Invoke(annotation);
    }

    /// <summary>
    /// Loads a mock preview for the given file path. In a real implementation
    /// this would render the file's DOM with injected <c>data-source-id</c>
    /// attributes. Here we synthesize a few elements mapped to line ranges in
    /// the file so the click-to-edit flow is demonstrable.
    /// </summary>
    public void LoadPreviewForFile(string filePath, string fileContent)
    {
        Elements.Clear();
        SelectedElement = null;
        ResetPropertyFields();

        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(fileContent))
        {
            StatusText = "No preview elements";
            return;
        }

        var lines = fileContent.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            // Heuristic: treat heading/markup lines as preview elements.
            if (line.StartsWith("#") || line.StartsWith("<") || line.Length > 0 && i % 5 == 0)
            {
                var start = i + 1;
                var end = Math.Min(i + 1, lines.Length);
                var id = $"src:{filePath}:{start}-{end}";
                Elements.Add(new PreviewElement(id, line, start, end));
            }
        }

        StatusText = Elements.Count > 0
            ? $"{Elements.Count} preview elements"
            : "No preview elements";
    }

    /// <summary>Called when an element is clicked in the preview.</summary>
    [RelayCommand]
    private void SelectElement(PreviewElement element)
    {
        SelectedElement = element;
        if (element == null)
        {
            ResetPropertyFields();
            return;
        }

        // Populate property panel from the element's current text. In a fuller
        // implementation this would parse computed styles from the rendered DOM.
        PropertyText = element.Text;
        PropertyColor = string.Empty;
        PropertyFontSize = string.Empty;
        PropertyPadding = string.Empty;
        StatusText = $"Selected: {element.DataSourceId}";
    }

    /// <summary>
    /// Apply the property panel edits back to the source file via a
    /// <see cref="DiffHunk"/> pushed into <see cref="SharedChangeset"/>. The
    /// editor pane is then notified so its diff overlay refreshes.
    /// </summary>
    [RelayCommand]
    private async Task ApplyEditAsync()
    {
        if (SelectedElement == null || _changeset == null)
        {
            StatusText = "No selection or changeset unavailable";
            return;
        }

        var newText = BuildNewText();
        var hunk = _resolver.ResolveAndBuildHunk(SelectedElement.DataSourceId, newText);
        if (hunk == null)
        {
            StatusText = "Could not resolve source span";
            return;
        }

        _changeset.AddHunks(new[] { hunk });
        _editorPane?.NotifyHunksChanged();

        // Optionally open the file in the editor so the user sees the pending hunk.
        if (_editorPane != null)
        {
            var span = _resolver.Resolve(SelectedElement.DataSourceId);
            if (span != null)
            {
                await _editorPane.OpenFileAsync(span.Value.FilePath);
            }
        }

        // Update the preview element's text locally so the preview reflects the edit.
        var index = Elements.IndexOf(SelectedElement);
        if (index >= 0)
        {
            Elements[index] = SelectedElement with { Text = newText };
            SelectedElement = Elements[index];
        }

        StatusText = $"Edit staged as hunk {hunk.HunkId}";
    }

    [RelayCommand]
    private void ClearSelection()
    {
        SelectedElement = null;
        ResetPropertyFields();
        StatusText = "Selection cleared";
    }

    partial void OnSelectedElementChanged(PreviewElement? value)
    {
        if (value == null)
            ResetPropertyFields();
    }

    private string BuildNewText()
    {
        // Compose a simple replacement text from the property panel fields.
        // This is intentionally minimal — a real implementation would produce
        // framework-aware JSX/HTML/CSS edits.
        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(PropertyText))
            parts.Add(PropertyText);
        if (!string.IsNullOrWhiteSpace(PropertyColor))
            parts.Add($"color: {PropertyColor};");
        if (!string.IsNullOrWhiteSpace(PropertyFontSize))
            parts.Add($"font-size: {PropertyFontSize};");
        if (!string.IsNullOrWhiteSpace(PropertyPadding))
            parts.Add($"padding: {PropertyPadding};");

        return parts.Count > 0 ? string.Join(" ", parts) : PropertyText;
    }

    private void ResetPropertyFields()
    {
        PropertyText = string.Empty;
        PropertyColor = string.Empty;
        PropertyFontSize = string.Empty;
        PropertyPadding = string.Empty;
    }
}

/// <summary>
/// A single rendered preview element with a back-reference to its source span
/// via <c>data-source-id</c>.
/// </summary>
public record PreviewElement(string DataSourceId, string Text, int StartLine, int EndLine)
{
    /// <summary>Short label shown in the preview list.</summary>
    public string Label => $"L{StartLine}: {Truncate(Text, 60)}";

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= n ? s : s[..n] + "…");
}