using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_calculate_relative_performance: the values the Relative Performance chart plots for one symbol and its comparison symbols. The symbols are aligned to the
/// primary symbol's bars by the chart's own <see cref="ComparisonDataAligner"/> and the values come from <see cref="ComparisonSeriesCalculator"/> (the chart's
/// formulas); the tool adds the argument mapping, the limits and the JSON.
/// </summary>
internal sealed class CalculateRelativePerformanceTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string ComparisonSymbolsArgument = "comparisonSymbols";
    private const string ModeArgument = "mode";
    private const string ZScorePeriodArgument = "zScorePeriod";
    private const string EndDateArgument = "endDate";
    private const string LookbackArgument = "lookbackBars";
    private const string TailArgument = "outputTailCount";

    private const string SamePrimaryReason = "same as the primary symbol";
    private const string DuplicateReason = "listed more than once";
    private const string ExcludedReason = "excluded by the aligner (see warnings)";

    private static readonly IReadOnlyDictionary<ComparisonMode, string> Units = new Dictionary<ComparisonMode, string>
    {
        [ComparisonMode.Performance] = "percent",
        [ComparisonMode.Ratio] = "ratio",
        [ComparisonMode.ZScore] = "sigma",
        [ComparisonMode.Spread] = "price-difference",
    };

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly ErrorMessageSanitizer _sanitizer;
    private readonly McpLimits _limits;

    public CalculateRelativePerformanceTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, ErrorMessageSanitizer sanitizer, McpLimits limits)
    {
        _gateway = gateway;
        _symbols = symbols;
        _loader = new CandleWindowLoader(gateway);
        _sanitizer = sanitizer;
        _limits = limits;
    }

    public string Name => "sa_calculate_relative_performance";

    public string Description =>
        "Calculates the values the Relative Performance chart plots for a primary symbol and its comparison symbols. " +
        $"Up to {_limits.MaxComparisonTargets} comparison symbols are aligned to the primary symbol's bars (gaps carried forward, badly fragmented symbols excluded, as the chart does). " +
        "mode Performance (default) is the percentage change of the close from the first returned bar (the chart's left edge, rebased at 0 %); Ratio is the close divided by the primary symbol's close; " +
        "Spread is the close minus the primary symbol's close (price units); ZScore is the close's distance from its mean over the last zScorePeriod bars in population standard deviations. " +
        "A value that cannot be defined is null. The unit of the values is in the result (unit). " +
        $"The last outputTailCount bars (default {_limits.DefaultOutputTailBars}, at most {_limits.MaxOutputTailBars}) of up to lookbackBars stored bars (default and maximum {_limits.MaxCandlesPerRequest}) are returned. " +
        "The tool never changes anything and never fetches data; it does not read the chart's saved symbol list or presets.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"comparisonSymbols\":[\"6758-T\"],\"mode\":\"Performance\",\"outputTailCount\":120}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Primary ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars. Supported: {ToolArgs.SupportedTimeFrameNames}.", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .StringArray(ComparisonSymbolsArgument, $"Comparison symbols (stored tickers). The primary symbol and repeats are ignored and listed in ignoredSymbols. At most {_limits.MaxComparisonTargets} different ones; default none.")
        .String(ModeArgument, $"Comparison mode. Supported: {string.Join(", ", Enum.GetNames<ComparisonMode>())}. Default {ComparisonMode.Performance}.", allowedValues: Enum.GetNames<ComparisonMode>())
        .Integer(ZScorePeriodArgument, $"Z-Score window in bars, {ComparisonSeriesDefaults.MinZScorePeriod} to {ComparisonSeriesDefaults.MaxZScorePeriod}. Default {ComparisonSeriesDefaults.DefaultZScorePeriod}. Used by mode ZScore only.", defaultValue: ComparisonSeriesDefaults.DefaultZScorePeriod, minimum: ComparisonSeriesDefaults.MinZScorePeriod, maximum: ComparisonSeriesDefaults.MaxZScorePeriod)
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .Integer(LookbackArgument, $"Stored bars the alignment runs over, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(TailArgument, $"Number of trailing bars returned; the first of them is the Performance base. Default {_limits.DefaultOutputTailBars}, at most {_limits.MaxOutputTailBars}.", defaultValue: _limits.DefaultOutputTailBars, minimum: 1, maximum: _limits.MaxOutputTailBars)
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string symbol = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, cancellationToken).ConfigureAwait(false);
        TimeFrame timeFrame = ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument);
        (List<string> comparisons, List<(string Symbol, string Reason)> ignored) = await ResolveComparisonSymbolsAsync(arguments, symbol, cancellationToken).ConfigureAwait(false);
        ComparisonMode mode = ToolArgs.String(arguments, ModeArgument) is { } modeName ? Enum.Parse<ComparisonMode>(modeName) : ComparisonMode.Performance;
        int zScorePeriod = ZScorePeriod(arguments);
        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.MaxCandlesPerRequest, _limits.MaxCandlesPerRequest);
        int tail = ToolArgs.PositiveWithin(arguments, TailArgument, _limits.DefaultOutputTailBars, _limits.MaxOutputTailBars);

        int primaryBars = await CountPrimaryBarsAsync(symbol, timeFrame, endDate, lookback, cancellationToken).ConfigureAwait(false);

        var aligner = new ComparisonDataAligner(new EndDateAwareDataService(_loader, endDate)) { MaxComparisonSymbols = _limits.MaxComparisonTargets };
        ComparisonAlignedData aligned = await aligner.AlignAsync(symbol, comparisons, timeFrame, primaryBars, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        int alignedCount = aligned.Timestamps.Length;
        int returned = Math.Min(tail, alignedCount);
        int windowStart = alignedCount - returned;
        ComparisonSeriesResult values = ComparisonSeriesCalculator.Calculate(aligned, windowStart, returned, mode, zScorePeriod);

        var warnings = aligned.Warnings.Select(_sanitizer.Sanitize).ToList();
        List<string> order = new() { aligned.PrimarySymbol };
        var excluded = new List<(string Symbol, string Reason)>();
        foreach (string comparison in comparisons)
        {
            if (aligned.Series.ContainsKey(comparison))
            {
                order.Add(comparison);
            }
            else
            {
                excluded.Add((comparison, ExcludedReason));
            }
        }

        foreach (string name in order)
        {
            if (values.Series[name].Values.All(v => v is null))
            {
                warnings.Add($"{name}: no valid value in the returned window.");
            }
        }

        return ToolJson.Build(writer => WriteResult(writer, aligned, values, order, excluded, ignored, warnings, symbol, timeFrame, endDate, mode, zScorePeriod, lookback, returned, windowStart));
    }

    /// <summary>
    /// Canonical, stored comparison symbols in the order given. The primary symbol (under the chart's own symbol equality) and repeats are
    /// dropped and reported, as the chart removes the primary symbol from its list.
    /// </summary>
    private async Task<(List<string> Comparisons, List<(string Symbol, string Reason)> Ignored)> ResolveComparisonSymbolsAsync(
        JsonElement arguments, string primary, CancellationToken cancellationToken)
    {
        var comparisons = new List<(string Symbol, int Index)>();
        var ignored = new List<(string Symbol, string Reason)>();
        IReadOnlyList<string> requested = ToolArgs.StringArray(arguments, ComparisonSymbolsArgument) ?? Array.Empty<string>();
        for (int i = 0; i < requested.Count; i++)
        {
            string canonical = SymbolResolver.Canonical(requested[i], ComparisonParameter(i));
            if (SymbolHelper.IsSameSymbol(canonical, primary))
            {
                ignored.Add((canonical, SamePrimaryReason));
            }
            else if (comparisons.Exists(c => string.Equals(c.Symbol, canonical, StringComparison.OrdinalIgnoreCase)))
            {
                ignored.Add((canonical, DuplicateReason));
            }
            else
            {
                comparisons.Add((canonical, i));

                // Refused at the first symbol over the limit: neither the rest of the list nor any stored-ticker lookup is spent on it,
                // and the duplicate scan above never runs over more than the limit.
                if (comparisons.Count > _limits.MaxComparisonTargets)
                {
                    throw new ToolException(
                        ToolErrorCodes.LimitExceeded,
                        $"At most {_limits.MaxComparisonTargets} different comparison symbols per call.",
                        ComparisonSymbolsArgument,
                        $"Send {_limits.MaxComparisonTargets} or fewer, or split the call.");
                }
            }
        }

        foreach ((string symbol, int index) in comparisons)
        {
            await _symbols.ResolveAsync(symbol, ComparisonParameter(index), cancellationToken).ConfigureAwait(false);
        }

        return (comparisons.Select(c => c.Symbol).ToList(), ignored);
    }

    private static string ComparisonParameter(int index) => $"{ComparisonSymbolsArgument}[{index}]";

    private static int ZScorePeriod(JsonElement arguments)
    {
        long? period = ToolArgs.Integer(arguments, ZScorePeriodArgument);
        if (period is null)
        {
            return ComparisonSeriesDefaults.DefaultZScorePeriod;
        }

        if (period < ComparisonSeriesDefaults.MinZScorePeriod || period > ComparisonSeriesDefaults.MaxZScorePeriod)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Argument '{ZScorePeriodArgument}' must be {ComparisonSeriesDefaults.MinZScorePeriod} to {ComparisonSeriesDefaults.MaxZScorePeriod}.",
                ZScorePeriodArgument,
                "A window of one bar has no deviation, so no Z-Score can be defined.");
        }

        return (int)period;
    }

    /// <summary>The number of primary bars the alignment will run over, with the same errors as the other single-symbol calculation tools.</summary>
    private async Task<int> CountPrimaryBarsAsync(string symbol, TimeFrame timeFrame, DateOnly? endDate, int lookback, CancellationToken cancellationToken)
    {
        if (endDate is not null)
        {
            List<CandleData> upTo = await _loader.LoadUpToAsync(symbol, timeFrame, endDate.Value, lookback, cancellationToken).ConfigureAwait(false);
            if (upTo.Count > 0)
            {
                return upTo.Count;
            }

            var check = await _gateway.GetCandlesAsync(symbol, timeFrame, 1, cancellationToken).ConfigureAwait(false);
            if (check.Count == 0)
            {
                throw new ToolException(ToolErrorCodes.SymbolNotFound, $"No stored market data exists for {symbol}.", SymbolArgument);
            }

            throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist on or before {endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)} for {symbol}.", EndDateArgument);
        }

        (List<CandleData> loaded, _) = await _loader.LoadAsync(symbol, timeFrame, lookback, cancellationToken).ConfigureAwait(false);
        if (loaded.Count == 0)
        {
            throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist for {symbol}.", SymbolArgument);
        }

        return loaded.Count;
    }

    private static void WriteResult(
        Utf8JsonWriter writer,
        ComparisonAlignedData aligned,
        ComparisonSeriesResult values,
        List<string> order,
        List<(string Symbol, string Reason)> excluded,
        List<(string Symbol, string Reason)> ignored,
        List<string> warnings,
        string symbol,
        TimeFrame timeFrame,
        DateOnly? endDate,
        ComparisonMode mode,
        int zScorePeriod,
        int lookback,
        int returned,
        int windowStart)
    {
        writer.WriteStartObject();
        writer.WriteString("symbol", symbol);
        writer.WriteString("timeFrame", timeFrame.ToString());
        if (endDate is not null)
        {
            writer.WriteString("endDate", endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
        }

        writer.WriteString("mode", mode.ToString());
        writer.WriteString("unit", Units[mode]);
        if (mode == ComparisonMode.ZScore)
        {
            writer.WriteNumber("zScorePeriod", zScorePeriod);
        }

        writer.WriteNumber("lookbackBars", lookback);
        writer.WriteNumber("alignedBarCount", aligned.Timestamps.Length);
        writer.WriteNumber("returnedBarCount", returned);
        if (mode == ComparisonMode.Performance && returned > 0)
        {
            writer.WriteString("baselineDate", ToolJson.FormatDate(aligned.Timestamps[windowStart]));
        }

        writer.WriteStartArray("timestamps");
        for (int i = 0; i < returned; i++)
        {
            writer.WriteStringValue(ToolJson.FormatDate(aligned.Timestamps[windowStart + i]));
        }

        writer.WriteEndArray();

        writer.WriteStartObject("series");
        foreach (string name in order)
        {
            ComparisonSeries series = values.Series[name];
            writer.WriteStartObject(name);
            writer.WriteBoolean("isPrimary", name == aligned.PrimarySymbol);
            if (series.BaseIndex is { } baseIndex && series.BasePrice is { } basePrice)
            {
                writer.WriteString("baseDate", ToolJson.FormatDate(aligned.Timestamps[baseIndex]));
                writer.WriteNumber("basePrice", basePrice);
            }

            writer.WriteStartArray("values");
            foreach (decimal? value in series.Values)
            {
                if (value is { } number)
                {
                    writer.WriteNumberValue(number);
                }
                else
                {
                    writer.WriteNullValue();
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();

        WriteReasons(writer, "excludedSymbols", excluded);
        WriteReasons(writer, "ignoredSymbols", ignored);
        writer.WriteStartArray("warnings");
        foreach (string warning in warnings)
        {
            writer.WriteStringValue(warning);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteReasons(Utf8JsonWriter writer, string name, List<(string Symbol, string Reason)> items)
    {
        writer.WriteStartArray(name);
        foreach ((string symbol, string reason) in items)
        {
            writer.WriteStartObject();
            writer.WriteString("symbol", symbol);
            writer.WriteString("reason", reason);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
