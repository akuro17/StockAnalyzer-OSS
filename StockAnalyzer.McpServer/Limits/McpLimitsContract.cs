namespace StockAnalyzer.McpServer.Limits;

/// <summary>A configured limit is outside its hard band or breaks a relation between limits; names the configuration key.</summary>
public sealed class McpLimitsException : Exception
{
    public McpLimitsException(string key, string message) : base($"{McpLimits.SectionName}:{key} {message}")
    {
        Key = key;
    }

    public string Key { get; }
}

/// <summary>
/// The hard bands of <see cref="McpLimits"/>: safety bounds that configuration cannot widen. The bands marked
/// "proposed" in the plan are owner-adjustable here; configuration only moves values inside them.
/// </summary>
public static class McpLimitsContract
{
    /// <summary>Exactly one tool call executes at a time (the pipeline is not thread-safe per call and calls are not cancellable); not configurable.</summary>
    public const int MaxConcurrentToolCalls = 1;

    /// <summary>Maximum nesting depth when traversing the group tree (All Tickers / category roots are depth 0); deeper subtrees are truncated.</summary>
    public const int MaxGroupTreeDepth = 16;

    private const int Kibibyte = 1024;
    private const int Mebibyte = 1024 * Kibibyte;

    public static void Validate(McpLimits limits)
    {
        Band(nameof(McpLimits.MaxCandlesPerRequest), limits.MaxCandlesPerRequest, 1, 10000);
        Band(nameof(McpLimits.DefaultCandleCount), limits.DefaultCandleCount, 1, limits.MaxCandlesPerRequest);
        Band(nameof(McpLimits.MaxIndicatorsPerCall), limits.MaxIndicatorsPerCall, 1, 64);
        Band(nameof(McpLimits.MaxSymbolsPerMetadataCall), limits.MaxSymbolsPerMetadataCall, 1, 500);
        Band(nameof(McpLimits.MaxReturnedTrades), limits.MaxReturnedTrades, 0, 1000);
        Band(nameof(McpLimits.MaxReturnedWarnings), limits.MaxReturnedWarnings, 0, 200);
        Band(nameof(McpLimits.MaxOutputTailBars), limits.MaxOutputTailBars, 1, limits.MaxCandlesPerRequest);
        Band(nameof(McpLimits.DefaultOutputTailBars), limits.DefaultOutputTailBars, 1, limits.MaxOutputTailBars);
        Band(nameof(McpLimits.ToolTimeoutSeconds), limits.ToolTimeoutSeconds, 1, 300);
        Band(nameof(McpLimits.HeavyToolTimeoutSeconds), limits.HeavyToolTimeoutSeconds, limits.ToolTimeoutSeconds, 600);
        Band(nameof(McpLimits.QueueWaitSeconds), limits.QueueWaitSeconds, 0, 120);
        Band(nameof(McpLimits.MaxResponseBytes), limits.MaxResponseBytes, 64 * Kibibyte, 8 * Mebibyte);
        Band(nameof(McpLimits.MaxRequestBytes), limits.MaxRequestBytes, 4 * Kibibyte, 8 * Mebibyte);
        Band(nameof(McpLimits.MaxScreenerSymbols), limits.MaxScreenerSymbols, 1, 5000);
        Band(nameof(McpLimits.MaxReturnedMatches), limits.MaxReturnedMatches, 1, 1000);
        Band(nameof(McpLimits.MaxSecondarySymbolsPerCall), limits.MaxSecondarySymbolsPerCall, 1, 8);
        Band(nameof(McpLimits.MaxTickersPerPage), limits.MaxTickersPerPage, 1, 5000);
        Band(nameof(McpLimits.DefaultTickerPageSize), limits.DefaultTickerPageSize, 1, limits.MaxTickersPerPage);
        Band(nameof(McpLimits.MaxGroupNodes), limits.MaxGroupNodes, 1, 5000);
        Band(nameof(McpLimits.MaxFilterEvaluationSymbols), limits.MaxFilterEvaluationSymbols, 1, 5000);
    }

    private static void Band(string key, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new McpLimitsException(key, $"= {value} is outside {minimum}..{maximum}.");
        }
    }
}
