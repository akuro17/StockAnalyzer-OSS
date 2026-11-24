using System.Collections.Frozen;
using System.Text.Json;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.CommandLine;
using StockAnalyzer.McpServer.Execution;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Meta-tool for progressive discovery (<c>--progressive-discovery</c>): deterministically searches active callable tools by keyword,
/// profile, or exact tool names and optionally returns full parameter schemas without dumping the entire tool catalog upfront.
/// </summary>
internal sealed class SearchToolsTool : IMcpTool
{
    public const string ToolName = "sa_search_tools";
    public const int MaxQueryChars = 200;
    public const int MaxQueryTokens = 10;
    public const int MaxRequestedTools = 10;
    public const int MaxSchemaResults = 5;
    public const int DefaultSchemaResults = 5;
    public const int MaxSummaryResults = 50;
    public const int DefaultSummaryResults = 20;

    private const string MatchAll = "All";
    private const string MatchAny = "Any";

    private static readonly IReadOnlyList<string> SearchableProfiles = new[]
    {
        McpToolsetCatalog.AllProfile,
        McpToolsetCatalog.CoreProfile,
        McpToolsetCatalog.TechnicalProfile,
        McpToolsetCatalog.TechnicalExtendedProfile,
        McpToolsetCatalog.ScreenerProfile,
        McpToolsetCatalog.BacktestProfile,
        McpToolsetCatalog.PortfolioProfile,
        McpToolsetCatalog.MlProfile,
        McpToolsetCatalog.ExecutionProfile,
        McpToolsetCatalog.DiscussionProfile,
    };

    private static readonly IReadOnlyList<string> MatchModes = new[] { MatchAll, MatchAny };

    private readonly Func<McpToolRegistry> _registry;
    private readonly IReadOnlyDictionary<string, string> _profilesByName;
    private readonly McpResponseFormat _responseFormat;
    private readonly bool _hasNonReadOnly;

    public SearchToolsTool(
        Func<McpToolRegistry> registry,
        IReadOnlyDictionary<string, string> profilesByName,
        McpResponseFormat responseFormat = McpResponseFormat.Dual,
        bool hasNonReadOnly = false)
    {
        _registry = registry;
        _profilesByName = profilesByName;
        _responseFormat = responseFormat;
        _hasNonReadOnly = hasNonReadOnly;
    }

    public string Name => ToolName;

    public string Description => _hasNonReadOnly
        ? "Searches active tools by keyword/profile/names; use includeSchema: true (or toolNames) before sa_invoke_tool (read-only) / sa_invoke_action (state-changing)."
        : "Searches active StockAnalyzer tools by keyword, profile, or exact names; use includeSchema: true (or toolNames) before calling sa_invoke_tool.";

    public bool IsHeavy => false;

    public bool IsExecution => false;

    public bool IsReadOnly => true;

    public string CompactUsageHint =>
        "Retry sa_search_tools with fewer toolNames, limit 1..2, includeSchema: false, or includeExamples: false.";

    public string? ExampleArgumentsJson =>
        "{\"query\":\"backtest\",\"includeSchema\":true,\"limit\":3}";

    public string InputSchemaJson => ToolSchema.Object()
        .String("query", $"Space-separated keywords matched against tool name, profile, argument names, and description (max {MaxQueryTokens} words, {MaxQueryChars} chars). Omit to browse active tools.")
        .String("profile", $"Filter to one toolset or mode profile. Default {McpToolsetCatalog.AllProfile}.", allowedValues: SearchableProfiles)
        .StringArray("toolNames", $"Exact tool names to inspect (1..{MaxRequestedTools}). Turns includeSchema on unless includeSchema is false.", minItems: 1)
        .String("matchMode", $"{MatchAll}: every query word must match. {MatchAny}: at least one word must match. Default {MatchAny}.", allowedValues: MatchModes)
        .Boolean("includeSchema", $"Include full inputSchema (and outputSchema in dual mode) per match; caps limit at {MaxSchemaResults} for query/profile searches ({MaxRequestedTools} when toolNames is given). Default: true when toolNames is given, otherwise false.")
        .Boolean("includeExamples", "Include exampleArguments when available. Default false.")
        .Integer("offset", "0-based match offset. Default 0.", defaultValue: 0, minimum: 0)
        .Integer("limit", $"Max results returned (default and max {DefaultSchemaResults} when includeSchema is true, or {MaxRequestedTools} when toolNames is given; default {DefaultSummaryResults}, max {MaxSummaryResults} when includeSchema is false).", minimum: 1, maximum: MaxSummaryResults)
        .Build();

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string[] queryTokens = ParseQueryTokens(ToolArgs.String(arguments, "query"));
        bool requireAll = string.Equals(ToolArgs.String(arguments, "matchMode"), MatchAll, StringComparison.Ordinal);
        string profile = ToolArgs.String(arguments, "profile") ?? McpToolsetCatalog.AllProfile;
        IReadOnlyList<string>? rawToolNames = ToolArgs.StringArray(arguments, "toolNames");

