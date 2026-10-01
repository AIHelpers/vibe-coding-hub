using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Indexing;

namespace AiCodeAgent.App.ViewModels;

/// <summary>One row in the Quick Open list — either a file or a symbol match.</summary>
public sealed class QuickOpenResult
{
    public required string DisplayName { get; init; }
    public required string SubText { get; init; }
    public required string FilePath { get; init; }
    /// <summary>0-based line to jump to, or -1 for a plain file open with no specific location.</summary>
    public int Line { get; init; } = -1;
    public bool IsSymbol { get; init; }
}

/// <summary>
/// Powers the "Quick Open" popup: fuzzy-search files as you type (Ctrl+P,
/// same muscle memory as VSCode/Cursor), or symbols workspace-wide when the
/// query starts with '#' (Ctrl+Shift+O seeds that prefix automatically).
///
/// This replaces the old <c>MainViewModel.OpenIndexedFileAsync</c>/
/// <c>OpenIndexedSymbolAsync</c> stubs, which had no query input at all and
/// always jumped to whatever the index's first result happened to be —
/// "go to file" that could only ever go to one specific file isn't a
/// working feature, just a placeholder for one.
/// </summary>
public partial class QuickOpenViewModel : ObservableObject
{
    private readonly WorkspaceIndexQueryService? _indexQueryService;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private int _selectedIndex = -1;

    [ObservableProperty]
    private bool _isSearching;

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                _ = RefreshAsync();
        }
    }

    public ObservableCollection<QuickOpenResult> Results { get; } = new();

    /// <summary>Raised when the user picks a result — the host (MainWindow) opens the file/jumps to the symbol.</summary>
    public event Action<QuickOpenResult>? ResultChosen;

    public QuickOpenViewModel(WorkspaceIndexQueryService? indexQueryService = null)
    {
        _indexQueryService = indexQueryService;
    }

    /// <summary>Open in file-search mode with an empty query.</summary>
    public void Open()
    {
        IsOpen = true;
        SearchText = string.Empty;
        _ = RefreshAsync();
    }

    /// <summary>Open pre-seeded for symbol search (the '#' prefix switches modes).</summary>
    public void OpenForSymbols()
    {
        IsOpen = true;
        SearchText = "#";
        _ = RefreshAsync();
    }

    public void Close()
    {
        IsOpen = false;
        _searchCts?.Cancel();
    }

    public void SelectNext()
    {
        if (Results.Count == 0) return;
        SelectedIndex = (SelectedIndex + 1) % Results.Count;
    }

    public void SelectPrevious()
    {
        if (Results.Count == 0) return;
        SelectedIndex = (SelectedIndex - 1 + Results.Count) % Results.Count;
    }

    /// <summary>Fire ResultChosen for the highlighted row and close the popup.</summary>
    public void ChooseSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count)
            return;

        var result = Results[SelectedIndex];
        Close();
        ResultChosen?.Invoke(result);
    }

    /// <summary>
    /// Re-queries the workspace index for the current SearchText. Debounced
    /// lightly (index queries hit SQLite) and cancels any in-flight query
    /// when the text changes again before it completes.
    /// </summary>
    private async Task RefreshAsync()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        if (_indexQueryService == null)
            return;

        try
        {
            IsSearching = true;
            await Task.Delay(120, cts.Token); // debounce
            if (cts.IsCancellationRequested) return;

            var query = SearchText.Trim();
            var isSymbolMode = query.StartsWith('#');
            if (isSymbolMode)
                query = query[1..].Trim();

            if (isSymbolMode)
            {
                var symbols = await _indexQueryService.SearchSymbolsAsync(
                    filter: query, limit: 40, cancellationToken: cts.Token);
                if (cts.IsCancellationRequested) return;

                Results.Clear();
                foreach (var s in symbols)
                {
                    Results.Add(new QuickOpenResult
                    {
                        DisplayName = s.Name,
                        SubText = $"{s.Kind} · {System.IO.Path.GetFileName(s.FilePath)}:{s.Line}",
                        FilePath = s.FilePath,
                        Line = Math.Max(0, s.Line - 1), // stored 1-based; OpenLocationAsync wants 0-based
                        IsSymbol = true
                    });
                }
            }
            else
            {
                var files = await _indexQueryService.SearchFilesAsync(
                    filter: query, limit: 40, cancellationToken: cts.Token);
                if (cts.IsCancellationRequested) return;

                Results.Clear();
                foreach (var f in files)
                {
                    Results.Add(new QuickOpenResult
                    {
                        DisplayName = f.FileName,
                        SubText = f.Path,
                        FilePath = f.Path,
                        Line = -1,
                        IsSymbol = false
                    });
                }
            }

            SelectedIndex = Results.Count > 0 ? 0 : -1;
        }
        catch (TaskCanceledException)
        {
            // superseded by a newer keystroke — expected, not an error
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                IsSearching = false;
        }
    }
}
