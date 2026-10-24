using System.Text.Json;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>The strict JSON reads the condition binders share (the Screener's and the Backtest's): required keys, object shape, unknown keys, enum names.</summary>
internal static class ConditionJson
{
    public static JsonElement Require(JsonElement owner, string name, string path, JsonValueKind? kind = null)
    {
        if (!owner.TryGetProperty(name, out JsonElement value))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{name}' is required.", $"{path}.{name}");
        }

        if (kind is { } expected && value.ValueKind != expected)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{name}' must be of JSON type {expected.ToString().ToLowerInvariant()}.", $"{path}.{name}");
        }

        return value;
    }

    public static void RequireObject(JsonElement node, string path)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "A condition node is a JSON object.", path);
        }
    }

    public static void RejectUnknownKeys(JsonElement node, string path, string[] allowed)
    {
        foreach (JsonProperty property in node.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Unknown property '{property.Name}'.", $"{path}.{property.Name}", $"Allowed here: {string.Join(", ", allowed)}.");
            }
        }
    }

    public static T ParseEnum<T>(JsonElement value, string path, IReadOnlyList<string> names) where T : struct, Enum
    {
        string text = value.GetString() ?? string.Empty;
        string? match = names.FirstOrDefault(n => string.Equals(n, text.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is null
            ? throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{text}' is not a valid value.", path, $"Allowed values: {string.Join(", ", names)}.")
            : Enum.Parse<T>(match);
    }
}
