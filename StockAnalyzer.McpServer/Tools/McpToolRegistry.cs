using System.Text.Json;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>A tool with its parsed argument schema and optional output schema.</summary>
internal sealed record RegisteredTool(IMcpTool Tool, JsonElement Schema, JsonElement? OutputSchema = null);

/// <summary>The tools the server offers, in a deterministic (ordinal name) order.</summary>
internal sealed class McpToolRegistry
{
    private readonly Dictionary<string, RegisteredTool> _byName;
    private readonly Dictionary<string, RegisteredTool> _callableByName;

    /// <param name="tools">Tools exposed in <c>tools/list</c>.</param>
    /// <param name="callableTools">
    /// Optional superset of tools callable at runtime (e.g. via <c>sa_invoke_tool</c>, prompts, or resources).
    /// When omitted, <see cref="CallableTools"/> matches <see cref="Tools"/> identically.
    /// </param>
    public McpToolRegistry(IEnumerable<IMcpTool> tools, IEnumerable<IMcpTool>? callableTools = null)
    {
        _byName = BuildMap(tools, nameof(tools), existing: null);
        Tools = _byName.Values.OrderBy(t => t.Tool.Name, StringComparer.Ordinal).ToList();
        if (callableTools is null)
        {
            _callableByName = _byName;
            CallableTools = Tools;
        }
        else
        {
            _callableByName = BuildMap(callableTools, nameof(callableTools), existing: _byName);
            CallableTools = _callableByName.Values.OrderBy(t => t.Tool.Name, StringComparer.Ordinal).ToList();
        }
    }

    public IReadOnlyList<RegisteredTool> Tools { get; }

    public IReadOnlyList<RegisteredTool> CallableTools { get; }

    public bool TryGet(string name, out RegisteredTool tool) => _byName.TryGetValue(name, out tool!);

    public bool TryGetCallable(string name, out RegisteredTool tool) => _callableByName.TryGetValue(name, out tool!);

    private static Dictionary<string, RegisteredTool> BuildMap(
        IEnumerable<IMcpTool> tools,
        string paramName,
        IReadOnlyDictionary<string, RegisteredTool>? existing)
    {
        var map = new Dictionary<string, RegisteredTool>(StringComparer.Ordinal);
        foreach (IMcpTool tool in tools)
        {
            if (map.ContainsKey(tool.Name))
            {
                throw new ArgumentException($"Duplicate tool name '{tool.Name}'.", paramName);
            }

            if (existing is not null
                && existing.TryGetValue(tool.Name, out RegisteredTool? reused)
                && ReferenceEquals(reused.Tool, tool))
            {
                map.Add(tool.Name, reused);
                continue;
            }

            // The parsed schema is kept for the process lifetime.
            JsonElement schema = JsonDocument.Parse(tool.InputSchemaJson).RootElement;
            JsonElement? outputSchema = tool.OutputSchemaJson is not null
                ? JsonDocument.Parse(tool.OutputSchemaJson).RootElement
                : null;
            map.Add(tool.Name, new RegisteredTool(tool, schema, outputSchema));
        }

        return map;
    }
}
