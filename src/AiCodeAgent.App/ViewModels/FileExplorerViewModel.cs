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

    public ObservableCollection<FileExplorerItem> Children { get; } = new();

    public bool IsInitiallyLoaded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !IsInitiallyLoaded && IsDirectory)
        {
            _ = LoadChildrenAsync();
        }
    }

    public async Task LoadChildrenAsync()
    {
        if (IsLoading || IsInitiallyLoaded) return;
        IsLoading = true;

        try
        {
            var items = await Task.Run(() =>
            {
                var children = new List<FileExplorerItem>();
                try
                {
                    var dirInfo = new DirectoryInfo(FullPath);
                    if (!dirInfo.Exists) return children;

                    foreach (var dir in dirInfo.GetDirectories()
                        .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Name.StartsWith('.'))
                        .OrderBy(d => d.Name))
                    {
                        children.Add(new FileExplorerItem
                        {
                            Name = dir.Name,
                            FullPath = dir.FullName,
                            IsDirectory = true,
                            Icon = "📁"
                        });
                    }

                    foreach (var file in dirInfo.GetFiles()
                        .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Name.StartsWith('.'))
                        .OrderBy(f => f.Name))
                    {
                        children.Add(new FileExplorerItem
                        {
                            Name = file.Name,
                            FullPath = file.FullName,
                            IsDirectory = false,
                            Icon = GetFileIcon(file.Extension)
                        });
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }

                return children;
            });

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

    private static string GetFileIcon(string extension) => extension.ToLowerInvariant() switch
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
            var rootItem = new FileExplorerItem
            {
                Name = rootDir.Name,
                FullPath = rootDir.FullName,
                IsDirectory = true,
                Icon = "📁"
            };

            // Load first level
            var items = await Task.Run(() =>
            {
                var children = new List<FileExplorerItem>();
                try
                {
                    foreach (var dir in rootDir.GetDirectories()
                        .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Name.StartsWith('.'))
                        .OrderBy(d => d.Name))
                    {
                        children.Add(new FileExplorerItem
                        {
                            Name = dir.Name,
                            FullPath = dir.FullName,
                            IsDirectory = true,
                            Icon = "📁"
                        });
                    }

                    foreach (var file in rootDir.GetFiles()
                        .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Name.StartsWith('.'))
                        .OrderBy(f => f.Name))
                    {
                        children.Add(new FileExplorerItem
                        {
                            Name = file.Name,
                            FullPath = file.FullName,
                            IsDirectory = false,
                            Icon = GetFileIcon(file.Extension)
                        });
                    }
                }
                catch (UnauthorizedAccessException) { }
                return children;
            });

            foreach (var child in items)
            {
                rootItem.Children.Add(child);
            }

            rootItem.IsInitiallyLoaded = true;
            RootItems.Add(rootItem);
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

    private static string GetFileIcon(string extension) => extension.ToLowerInvariant() switch
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