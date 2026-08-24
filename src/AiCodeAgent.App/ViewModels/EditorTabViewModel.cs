using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Threading.Tasks;
using AvaloniaEdit.Document;

namespace AiCodeAgent.App.ViewModels;

/// <summary>
/// One per open file. Wraps an AvaloniaEdit TextDocument, tracks FilePath, IsDirty, IsReadOnly.
/// </summary>
public partial class EditorTabViewModel : ObservableObject
{
    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isReadOnly;

    [ObservableProperty]
    private bool _isPreview;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _toolTip = string.Empty;

    private TextDocument? _document;

    /// <summary>Current caret offset within the document, updated by the view.</summary>
    [ObservableProperty]
    private int _caretOffset;

    /// <summary>1-based caret line, derived from <see cref="CaretOffset"/>.</summary>
    public int CaretLine => Document.GetLocation(CaretOffset).Line;

    /// <summary>1-based caret column, derived from <see cref="CaretOffset"/>.</summary>
    public int CaretColumn => Document.GetLocation(CaretOffset).Column;

    public TextDocument Document
    {
        get => _document ??= new TextDocument();
        set
        {
            if (_document != null)
            {
                _document.TextChanged -= OnDocumentTextChanged;
            }
            _document = value;
            if (_document != null)
            {
                _document.TextChanged += OnDocumentTextChanged;
            }
            OnPropertyChanged();
        }
    }

    public string FileName => Path.GetFileName(FilePath);

    public string Text => Document?.Text ?? string.Empty;

    public EditorTabViewModel()
    {
        Title = "Untitled";
        ToolTip = "Untitled";
    }

    partial void OnFilePathChanged(string value)
    {
        Title = Path.GetFileName(value);
        ToolTip = value;
    }

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        IsDirty = true;
    }

    partial void OnCaretOffsetChanged(int value)
    {
        OnPropertyChanged(nameof(CaretLine));
        OnPropertyChanged(nameof(CaretColumn));
    }

    /// <summary>Load file content into the document.</summary>
    public async Task LoadAsync(string path, bool readOnly = false)
    {
        FilePath = path;
        IsReadOnly = readOnly;

        var content = await File.ReadAllTextAsync(path);
        
        Document = new TextDocument(content);
        
        IsDirty = false;
    }

    /// <summary>Save the document back to disk.</summary>
    public async Task<bool> SaveAsync()
    {
        if (string.IsNullOrEmpty(FilePath) || IsReadOnly)
            return false;

        return await SaveToAsync(FilePath);
    }

    /// <summary>Save the document to a specific path (used by Save As).</summary>
    public async Task<bool> SaveToAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || IsReadOnly)
            return false;

        try
        {
            await File.WriteAllTextAsync(path, Document.Text);
            FilePath = path; // Updates Title / ToolTip via OnFilePathChanged
            IsDirty = false;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reload from disk, discarding unsaved changes.</summary>
    public async Task ReloadAsync()
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            return;

        var content = await File.ReadAllTextAsync(FilePath);
        Document = new TextDocument(content);
        IsDirty = false;
    }

    [RelayCommand]
    private async Task Save()
    {
        await SaveAsync();
    }

    [RelayCommand]
    private async Task Reload()
    {
        await ReloadAsync();
    }
}