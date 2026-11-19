using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_calculate_cross_asset_stats: computes cross-sectional correlation matrix, sample covariance matrix
/// (N-1 Bessel correction), and benchmark Beta/Alpha/R2 across 2 to MaxCrossAssetSymbols symbols.
/// </summary>
internal sealed class CalculateCrossAssetStatsTool : IMcpTool
{
    private const string SymbolsArgument = "symbols";
    private const string GroupIdArgument = "groupId";
    private const string BenchmarkArgument = "benchmark";
    private const string TimeFrameArgument = "timeFrame";
    private const string LookbackArgument = "lookbackBars";
    private const string EndDateArgument = "endDate";
    private const string ReturnBasisArgument = "returnBasis";
    private const string HoldingPeriodModeArgument = "holdingPeriodMode";
    private const string IncludeCovarianceArgument = "includeCovariance";
    private const string IncludeDistributionStatsArgument = "includeDistributionStats";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly ITickerGroupResolver _groupResolver;
    private readonly McpLimits _limits;
    private readonly McpResultHandleStore _resultHandles;
    private readonly CandleWindowLoader _loader;

    public CalculateCrossAssetStatsTool(
        IReadOnlyStockAnalyzerGateway gateway,
        SymbolResolver symbols,
        ITickerGroupResolver groupResolver,
        McpLimits limits,
        McpResultHandleStore? resultHandles = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _groupResolver = groupResolver ?? throw new ArgumentNullException(nameof(groupResolver));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _resultHandles = resultHandles ?? new McpResultHandleStore();
        _loader = new CandleWindowLoader(gateway);
    }

    public string Name => "sa_calculate_cross_asset_stats";

