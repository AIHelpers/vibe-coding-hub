using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Core.Agent;

public interface IToolRegistry
{
    void Register(ITool tool);
    ITool? GetTool(string name);
    List<ITool> GetTools(List<string> enabledNames);
    IReadOnlyList<ITool> GetAllTools();

    /// <summary>Removes a tool by name (used when an MCP server is disabled or removed). Returns false when it was not registered.</summary>
    bool Unregister(string name) => false;
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
        // Entries are tool names or patterns such as mcp:github/* (see ToolPatterns).
        return _tools.Values.Where(t => ToolPatterns.MatchesAny(enabledNames, t.Name)).ToList();
    }

    public bool Unregister(string name) => _tools.Remove(name);

    public IReadOnlyList<ITool> GetAllTools() => _tools.Values.ToList();
}