        bool includeSchema = ToolArgs.Boolean(arguments, "includeSchema") ?? (rawToolNames is not null);
        bool includeExamples = ToolArgs.Boolean(arguments, "includeExamples") ?? false;
        int offset = ToolArgs.NonNegativeWithin(arguments, "offset", 0, int.MaxValue);
        int maxLimit = includeSchema
            ? (rawToolNames is not null ? MaxRequestedTools : MaxSchemaResults)
            : MaxSummaryResults;
        int defaultLimit = includeSchema
            ? (rawToolNames is not null ? MaxRequestedTools : DefaultSchemaResults)
            : DefaultSummaryResults;
        long? requestedLimit = ToolArgs.Integer(arguments, "limit");
        if (includeSchema && rawToolNames is null && requestedLimit is > MaxSchemaResults and <= MaxSummaryResults)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"Argument 'limit' ({requestedLimit.Value}) exceeds the schema-search limit {MaxSchemaResults}.",
                "limit",
                $"When includeSchema is true without toolNames, use limit <= {MaxSchemaResults} (or pass toolNames for up to {MaxRequestedTools}, or set includeSchema: false for up to {MaxSummaryResults}).");
        }

        int limit = ToolArgs.PositiveWithin(arguments, "limit", defaultLimit, maxLimit);

        McpToolRegistry registry = _registry();
        List<RegisteredTool> activeDomainTools = registry.CallableTools
            .Where(r => r.Tool is not SearchToolsTool and not InvokeToolProxyTool)
            .ToList();
        HashSet<string>? requestedSet = ResolveRequestedToolNames(rawToolNames, activeDomainTools, out List<string> unmatchedToolNames);

        List<ScoredMatch> matched = MatchTools(activeDomainTools, profile, requestedSet, queryTokens, requireAll);
        List<ScoredMatch> page = matched.Skip(offset).Take(limit).ToList();
        bool hasMore = (long)offset + page.Count < matched.Count;

        IReadOnlyList<string>? availableProfiles = matched.Count == 0 && !string.Equals(profile, McpToolsetCatalog.AllProfile, StringComparison.Ordinal)
            ? activeDomainTools.Select(t => ResolveProfile(t.Tool.Name)).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList()
            : null;

        string json = BuildResponse(
            activeDomainTools.Count,
            matched.Count,
            offset,
            limit,
            hasMore,
            page,
            includeSchema,
            includeExamples,
            unmatchedToolNames,
            availableProfiles);

        return Task.FromResult(json);
    }

    private static string[] ParseQueryTokens(string? rawQuery)
    {
        if (rawQuery is null)
        {
            return Array.Empty<string>();
        }

        if (rawQuery.Length > MaxQueryChars)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"Argument 'query' exceeds {MaxQueryChars} characters.",
                "query",
                $"Use {MaxQueryChars} characters or less.");
        }

        string[] split = rawQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (split.Length == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = new List<string>(split.Length);
        foreach (string token in split)
        {
            if (seen.Add(token))
            {
                tokens.Add(token);
            }
        }

        if (tokens.Count > MaxQueryTokens)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"Argument 'query' contains more than {MaxQueryTokens} unique words.",
                "query",
                $"Use {MaxQueryTokens} words or less.");
        }

        return tokens.ToArray();
    }

    private static HashSet<string>? ResolveRequestedToolNames(
        IReadOnlyList<string>? rawToolNames,
        IReadOnlyList<RegisteredTool> activeDomainTools,
        out List<string> unmatchedToolNames)
    {
        unmatchedToolNames = new List<string>();
        if (rawToolNames is null)
        {
            return null;
        }

        if (rawToolNames.Count > MaxRequestedTools)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"Argument 'toolNames' contains {rawToolNames.Count} entries, exceeding the limit {MaxRequestedTools}.",
                "toolNames",
                $"Request at most {MaxRequestedTools} tools per call.");
        }

        var activeNames = new HashSet<string>(activeDomainTools.Select(t => t.Tool.Name), StringComparer.Ordinal);
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var unmatchedSeen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < rawToolNames.Count; i++)
        {
            string candidate = rawToolNames[i];
            if (string.IsNullOrWhiteSpace(candidate))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"Argument 'toolNames[{i}]' must not be empty.",
                    $"toolNames[{i}]");
            }

            if (activeNames.Contains(candidate))
            {
                requested.Add(candidate);
            }
            else if (unmatchedSeen.Add(candidate))
            {
                unmatchedToolNames.Add(candidate);
            }
        }

        return requested;
    }

    private List<ScoredMatch> MatchTools(
        IReadOnlyList<RegisteredTool> activeDomainTools,
        string profile,
        HashSet<string>? requestedSet,
        string[] queryTokens,
        bool requireAll)
    {
        bool filterByProfile = !string.Equals(profile, McpToolsetCatalog.AllProfile, StringComparison.Ordinal);
        var matches = new List<ScoredMatch>();

        foreach (RegisteredTool registered in activeDomainTools)
        {
            string entryProfile = ResolveProfile(registered.Tool.Name);
            if (filterByProfile && !string.Equals(entryProfile, profile, StringComparison.Ordinal))
            {
                continue;
            }

            if (requestedSet is not null && !requestedSet.Contains(registered.Tool.Name))
            {
                continue;
            }

            if (!TryScoreTool(registered, entryProfile, queryTokens, requireAll, out int matchedTokens, out int score))
            {
                continue;
            }

            matches.Add(new ScoredMatch(registered, entryProfile, matchedTokens, score));
        }

        matches.Sort(static (left, right) =>
        {
            int tokenOrder = right.MatchedTokens.CompareTo(left.MatchedTokens);
            if (tokenOrder != 0)
            {
                return tokenOrder;
            }

            int scoreOrder = right.Score.CompareTo(left.Score);
            return scoreOrder != 0
                ? scoreOrder
                : string.Compare(left.Registered.Tool.Name, right.Registered.Tool.Name, StringComparison.Ordinal);
        });

        return matches;
    }

    private static bool TryScoreTool(RegisteredTool registered, string profile, string[] tokens, bool requireAll, out int matchedTokens, out int totalScore)
    {
        if (tokens.Length == 0)
        {
            matchedTokens = 0;
            totalScore = 0;
            return true;
        }

        string name = registered.Tool.Name;
        string summary = McpHelpText.Summary(registered.Tool.Description);
        string description = registered.Tool.Description;
        IReadOnlyList<string> propertyNames = ExtractPropertyNames(registered.Schema);

        int sum = 0;
        matchedTokens = 0;
        foreach (string token in tokens)
        {
            int tier = ScoreToken(name, profile, propertyNames, summary, description, token);
            if (tier > 0)
            {
                matchedTokens++;
                sum += tier;
            }
            else if (requireAll)
            {
                matchedTokens = 0;
                totalScore = 0;
                return false;
            }
        }

        if (matchedTokens == 0)
        {
            totalScore = 0;
            return false;
        }

        totalScore = sum;
        return true;
    }

    private static int ScoreToken(
        string toolName,
        string profile,
        IReadOnlyList<string> propertyNames,
        string summary,
        string description,
        string token)
    {
        if (string.Equals(toolName, token, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (HasAliasMatch(toolName, token))
        {
            return 50;
        }

        if (ContainsTokenMatch(toolName, token))
        {
            return 40;
        }

        if (EqualsFolded(profile, token))
        {
            return 20;
        }

        foreach (string property in propertyNames)
        {
            if (ContainsTokenMatch(property, token))
            {
                return 15;
            }
        }

        if (ContainsTokenMatch(summary, token))
        {
            return 10;
        }

        return ContainsTokenMatch(description, token) ? 5 : 0;
    }

    private static bool HasAliasMatch(string toolName, string token)
    {
        if (!McpToolSearchAliases.ByToolName.TryGetValue(toolName, out FrozenSet<string>? aliases))
        {
            return false;
        }

        return aliases.Contains(token) || aliases.Contains(NormalizeSeparators(token));
    }

    private static bool ContainsTokenMatch(string haystack, string needle)
    {
        if (needle.Length >= 4)
        {
            return ContainsFolded(haystack, needle);
        }

        if (EqualsFolded(haystack, needle))
        {
            return true;
        }

        ReadOnlySpan<char> span = haystack.AsSpan();
        ReadOnlySpan<char> needleSpan = needle.AsSpan();
        int start = -1;
        for (int i = 0; i <= span.Length; i++)
        {
            bool atEnd = i == span.Length;
            bool isWordChar = !atEnd && char.IsAsciiLetterOrDigit(span[i]);
            bool isAcronymSplit = !atEnd && start >= 0 && i > start
                && char.IsAsciiLetterUpper(span[i - 1]) && char.IsAsciiLetterUpper(span[i])
                && i + 1 < span.Length && char.IsAsciiLetterLower(span[i + 1]);
            bool isCamelSplit = !atEnd && start >= 0 && i > start
                && ((char.IsAsciiLetterLower(span[i - 1]) && char.IsAsciiLetterUpper(span[i])) || isAcronymSplit);

            if (start >= 0 && (atEnd || !isWordChar || isCamelSplit))
            {
                if (span[start..i].Equals(needleSpan, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                start = isWordChar ? i : -1;
            }
            else if (start < 0 && isWordChar)
            {
                start = i;
            }
        }

        return false;
    }

    private static string NormalizeSeparators(string value) => value.Replace('-', '_');

    private static bool ContainsFolded(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || NormalizeSeparators(haystack).Contains(NormalizeSeparators(needle), StringComparison.OrdinalIgnoreCase);

    private static bool EqualsFolded(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeSeparators(left), NormalizeSeparators(right), StringComparison.OrdinalIgnoreCase);

    private string ResolveProfile(string toolName)
        => _profilesByName.TryGetValue(toolName, out string? profile) ? profile : McpToolsetCatalog.CoreProfile;

    private string BuildResponse(
        int totalAvailableTools,
        int matchedCount,
        int offset,
        int limit,
        bool hasMore,
        IReadOnlyList<ScoredMatch> page,
        bool includeSchema,
        bool includeExamples,
        IReadOnlyList<string> unmatchedToolNames,
        IReadOnlyList<string>? availableProfiles)
    {
        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("totalAvailableTools", totalAvailableTools);
            writer.WriteNumber("matchedCount", matchedCount);
            writer.WriteNumber("offset", offset);
            writer.WriteNumber("limit", limit);
            writer.WriteNumber("returnedCount", page.Count);
            writer.WriteBoolean("hasMore", hasMore);

            if (unmatchedToolNames.Count > 0)
            {
                writer.WriteStartArray("unmatchedToolNames");
                foreach (string missing in unmatchedToolNames)
                {
                    writer.WriteStringValue(missing);
                }

                writer.WriteEndArray();
            }

            if (availableProfiles is not null)
            {
                writer.WriteStartArray("availableProfiles");
                foreach (string activeProfile in availableProfiles)
                {
                    writer.WriteStringValue(activeProfile);
                }

                writer.WriteEndArray();
            }

            writer.WriteStartArray("tools");
            foreach (ScoredMatch entry in page)
            {
                WriteToolEntry(writer, entry.Registered, entry.Profile, includeSchema, includeExamples);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private void WriteToolEntry(
        Utf8JsonWriter writer,
        RegisteredTool registered,
        string profile,
        bool includeSchema,
        bool includeExamples)
    {
        IMcpTool tool = registered.Tool;
        IReadOnlyList<string> argumentNames = ExtractPropertyNames(registered.Schema);
        IReadOnlyList<string> requiredArguments = ExtractRequiredNames(registered.Schema);

        writer.WriteStartObject();
        writer.WriteString("name", tool.Name);
        writer.WriteString("profile", profile);
        writer.WriteString("summary", McpHelpText.Summary(tool.Description));
        writer.WriteString("docUri", ToolJson.DocUri(tool.Name));
        writer.WriteBoolean("isHeavy", tool.IsHeavy);
        writer.WriteBoolean("isExecution", tool.IsExecution);
        writer.WriteBoolean("isReadOnly", tool.IsReadOnly);

        writer.WriteStartArray("argumentNames");
        foreach (string arg in argumentNames)
        {
            writer.WriteStringValue(arg);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("requiredArguments");
        foreach (string req in requiredArguments)
        {
            writer.WriteStringValue(req);
        }

        writer.WriteEndArray();

        if (includeSchema)
        {
            writer.WritePropertyName("inputSchema");
            registered.Schema.WriteTo(writer);
            if (_responseFormat == McpResponseFormat.Dual && registered.OutputSchema is { } outputSchema)
            {
                writer.WritePropertyName("outputSchema");
                outputSchema.WriteTo(writer);
            }
        }

        if (includeExamples && !string.IsNullOrWhiteSpace(tool.ExampleArgumentsJson))
        {
            writer.WritePropertyName("exampleArguments");
            writer.WriteRawValue(tool.ExampleArgumentsJson);
        }

        writer.WriteEndObject();
    }

    private static IReadOnlyList<string> ExtractPropertyNames(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (JsonProperty property in properties.EnumerateObject())
        {
            names.Add(property.Name);
        }

        return names;
    }

    private static IReadOnlyList<string> ExtractRequiredNames(JsonElement schema)
    {
        if (!schema.TryGetProperty("required", out JsonElement required) || required.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (JsonElement item in required.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private readonly record struct ScoredMatch(RegisteredTool Registered, string Profile, int MatchedTokens, int Score);
}
