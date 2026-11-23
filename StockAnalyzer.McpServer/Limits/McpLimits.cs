using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Backtest.Reporting;

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

    /// <summary>
    /// How many light read-only tool calls (not heavy, not execution) may run at the same time. A heavy or execution tool waits until none runs and then runs alone.
    /// 1 makes every call strictly one at a time (the behaviour before this limit existed). Standard value: 2 (owner decision U-01, 2026-11-16).
    /// </summary>
    public int MaxConcurrentReadOnlyCalls { get; set; } = 2;

    public int MaxResponseBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Estimated maximum tokens (4 UTF-8 bytes per token, rounded up) of the JSON text part of one tool response.
    /// Default equals <see cref="MaxResponseBytes"/> / 4 so existing responses are unaffected; lower in configuration to enforce a strict token budget.
    /// </summary>
    public int MaxResponseTokens { get; set; } = McpResponseTokenLimits.Default;

    public int MaxRequestBytes { get; set; } = 1024 * 1024;

    public int MaxScreenerSymbols { get; set; } = 500;

    public int MaxReturnedMatches { get; set; } = 200;

    public int MaxSecondarySymbolsPerCall { get; set; } = 4;

    /// <summary>
    /// Most symbols one sa_calculate_cross_asset_stats call may analyze; more is LIMIT_EXCEEDED (the list is never cut).
    /// Standard value: 50.
    /// </summary>
    public int MaxCrossAssetSymbols { get; set; } = 50;

    /// <summary>
    /// Most symbols one sa_calculate_market_breadth call may analyze; more is LIMIT_EXCEEDED (the list is never cut).
    /// Standard value: 500.
    /// </summary>
    public int MaxBreadthSymbols { get; set; } = 500;

    /// <summary>
    /// Most symbols one sa_run_portfolio_backtest call may analyze; more is LIMIT_EXCEEDED (the list is never cut).
    /// Standard value: 50.
    /// </summary>
    public int MaxPortfolioBacktestSymbols { get; set; } = 50;

    /// <summary>
    /// Most symbols one sa_calculate_indicators_batch call may evaluate; more is LIMIT_EXCEEDED (the list is never cut).
    /// Standard value: 50 (owner decision U-05, 2026-11-16).
    /// </summary>
    public int MaxBatchIndicatorSymbols { get; set; } = 50;

    /// <summary>
    /// Most trailing bars per series one sa_calculate_indicators_batch call may return (outputTailCount); more is LIMIT_EXCEEDED. Small on purpose: every symbol repeats the series.
    /// Standard value: 5 (owner decision U-05, 2026-11-16).
    /// </summary>
    public int MaxBatchIndicatorTailBars { get; set; } = 5;

    /// <summary>Most notes (and catalysts) sa_search_notes and sa_list_upcoming_catalysts return; the answer says when more matched (truncated). Standard value: 100 (owner decision U-08).</summary>
    public int MaxReturnedNotes { get; set; } = 100;

    /// <summary>Most closed positions sa_get_portfolio_summary lists (includeClosedPositions); the answer carries totalCount and truncated. Standard value: 200.</summary>
    public int MaxReturnedClosedPositions { get; set; } = 200;

    /// <summary>Most transactions sa_get_portfolio_summary lists (includeTransactions); the answer carries totalCount and truncated. Standard value: 200.</summary>
    public int MaxReturnedTransactions { get; set; } = 200;

    /// <summary>Most expressions one sa_evaluate_expressions call may carry; more is LIMIT_EXCEEDED. Standard value: 20.</summary>
    public int MaxCalculatorExpressions { get; set; } = 20;

    /// <summary>Most variables one sa_evaluate_expressions call may carry; more is LIMIT_EXCEEDED. Standard value: 50.</summary>
    public int MaxCalculatorVariables { get; set; } = 50;

    /// <summary>Most days ahead of fromDate sa_list_upcoming_catalysts looks (horizonDays); more is LIMIT_EXCEEDED. Standard value: 365 (owner decision U-09).</summary>
    public int MaxCatalystHorizonDays { get; set; } = 365;

    /// <summary>Largest pivotLeftStrength and pivotRightStrength of anchorMode AutoPivots in sa_calculate_drawing_values (bars each side a swing must dominate); more is LIMIT_EXCEEDED. Standard value: 50 (owner decision U-12).</summary>
    public int MaxPivotStrength { get; set; } = 50;

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
    /// The fewest trades a combination needs to be the best one of a walk-forward window (minInSampleTrades) or to be ranked by sa_optimize_backtest (minTrades)
    /// when the call does not say. 1: a combination that never traded has an objective such as totalReturn 0 that says nothing about the strategy
    /// (owner decisions 2026-10-30 and D-5 = A, 2026-11-04).
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
    /// Most evaluation dates one sa_calculate_drawing_values call may calculate (evaluationDates).
    /// Standard value: 20.
    /// </summary>
    public int MaxDrawingEvaluationDates { get; set; } = 20;

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

    /// <summary>
    /// Most model generations sa_list_models returns (limit).
    /// Standard value: 100.
    /// </summary>
    public int MaxListedModels { get; set; } = 100;

    /// <summary>
    /// Widest image one sa_render_chart_image call may ask for (width, pixels); more is LIMIT_EXCEEDED. The native image buffer is width x height x 4 bytes
    /// (at most 4096 x 4096 x 4 = 64 MiB inside the bands). Standard value: 1920.
    /// </summary>
    public int MaxChartImageWidth { get; set; } = 1920;

    /// <summary>Tallest image one sa_render_chart_image call may ask for (height, pixels); more is LIMIT_EXCEEDED. Standard value: 1200.</summary>
    public int MaxChartImageHeight { get; set; } = 1200;

    /// <summary>The image width in pixels when sa_render_chart_image is called without width. Standard value: 1200.</summary>
    public int DefaultChartImageWidth { get; set; } = 1200;

    /// <summary>The image height in pixels when sa_render_chart_image is called without height. Standard value: 800.</summary>
    public int DefaultChartImageHeight { get; set; } = 800;

    /// <summary>
    /// Most bars one sa_render_chart_image image may draw (barCount); more is LIMIT_EXCEEDED. The limit in force never exceeds
    /// <see cref="MaxCandlesPerRequest"/> (<see cref="McpLimitsContract.EffectiveMaxChartImageBars"/>). Standard value: 500.
    /// </summary>
    public int MaxChartImageBars { get; set; } = 500;

    /// <summary>The number of bars sa_render_chart_image draws when the call does not say (barCount). Standard value: 200 (as <see cref="DefaultCandleCount"/>).</summary>
    public int DefaultChartImageBars { get; set; } = 200;

    /// <summary>Most overlay indicators (drawn on the price panel) one sa_render_chart_image call may name; more is LIMIT_EXCEEDED. Standard value: 5.</summary>
    public int MaxImageOverlays { get; set; } = 5;

    /// <summary>Most buy/sell markers one sa_render_chart_image call may name; more is LIMIT_EXCEEDED. Standard value: 50.</summary>
    public int MaxImageMarkers { get; set; } = 50;

    /// <summary>Most saved drawings one sa_render_chart_image call draws with includeSavedDrawings (the first in the saved order; the rest are named in warnings, nothing is refused). Standard value: 50 (owner decision U-14).</summary>
    public int MaxImageSavedDrawings { get; set; } = 50;

    /// <summary>Largest saved drawing file (bytes) the server reads into memory for sa_get_chart_drawings and includeSavedDrawings; a larger file is DATA_UNAVAILABLE. Standard value: 1 MiB (1048576; the largest file measured in the application's data was about 17 KB).</summary>
    public int MaxDrawingFileBytes { get; set; } = FileReadOnlyDrawingDocumentSource.DefaultMaxFileBytes;

    /// <summary>
    /// Most bars the structured result of sa_render_chart_image lists (the pixel positions of the drawn bars); the bars left out are counted
    /// (barsOmittedCount) and never silently dropped. Standard value: 200.
    /// </summary>
    public int MaxImageBarsListed { get; set; } = 200;

    /// <summary>
    /// Minimum evaluated return samples (<c>m = SampleCount = evaluatedBarCount = Equity.Length - 1</c>)
    /// before ratio metrics omit <c>SAMPLE_TOO_SMALL</c> advisory warnings.
    /// Defaults to <see cref="BacktestReportGenerator.MinReturnSamplesForRatioMetrics"/>. Standard value: 20.
    /// </summary>
    public int BacktestMinBarsForRatioMetrics { get; set; } = BacktestReportGenerator.MinReturnSamplesForRatioMetrics;

    /// <summary>
    /// Minimum fraction of initial capital that a FixedQuantity backtest position's largest notional
    /// (<c>sizingParameter * highestClose</c>) must reach before <c>SIZING_NOTIONAL_SMALL</c> warns.
    /// Standard value: 0.01 (1%).
    /// </summary>
    public decimal BacktestSizingWarnRatio { get; set; } = 0.01m;
}
