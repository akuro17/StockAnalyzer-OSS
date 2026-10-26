using System.Reflection;
using System.Text;
using System.Text.Json;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.CommandLine;

/// <summary>
/// The text of <c>--help</c> and <c>--version</c> (English, LF, one item per line, never wrapped). The tool list and the argument
/// tables are generated from the tool registry, so a tool added later appears without touching this class.
/// </summary>
internal static class McpHelpText
{
    private const string ExecutableName = "StockAnalyzer.McpServer";
    private const int MaxSummaryChars = 160;
    private const string SummaryEllipsis = "...";
    private const string SummaryTerminator = ". ";
    private const string NewLine = "\n";

    public static string Version()
    {
        string version = typeof(McpHelpText).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(McpHelpText).Assembly.GetName().Version?.ToString()
            ?? string.Empty;
        return $"{ExecutableName} {version}{NewLine}MCP protocol {string.Join(", ", McpProtocolVersions.Supported)}{NewLine}";
    }

    public static string Overview(McpToolRegistry registry, bool includeExecutionTools)
    {
        var text = new StringBuilder();
        text.Append($"Usage: {ExecutableName} [options]{NewLine}{NewLine}");

        text.Append($"Options:{NewLine}");
        var options = new List<(string Token, string Description)>();
        foreach (string token in McpCommandLine.HelpTokens)
        {
            options.Add(($"{token} [tool]", token == McpCommandLine.HelpToken ? "Print this help, or the arguments of one tool, and exit." : $"Same as {McpCommandLine.HelpToken}."));
        }

        options.Add((McpCommandLine.VersionToken, "Print the version and the supported MCP protocol versions, and exit. Cannot be combined with another argument."));
        options.Add((McpCommandLine.EnableExecutionToken, "Offer the opt-in execution tools: they train a model and run it in a temporary folder of this computer (nothing is saved under Data); listed below when this option is given."));
        AppendTable(text, options);

        text.Append($"{NewLine}Configuration:{NewLine}");
        text.Append($"  {McpServerComposition.ConfigurationFileName} next to the executable, section {Limits.McpLimits.SectionName}; every value is optional.{NewLine}");

        text.Append($"{NewLine}Exit codes:{NewLine}");
        AppendTable(text, new (string, string)[]
        {
            (McpCommandLine.ExitOk.ToString(), "Normal end (also --help and --version)."),
            (McpCommandLine.ExitInvalidConfiguration.ToString(), "A configured limit is outside its allowed band."),
            (McpCommandLine.ExitInvalidUsage.ToString(), "Wrong command-line arguments."),
        });

        text.Append($"{NewLine}Tools (--help <tool> shows the arguments):{NewLine}");
        AppendTable(text, VisibleTools(registry, includeExecutionTools).Select(tool => (tool.Name, Summary(tool.Description))));
        return text.ToString();
    }

    /// <summary>The detail of one tool; false (with the message for standard error) when the name is not an offered tool.</summary>
    public static bool TryTool(McpToolRegistry registry, bool includeExecutionTools, string name, out string text, out string error)
    {
        if (!registry.TryGet(name, out RegisteredTool? registered) || (registered.Tool.IsExecution && !includeExecutionTools))
        {
            text = string.Empty;
            error = McpCommandLine.UnknownToolMessage(name);
            return false;
        }

        IMcpTool tool = registered.Tool;
        var builder = new StringBuilder();
        builder.Append($"{tool.Name}{NewLine}{NewLine}{tool.Description}{NewLine}{NewLine}Arguments:{NewLine}");
        JsonElement schema = registered.Schema;
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out JsonElement requiredNames))
        {
            foreach (JsonElement requiredName in requiredNames.EnumerateArray())
            {
                required.Add(requiredName.GetString()!);
            }
        }

        bool any = false;
        if (schema.TryGetProperty("properties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                any = true;
                builder.Append($"  {property.Name} ({Facts(property.Value, required.Contains(property.Name))}) {Describe(property.Value)}{NewLine}");
            }
        }

        if (!any)
        {
            builder.Append($"  (none){NewLine}");
        }

        if (!string.IsNullOrWhiteSpace(tool.ExampleArgumentsJson))
        {
            builder.Append($"{NewLine}Example arguments:{NewLine}  {tool.ExampleArgumentsJson}{NewLine}");
        }

        text = builder.ToString();
        error = string.Empty;
        return true;
    }

    /// <summary>The text up to the first ". " (the whole text when there is none), cut to <see cref="MaxSummaryChars"/> characters plus an ellipsis.</summary>
    public static string Summary(string description)
    {
        int end = description.IndexOf(SummaryTerminator, StringComparison.Ordinal);
        string summary = end < 0 ? description : description[..end];
        return summary.Length > MaxSummaryChars ? summary[..MaxSummaryChars] + SummaryEllipsis : summary;
    }

    private static IEnumerable<IMcpTool> VisibleTools(McpToolRegistry registry, bool includeExecutionTools)
        => registry.Tools.Select(registered => registered.Tool).Where(tool => includeExecutionTools || !tool.IsExecution);

    private static void AppendTable(StringBuilder text, IEnumerable<(string Token, string Description)> rows)
    {
        List<(string Token, string Description)> list = rows.ToList();
        int width = list.Count == 0 ? 0 : list.Max(row => row.Token.Length);
        foreach ((string token, string description) in list)
        {
            text.Append($"  {token.PadRight(width)}  {description}".TrimEnd()).Append(NewLine);
        }
    }

    /// <summary>Type, required or optional, then default, minimum, maximum and allowed values when the schema states them.</summary>
    private static string Facts(JsonElement property, bool required)
    {
        var facts = new List<string>
        {
            property.TryGetProperty("type", out JsonElement type) ? type.GetString()! : "any",
            required ? "required" : "optional",
        };
        AddRaw(facts, property, "default");
        AddRaw(facts, property, "minimum");
        AddRaw(facts, property, "maximum");
        if (property.TryGetProperty("enum", out JsonElement allowed))
        {
            facts.Add("allowed " + string.Join("|", allowed.EnumerateArray().Select(value => value.GetString())));
        }

        return string.Join(", ", facts);
    }

    private static void AddRaw(List<string> facts, JsonElement property, string key)
    {
        if (property.TryGetProperty(key, out JsonElement value))
        {
            facts.Add($"{key} {value.GetRawText()}");
        }
    }

    private static string Describe(JsonElement property)
        => property.TryGetProperty("description", out JsonElement description) ? description.GetString() ?? string.Empty : string.Empty;
}
