using AiCodeAgent.App.ViewModels;
using Xunit;

namespace AiCodeAgent.App.Tests.Git;

public class DiffViewerReadOnlyTests
{
    [Fact]
    public void LoadComparison_IsReadOnly_NeverDirty_AndRefusesRevertAndSave()
    {
        var vm = new DiffViewerViewModel();

        vm.LoadComparison("/x/file.txt", "one\ntwo\n", "one\nTWO\n", "HEAD", "Staged (index)");

        Assert.True(vm.IsReadOnly);
        Assert.False(vm.IsEditable);
        Assert.False(vm.IsDirty);
        Assert.Equal("Staged (index)", vm.CurrentLabel);
        Assert.True(vm.IsVisible);
        Assert.NotEmpty(vm.Hunks);
        Assert.EndsWith("read-only", vm.StatusText);

        var before = vm.CurrentContent;
        vm.RevertHunk(vm.Hunks[0]);
        Assert.Equal(before, vm.CurrentContent);

        vm.CurrentContent = "typed"; // even if something pokes the text, it is never "dirty"
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Load_AfterComparison_BecomesEditableAgain()
    {
        var vm = new DiffViewerViewModel();
        vm.LoadComparison("/x/file.txt", "a\n", "b\n", "HEAD", "Staged (index)");

        var path = Path.Combine(Path.GetTempPath(), "dv-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(path, "disk\n");
        try
        {
            vm.Load(path, "base\n", "Index");

            Assert.False(vm.IsReadOnly);
            Assert.True(vm.IsEditable);
            Assert.Equal("Current (editable)", vm.CurrentLabel);
            Assert.Equal("disk\n", vm.CurrentContent);
            Assert.False(vm.IsDirty);

            vm.CurrentContent = "edited\n";
            Assert.True(vm.IsDirty); // dirtiness is tracked against the file just loaded, not a previous one
        }
        finally { File.Delete(path); }
    }
}
