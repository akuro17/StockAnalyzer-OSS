using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// The optional output shaping of sa_calculate_indicators: a statistical summary of every returned series (<c>summary</c>, <c>trendBars</c>), the values a given number of bars
/// before the last calculated bar (<c>lookbackOffsets</c>), the choice to leave the series arrays out (<c>includeSeries</c>) and the rounding of every written decimal
/// number (<c>decimalPlaces</c>). The default instance changes nothing.
/// The statistics are <see cref="SeriesSummaryStatistics"/> in Core; this class only validates the arguments and writes the JSON.
/// </summary>
internal sealed class SeriesOutputOptions
{
    public const string SummaryArgument = "summary";
    public const string TrendBarsArgument = "trendBars";
    public const string LookbackOffsetsArgument = "lookbackOffsets";
    public const string IncludeSeriesArgument = "includeSeries";
    public const string DecimalPlacesArgument = "decimalPlaces";

    /// <summary>The most decimal places <c>decimalPlaces</c> may name (owner decision U-02: 0..10).</summary>
    public const int MaxDecimalPlaces = 10;

    private SeriesOutputOptions(bool includeSeries, bool summary, int trendBars, IReadOnlyList<long> lookbackOffsets, int? decimalPlaces)
    {
        IncludeSeries = includeSeries;
        Summary = summary;
        TrendBars = trendBars;
        LookbackOffsets = lookbackOffsets;
        DecimalPlaces = decimalPlaces;
    }

    /// <summary>False leaves out <c>series</c>, <c>projectedValues</c> and the top-level <c>timestamps</c>.</summary>
    public bool IncludeSeries { get; }

    public bool Summary { get; }

    /// <summary>The trend window, in bars, of the summary's <c>trendSlope</c>.</summary>
    public int TrendBars { get; }

    /// <summary>The distinct offsets asked for, in the order they first appear; offset k is the bar k bars before the last calculated bar.</summary>
    public IReadOnlyList<long> LookbackOffsets { get; }

    /// <summary>The decimal places every written decimal number is rounded to (away from zero at the midpoint); null leaves the numbers as calculated.</summary>
    public int? DecimalPlaces { get; }

    /// <summary>
    /// Reads <c>decimalPlaces</c> (0 to <see cref="MaxDecimalPlaces"/>; below 0 is INVALID_ARGUMENT, above it LIMIT_EXCEEDED); null when the call does not name it.
    /// The one reading of the argument for every tool that offers it.
    /// </summary>
    public static int? ReadDecimalPlaces(JsonElement arguments)
        => ToolArgs.Integer(arguments, DecimalPlacesArgument) is null
            ? null
            : ToolArgs.NonNegativeWithin(arguments, DecimalPlacesArgument, 0, MaxDecimalPlaces);

    /// <summary>
    /// Rounds <paramref name="value"/> to <paramref name="places"/> decimal places, away from zero at the midpoint; a result of zero is written as a plain zero.
    /// Only the written number changes: the calculation that produced it is never rounded.
    /// </summary>
    public static decimal Round(decimal value, int places)
    {
        decimal rounded = Math.Round(value, places, MidpointRounding.AwayFromZero);
        return rounded == 0m ? 0m : rounded;
    }

    /// <summary><see cref="Round(decimal, int)"/> by <see cref="DecimalPlaces"/>; null stays null, and without <see cref="DecimalPlaces"/> the value is returned unchanged.</summary>
    public decimal? Round(decimal? value) => value is { } number && DecimalPlaces is { } places ? Round(number, places) : value;

    /// <summary>
    /// Reads and validates the arguments; nothing is loaded yet, so a refusal costs no data access. <paramref name="includeSeriesDefault"/> is what a call that does not name
    /// <c>includeSeries</c> gets; the rule that a call without series must ask for a summary or past values applies only where the series are on by default
    /// (a tool whose default answer is the latest values, as sa_calculate_indicators_batch, needs no such request).
    /// </summary>
    public static SeriesOutputOptions Read(JsonElement arguments, McpLimits limits, bool includeSeriesDefault = true)
    {
        bool includeSeries = ToolArgs.Boolean(arguments, IncludeSeriesArgument) ?? includeSeriesDefault;
        bool summary = ToolArgs.Boolean(arguments, SummaryArgument) ?? false;

        int trendBars = McpLimitsContract.EffectiveDefaultTrendBars(limits);
        if (ToolArgs.Integer(arguments, TrendBarsArgument) is { } requested)
        {
            int largest = McpLimitsContract.EffectiveMaxTrendBars(limits);
            if (!summary)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"'{TrendBarsArgument}' is the trend window of the summary and has no effect without '{SummaryArgument}'.",
                    TrendBarsArgument,
                    $"Set {SummaryArgument} to true, or omit {TrendBarsArgument}.");
            }

