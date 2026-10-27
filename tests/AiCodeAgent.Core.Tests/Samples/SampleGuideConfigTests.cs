using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Samples;

namespace AiCodeAgent.Core.Tests.Samples;

/// <summary>E3-S3: the progress of the tour is saved in config.json.</summary>
public sealed class SampleGuideConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sample-guide-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task TheProgressSurvivesARestart()
    {
        var first = new ConfigurationService(_dir);
        await first.LoadAsync();
        Assert.Null(first.Config.Ui.SampleGuide);

        first.Config.Ui.SampleGuide = new SampleGuideState { ProjectPath = "/docs/TodoApp", Completed = 2, Finished = false };
        await first.SaveAsync();

        var second = new ConfigurationService(_dir);
        await second.LoadAsync();
        var state = second.Config.Ui.SampleGuide;
        Assert.NotNull(state);
        Assert.Equal("/docs/TodoApp", state!.ProjectPath);
        Assert.Equal(2, state.Completed);
        Assert.False(state.Finished);
    }

    [Fact]
    public async Task AFinishedTourStaysFinished()
    {
        var first = new ConfigurationService(_dir);
        await first.LoadAsync();
        first.Config.Ui.SampleGuide = new SampleGuideState { ProjectPath = "/docs/TodoApp", Completed = 4, Finished = true };
        await first.SaveAsync();

        var second = new ConfigurationService(_dir);
        await second.LoadAsync();

        Assert.True(second.Config.Ui.SampleGuide!.Finished);
    }
}
