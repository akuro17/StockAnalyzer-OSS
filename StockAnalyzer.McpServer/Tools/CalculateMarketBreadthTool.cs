using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_calculate_market_breadth: computes market breadth and internal market structure across a universe
/// of up to MaxBreadthSymbols symbols (A/D Line, A/D Ratio, McClellan Oscillator, Summation Index,
/// % Above SMA, New Highs/Lows, TRIN, and CVI).
/// </summary>
internal sealed class CalculateMarketBreadthTool : IMcpTool
{
    private const string SymbolsArgument = "symbols";
    private const string GroupIdArgument = "groupId";
    private const string TimeFrameArgument = "timeFrame";
    private const string LookbackArgument = "lookbackBars";
    private const string TailArgument = "outputTailCount";
    private const string EndDateArgument = "endDate";
    private const string AdRatioBarsArgument = "adRatioBars";
    private const string McClellanFastArgument = "mcClellanFast";
    private const string McClellanSlowArgument = "mcClellanSlow";
    private const string MaBarsArgument = "maBars";
    private const string HighLowBarsArgument = "highLowBars";
    private const string IncludeArgument = "include";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly ITickerGroupResolver _groupResolver;
    private readonly McpLimits _limits;
    private readonly CandleWindowLoader _loader;

    public CalculateMarketBreadthTool(
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

    public string Name => "sa_calculate_market_breadth";

    public string Description =>
        $"Calculates market breadth and internal market structure across a universe of up to {_limits.MaxBreadthSymbols} symbols: " +
        "Advancers, Decliners, Unchanged, A/D Line, A/D Ratio, McClellan Oscillator, Summation Index, " +
        "% Above SMA, New Highs/Lows, TRIN, and CVI. " +
        "Aligns bars on the union of trading days. " +
        "With timeFrame W1 or MN1 the bars are matched by period. " +
        $"{ReturnStatistics.PeriodStartDateNote} Read-only (heavy).";

    public bool IsHeavy => true;

    public string? ExampleArgumentsJson =>
        "{\"symbols\":[\"MSFT\",\"AAPL\"],\"timeFrame\":\"D1\",\"outputTailCount\":50}";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(SymbolsArgument, $"Universe ticker symbols to analyze (up to {_limits.MaxBreadthSymbols}). Mutually exclusive with groupId.", minItems: 1)
        .String(GroupIdArgument, "Guid of a saved ticker group (from sa_list_ticker_groups) to analyze. Mutually exclusive with symbols.")
        .String(TimeFrameArgument, $"Time frame of the bars. Supported: {ToolArgs.SupportedTimeFrameNames}. Default {TimeFrame.D1}.", allowedValues: Enum.GetNames<TimeFrame>())
        .Integer(LookbackArgument, $"Stored bars each symbol evaluates, newest last. Default {_limits.DefaultCandleCount}, maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.DefaultCandleCount, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(TailArgument, $"Number of trailing bars returned per series. Default {_limits.DefaultOutputTailBars}, at most {_limits.MaxOutputTailBars}.", defaultValue: _limits.DefaultOutputTailBars, minimum: 1, maximum: _limits.MaxOutputTailBars)
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .Integer(AdRatioBarsArgument, $"Lookback window in days for A/D Ratio. Default {MarketBreadthCalculator.DefaultAdRatioBars}.", defaultValue: MarketBreadthCalculator.DefaultAdRatioBars, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(McClellanFastArgument, $"Fast EMA period for McClellan Oscillator. Default {MarketBreadthCalculator.DefaultMcClellanFast}.", defaultValue: MarketBreadthCalculator.DefaultMcClellanFast, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(McClellanSlowArgument, $"Slow EMA period for McClellan Oscillator. Default {MarketBreadthCalculator.DefaultMcClellanSlow}.", defaultValue: MarketBreadthCalculator.DefaultMcClellanSlow, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(MaBarsArgument, $"Lookback period in bars for % Above SMA. Default {MarketBreadthCalculator.DefaultMaBars}.", defaultValue: MarketBreadthCalculator.DefaultMaBars, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(HighLowBarsArgument, $"Lookback window in bars for New Highs / New Lows. Default {MarketBreadthCalculator.DefaultHighLowBars}.", defaultValue: MarketBreadthCalculator.DefaultHighLowBars, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .StringArray(IncludeArgument, $"Optional array of series names to include in output. Allowed: {string.Join(", ", MarketBreadthCalculator.AllSeriesKeys)}. Omit for all series.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        TimeFrame timeFrame = arguments.TryGetProperty(TimeFrameArgument, out _)
            ? ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument)
            : TimeFrame.D1;
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.DefaultCandleCount, _limits.MaxCandlesPerRequest);
        int outputTailCount = ToolArgs.PositiveWithin(arguments, TailArgument, _limits.DefaultOutputTailBars, _limits.MaxOutputTailBars);
        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);

        int adRatioBars = ToolArgs.PositiveWithin(arguments, AdRatioBarsArgument, MarketBreadthCalculator.DefaultAdRatioBars, _limits.MaxCandlesPerRequest);
        int mcClellanFast = ToolArgs.PositiveWithin(arguments, McClellanFastArgument, MarketBreadthCalculator.DefaultMcClellanFast, _limits.MaxCandlesPerRequest);
        int mcClellanSlow = ToolArgs.PositiveWithin(arguments, McClellanSlowArgument, MarketBreadthCalculator.DefaultMcClellanSlow, _limits.MaxCandlesPerRequest);
        if (mcClellanFast >= mcClellanSlow)
        {
            // Fast >= slow would give an oscillator of the opposite sign (or a constant zero); the tool refuses it like sa_detect_divergences does for its moving averages.
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"{McClellanFastArgument} ({mcClellanFast}) must be strictly less than {McClellanSlowArgument} ({mcClellanSlow}).",
                McClellanFastArgument,
                $"Defaults are {MarketBreadthCalculator.DefaultMcClellanFast} and {MarketBreadthCalculator.DefaultMcClellanSlow}; when you set only one of them, keep fast below slow.");
        }

        int maBars = ToolArgs.PositiveWithin(arguments, MaBarsArgument, MarketBreadthCalculator.DefaultMaBars, _limits.MaxCandlesPerRequest);
        int highLowBars = ToolArgs.PositiveWithin(arguments, HighLowBarsArgument, MarketBreadthCalculator.DefaultHighLowBars, _limits.MaxCandlesPerRequest);

        HashSet<string>? includeSeriesSet = null;
        IReadOnlyList<string>? rawInclude = ToolArgs.StringArray(arguments, IncludeArgument);
        if (rawInclude != null && rawInclude.Count > 0)
        {
            includeSeriesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rawInclude.Count; i++)
            {
                string seriesName = rawInclude[i]?.Trim() ?? string.Empty;
                if (!MarketBreadthCalculator.AllSeriesKeys.Contains(seriesName))
                {
                    throw new ToolException(
                        ToolErrorCodes.InvalidArgument,
                        $"Unknown series name '{seriesName}'. Allowed values: {string.Join(", ", MarketBreadthCalculator.AllSeriesKeys)}.",
                        $"{IncludeArgument}[{i}]");
                }
                includeSeriesSet.Add(seriesName);
            }
        }

        // 1. Resolve Universe Symbols
        List<string> universeSymbols = await ResolveUniverseSymbolsAsync(arguments, cancellationToken).ConfigureAwait(false);

        // 2. Load candle data for all universe symbols via CandleWindowLoader SSoT
        var universeCandles = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase);

        foreach (string sym in universeSymbols)
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

            universeCandles[sym] = candles;
        }

        // 3. Compute Market Breadth
        var breadthParams = new MarketBreadthParameters
        {
            AdRatioBars = adRatioBars,
            McClellanFast = mcClellanFast,
            McClellanSlow = mcClellanSlow,
            MaBars = maBars,
            HighLowBars = highLowBars,
            OutputTailCount = outputTailCount,
            IncludeSeries = includeSeriesSet,
            TimeFrame = timeFrame
        };

        MarketBreadthResult result = MarketBreadthCalculator.Calculate(universeCandles, breadthParams);

        if (result.DateCount == 0)
        {
            throw new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                "Insufficient price data across the universe to compute market breadth.",
                SymbolsArgument);
        }