            if (requested < SeriesSummaryStatistics.MinTrendBars)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{TrendBarsArgument}' must be at least {SeriesSummaryStatistics.MinTrendBars}.", TrendBarsArgument);
            }

            if (requested > largest)
            {
                throw new ToolException(ToolErrorCodes.LimitExceeded, $"Argument '{TrendBarsArgument}' is larger than the limit {largest}.", TrendBarsArgument, $"Use {largest} or less.");
            }

            trendBars = (int)requested;
        }

        var offsets = new List<long>();
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(LookbackOffsetsArgument, out JsonElement list))
        {
            if (list.GetArrayLength() > limits.MaxLookbackOffsets)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"At most {limits.MaxLookbackOffsets} lookback offsets per call.",
                    LookbackOffsetsArgument,
                    $"Send {limits.MaxLookbackOffsets} or fewer, or split the call.");
            }

            int index = 0;
            foreach (JsonElement item in list.EnumerateArray())
            {
                long offset = item.GetInt64();
                if (offset < 0)
                {
                    throw new ToolException(
                        ToolErrorCodes.InvalidArgument,
                        "A lookback offset must be 0 or more (0 is the last calculated bar, 1 the bar before it).",
                        $"{LookbackOffsetsArgument}[{index}]");
                }

                if (!offsets.Contains(offset))
                {
                    offsets.Add(offset);
                }

                index++;
            }
        }

        if (includeSeriesDefault && !includeSeries && !summary && offsets.Count == 0)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{IncludeSeriesArgument}' false would return no series and nothing else to read.",
                IncludeSeriesArgument,
                $"Also set {SummaryArgument} true or name {LookbackOffsetsArgument}, or leave {IncludeSeriesArgument} out.");
        }

        return new SeriesOutputOptions(includeSeries, summary, trendBars, offsets, ReadDecimalPlaces(arguments));
    }

    /// <summary>
    /// Writes <c>summary</c> and <c>pastValues</c> (each only when asked for) for the series of one result. A series is read over the whole calculation window of
    /// <paramref name="barCount"/> bars, whatever tail is returned; entries beyond it (a projection) are not part of the window.
    /// </summary>
    public void WriteStatistics(Utf8JsonWriter writer, IEnumerable<(string Name, IReadOnlyList<decimal?> Values)> series, int barCount)
    {
        if (!Summary && LookbackOffsets.Count == 0)
        {
            return;
        }

        var windows = series.Select(s => (s.Name, Values: Window(s.Values, barCount))).ToList();
        if (Summary)
        {
            writer.WriteStartObject("summary");
            foreach ((string name, IReadOnlyList<decimal?> values) in windows)
            {
                SeriesSummary summary = SeriesSummaryStatistics.Calculate(values, TrendBars);
                writer.WriteStartObject(name);
                writer.WriteNumber("count", summary.Count);
                WriteNullable(writer, "latest", Round(summary.Latest));
                WriteNullable(writer, "mean", Round(summary.Mean));
                WriteNullable(writer, "stdDev", Round(summary.StdDev));
                WriteNullable(writer, "zScoreOfLatest", Round(summary.ZScoreOfLatest));
                WriteNullable(writer, "percentileRankOfLatest", Round(summary.PercentileRankOfLatest));
                WriteNullable(writer, "trendSlope", Round(summary.TrendSlope));
                writer.WriteNumber("trendBars", summary.TrendBars);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        if (LookbackOffsets.Count > 0)
        {
            writer.WriteStartObject("pastValues");
            foreach ((string name, IReadOnlyList<decimal?> values) in windows)
            {
                writer.WriteStartObject(name);
                foreach (long offset in LookbackOffsets)
                {
                    long index = barCount - 1 - offset;
                    WriteNullable(writer, offset.ToString(CultureInfo.InvariantCulture), index >= 0 && index < values.Count ? Round(values[(int)index]) : null);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }
    }

    /// <summary>The first <paramref name="barCount"/> entries of <paramref name="values"/>, padded with nulls when the series is shorter.</summary>
    private static IReadOnlyList<decimal?> Window(IReadOnlyList<decimal?> values, int barCount)
    {
        if (values.Count == barCount)
        {
            return values;
        }

        var window = new decimal?[barCount];
        for (int i = 0; i < barCount && i < values.Count; i++)
        {
            window[i] = values[i];
        }

        return window;
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
