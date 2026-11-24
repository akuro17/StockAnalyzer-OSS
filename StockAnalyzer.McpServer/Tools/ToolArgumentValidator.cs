using System.Text.Json;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Checks the raw <c>arguments</c> against a tool's schema before any typed binding: unknown names, missing required names,
/// JSON types, enum values and array/object shape (nested names are reported as <c>name[i].key</c>). Range and relation rules
/// (limits, dates) belong to the tool.
/// </summary>
internal static class ToolArgumentValidator
{
    public const string ProgressiveUnknownArgumentHint =
        "Call sa_search_tools with toolNames and includeSchema: true to inspect accepted arguments.";

    /// <param name="misnamedHints">Optional hints for top-level names that are unknown here but named by a sibling tool (see <see cref="IMcpTool.MisnamedArgumentHints"/>); null keeps the generic hint.</param>
    /// <param name="unknownArgumentHint">Optional fallback hint when a top-level argument is unknown and no misnamed hint applies; null keeps the legacy <c>tools/list</c> hint.</param>
    public static void Validate(
        JsonElement schema,
        JsonElement arguments,
        IReadOnlyDictionary<string, string>? misnamedHints = null,
        string? unknownArgumentHint = null)
        => ValidateObject(string.Empty, schema, arguments, misnamedHints, unknownArgumentHint);

    private static void ValidateObject(
        string prefix,
        JsonElement schema,
        JsonElement arguments,
        IReadOnlyDictionary<string, string>? misnamedHints = null,
        string? unknownArgumentHint = null)
    {
        JsonElement properties = schema.GetProperty("properties");
        bool hasObject = arguments.ValueKind == JsonValueKind.Object;

        if (hasObject)
        {
            foreach (JsonProperty argument in arguments.EnumerateObject())
            {
                string path = prefix + argument.Name;
                if (!properties.TryGetProperty(argument.Name, out JsonElement propertySchema))
                {
                    string hint = prefix.Length == 0 && misnamedHints is not null && misnamedHints.TryGetValue(argument.Name, out string? misnamed)
                        ? misnamed
                        : unknownArgumentHint ?? "Call tools/list to see the accepted arguments.";
                    throw new ToolException(ToolErrorCodes.InvalidArgument, $"Unknown argument '{path}'.", path, hint);
                }

                if (argument.Value.ValueKind == JsonValueKind.Null
                    && !IsRequired(schema, argument.Name)
                    && propertySchema.GetProperty("type").GetString() is "string" or "integer" or "number" or "boolean")
                {
                    continue;
                }

                CheckValue(path, argument.Value, propertySchema, unknownArgumentHint);
            }
        }

        if (schema.TryGetProperty("required", out JsonElement required))
        {
            foreach (JsonElement name in required.EnumerateArray())
            {
                if (!hasObject || !arguments.TryGetProperty(name.GetString()!, out _))
                {
                    string path = prefix + name.GetString();
                    throw new ToolException(ToolErrorCodes.InvalidArgument, $"Missing required argument '{path}'.", path);
                }
            }
        }
    }

    private static bool IsRequired(JsonElement schema, string propertyName)
    {
        if (!schema.TryGetProperty("required", out JsonElement required) || required.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in required.EnumerateArray())
        {
            if (string.Equals(item.GetString(), propertyName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckValue(string name, JsonElement value, JsonElement schema, string? unknownArgumentHint = null)
    {
        string type = schema.GetProperty("type").GetString()!;
        bool typeMatches = type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => throw new InvalidOperationException($"Unsupported schema type '{type}'."),
        };

        if (!typeMatches)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{name}' must be of type {type}.", name);
        }

        if (type == "string" && schema.TryGetProperty("enum", out JsonElement allowed))
        {
            string text = value.GetString()!;
            if (!allowed.EnumerateArray().Any(a => string.Equals(a.GetString(), text, StringComparison.Ordinal)))
            {
                string names = string.Join(", ", allowed.EnumerateArray().Select(a => a.GetString()));
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{name}' has an unsupported value.", name, $"Allowed values: {names}.");
            }
        }

        if (type == "object" && schema.TryGetProperty("properties", out _))
        {
            ValidateObject(name + ".", schema, value, misnamedHints: null, unknownArgumentHint: unknownArgumentHint);
        }

        if (type == "array")
        {
            if (schema.TryGetProperty("minItems", out JsonElement minItems) && value.GetArrayLength() < minItems.GetInt32())
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{name}' needs at least {minItems.GetInt32()} item(s).", name);
            }

            if (schema.TryGetProperty("items", out JsonElement items))
            {
                int index = 0;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    CheckValue($"{name}[{index}]", item, items, unknownArgumentHint);
                    index++;
                }
            }
        }
    }
}
