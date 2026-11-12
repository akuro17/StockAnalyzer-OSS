using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Watchlist;

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
    /// <summary>Exactly one tool call executes at a time (the pipeline is not thread-safe per call, and a calculation that is already running cannot be interrupted); not configurable.</summary>
    public const int MaxConcurrentToolCalls = 1;

    /// <summary>Maximum nesting depth when traversing the group tree (All Tickers / category roots are depth 0); deeper subtrees are truncated.</summary>
    public const int MaxGroupTreeDepth = 16;

    /// <summary>Hard maximum of <see cref="McpLimits.MaxCandlesPerRequest"/>; also the ceiling of every default that is a window length within it.</summary>
    private const int MaxCandlesPerRequestBandMaximum = 50000;

    /// <summary>Hard lower end of <see cref="McpLimits.MaxChartImageWidth"/>: narrower than this, the plot area (after the fixed margins) is too small to hold any bar.</summary>
    private const int MinChartImageWidthBand = 320;

    /// <summary>Hard lower end of <see cref="McpLimits.MaxChartImageHeight"/>: lower, the volume-only layout does not fit.</summary>
    private const int MinChartImageHeightBand = 240;

    /// <summary>Hard upper end of an image side: 4096 x 4096 x 4 bytes = 64 MiB of native memory for one image.</summary>
    private const int MaxChartImageSideBand = 4096;

    /// <summary>Hard upper end of <see cref="McpLimits.MaxChartImageBars"/> and of the listed bars.</summary>
    private const int MaxChartImageBarsBandMaximum = 5000;

    private const int Kibibyte = 1024;
    private const int Mebibyte = 1024 * Kibibyte;

    /// <summary>
    /// Safe upper estimate of the JSON bytes of one sa_run_backtest equity-curve point (date, a decimal equity of up to 28 digits, a decimal drawdown
    /// ratio, field names and separators), so that a full curve still fits <see cref="McpLimits.MaxResponseBytes"/>.
    /// </summary>
    private const int EquityCurvePointMaxBytes = 160;

    public static void Validate(McpLimits limits)
    {
        Band(nameof(McpLimits.MaxCandlesPerRequest), limits.MaxCandlesPerRequest, 1, MaxCandlesPerRequestBandMaximum);
        Band(nameof(McpLimits.DefaultCandleCount), limits.DefaultCandleCount, 1, limits.MaxCandlesPerRequest);
        Band(nameof(McpLimits.MaxIndicatorsPerCall), limits.MaxIndicatorsPerCall, 1, 64);
        Band(nameof(McpLimits.MaxDerivedSeriesPerCall), limits.MaxDerivedSeriesPerCall, 1, 64);
        Band(nameof(McpLimits.DefaultSummaryTrendBars), limits.DefaultSummaryTrendBars, SeriesSummaryStatistics.MinTrendBars, MaxCandlesPerRequestBandMaximum);
        Band(nameof(McpLimits.MaxLookbackOffsets), limits.MaxLookbackOffsets, 1, 1000);
        Band(nameof(McpLimits.MaxSymbolsPerMetadataCall), limits.MaxSymbolsPerMetadataCall, 1, 500);
        Band(nameof(McpLimits.MaxReturnedTrades), limits.MaxReturnedTrades, 0, 1000);
        Band(nameof(McpLimits.MaxReturnedEvents), limits.MaxReturnedEvents, 1, 2000);
        Band(nameof(McpLimits.MaxForwardBars), limits.MaxForwardBars, 1, 500);
        Band(nameof(McpLimits.MaxForwardBarsList), limits.MaxForwardBarsList, 1, 32);
        Band(nameof(McpLimits.MaxReturnedWarnings), limits.MaxReturnedWarnings, 0, 200);
        Band(nameof(McpLimits.MaxOutputTailBars), limits.MaxOutputTailBars, 1, limits.MaxCandlesPerRequest);
        Band(nameof(McpLimits.DefaultOutputTailBars), limits.DefaultOutputTailBars, 1, limits.MaxOutputTailBars);
        Band(nameof(McpLimits.ToolTimeoutSeconds), limits.ToolTimeoutSeconds, 1, 900);
        Band(nameof(McpLimits.HeavyToolTimeoutSeconds), limits.HeavyToolTimeoutSeconds, limits.ToolTimeoutSeconds, 1800);
        Band(nameof(McpLimits.QueueWaitSeconds), limits.QueueWaitSeconds, 0, 600);
        Band(nameof(McpLimits.MaxResponseBytes), limits.MaxResponseBytes, 64 * Kibibyte, 8 * Mebibyte);
        Band(nameof(McpLimits.MaxRequestBytes), limits.MaxRequestBytes, 4 * Kibibyte, 8 * Mebibyte);
        Band(nameof(McpLimits.MaxScreenerSymbols), limits.MaxScreenerSymbols, 1, 5000);
        Band(nameof(McpLimits.MaxReturnedMatches), limits.MaxReturnedMatches, 1, 1000);
        Band(nameof(McpLimits.MaxSecondarySymbolsPerCall), limits.MaxSecondarySymbolsPerCall, 1, 8);
        Band(nameof(McpLimits.MaxCrossAssetSymbols), limits.MaxCrossAssetSymbols, 2, 500);
        Band(nameof(McpLimits.MaxBreadthSymbols), limits.MaxBreadthSymbols, 1, 5000);
        Band(nameof(McpLimits.MaxPortfolioBacktestSymbols), limits.MaxPortfolioBacktestSymbols, 1, 500);
        Band(nameof(McpLimits.MaxComparisonTargets), limits.MaxComparisonTargets, 1, WatchlistConstants.MaxRelativePerformanceTargets);
        Band(nameof(McpLimits.MaxTickersPerPage), limits.MaxTickersPerPage, 1, 5000);
        Band(nameof(McpLimits.DefaultTickerPageSize), limits.DefaultTickerPageSize, 1, limits.MaxTickersPerPage);
        Band(nameof(McpLimits.MaxGroupNodes), limits.MaxGroupNodes, 1, 5000);
        Band(nameof(McpLimits.MaxFilterEvaluationSymbols), limits.MaxFilterEvaluationSymbols, 1, 5000);
        Band(nameof(McpLimits.MaxInlineFilterRules), limits.MaxInlineFilterRules, 1, 128);
        Band(nameof(McpLimits.MaxEquityCurvePoints), limits.MaxEquityCurvePoints, 1, 50000);
        Band(nameof(McpLimits.MaxDrawdownEpisodes), limits.MaxDrawdownEpisodes, 1, 50);
        Band(nameof(McpLimits.MaxOptimizationCombinations), limits.MaxOptimizationCombinations, 1, 20000);
        Band(nameof(McpLimits.MaxOptimizationTopK), limits.MaxOptimizationTopK, 1, 100);
        Band(nameof(McpLimits.MaxOptimizationParameters), limits.MaxOptimizationParameters, 1, 8);
        Band(nameof(McpLimits.OptimizationDegreeOfParallelism), limits.OptimizationDegreeOfParallelism, 1, 64);
        Band(nameof(McpLimits.MaxWalkForwardWindows), limits.MaxWalkForwardWindows, 1, 60);
        Band(nameof(McpLimits.DefaultMinInSampleTrades), limits.DefaultMinInSampleTrades, 0, 1000);
        Band(nameof(McpLimits.MaxVolumeProfileBins), limits.MaxVolumeProfileBins, 2, 500);
        Band(nameof(McpLimits.MaxSupportResistanceLevels), limits.MaxSupportResistanceLevels, 1, 100);
        Band(nameof(McpLimits.MaxDrawingEvaluationDates), limits.MaxDrawingEvaluationDates, 1, 500);
        Band(nameof(McpLimits.MaxCycleBins), limits.MaxCycleBins, 1, 500);
        Band(nameof(McpLimits.MaxReturnedPatterns), limits.MaxReturnedPatterns, 1, 1000);
        Band(nameof(McpLimits.MaxProjectionBars), limits.MaxProjectionBars, 1, 100);
        Band(nameof(McpLimits.MaxSeasonalityYears), limits.MaxSeasonalityYears, (int)SeasonalityChartConstants.MinYearsToOverlay, (int)SeasonalityChartConstants.MaxYearsToOverlay);
        Band(nameof(McpLimits.MaxListedModels), limits.MaxListedModels, 1, 1000);
        Band(nameof(McpLimits.MaxChartImageWidth), limits.MaxChartImageWidth, MinChartImageWidthBand, MaxChartImageSideBand);
        Band(nameof(McpLimits.MaxChartImageHeight), limits.MaxChartImageHeight, MinChartImageHeightBand, MaxChartImageSideBand);
        Band(nameof(McpLimits.DefaultChartImageWidth), limits.DefaultChartImageWidth, MinChartImageWidthBand, limits.MaxChartImageWidth);
        Band(nameof(McpLimits.DefaultChartImageHeight), limits.DefaultChartImageHeight, MinChartImageHeightBand, limits.MaxChartImageHeight);
        Band(nameof(McpLimits.MaxChartImageBars), limits.MaxChartImageBars, 1, MaxChartImageBarsBandMaximum);
        Band(nameof(McpLimits.DefaultChartImageBars), limits.DefaultChartImageBars, 1, limits.MaxChartImageBars);
        Band(nameof(McpLimits.MaxImageOverlays), limits.MaxImageOverlays, 1, 16);
        Band(nameof(McpLimits.MaxImageMarkers), limits.MaxImageMarkers, 0, 500);
        Band(nameof(McpLimits.MaxImageBarsListed), limits.MaxImageBarsListed, 0, MaxChartImageBarsBandMaximum);
    }

    /// <summary>
    /// The bar limit of an image in force: <see cref="McpLimits.MaxChartImageBars"/>, but never more than a request can read (<see cref="McpLimits.MaxCandlesPerRequest"/>).
    /// Applied at run time and not as a start-up refusal, because a request limit below the image limit is a valid configuration that must keep starting.
    /// </summary>
    public static int EffectiveMaxChartImageBars(McpLimits limits) => Math.Min(limits.MaxChartImageBars, limits.MaxCandlesPerRequest);

    /// <summary>The bar count of an image in force when a call names none: <see cref="McpLimits.DefaultChartImageBars"/> within <see cref="EffectiveMaxChartImageBars"/>.</summary>
    public static int EffectiveDefaultChartImageBars(McpLimits limits) => Math.Min(limits.DefaultChartImageBars, EffectiveMaxChartImageBars(limits));

    /// <summary>
    /// The equity-curve point limit in force: <see cref="McpLimits.MaxEquityCurvePoints"/>, but never more than a full curve of
    /// <see cref="EquityCurvePointMaxBytes"/> per point can carry in <see cref="McpLimits.MaxResponseBytes"/>. It is applied at run time and not as a start-up
    /// refusal, because a response limit at its band minimum is a valid configuration that must keep starting.
    /// </summary>
    public static int EffectiveMaxEquityCurvePoints(McpLimits limits) => Math.Min(limits.MaxEquityCurvePoints, limits.MaxResponseBytes / EquityCurvePointMaxBytes);

    /// <summary>
    /// The largest <c>trendBars</c> a call may name: the calculation window (<see cref="McpLimits.MaxCandlesPerRequest"/>), but never below <see cref="SeriesSummaryStatistics.MinTrendBars"/>
    /// (the Core definition of the smallest trend window). Applied at run time, not as a start-up refusal, because a request limit below two bars is a valid configuration that must keep starting.
    /// </summary>
    public static int EffectiveMaxTrendBars(McpLimits limits) => Math.Max(SeriesSummaryStatistics.MinTrendBars, limits.MaxCandlesPerRequest);

    /// <summary>The trend window in force when a call names none: <see cref="McpLimits.DefaultSummaryTrendBars"/> within <see cref="EffectiveMaxTrendBars"/>.</summary>
    public static int EffectiveDefaultTrendBars(McpLimits limits) => Math.Min(limits.DefaultSummaryTrendBars, EffectiveMaxTrendBars(limits));

    private static void Band(string key, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new McpLimitsException(key, $"= {value} is outside {minimum}..{maximum}.");
        }
    }
}
