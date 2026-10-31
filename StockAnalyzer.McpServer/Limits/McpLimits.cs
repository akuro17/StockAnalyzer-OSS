using StockAnalyzer.Core.Models.Watchlist;

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

    /// <summary>Most derived series (binary operations on indicator series) one sa_calculate_indicators call may define; more is LIMIT_EXCEEDED (the list is never cut).</summary>
    public int MaxDerivedSeriesPerCall { get; set; } = 16;

    /// <summary>
    /// The trend window, in bars, of the series summary of sa_calculate_indicators (summary true) when the call does not say (trendBars). Where the calculation window is
    /// shorter than this, the whole window is used. The window in force never exceeds the largest <c>trendBars</c> a call may name (<see cref="McpLimitsContract.EffectiveMaxTrendBars"/>).
    /// </summary>
    public int DefaultSummaryTrendBars { get; set; } = 20;

    /// <summary>Most entries one sa_calculate_indicators lookbackOffsets list may hold; more is LIMIT_EXCEEDED (the list is never cut).</summary>
    public int MaxLookbackOffsets { get; set; } = 32;

    public int MaxSymbolsPerMetadataCall { get; set; } = 100;

    public int MaxReturnedTrades { get; set; } = 200;

    /// <summary>Most signal events (condition matched bars) sa_extract_signal_events returns.</summary>
    public int MaxReturnedEvents { get; set; } = 200;

    /// <summary>The maximum horizon in bars for any forward return calculation in sa_extract_signal_events.</summary>
    public int MaxForwardBars { get; set; } = 60;

    /// <summary>Most entries sa_extract_signal_events forwardBars list may hold; more is LIMIT_EXCEEDED.</summary>
    public int MaxForwardBarsList { get; set; } = 8;

    /// <summary>Most engine warnings (one per affected bar, so there can be thousands) that sa_run_backtest lists; the counts by code are always complete.</summary>
    public int MaxReturnedWarnings { get; set; } = 20;

    public int MaxOutputTailBars { get; set; } = 5000;

    /// <summary>How many trailing bars of each indicator series a result carries when the client does not say.</summary>
    public int DefaultOutputTailBars { get; set; } = 200;

    public int ToolTimeoutSeconds { get; set; } = 60;

    public int HeavyToolTimeoutSeconds { get; set; } = 180;

    public int QueueWaitSeconds { get; set; } = 60;

    public int MaxResponseBytes { get; set; } = 4 * 1024 * 1024;

    public int MaxRequestBytes { get; set; } = 1024 * 1024;

    public int MaxScreenerSymbols { get; set; } = 500;

    public int MaxReturnedMatches { get; set; } = 200;

    public int MaxSecondarySymbolsPerCall { get; set; } = 4;

    /// <summary>Most different comparison symbols (the primary symbol not counted) one relative-performance call aligns; more is LIMIT_EXCEEDED (never cut). Standard value = the chart's limit (the aligner's default, WatchlistConstants.MaxRelativePerformanceTargets).</summary>
    public int MaxComparisonTargets { get; set; } = WatchlistConstants.MaxRelativePerformanceTargets;

    public int DefaultTickerPageSize { get; set; } = 100;

    public int MaxTickersPerPage { get; set; } = 1000;

    /// <summary>Most nodes sa_list_ticker_groups lists; more is LIMIT_EXCEEDED (the tree is never cut).</summary>
    public int MaxGroupNodes { get; set; } = 500;

    /// <summary>Most symbols a Tickers-tab filter is evaluated on; more is LIMIT_EXCEEDED (the list is never cut).</summary>
    public int MaxFilterEvaluationSymbols { get; set; } = 1000;

    /// <summary>Most rules one sa_filter_tickers call evaluates; more is LIMIT_EXCEEDED (the list is never cut).</summary>
    public int MaxInlineFilterRules { get; set; } = 32;

    /// <summary>Most points one sa_run_backtest equity curve lists; more is LIMIT_EXCEEDED (the curve is never cut).</summary>
    public int MaxEquityCurvePoints { get; set; } = 5000;

    /// <summary>Most drawdown episodes one sa_run_backtest call may ask for (includeDrawdownEpisodes); more is LIMIT_EXCEEDED.</summary>
    public int MaxDrawdownEpisodes { get; set; } = 5;

    /// <summary>
    /// Most parameter combinations one sa_optimize_backtest call runs (the product of the value counts of its parameterRanges); more is LIMIT_EXCEEDED
    /// (the grid is never thinned). Standard value: a full-history daily run costs about 0.15 s (measured 2026-10-30), so 500 runs take about 75 s on one
    /// thread and about 25 s with <see cref="OptimizationDegreeOfParallelism"/> runs at a time, inside half of <see cref="HeavyToolTimeoutSeconds"/>.
    /// </summary>
    public int MaxOptimizationCombinations { get; set; } = 500;

    /// <summary>Most ranked combinations one sa_optimize_backtest call returns (topK); more is LIMIT_EXCEEDED.</summary>
    public int MaxOptimizationTopK { get; set; } = 10;

    /// <summary>Most parameters (entries of parameterRanges) one sa_optimize_backtest call varies; more is LIMIT_EXCEEDED.</summary>
    public int MaxOptimizationParameters { get; set; } = 4;

    /// <summary>How many backtests of one optimisation run at the same time. The result never depends on it (measured: 4 threads about 2.9x, 8 threads about 4.6x faster than 1).</summary>
    public int OptimizationDegreeOfParallelism { get; set; } = 4;

    /// <summary>
    /// Most windows one sa_run_walk_forward_analysis call runs; more is LIMIT_EXCEEDED (the windows are never cut). Every window runs the whole grid on its
    /// in-sample part, so a call costs at most this many times <see cref="MaxOptimizationCombinations"/> backtests, each about 0.07 ms per in-sample bar.
    /// Standard value, measured 2026-10-30 on 7203-T with 500 combinations and 4 threads: 12 windows of 1000 in-sample bars took 102 s (more than half of
    /// <see cref="HeavyToolTimeoutSeconds"/>), 12 windows of 500 bars 58 s; 8 windows of 1000 bars (about 68 s) stay inside half of it.
    /// </summary>
    public int MaxWalkForwardWindows { get; set; } = 8;

    /// <summary>
    /// The fewest in-sample trades a combination needs to be the best one of a walk-forward window when the call does not say (minInSampleTrades). 1: a
    /// combination that never traded has an objective such as totalReturn 0 that says nothing about the strategy (owner decision 2026-10-30).
    /// </summary>
    public int DefaultMinInSampleTrades { get; set; } = 1;

    /// <summary>
    /// Most vertical price bins one sa_calculate_volume_profile call may divide the price range into (binCount).
    /// Standard value: 200 (the maximum row count of CoreVolumeProfileParameter).
    /// </summary>
    public int MaxVolumeProfileBins { get; set; } = 200;

    /// <summary>
    /// Most support and resistance levels one sa_find_support_resistance call may return (maxLevels).
    /// Standard value: 20.
    /// </summary>
    public int MaxSupportResistanceLevels { get; set; } = 20;

    /// <summary>
    /// Most dominant frequency bins one sa_analyze_cycles call may return (topBins).
    /// Standard value: 50.
    /// </summary>
    public int MaxCycleBins { get; set; } = 50;

    /// <summary>
    /// Most chart patterns one sa_detect_chart_patterns call may return (maxPatterns).
    /// Standard value: 100.
    /// </summary>
    public int MaxReturnedPatterns { get; set; } = 100;

    /// <summary>
    /// Most future projection bars one sa_project_price_path call may request (horizonBars).
    /// Standard value: 100.
    /// </summary>
    public int MaxProjectionBars { get; set; } = 100;

    /// <summary>
    /// Most historical calendar years one sa_get_seasonality call may overlay (years).
    /// Standard value: 50.
    /// </summary>
    public int MaxSeasonalityYears { get; set; } = 50;
}
