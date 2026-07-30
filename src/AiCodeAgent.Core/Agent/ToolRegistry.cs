using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

public interface IToolRegistry
{
    void Register(ITool tool);
    ITool? GetTool(string name);
    List<ITool> GetTools(List<string> enabledNames);
    IReadOnlyList<ITool> GetAllTools();
}

public class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    public ITool? GetTool(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool : null;

    public List<ITool> GetTools(List<string> enabledNames)
    {
        if (enabledNames.Count == 0) return _tools.Values.ToList();
        return _tools.Values.Where(t => enabledNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public IReadOnlyList<ITool> GetAllTools() => _tools.Values.ToList();
}