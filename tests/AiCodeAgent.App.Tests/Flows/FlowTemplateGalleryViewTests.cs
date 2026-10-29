using System;
using System.IO;
using System.Linq;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using Avalonia.Controls;
using Shape = Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCodeAgent.App.Tests.Flows;

/// <summary>The template gallery renders a card per template over the canvas.</summary>
public class FlowTemplateGalleryViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flows-gallery-view-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [AvaloniaFact]
    public void Gallery_ShowsACardPerTemplate_WithDiagramCharactersAndButtons()
    {
        var presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        var loader = new SdlcPipelineLoader(NullLogger<SdlcPipelineLoader>.Instance, Path.Combine(_root, "pipelines"));
        var characters = new CharacterRegistry(presets, NullLogger<CharacterRegistry>.Instance, Path.Combine(_root, "characters"));
        var vm = new FlowEditorViewModel(loader, null, characters);
        vm.OpenAsync(_root).GetAwaiter().GetResult();
        var view = new FlowsView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var gallery = view.FindControl<Border>("TemplateGallery")!;
        Assert.False(gallery.IsVisible);

        vm.OpenGalleryAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(gallery.IsVisible);
        var cards = gallery.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("templateCard")).ToList();
        Assert.Equal(6, cards.Count);
        var texts = gallery.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Join("", t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? Array.Empty<string>())).ToList();
        Assert.Contains(texts, t => t.Contains("Bug fix"));
        Assert.Contains(texts, t => t.Contains("✗") && t.Contains("quinn-qa"));
        var buttons = gallery.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
        Assert.Equal(6, buttons.Count(b => b == "Use template"));
        Assert.Contains("Blank flow", buttons);
        // Each card draws its diagram as paths.
        Assert.True(gallery.GetVisualDescendants().OfType<Shape.Path>().Count() >= 18);
        window.Close();
    }
}
