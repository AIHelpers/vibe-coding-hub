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

    /// <summary>True for a folder the user added to the task in addition to the main working folder.</summary>
    [ObservableProperty]
    private bool _isExtraRoot;

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

    /// <summary>Extra folders added to the task besides <see cref="RootPath"/>.</summary>
    public ObservableCollection<string> ExtraFolders { get; } = new();

    /// <summary>Short text for the explorer footer, e.g. "+2 extra folders in this task".</summary>
    public string ExtraFoldersSummary => ExtraFolders.Count switch
    {
        0 => "Main folder only. Use ➕ to add more folders to the task.",
        1 => "+1 extra folder in this task",
        var n => $"+{n} extra folders in this task"
    };

    /// <summary>Replaces the extra folder list (used at startup from saved settings).</summary>
    public void SetExtraFolders(IEnumerable<string> folders)
    {
        ExtraFolders.Clear();
        foreach (var f in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            ExtraFolders.Add(f);
        OnPropertyChanged(nameof(ExtraFoldersSummary));
    }

    /// <summary>Adds a folder to the task. Returns a reason when it was not added, or null on success.</summary>
    public string? AddExtraFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return "That folder does not exist.";
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsSameOrInside(full, RootPath)) return "That folder is already part of the main folder.";
        if (ExtraFolders.Any(e => IsSameOrInside(full, e))) return "That folder is already in the task.";
        // A new parent folder swallows any extras it contains.
        foreach (var inner in ExtraFolders.Where(e => IsSameOrInside(e, full)).ToList()) ExtraFolders.Remove(inner);
        ExtraFolders.Add(full);
        OnPropertyChanged(nameof(ExtraFoldersSummary));
        return null;
    }

    public bool RemoveExtraFolder(string path)
    {
        var existing = ExtraFolders.FirstOrDefault(e => string.Equals(e, path, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return false;
        ExtraFolders.Remove(existing);
        OnPropertyChanged(nameof(ExtraFoldersSummary));
        return true;
    }

    private static bool IsSameOrInside(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

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

            // Folders the user added to the task, listed after the main folder (collapsed until opened).
            foreach (var extra in ExtraFolders.ToList())
            {
                if (!Directory.Exists(extra)) continue;
                var info = new DirectoryInfo(extra);
                var item = FileExplorerItem.CreateDirectory(info.Name.Length > 0 ? info.Name : extra, info.FullName);
                item.IsExtraRoot = true;
                item.Icon = "📌";
                RootItems.Add(item);
            }
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
