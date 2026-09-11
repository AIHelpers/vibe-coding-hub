using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace AiCodeAgent.App.ViewModels;

public partial class FileExplorerItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private bool _isDirectory;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _icon = "📄";

    /// <summary>Dummy child used so directory expanders appear before children are loaded.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>True when this item is a real file that can be opened in the editor.</summary>
    public bool CanOpen => !IsDirectory && !IsPlaceholder;

    public ObservableCollection<FileExplorerItem> Children { get; } = new();

    public bool IsInitiallyLoaded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && IsDirectory && !IsInitiallyLoaded)
        {
            _ = LoadChildrenAsync();
        }
    }

    public static FileExplorerItem CreateDirectory(string name, string fullPath)
    {
        var item = new FileExplorerItem
        {
            Name = name,
            FullPath = fullPath,
            IsDirectory = true,
            Icon = "📁"
        };
        item.Children.Add(CreatePlaceholder());
        return item;
    }

    public static FileExplorerItem CreateFile(string name, string fullPath)
    {
        return new FileExplorerItem
        {
            Name = name,
            FullPath = fullPath,
            IsDirectory = false,
            Icon = GetFileIcon(Path.GetExtension(name))
        };
    }

    public static FileExplorerItem CreatePlaceholder() => new()
    {
        Name = string.Empty,
        Icon = string.Empty,
        IsPlaceholder = true
    };

    public async Task LoadChildrenAsync()
    {
        if (!IsDirectory || IsPlaceholder || IsLoading || IsInitiallyLoaded)
            return;

        IsLoading = true;

        try
        {
            var path = FullPath;
            var items = await Task.Run(() => EnumerateChildren(path));

            Children.Clear();
            foreach (var child in items)
            {
                Children.Add(child);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading children: {ex.Message}");
        }
        finally
        {
            IsInitiallyLoaded = true;
            IsLoading = false;
        }
    }

    internal static List<FileExplorerItem> EnumerateChildren(string directoryPath)
    {
        var children = new List<FileExplorerItem>();
        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            if (!dirInfo.Exists)
                return children;

            foreach (var dir in dirInfo.GetDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name))
            {
                children.Add(CreateDirectory(dir.Name, dir.FullName));
            }

            foreach (var file in dirInfo.GetFiles()
                .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Name.StartsWith('.'))
                .OrderBy(f => f.Name))
            {
                children.Add(CreateFile(file.Name, file.FullName));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        return children;
    }

    internal static string GetFileIcon(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" => "🔷",
        ".xaml" or ".axaml" => "🟦",
        ".json" or ".xml" or ".yaml" or ".yml" or ".toml" => "📋",
        ".md" or ".txt" => "📝",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" => "🖼️",
        ".csproj" or ".sln" or ".slnx" => "📦",
        ".gitignore" or ".gitattributes" => "🔧",
        ".js" or ".ts" or ".jsx" or ".tsx" => "🟨",
        ".py" => "🐍",
        ".html" or ".css" or ".scss" => "🌐",
        ".sql" or ".db" => "🗄️",
        ".dll" or ".exe" => "⚙️",
        _ => "📄"
    };
}

public partial class FileExplorerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _rootPath = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private FileExplorerItem? _selectedItem;

    public ObservableCollection<FileExplorerItem> RootItems { get; } = new();

    public FileExplorerViewModel()
    {
        RootPath = Directory.GetCurrentDirectory();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (string.IsNullOrEmpty(RootPath) || !Directory.Exists(RootPath))
            return;

        IsLoading = true;
        RootItems.Clear();

        try
        {
            var rootDir = new DirectoryInfo(RootPath);
            var rootItem = FileExplorerItem.CreateDirectory(rootDir.Name, rootDir.FullName);

            var items = await Task.Run(() => FileExplorerItem.EnumerateChildren(rootDir.FullName));

            rootItem.Children.Clear();
            foreach (var child in items)
            {
                rootItem.Children.Add(child);
            }

            rootItem.IsInitiallyLoaded = true;
            RootItems.Add(rootItem);
            rootItem.IsExpanded = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        _ = LoadAsync();
    }

    [RelayCommand]
    private void SetRootPath(string path)
    {
        RootPath = path;
        _ = LoadAsync();
    }

    public List<string> GetFilesRecursive(string? directory = null, int maxDepth = 3)
    {
        var files = new List<string>();
        var dir = directory ?? RootPath;

        if (!Directory.Exists(dir)) return files;

        try
        {
            foreach (var file in Directory.GetFiles(dir)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .Take(50))
            {
                files.Add(file);
            }

            if (maxDepth > 0)
            {
                foreach (var subDir in Directory.GetDirectories(dir)
                    .Where(d => !Path.GetFileName(d).StartsWith('.')))
                {
                    files.AddRange(GetFilesRecursive(subDir, maxDepth - 1));
                }
            }
        }
        catch (UnauthorizedAccessException) { }

        return files;
    }
}
