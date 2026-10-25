namespace StockAnalyzer.McpServer.Limits;

/// <summary>
/// Tunable limits, bound from the <c>AiIntegration</c> configuration section. The property initialisers are the standard
/// values; the hard bands they must stay inside live in <see cref="McpLimitsContract"/> and are checked at start-up.
/// Values marked "provisional" in the decision register are confirmed by measurement in later Runs.
/// </summary>
public sealed class McpLimits
{
    public const string SectionName = "AiIntegration";

    public int MaxCandlesPerRequest { get; set; } = 10000;

    public int DefaultCandleCount { get; set; } = 200;

    public int MaxIndicatorsPerCall { get; set; } = 16;

    public int MaxSymbolsPerMetadataCall { get; set; } = 100;

    public int MaxReturnedTrades { get; set; } = 200;

    /// <summary>Most engine warnings (one per affected bar, so there can be thousands) that sa_run_backtest lists; the counts by code are always complete.</summary>
    public int MaxReturnedWarnings { get; set; } = 20;

    public int MaxOutputTailBars { get; set; } = 500;

    /// <summary>How many trailing bars of each indicator series a result carries when the client does not say (plan S-05: 50).</summary>
    public int DefaultOutputTailBars { get; set; } = 50;

    public int ToolTimeoutSeconds { get; set; } = 30;

    public int HeavyToolTimeoutSeconds { get; set; } = 60;

    public int QueueWaitSeconds { get; set; } = 30;

    public int MaxResponseBytes { get; set; } = 4 * 1024 * 1024;

    public int MaxRequestBytes { get; set; } = 1024 * 1024;

    public int MaxScreenerSymbols { get; set; } = 500;

    public int MaxReturnedMatches { get; set; } = 200;

    public int MaxSecondarySymbolsPerCall { get; set; } = 4;

    public int DefaultTickerPageSize { get; set; } = 100;

    public int MaxTickersPerPage { get; set; } = 1000;

    /// <summary>Most nodes sa_list_ticker_groups lists; more is LIMIT_EXCEEDED (the tree is never cut).</summary>
    public int MaxGroupNodes { get; set; } = 500;

    /// <summary>Most symbols a Tickers-tab filter is evaluated on; more is LIMIT_EXCEEDED (the list is never cut).</summary>
    public int MaxFilterEvaluationSymbols { get; set; } = 1000;
}
