using AiCodeAgent.App.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AiCodeAgent.App.Tests.Explorer;

public class FileExplorerLiveUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "explorer-" + Guid.NewGuid().ToString("N")[..8]);

    public FileExplorerLiveUpdateTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "src", "inner.cs"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private async Task<FileExplorerViewModel> LoadAsync()
    {
        var vm = new FileExplorerViewModel { EnableWatching = false, EnableGit = false, RootPath = _root };
        await vm.LoadCommand.ExecuteAsync(null);
        return vm;
    }

    private static string[] Names(FileExplorerItem item) => item.Children.Select(c => c.Name).ToArray();

    [Fact]
    public async Task NewFileAndFolder_AppearInSortedPosition()
    {
        var vm = await LoadAsync();
        var root = vm.RootItems[0];
        Assert.Equal(new[] { "src", "a.txt" }, Names(root));

        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        await vm.SyncDirectoryAsync(_root);

        // folders first (alphabetical), then files (alphabetical)
        Assert.Equal(new[] { "docs", "src", "a.txt", "b.txt" }, Names(root));
    }

    [Fact]
    public async Task DeletedFile_IsRemoved()
    {
        var vm = await LoadAsync();
        File.Delete(Path.Combine(_root, "a.txt"));

        await vm.SyncDirectoryAsync(_root);

        Assert.Equal(new[] { "src" }, Names(vm.RootItems[0]));
    }

    [Fact]
    public async Task Sync_KeepsExistingNodes_SoExpandedFoldersStayExpanded()
    {
        var vm = await LoadAsync();
        var root = vm.RootItems[0];
        var src = root.Children.First(c => c.Name == "src");
        src.IsExpanded = true;
        await WaitUntilAsync(() => src.IsInitiallyLoaded);

        File.WriteAllText(Path.Combine(_root, "new.txt"), "n");
        await vm.SyncDirectoryAsync(_root);

        Assert.Same(src, root.Children.First(c => c.Name == "src"));
        Assert.True(src.IsExpanded);
        Assert.Contains("inner.cs", Names(src));
    }

    [Fact]
    public async Task FileCreatedInsideOpenSubfolder_Appears()
    {
        var vm = await LoadAsync();
        var src = vm.RootItems[0].Children.First(c => c.Name == "src");
        src.IsExpanded = true;
        await WaitUntilAsync(() => src.IsInitiallyLoaded);

        File.WriteAllText(Path.Combine(_root, "src", "Generated.cs"), "g");
        await vm.SyncDirectoryAsync(Path.Combine(_root, "src"));

        Assert.Equal(new[] { "Generated.cs", "inner.cs" }, Names(src));
    }

    [Fact]
    public async Task UnopenedFolder_IsSkipped_AndReadsDiskWhenExpandedLater()
    {
        var vm = await LoadAsync();
        var src = vm.RootItems[0].Children.First(c => c.Name == "src");
        File.WriteAllText(Path.Combine(_root, "src", "late.cs"), "l");

        await vm.SyncDirectoryAsync(Path.Combine(_root, "src"));
        Assert.False(src.IsInitiallyLoaded);
        Assert.True(src.Children.Single().IsPlaceholder);

        src.IsExpanded = true;
        await WaitUntilAsync(() => src.IsInitiallyLoaded);
        Assert.Contains("late.cs", Names(src));
    }

    [AvaloniaFact]
    public async Task Watcher_PicksUpNewFile_WithoutManualRefresh()
    {
        var vm = new FileExplorerViewModel { EnableGit = false, RootPath = _root }; // watching on
        try
        {
            await vm.LoadCommand.ExecuteAsync(null);
            File.WriteAllText(Path.Combine(_root, "from-agent.txt"), "hi");

            // The watcher debounces then posts to the UI dispatcher; drive that dispatcher here.
            var root = vm.RootItems[0];
            for (var i = 0; i < 100 && !Names(root).Contains("from-agent.txt"); i++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(50);
            }
            Assert.Contains("from-agent.txt", Names(root));
        }
        finally { vm.Dispose(); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition());
    }
}
