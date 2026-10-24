using System.Text.Json;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>A tool with its parsed argument schema.</summary>
internal sealed record RegisteredTool(IMcpTool Tool, JsonElement Schema);

/// <summary>The tools the server offers, in a deterministic (ordinal name) order.</summary>
internal sealed class McpToolRegistry
{
    private readonly Dictionary<string, RegisteredTool> _byName = new(StringComparer.Ordinal);

    public McpToolRegistry(IEnumerable<IMcpTool> tools)
    {
        foreach (IMcpTool tool in tools)
        {
            if (_byName.ContainsKey(tool.Name))
            {
                throw new ArgumentException($"Duplicate tool name '{tool.Name}'.", nameof(tools));
            }

            // The parsed schema is kept for the process lifetime.
            JsonElement schema = JsonDocument.Parse(tool.InputSchemaJson).RootElement;
            _byName.Add(tool.Name, new RegisteredTool(tool, schema));
        }

        Tools = _byName.Values.OrderBy(t => t.Tool.Name, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<RegisteredTool> Tools { get; }

    public bool TryGet(string name, out RegisteredTool tool) => _byName.TryGetValue(name, out tool!);
}