        // 4. Serialize JSON output
        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("dateCount", result.DateCount);
            writer.WriteNumber("symbolCount", result.SymbolCount);

            writer.WriteStartArray("excludedSymbols");
            foreach (var excluded in result.ExcludedSymbols)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", excluded.Symbol);
                writer.WriteString("reason", excluded.Reason);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartObject("series");

            // Dates array is always written
            writer.WriteStartArray("dates");
            foreach (string d in result.Series.Dates)
            {
                writer.WriteStringValue(d);
            }
            writer.WriteEndArray();

            void WriteIntArray(string key, IReadOnlyList<int> list)
            {
                if (includeSeriesSet != null && !includeSeriesSet.Contains(key)) return;
                writer.WriteStartArray(key);
                foreach (int v in list) writer.WriteNumberValue(v);
                writer.WriteEndArray();
            }

            void WriteNullableIntArray(string key, IReadOnlyList<int?> list)
            {
                if (includeSeriesSet != null && !includeSeriesSet.Contains(key)) return;
                writer.WriteStartArray(key);
                foreach (int? v in list)
                {
                    if (v.HasValue) writer.WriteNumberValue(v.Value);
                    else writer.WriteNullValue();
                }
                writer.WriteEndArray();
            }

            void WriteNullableDoubleArray(string key, IReadOnlyList<double?> list)
            {
                if (includeSeriesSet != null && !includeSeriesSet.Contains(key)) return;
                writer.WriteStartArray(key);
                foreach (double? v in list)
                {
                    if (v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value))
                    {
                        writer.WriteNumberValue(v.Value);
                    }
                    else
                    {
                        writer.WriteNullValue();
                    }
                }
                writer.WriteEndArray();
            }

            void WriteNullableDecimalArray(string key, IReadOnlyList<decimal?> list)
            {
                if (includeSeriesSet != null && !includeSeriesSet.Contains(key)) return;
                writer.WriteStartArray(key);
                foreach (decimal? v in list)
                {
                    if (v.HasValue) writer.WriteNumberValue(v.Value);
                    else writer.WriteNullValue();
                }
                writer.WriteEndArray();
            }

            WriteIntArray(MarketBreadthCalculator.AdvancersKey, result.Series.Advancers);
            WriteIntArray(MarketBreadthCalculator.DeclinersKey, result.Series.Decliners);
            WriteIntArray(MarketBreadthCalculator.UnchangedKey, result.Series.Unchanged);
            WriteNullableIntArray(MarketBreadthCalculator.AdLineKey, result.Series.AdLine);
            WriteNullableDoubleArray(MarketBreadthCalculator.AdRatioKey, result.Series.AdRatio);
            WriteNullableDoubleArray(MarketBreadthCalculator.McClellanKey, result.Series.McClellan);
            WriteNullableDoubleArray(MarketBreadthCalculator.SummationIndexKey, result.Series.SummationIndex);
            WriteNullableDoubleArray(MarketBreadthCalculator.PctAboveMaKey, result.Series.PctAboveMa);
            WriteNullableIntArray(MarketBreadthCalculator.NewHighsKey, result.Series.NewHighs);
            WriteNullableIntArray(MarketBreadthCalculator.NewLowsKey, result.Series.NewLows);
            WriteNullableIntArray(MarketBreadthCalculator.NetNewHighsKey, result.Series.NetNewHighs);
            WriteNullableDoubleArray(MarketBreadthCalculator.TrinKey, result.Series.Trin);
            WriteNullableDecimalArray(MarketBreadthCalculator.CviKey, result.Series.Cvi);

            writer.WriteEndObject(); // series

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
            if (requested.Count > _limits.MaxBreadthSymbols)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"At most {_limits.MaxBreadthSymbols} symbols per call.",
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

            if (distinctList.Count < 1)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "At least 1 symbol is required for market breadth.", SymbolsArgument);
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

            if (group.Symbols.Count > _limits.MaxBreadthSymbols)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"The group contains {group.Symbols.Count} symbols, exceeding the limit of {_limits.MaxBreadthSymbols}.",
                    GroupIdArgument);
            }

            if (group.Symbols.Count < 1)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "The group contains no symbols.", GroupIdArgument);
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

        if (all.Count > _limits.MaxBreadthSymbols)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"The universe contains {all.Count} symbols, exceeding the limit of {_limits.MaxBreadthSymbols}.",
                SymbolsArgument,
                "Specify symbols or groupId to narrow the universe.");
        }

        if (all.Count < 1)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "No symbols are available in the repository.", SymbolsArgument);
        }

        return all;
    }
}
