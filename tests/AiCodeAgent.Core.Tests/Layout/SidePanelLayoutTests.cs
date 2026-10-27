using System.Text.Json;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Layout;
using Xunit;

namespace AiCodeAgent.Core.Tests.Layout;

public class SidePanelLayoutTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "vch-panels");
    private static string Ws(string name) => Path.Combine(Root, name);

    [Theory]
    [InlineData(double.NaN, 380)]
    [InlineData(0, 380)]
    [InlineData(-5, 380)]
    [InlineData(double.PositiveInfinity, 380)]
    [InlineData(100, 280)]
    [InlineData(500, 500)]
    [InlineData(5000, 720)]
    public void ClampWidth_KeepsWidthInRange(double input, double expected)
        => Assert.Equal(expected, SidePanelLayout.ClampWidth(input));

    [Fact]
    public void ClampWidth_NarrowWindowLeavesRoomForTheChat()
    {
        // 900 px window: at most 900 - 520 = 380 px for the panel.
        Assert.Equal(380, SidePanelLayout.ClampWidth(700, availableWidth: 900));
        // A tiny window never goes below the minimum.
        Assert.Equal(280, SidePanelLayout.ClampWidth(700, availableWidth: 500));
        // A wide window allows the maximum.
        Assert.Equal(720, SidePanelLayout.ClampWidth(900, availableWidth: 2400));
    }

    [Fact]
    public void WorkspaceKey_IgnoresTrailingSeparators()
    {
        var a = SidePanelLayout.WorkspaceKey(Ws("app"));
        var b = SidePanelLayout.WorkspaceKey(Ws("app") + Path.DirectorySeparatorChar);
        Assert.Equal(a, b);
        Assert.Equal(string.Empty, SidePanelLayout.WorkspaceKey("  "));
        Assert.Equal(string.Empty, SidePanelLayout.WorkspaceKey(null));
    }

    [Fact]
    public void Remember_ThenGet_RoundTripsPanelAndWidth()
    {
        var ui = new UiConfiguration();
        var memory = new SidePanelMemory(ui);

        Assert.Null(memory.Get(Ws("app")));
        Assert.True(memory.Remember(Ws("app"), SidePanelIds.Changes, 450));

        var state = memory.Get(Ws("app") + Path.DirectorySeparatorChar);
        Assert.NotNull(state);
        Assert.Equal(SidePanelIds.Changes, state!.ActivePanel);
        Assert.Equal(450, state.Width);
    }

    [Fact]
    public void Remember_KeepsWorkspacesApart()
    {
        var memory = new SidePanelMemory(new UiConfiguration());
        memory.Remember(Ws("a"), SidePanelIds.Tasks, 300);
        memory.Remember(Ws("b"), SidePanelIds.Dashboard, 600);

        Assert.Equal(SidePanelIds.Tasks, memory.Get(Ws("a"))!.ActivePanel);
        Assert.Equal(300, memory.Get(Ws("a"))!.Width);
        Assert.Equal(SidePanelIds.Dashboard, memory.Get(Ws("b"))!.ActivePanel);
        Assert.Equal(600, memory.Get(Ws("b"))!.Width);
    }

    [Fact]
    public void Remember_ClosedPanelIsRemembered_AsNull()
    {
        var memory = new SidePanelMemory(new UiConfiguration());
        memory.Remember(Ws("a"), SidePanelIds.Changes, 400);
        Assert.True(memory.Remember(Ws("a"), null, 400));

        var state = memory.Get(Ws("a"));
        Assert.NotNull(state);
        Assert.Null(state!.ActivePanel);
    }

    [Fact]
    public void Remember_SameValues_ReportsNoChange()
    {
        var memory = new SidePanelMemory(new UiConfiguration());
        Assert.True(memory.Remember(Ws("a"), SidePanelIds.Changes, 400));
        Assert.False(memory.Remember(Ws("a"), SidePanelIds.Changes, 400.2));
        Assert.True(memory.Remember(Ws("a"), SidePanelIds.Changes, 410));
    }

    [Fact]
    public void Remember_PlanAndUnknownPanels_AreNotRestored()
    {
        var memory = new SidePanelMemory(new UiConfiguration());
        memory.Remember(Ws("a"), SidePanelIds.Plan, 400);
        Assert.Null(memory.Get(Ws("a"))!.ActivePanel);

        memory.Remember(Ws("b"), "does-not-exist", 400);
        Assert.Null(memory.Get(Ws("b"))!.ActivePanel);
    }

    [Fact]
    public void Get_RepairsBrokenStoredValues()
    {
        var ui = new UiConfiguration();
        ui.WorkspacePanels[SidePanelLayout.WorkspaceKey(Ws("a"))] =
            new WorkspacePanelState { ActivePanel = "gone", Width = 99999 };
        var state = new SidePanelMemory(ui).Get(Ws("a"));
        Assert.Null(state!.ActivePanel);
        Assert.Equal(SidePanelLayout.MaxWidth, state.Width);
    }

    [Fact]
    public void Remember_EmptyWorkspace_IsIgnored()
    {
        var ui = new UiConfiguration();
        Assert.False(new SidePanelMemory(ui).Remember("", SidePanelIds.Changes, 400));
        Assert.Empty(ui.WorkspacePanels);
    }

    [Fact]
    public void Remember_DropsTheOldestWorkspacesBeyondTheLimit()
    {
        var ui = new UiConfiguration();
        var memory = new SidePanelMemory(ui);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < SidePanelMemory.MaxEntries + 5; i++)
            memory.Remember(Ws("w" + i), SidePanelIds.Changes, 400, start.AddMinutes(i));

        Assert.Equal(SidePanelMemory.MaxEntries, ui.WorkspacePanels.Count);
        Assert.Null(memory.Get(Ws("w0")));
        Assert.NotNull(memory.Get(Ws("w" + (SidePanelMemory.MaxEntries + 4))));
    }

    [Fact]
    public void Configuration_SurvivesAJsonRoundTrip()
    {
        var ui = new UiConfiguration { ActivityBarExpanded = true };
        new SidePanelMemory(ui).Remember(Ws("a"), SidePanelIds.Checkpoints, 520);

        var json = JsonSerializer.Serialize(ui);
        var back = JsonSerializer.Deserialize<UiConfiguration>(json)!;

        Assert.True(back.ActivityBarExpanded);
        var state = new SidePanelMemory(back).Get(Ws("a"));
        Assert.Equal(SidePanelIds.Checkpoints, state!.ActivePanel);
        Assert.Equal(520, state.Width);
    }

    [Fact]
    public void OldConfigurationWithoutPanels_LoadsWithDefaults()
    {
        var back = JsonSerializer.Deserialize<UiConfiguration>("{\"Theme\":\"dark\"}")!;
        Assert.False(back.ActivityBarExpanded);
        Assert.Empty(back.WorkspacePanels);
    }
}
