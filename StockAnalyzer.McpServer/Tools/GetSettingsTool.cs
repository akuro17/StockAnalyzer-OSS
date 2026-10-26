using System.Reflection;
using System.Text.Json;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_get_settings: the allow-listed application settings (limits and defaults), never paths or credentials.</summary>
internal sealed class GetSettingsTool : IMcpTool
{
    private const string SectionArgument = "section";

    private readonly IStockAnalyzerSettings _settings;

    public GetSettingsTool(IStockAnalyzerSettings settings)
    {
        _settings = settings;
    }

    public string Name => "sa_get_settings";

    public string Description =>
        "Returns selected StockAnalyzer settings that shape what the tools accept: the default symbol, screener and backtest condition limits, " +
        "the zigzag threshold (percent) and the prediction window. File locations, credentials and interface settings are never returned. " +
        "Without section, all sections are returned.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"section\":\"Screener\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SectionArgument, "Only this section.", allowedValues: McpSettingsExposure.Sections)
        .Build();

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        => Task.FromResult(Build(ToolArgs.String(arguments, SectionArgument)));

    /// <summary>The settings JSON of one section, or of every section when <paramref name="section"/> is null (also the content of the settings resource).</summary>
    public string Build(string? section = null)
    {
        IEnumerable<string> sections = section is null ? McpSettingsExposure.Sections : new[] { section };

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("settings");
            foreach (string name in sections)
            {
                writer.WriteStartObject(name);
                foreach (McpSettingsExposure.Entry entry in McpSettingsExposure.Allowed.Where(e => e.Section == name))
                {
                    WriteValue(writer, entry.JsonName, ReadProperty(entry.Property));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private object? ReadProperty(string propertyName)
    {
        PropertyInfo property = typeof(IStockAnalyzerSettings).GetProperty(propertyName)
            ?? throw new InvalidOperationException($"IStockAnalyzerSettings has no property {propertyName}.");
        return property.GetValue(_settings);
    }

    private static void WriteValue(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(name);
                break;
            case string text:
                writer.WriteString(name, text);
                break;
            case bool flag:
                writer.WriteBoolean(name, flag);
                break;
            case int number:
                writer.WriteNumber(name, number);
                break;
            case decimal number:
                writer.WriteNumber(name, number);
                break;
            case double number when double.IsFinite(number):
                writer.WriteNumber(name, number);
                break;
            case double:
                writer.WriteNull(name); // NaN and Infinity have no JSON form.
                break;
            case Enum choice:
                writer.WriteString(name, choice.ToString());
                break;
            default:
                throw new NotSupportedException($"Setting {name} has the unsupported type {value.GetType().Name}.");
        }
    }
}
