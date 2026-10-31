using System.Text;
using System.Text.Json;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>Builds the JSON Schema text of a tool's arguments (flat object, no extra properties allowed).</summary>
internal sealed class ToolSchema
{
    private readonly List<(string Name, Action<Utf8JsonWriter> Write)> _properties = new();
    private readonly List<string> _required = new();

    public static ToolSchema Object() => new();

    public ToolSchema String(string name, string description, bool required = false, IEnumerable<string>? allowedValues = null)
    {
        string[]? allowed = allowedValues?.ToArray();
        return Add(name, required, writer =>
        {
            writer.WriteString("type", "string");
            writer.WriteString("description", description);
            if (allowed is not null)
            {
                writer.WriteStartArray("enum");
                foreach (string value in allowed)
                {
                    writer.WriteStringValue(value);
                }

                writer.WriteEndArray();
            }
        });
    }

    /// <summary>
    /// <paramref name="defaultValue"/>, <paramref name="minimum"/> and <paramref name="maximum"/> are optional machine-readable hints
    /// (the standard <c>default</c>, <c>minimum</c>, <c>maximum</c> keywords); a caller passes the same variable that its argument check uses.
    /// Left out, the output is unchanged.
    /// </summary>
    public ToolSchema Integer(string name, string description, bool required = false, long? defaultValue = null, long? minimum = null, long? maximum = null)
        => Add(name, required, writer =>
        {
            writer.WriteString("type", "integer");
            writer.WriteString("description", description);
            if (defaultValue is not null)
            {
                writer.WriteNumber("default", defaultValue.Value);
            }

            if (minimum is not null)
            {
                writer.WriteNumber("minimum", minimum.Value);
            }

            if (maximum is not null)
            {
                writer.WriteNumber("maximum", maximum.Value);
            }
        });

    public ToolSchema Number(string name, string description, bool required = false)
        => Add(name, required, writer =>
        {
            writer.WriteString("type", "number");
            writer.WriteString("description", description);
        });

    public ToolSchema Boolean(string name, string description, bool required = false)
        => Add(name, required, writer =>
        {
            writer.WriteString("type", "boolean");
            writer.WriteString("description", description);
        });

    public ToolSchema StringArray(string name, string description, bool required = false, int minItems = 0)
        => Add(name, required, writer =>
        {
            writer.WriteString("type", "array");
            writer.WriteString("description", description);
            if (minItems > 0)
            {
                writer.WriteNumber("minItems", minItems);
            }

            writer.WriteStartObject("items");
            writer.WriteString("type", "string");
            writer.WriteEndObject();
        });

    /// <summary>An array of integers; the count and the range of the items are checked by the tool.</summary>
    public ToolSchema IntegerArray(string name, string description, bool required = false)
        => Add(name, required, writer =>
        {
            writer.WriteString("type", "array");
            writer.WriteString("description", description);
            writer.WriteStartObject("items");
            writer.WriteString("type", "integer");
            writer.WriteEndObject();
        });

    /// <summary>An array of objects, each checked against <paramref name="item"/> (nested names are reported as <c>name[i].key</c>).</summary>
    public ToolSchema ObjectArray(string name, string description, ToolSchema item, bool required = false, int minItems = 0)
        => Add(name, required, writer =>
        {
            writer.WriteString("type", "array");
            writer.WriteString("description", description);
            if (minItems > 0)
            {
                writer.WriteNumber("minItems", minItems);
            }

            writer.WritePropertyName("items");
            item.WriteTo(writer);
        });

    /// <summary>An object with fixed keys, each checked against <paramref name="inner"/> (nested names are reported as <c>name.key</c>).</summary>
    public ToolSchema NestedObject(string name, string description, ToolSchema inner, bool required = false)
        => Add(name, required, writer =>
        {
            writer.WriteString("description", description);
            inner.WriteMembers(writer);
        });

    /// <summary>An object whose keys are not fixed by the schema (the tool checks them); values are checked by the tool.</summary>
    public ToolSchema FreeObject(string name, string description, bool required = false)
        => Add(name, required, writer =>
        {
            writer.WriteString("type", "object");
            writer.WriteString("description", description);
            writer.WriteBoolean("additionalProperties", true);
        });

    public string Build()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        WriteMembers(writer);
        writer.WriteEndObject();
    }

    /// <summary>The members of the schema object (type, properties, required, additionalProperties) without the surrounding braces.</summary>
    private void WriteMembers(Utf8JsonWriter writer)
    {
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        foreach (var (name, write) in _properties)
        {
            writer.WriteStartObject(name);
            write(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        if (_required.Count > 0)
        {
            writer.WriteStartArray("required");
            foreach (string name in _required)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
        }

        writer.WriteBoolean("additionalProperties", false);
    }

    private ToolSchema Add(string name, bool required, Action<Utf8JsonWriter> write)
    {
        _properties.Add((name, write));
        if (required)
        {
            _required.Add(name);
        }

        return this;
    }
}