    public string Description =>
        $"Calculates inner-joined correlation matrix, sample covariance matrix (N-1), benchmark Beta/Alpha/R2, holdingPeriodMode alignment, includeDistributionStats (cornishFisherVar95 is a negative loss vs sa_analyze_portfolio_risk positive valueAtRisk), and decimalPlaces across 2..{_limits.MaxCrossAssetSymbols} symbols. Read-only. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"symbols\":[\"MSFT\",\"7203-T\"],\"timeFrame\":\"D1\",\"benchmark\":\"MSFT\",\"returnBasis\":\"SimpleReturn\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(SymbolsArgument, $"Universe ticker symbols to analyze (2 to {_limits.MaxCrossAssetSymbols}). Mutually exclusive with {GroupIdArgument} and {McpResultHandleStore.SymbolsFromResultIdArgument}.", minItems: 2)
        .String(GroupIdArgument, $"Guid of a saved ticker group (from sa_list_ticker_groups) to analyze. Mutually exclusive with {SymbolsArgument} and {McpResultHandleStore.SymbolsFromResultIdArgument}.")
        .String(McpResultHandleStore.SymbolsFromResultIdArgument, $"Handle id from sa_run_screener or sa_filter_tickers ({McpResultHandleStore.StoreResultArgument} true). Mutually exclusive with {SymbolsArgument} and {GroupIdArgument}.")
        .String(BenchmarkArgument, "Optional benchmark symbol to compute Beta, Alpha, and R2 against.")
        .String(TimeFrameArgument, $"Time frame of the bars. Supported: {ToolArgs.SupportedTimeFrameNames}. Default {TimeFrame.D1}.", allowedValues: Enum.GetNames<TimeFrame>())
        .Integer(LookbackArgument, $"Stored bars each symbol evaluates, newest last. Default {_limits.DefaultCandleCount}, maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.DefaultCandleCount, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .String(ReturnBasisArgument, "Return calculation basis: 'SimpleReturn' (default) or 'LogReturn'.", allowedValues: new[] { ReturnStatistics.SimpleReturnBasis, ReturnStatistics.LogReturnBasis })
        .String(HoldingPeriodModeArgument, $"Common return dates whose returns cover different holding periods (multiBarReturnCount): '{nameof(HoldingPeriodAlignmentMode.IncludeAll)}' (default) uses them unchanged, '{nameof(HoldingPeriodAlignmentMode.StrictEqualHoldingPeriod)}' leaves them out.", allowedValues: Enum.GetNames<HoldingPeriodAlignmentMode>())
        .Boolean(IncludeCovarianceArgument, "Whether to include the sample covariance matrix in the result. Default false.")
        .Integer(SeriesOutputOptions.DecimalPlacesArgument, SeriesOutputOptions.DecimalPlacesDescription("every correlation, covariance, beta, alpha, R2 and distribution statistic"), minimum: 0, maximum: SeriesOutputOptions.MaxDecimalPlaces)
        .Boolean(IncludeDistributionStatsArgument, "Whether to add downsideBeta / upsideBeta to betaAlpha (needs a benchmark) and the symbolStats array (mean, stdDev, downsideDeviation, skewness, excessKurtosis, jarqueBeraStat, jarqueBeraPValue, cornishFisherVar95; the VaR is the negative lower-tail return). Default false: the output is unchanged.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        TimeFrame timeFrame = arguments.TryGetProperty(TimeFrameArgument, out _)
            ? ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument)
            : TimeFrame.D1;
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.DefaultCandleCount, _limits.MaxCandlesPerRequest);
        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);
        bool includeCovariance = ToolArgs.Boolean(arguments, IncludeCovarianceArgument) ?? false;
        bool includeDistributionStats = ToolArgs.Boolean(arguments, IncludeDistributionStatsArgument) ?? false;
        int? decimalPlaces = SeriesOutputOptions.ReadDecimalPlaces(arguments);

        string returnBasis = ToolArgs.String(arguments, ReturnBasisArgument) ?? ReturnStatistics.SimpleReturnBasis;
        if (!string.Equals(returnBasis, ReturnStatistics.SimpleReturnBasis, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(returnBasis, ReturnStatistics.LogReturnBasis, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"returnBasis must be '{ReturnStatistics.SimpleReturnBasis}' or '{ReturnStatistics.LogReturnBasis}'.", ReturnBasisArgument);
        }

        HoldingPeriodAlignmentMode holdingPeriodMode = ReadHoldingPeriodMode(arguments);

        // 1. Resolve Benchmark Symbol shape (if any) first per sa_ai_integration_0007
        string? canonicalBenchmark = null;
        if (arguments.TryGetProperty(BenchmarkArgument, out _))
        {
            string benchmarkRaw = ToolArgs.String(arguments, BenchmarkArgument) ?? string.Empty;
            canonicalBenchmark = SymbolResolver.Canonical(benchmarkRaw, BenchmarkArgument);
        }

        // 2. Resolve Universe Symbols (validates shape, limits, and stored availability)
        List<string> universeSymbols = await ResolveUniverseSymbolsAsync(arguments, cancellationToken).ConfigureAwait(false);

        // 3. Resolve Benchmark Symbol availability (if any)
        if (canonicalBenchmark != null)
        {
            IReadOnlyList<string> available = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
            if (!available.Contains(canonicalBenchmark, StringComparer.OrdinalIgnoreCase))
            {
                throw new ToolException(ToolErrorCodes.SymbolNotFound, $"Symbol '{canonicalBenchmark}' is not available.", BenchmarkArgument, "Call sa_list_tickers to see the available symbols.");
            }
        }

        // 4. Load candle data for all distinct symbols needed (universe + benchmark) via CandleWindowLoader SSoT
        var allNeededSymbols = new HashSet<string>(universeSymbols, StringComparer.OrdinalIgnoreCase);
        if (canonicalBenchmark != null)
        {
            allNeededSymbols.Add(canonicalBenchmark);
        }

        var symbolCandles = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase);

        foreach (string sym in allNeededSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CandleData> candles;
            if (endDate.HasValue)
            {
                candles = await _loader.LoadUpToAsync(sym, timeFrame, endDate.Value, lookback, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (candles, _) = await _loader.LoadAsync(sym, timeFrame, lookback, cancellationToken).ConfigureAwait(false);
            }

            symbolCandles[sym] = candles;
        }

        // Check if benchmark had zero bars
        if (canonicalBenchmark != null && (!symbolCandles.TryGetValue(canonicalBenchmark, out var bCandles) || bCandles.Count < 2))
        {
            throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"Benchmark symbol '{canonicalBenchmark}' has insufficient price bars to compute returns.", BenchmarkArgument);
        }

        // Filter symbolCandles input strictly to universe symbols (preserving order)
        var universeDict = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase);
        foreach (string sym in universeSymbols)
        {
            if (symbolCandles.TryGetValue(sym, out var bars))
            {
                universeDict[sym] = bars;
            }
        }

        IReadOnlyList<CandleData>? benchmarkCandles = null;
        if (canonicalBenchmark != null && symbolCandles.TryGetValue(canonicalBenchmark, out var benchBars))
        {
            benchmarkCandles = benchBars;
        }

        // 5. Calculate Cross-Asset Statistics (external benchmark passed separately)
        CrossAssetStatsResult result = ReturnStatistics.Calculate(universeDict, canonicalBenchmark, includeCovariance, returnBasis, benchmarkCandles, timeFrame, holdingPeriodMode);

        if (result.CommonDateCount < ReturnStatistics.MinCommonDates)
        {
            string left = result.ExcludedMultiBarDateCount > 0
                ? $" after leaving out {result.ExcludedMultiBarDateCount} whose returns cover different holding periods ({HoldingPeriodModeArgument} {result.HoldingPeriodMode})"
                : string.Empty;
            throw new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                $"At least {ReturnStatistics.MinCommonDates} common return dates are required across the evaluated symbols, but only {result.CommonDateCount} were found{left}.",
                LookbackArgument);
        }

        if (result.Symbols.Count < 2)
        {
            throw new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                "Fewer than 2 symbols have sufficient market data for cross-asset statistics.",
                SymbolsArgument);
        }

        // 5. Serialize JSON output
        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();

            writer.WriteStartArray("symbols");
            foreach (string sym in result.Symbols)
            {
                writer.WriteStringValue(sym);
            }
            writer.WriteEndArray();

            writer.WriteNumber("commonDateCount", result.CommonDateCount);
            writer.WriteNumber("multiBarReturnCount", result.MultiBarReturnCount);
            if (result.HoldingPeriodMode != HoldingPeriodAlignmentMode.IncludeAll)
            {
                // Only a non-standard mode adds its fields, so a call without the argument keeps the known output byte for byte.
                writer.WriteString("holdingPeriodMode", result.HoldingPeriodMode.ToString());
                writer.WriteNumber("excludedMultiBarDateCount", result.ExcludedMultiBarDateCount);
            }

            if (result.StartDate.HasValue)
            {
                writer.WriteString("startDate", ToolJson.FormatDate(result.StartDate.Value));
            }
            if (result.EndDate.HasValue)
            {
                writer.WriteString("endDate", ToolJson.FormatDate(result.EndDate.Value));
            }

            writer.WriteString("returnBasis", result.ReturnBasis);
            writer.WriteString("covarianceDegreesOfFreedom", result.CovarianceDegreesOfFreedom);

            // Correlation matrix
            writer.WriteStartArray("correlation");
            foreach (var row in result.CorrelationMatrix)
            {
                writer.WriteStartArray();
                foreach (double? val in row)
                {
                    WriteNullableDouble(writer, val, decimalPlaces);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();

            // Covariance matrix (if requested)
            if (result.CovarianceMatrix != null)
            {
                writer.WriteStartArray("covariance");
                foreach (var row in result.CovarianceMatrix)
                {
                    writer.WriteStartArray();
                    foreach (double? val in row)
                    {
                        WriteNullableDouble(writer, val, decimalPlaces);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }

            // Beta / Alpha (if benchmark provided)
            if (canonicalBenchmark != null && result.BetaAlpha.Count > 0)
            {
                writer.WriteStartArray("betaAlpha");
                foreach (var entry in includeDistributionStats ? CrossAssetDistributionStatistics.WithConditionalBetas(result) : result.BetaAlpha)
                {
                    writer.WriteStartObject();
                    writer.WriteString("symbol", entry.Symbol);
                    WriteNullableDouble(writer, "beta", entry.Beta, decimalPlaces);
                    WriteNullableDouble(writer, "alpha", entry.Alpha, decimalPlaces);
                    WriteNullableDouble(writer, "rSquared", entry.RSquared, decimalPlaces);
                    if (includeDistributionStats)
                    {
                        WriteNullableDouble(writer, "downsideBeta", entry.DownsideBeta, decimalPlaces);
                        WriteNullableDouble(writer, "upsideBeta", entry.UpsideBeta, decimalPlaces);
                    }

                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            // Distribution statistics per symbol (only when asked for)
            if (includeDistributionStats)
            {
                writer.WriteStartArray("symbolStats");
                foreach (SymbolDistributionStats stats in CrossAssetDistributionStatistics.SymbolStats(result))
                {
                    writer.WriteStartObject();
                    writer.WriteString("symbol", stats.Symbol);
                    WriteNullableDouble(writer, "mean", stats.Mean, decimalPlaces);
                    WriteNullableDouble(writer, "stdDev", stats.StdDev, decimalPlaces);
                    WriteNullableDouble(writer, "downsideDeviation", stats.DownsideDeviation, decimalPlaces);
                    WriteNullableDouble(writer, "skewness", stats.Skewness, decimalPlaces);
                    WriteNullableDouble(writer, "excessKurtosis", stats.ExcessKurtosis, decimalPlaces);
                    WriteNullableDouble(writer, "jarqueBeraStat", stats.JarqueBeraStatistic, decimalPlaces);
                    WriteNullableDouble(writer, "jarqueBeraPValue", stats.JarqueBeraPValue, decimalPlaces);
                    WriteNullableDouble(writer, "cornishFisherVar95", stats.CornishFisherVar95, decimalPlaces);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            // Excluded symbols
            writer.WriteStartArray("excludedSymbols");
            foreach (var excluded in result.ExcludedSymbols)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", excluded.Symbol);
                writer.WriteString("reason", excluded.Reason);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Notes
            writer.WriteStartArray("notes");
            foreach (string note in result.Notes)
            {
                writer.WriteStringValue(note);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        });
    }

    private static HoldingPeriodAlignmentMode ReadHoldingPeriodMode(JsonElement arguments)
    {
        // The input schema (allowedValues) refuses every value that is not an enum name before the tool runs.
        string? raw = ToolArgs.String(arguments, HoldingPeriodModeArgument);
        return raw is null ? HoldingPeriodAlignmentMode.IncludeAll : Enum.Parse<HoldingPeriodAlignmentMode>(raw);
    }

    private async Task<List<string>> ResolveUniverseSymbolsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? requested = ToolArgs.StringArray(arguments, SymbolsArgument);
        string? groupIdRaw = ToolArgs.String(arguments, GroupIdArgument)?.Trim();
        string? resultId = ToolArgs.String(arguments, McpResultHandleStore.SymbolsFromResultIdArgument);

        int sourceCount = (requested is not null ? 1 : 0) + (groupIdRaw is not null ? 1 : 0) + (resultId is not null ? 1 : 0);
        if (sourceCount > 1)
        {
            string parameter = resultId is not null ? McpResultHandleStore.SymbolsFromResultIdArgument : GroupIdArgument;
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{SymbolsArgument}', '{GroupIdArgument}' and '{McpResultHandleStore.SymbolsFromResultIdArgument}' cannot be combined.", parameter, "Send one of them, or neither for every registered symbol.");
        }

        if (resultId is not null)
        {
            IReadOnlyList<string> stored = _resultHandles.ResolveSymbols(resultId, 2, _limits.MaxCrossAssetSymbols);
            return stored.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (requested is not null)
        {
            if (requested.Count > _limits.MaxCrossAssetSymbols)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"At most {_limits.MaxCrossAssetSymbols} symbols per call.",
                    SymbolsArgument);
            }

            var canonicalList = new List<string>(requested.Count);
            var resolvedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var distinctList = new List<string>();

            for (int i = 0; i < requested.Count; i++)
            {
                string raw = requested[i];
                string canonical = SymbolResolver.Canonical(raw, $"{SymbolsArgument}[{i}]");
                canonicalList.Add(canonical);
                if (resolvedSet.Add(canonical))
                {
                    distinctList.Add(canonical);
                }
            }

            if (distinctList.Count < 2)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "At least 2 distinct symbols are required for cross-asset statistics.", SymbolsArgument);
            }

            IReadOnlyList<string> available = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
            var availableSet = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < requested.Count; i++)
            {
                string canonical = canonicalList[i];
                if (!availableSet.Contains(canonical))
                {
                    throw new ToolException(ToolErrorCodes.SymbolNotFound, $"Symbol '{canonical}' is not available.", $"{SymbolsArgument}[{i}]", "Call sa_list_tickers to see the available symbols.");
                }
            }

            return distinctList;
        }

        if (groupIdRaw is not null)
        {
            (Guid _, GroupMembers group) = await GroupMembersLookup.ResolveAsync(
                _groupResolver, groupIdRaw, GroupIdArgument, _limits.MaxFilterEvaluationSymbols, cancellationToken).ConfigureAwait(false);

            if (group.Symbols.Count > _limits.MaxCrossAssetSymbols)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"The group contains {group.Symbols.Count} symbols, exceeding the limit of {_limits.MaxCrossAssetSymbols}.",
                    GroupIdArgument);
            }

            if (group.Symbols.Count < 2)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "The group contains fewer than 2 symbols.", GroupIdArgument);
            }

            return group.Symbols.ToList();
        }

        // Neither symbols nor groupId: take all available registered symbols
        IReadOnlyList<string> availableUniverse = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
        List<string> all = availableUniverse
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(TickerSymbol.IsWellFormed)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        if (all.Count > _limits.MaxCrossAssetSymbols)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"The universe contains {all.Count} symbols, exceeding the limit of {_limits.MaxCrossAssetSymbols}.",
                SymbolsArgument,
                "Specify symbols or groupId to narrow the universe.");
        }

        if (all.Count < 2)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "Fewer than 2 symbols are available in the repository.", SymbolsArgument);
        }

        return all;
    }

    private static void WriteNullableDouble(Utf8JsonWriter writer, double? value, int? decimalPlaces)
    {
        if (value is { } v && !double.IsNaN(v) && !double.IsInfinity(v))
        {
            writer.WriteNumberValue(SeriesOutputOptions.RoundIfRequested(v, decimalPlaces));
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static void WriteNullableDouble(Utf8JsonWriter writer, string propertyName, double? value, int? decimalPlaces)
    {
        if (value is { } v && !double.IsNaN(v) && !double.IsInfinity(v))
        {
            writer.WriteNumber(propertyName, SeriesOutputOptions.RoundIfRequested(v, decimalPlaces));
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }
}
