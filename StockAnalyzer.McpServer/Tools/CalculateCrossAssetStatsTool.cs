using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
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
    private const string IncludeCovarianceArgument = "includeCovariance";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly ITickerGroupResolver _groupResolver;
    private readonly McpLimits _limits;
    private readonly CandleWindowLoader _loader;

    public CalculateCrossAssetStatsTool(
        IReadOnlyStockAnalyzerGateway gateway,
        SymbolResolver symbols,
        ITickerGroupResolver groupResolver,
        McpLimits limits)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _groupResolver = groupResolver ?? throw new ArgumentNullException(nameof(groupResolver));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _loader = new CandleWindowLoader(gateway);
    }

    public string Name => "sa_calculate_cross_asset_stats";

    public string Description =>
        $"Calculates cross-asset return statistics across 2 to {_limits.MaxCrossAssetSymbols} symbols: " +
        "correlation matrix, sample covariance matrix (N-1 degrees of freedom), and benchmark Beta, Alpha, and R2. " +
        "Aligns bars using an inner join on common trading dates. " +
        "With timeFrame W1 or MN1 the bars are matched by period. " +
        $"{ReturnStatistics.PeriodStartDateNote} " +
        "multiBarReturnCount counts the common dates whose returns cover different holding periods because a market traded on a day another was closed. " +
        "Supports returnBasis 'SimpleReturn' (default) or 'LogReturn'. Read-only.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"symbols\":[\"MSFT\",\"7203-T\"],\"timeFrame\":\"D1\",\"benchmark\":\"MSFT\",\"returnBasis\":\"SimpleReturn\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(SymbolsArgument, $"Universe ticker symbols to analyze (2 to {_limits.MaxCrossAssetSymbols}). Mutually exclusive with groupId.", minItems: 2)
        .String(GroupIdArgument, "Guid of a saved ticker group (from sa_list_ticker_groups) to analyze. Mutually exclusive with symbols.")
        .String(BenchmarkArgument, "Optional benchmark symbol to compute Beta, Alpha, and R2 against.")
        .String(TimeFrameArgument, $"Time frame of the bars. Supported: {ToolArgs.SupportedTimeFrameNames}. Default {TimeFrame.D1}.", allowedValues: Enum.GetNames<TimeFrame>())
        .Integer(LookbackArgument, $"Stored bars each symbol evaluates, newest last. Default {_limits.DefaultCandleCount}, maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.DefaultCandleCount, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .String(ReturnBasisArgument, "Return calculation basis: 'SimpleReturn' (default) or 'LogReturn'.", allowedValues: new[] { ReturnStatistics.SimpleReturnBasis, ReturnStatistics.LogReturnBasis })
        .Boolean(IncludeCovarianceArgument, "Whether to include the sample covariance matrix in the result. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        TimeFrame timeFrame = arguments.TryGetProperty(TimeFrameArgument, out _)
            ? ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument)
            : TimeFrame.D1;
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.DefaultCandleCount, _limits.MaxCandlesPerRequest);
        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);
        bool includeCovariance = ToolArgs.Boolean(arguments, IncludeCovarianceArgument) ?? false;

        string returnBasis = ToolArgs.String(arguments, ReturnBasisArgument) ?? ReturnStatistics.SimpleReturnBasis;
        if (!string.Equals(returnBasis, ReturnStatistics.SimpleReturnBasis, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(returnBasis, ReturnStatistics.LogReturnBasis, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"returnBasis must be '{ReturnStatistics.SimpleReturnBasis}' or '{ReturnStatistics.LogReturnBasis}'.", ReturnBasisArgument);
        }

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
        CrossAssetStatsResult result = ReturnStatistics.Calculate(universeDict, canonicalBenchmark, includeCovariance, returnBasis, benchmarkCandles, timeFrame);

        if (result.CommonDateCount < ReturnStatistics.MinCommonDates)
        {
            throw new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                $"At least {ReturnStatistics.MinCommonDates} common return dates are required across the evaluated symbols, but only {result.CommonDateCount} were found.",
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
                    WriteNullableDouble(writer, val);
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
                        WriteNullableDouble(writer, val);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }

            // Beta / Alpha (if benchmark provided)
            if (canonicalBenchmark != null && result.BetaAlpha.Count > 0)
            {
                writer.WriteStartArray("betaAlpha");
                foreach (var entry in result.BetaAlpha)
                {
                    writer.WriteStartObject();
                    writer.WriteString("symbol", entry.Symbol);
                    WriteNullableDouble(writer, "beta", entry.Beta);
                    WriteNullableDouble(writer, "alpha", entry.Alpha);
                    WriteNullableDouble(writer, "rSquared", entry.RSquared);
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

    private async Task<List<string>> ResolveUniverseSymbolsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? requested = ToolArgs.StringArray(arguments, SymbolsArgument);
        string? groupIdRaw = ToolArgs.String(arguments, GroupIdArgument)?.Trim();

        if (requested is not null && groupIdRaw is not null)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{SymbolsArgument}' and '{GroupIdArgument}' cannot be combined.", GroupIdArgument, "Send one of them, or neither for every registered symbol.");
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

    private static void WriteNullableDouble(Utf8JsonWriter writer, double? value)
    {
        if (value is { } v && !double.IsNaN(v) && !double.IsInfinity(v))
        {
            writer.WriteNumberValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static void WriteNullableDouble(Utf8JsonWriter writer, string propertyName, double? value)
    {
        if (value is { } v && !double.IsNaN(v) && !double.IsInfinity(v))
        {
            writer.WriteNumber(propertyName, v);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }
}
