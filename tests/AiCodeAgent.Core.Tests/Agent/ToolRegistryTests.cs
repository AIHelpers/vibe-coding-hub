using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using NSubstitute;

namespace AiCodeAgent.Core.Tests.Agent;

public class ToolRegistryTests
{
    private static ITool CreateNamedTool(string name)
    {
        var tool = Substitute.For<ITool>();
        tool.Name.Returns(name);
        tool.Description.Returns($"desc-{name}");
        tool.Definition.Returns(new ToolDefinition { Name = name });
        return tool;
    }

    [Fact]
    public void GetTool_ReturnsNull_WhenNotRegistered()
    {
        var registry = new ToolRegistry();

        var result = registry.GetTool("missing");

        Assert.Null(result);
    }

    [Fact]
    public void Register_And_GetTool_ReturnsSameInstance()
    {
        var registry = new ToolRegistry();
        var tool = CreateNamedTool("read_file");
        registry.Register(tool);

        var result = registry.GetTool("read_file");

        Assert.Same(tool, result);
    }

    [Fact]
    public void Register_OverwritesExistingTool_WithSameName()
    {
        var registry = new ToolRegistry();
        var first = CreateNamedTool("read_file");
        var second = CreateNamedTool("read_file");
        registry.Register(first);
        registry.Register(second);

        var result = registry.GetTool("read_file");

        Assert.Same(second, result);
    }

    [Fact]
    public void GetTool_IsCaseInsensitive()
    {
        var registry = new ToolRegistry();
        var tool = CreateNamedTool("ReadFile");
        registry.Register(tool);

        var result = registry.GetTool("readfile");

        Assert.Same(tool, result);
    }

    [Fact]
    public void GetTools_ReturnsAll_WhenEnabledNamesEmpty()
    {
        var registry = new ToolRegistry();
        var a = CreateNamedTool("a");
        var b = CreateNamedTool("b");
        registry.Register(a);
        registry.Register(b);

        var result = registry.GetTools(new List<string>());

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetTools_FiltersByEnabledNames_CaseInsensitive()
    {
        var registry = new ToolRegistry();
        var a = CreateNamedTool("alpha");
        var b = CreateNamedTool("beta");
        var c = CreateNamedTool("gamma");
        registry.Register(a);
        registry.Register(b);
        registry.Register(c);

        var result = registry.GetTools(new List<string> { "ALPHA", "gamma" });

        Assert.Equal(2, result.Count);
        Assert.Contains(a, result);
        Assert.Contains(c, result);
    }

    [Fact]
    public void GetTools_ReturnsEmpty_WhenNoEnabledNamesMatch()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateNamedTool("alpha"));

        var result = registry.GetTools(new List<string> { "delta" });

        Assert.Empty(result);
    }

    [Fact]
    public void GetAllTools_ReturnsAllRegisteredTools()
    {
        var registry = new ToolRegistry();
        var a = CreateNamedTool("a");
        var b = CreateNamedTool("b");
        registry.Register(a);
        registry.Register(b);

        var result = registry.GetAllTools();

        Assert.Equal(2, result.Count);
    }
}
