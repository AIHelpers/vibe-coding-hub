using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Models;
using Xunit;

namespace AiCodeAgent.App.Tests.PreviewPane;

public class PreviewPaneViewModelTests
{
    private readonly SourceIdResolver _resolver = new();

    [Fact]
    public void LoadPreviewForFile_GeneratesElements()
    {
        var vm = new PreviewPaneViewModel(_resolver);
        var content = "# Title\nline2\nline3\nline4\nline5\n## Subtitle";
        vm.LoadPreviewForFile("demo.md", content);

        Assert.True(vm.Elements.Count > 0);
        Assert.All(vm.Elements, e => Assert.StartsWith("src:demo.md:", e.DataSourceId));
        Assert.Equal($"{vm.Elements.Count} preview elements", vm.StatusText);
    }

    [Fact]
    public void LoadPreviewForFile_EmptyContent_ClearsElements()
    {
        var vm = new PreviewPaneViewModel(_resolver);
        vm.LoadPreviewForFile("a.md", "line1\nline2");
        Assert.True(vm.Elements.Count > 0);

        vm.LoadPreviewForFile("", "");
        Assert.Empty(vm.Elements);
        Assert.Equal("No preview elements", vm.StatusText);
    }

    [Fact]
    public void SelectElement_SetsPropertyText()
    {
        var vm = new PreviewPaneViewModel(_resolver);
        vm.LoadPreviewForFile("demo.md", "# Heading\nbody");
        var first = vm.Elements.First();

        vm.SelectElementCommand.Execute(first);

        Assert.Same(first, vm.SelectedElement);
        Assert.Equal(first.Text, vm.PropertyText);
        Assert.Contains(first.DataSourceId, vm.StatusText);
    }

    [Fact]
    public void ClearSelection_ResetsPropertyFields()
    {
        var vm = new PreviewPaneViewModel(_resolver);
        vm.LoadPreviewForFile("demo.md", "# Heading");
        var first = vm.Elements.First();
        vm.SelectElementCommand.Execute(first);
        Assert.NotEmpty(vm.PropertyText);

        vm.ClearSelectionCommand.Execute(null);

        Assert.Null(vm.SelectedElement);
        Assert.Empty(vm.PropertyText);
        Assert.Empty(vm.PropertyColor);
    }

    [Fact]
    public async Task ApplyEdit_StagesHunkInChangeset()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, new[] { "# Hello", "world" });
        try
        {
            var changeset = new SharedChangeset();
            var vm = new PreviewPaneViewModel(_resolver, changeset);
            vm.LoadPreviewForFile(path, File.ReadAllText(path));
            var first = vm.Elements.First();
            vm.SelectElementCommand.Execute(first);
            vm.PropertyText = "# Updated";

            // Use a VM subclass that disables OpenFileAsync (which requires host window).
            // Here we pass no editorPane so OpenFileAsync is skipped.
            await vm.ApplyEditCommand.ExecuteAsync(null);

            var hunks = changeset.GetHunksForFile(path).ToList();
            Assert.Single(hunks);
            Assert.Contains("Updated", hunks[0].Lines.Single(l => l.Kind == Core.Diffing.DiffLineKind.Added).Text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}