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
    public const string SummaryBarsArgument = "summaryBars";
    public const string SummaryWindowBarsKey = "summaryWindowBars";
    public const string SummaryDistributionArgument = "summaryDistribution";
    public const string LookbackOffsetsArgument = "lookbackOffsets";
    public const string IncludeSeriesArgument = "includeSeries";
    public const string DecimalPlacesArgument = "decimalPlaces";

    /// <summary>The most decimal places <c>decimalPlaces</c> may name (owner decision U-02: 0..10).</summary>
    public const int MaxDecimalPlaces = 10;

    /// <summary>
    /// The description every tool that offers <c>summaryDistribution</c> gives the argument (one text, so the two tools cannot disagree): the six added fields and the rule for robustTrendSlope.
    /// </summary>
    public static readonly string SummaryDistributionDescription =
        "true adds, to every series summary, median, iqr (interquartile range, type 7), mad (median absolute deviation, raw), skewness (sample G1; null below 3 values or for a flat series), " +
        "excessKurtosis (sample G2; null below 4 values or for a flat series) and robustTrendSlope (Theil-Sen slope per bar over the same trendBars window as trendSlope; null below two values). " +
        $"For protection against the quadratic cost, robustTrendSlope is only calculated when trendBars is {SeriesSummaryStatistics.MinTrendBars} to {SeriesSummaryStatistics.MaxRobustTrendBars}: with a larger trendBars it is null " +
        "(that is not an error and not a data shortage; trendSlope and every other field are unaffected). Needs summary true. Default false.";

    /// <summary>
    /// The description every tool that offers <c>summaryBars</c> gives the argument (one text, so the two tools cannot disagree).
    /// </summary>
    public static string SummaryBarsDescription(McpLimits limits)
        => $"Number of the LAST calculated bars the summary is computed over (needs summary true; from {SeriesSummaryStatistics.MinTrendBars} to {McpLimitsContract.EffectiveMaxSummaryBars(limits)}). " +
           "Null values inside that tail count as bars; a value above the number of calculated bars is clamped to it (not an error). " +
           "Each series summary then also reports summaryBars (as sent) and summaryWindowBars (the bars actually used). pastValues always read the whole calculated window. Default: the whole calculated window.";

    private SeriesOutputOptions(bool includeSeries, bool summary, bool summaryDistribution, int trendBars, int? summaryBars, IReadOnlyList<long> lookbackOffsets, int? decimalPlaces)
    {
        IncludeSeries = includeSeries;
        Summary = summary;
        SummaryDistribution = summaryDistribution;
        TrendBars = trendBars;
        SummaryBars = summaryBars;
        LookbackOffsets = lookbackOffsets;
        DecimalPlaces = decimalPlaces;
    }

    /// <summary>False leaves out <c>series</c>, <c>projectedValues</c> and the top-level <c>timestamps</c>.</summary>
    public bool IncludeSeries { get; }

    public bool Summary { get; }

    /// <summary>True adds the distribution figures (median, iqr, mad, skewness, excessKurtosis, robustTrendSlope) to every series summary; needs <see cref="Summary"/>.</summary>
    public bool SummaryDistribution { get; }

    /// <summary>The trend window, in bars, of the summary's <c>trendSlope</c>.</summary>
    public int TrendBars { get; }

    /// <summary>The number of last calculated bars the summary covers, as sent; null (the argument is absent) means the whole calculated window.</summary>
    public int? SummaryBars { get; }

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

    /// <summary>The schema text of <c>decimalPlaces</c> for a tool that rounds the numbers named by <paramref name="what"/> (output only; no calculation uses a rounded number).</summary>
    public static string DecimalPlacesDescription(string what)
        => $"Round {what} to this many decimal places (0 to {MaxDecimalPlaces}; away from zero at the midpoint). Counts, indexes, dates and text are never rounded. Default: the numbers as calculated.";

    /// <summary>
    /// The double counterpart of <see cref="Round(decimal, int)"/>, for a tool whose statistic is a double: a non-finite value is returned unchanged (the writer decides how it is shown),
    /// anything else is rounded away from zero at the midpoint of its binary value, and a result of zero is a plain zero (never -0).
    /// </summary>
    public static double Round(double value, int places)
    {
        if (!double.IsFinite(value))
        {
            return value;
        }

        double rounded = Math.Round(value, places, MidpointRounding.AwayFromZero);
        return rounded == 0d ? 0d : rounded;
    }

    /// <summary><see cref="Round(decimal, int)"/> when <paramref name="places"/> is given (the tool's <c>decimalPlaces</c>), otherwise <paramref name="value"/> unchanged.</summary>
    public static decimal RoundIfRequested(decimal value, int? places) => places is { } p ? Round(value, p) : value;

    /// <summary><see cref="Round(double, int)"/> when <paramref name="places"/> is given, otherwise <paramref name="value"/> unchanged.</summary>
    public static double RoundIfRequested(double value, int? places) => places is { } p ? Round(value, p) : value;

    /// <summary><see cref="RoundIfRequested(decimal, int?)"/> for a value that may be missing; null stays null.</summary>
    public static decimal? RoundIfRequested(decimal? value, int? places) => value is { } number ? RoundIfRequested(number, places) : null;

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
        bool summaryDistribution = ToolArgs.Boolean(arguments, SummaryDistributionArgument) ?? false;
        if (summaryDistribution && !summary)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{SummaryDistributionArgument}' adds fields to the summary and has no effect without '{SummaryArgument}'.",
                SummaryDistributionArgument,
                $"Set {SummaryArgument} to true, or omit {SummaryDistributionArgument}.");
        }

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

        int? summaryBars = null;
        if (ToolArgs.Integer(arguments, SummaryBarsArgument) is { } requestedBars)
        {
            int largestBars = McpLimitsContract.EffectiveMaxSummaryBars(limits);
            if (!summary)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"'{SummaryBarsArgument}' is the number of last bars the summary covers and has no effect without '{SummaryArgument}'.",
                    SummaryBarsArgument,
                    $"Set {SummaryArgument} to true, or omit {SummaryBarsArgument}.");
            }

            if (requestedBars < SeriesSummaryStatistics.MinTrendBars)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{SummaryBarsArgument}' must be at least {SeriesSummaryStatistics.MinTrendBars}.", SummaryBarsArgument);
            }

            if (requestedBars > largestBars)
            {
                throw new ToolException(ToolErrorCodes.LimitExceeded, $"Argument '{SummaryBarsArgument}' is larger than the limit {largestBars}.", SummaryBarsArgument, $"Use {largestBars} or less.");
            }

            summaryBars = (int)requestedBars;
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

        return new SeriesOutputOptions(includeSeries, summary, summaryDistribution, trendBars, summaryBars, offsets, ReadDecimalPlaces(arguments));
    }

    /// <summary>
    /// Writes <c>summary</c> and <c>pastValues</c> (each only when asked for) for the series of one result. A series is read over the whole calculation window of
    /// <paramref name="barCount"/> bars, whatever tail is returned; entries beyond it (a projection) are not part of the window. With <see cref="SummaryBars"/> the summary
    /// covers only the last bars of that window; <c>pastValues</c> always read the whole window.
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
            // The summary window: the whole calculated window, or its last K bars when summaryBars was sent. It is a separate variable, never a slice of
            // `windows`, because pastValues below index the whole window (barCount - 1 - offset).
            int? windowBars = SummaryBars is { } requestedBars ? Math.Min(requestedBars, barCount) : null;
            foreach ((string name, IReadOnlyList<decimal?> values) in windows)
            {
                IReadOnlyList<decimal?> summaryValues = windowBars is { } tailBars ? Tail(values, tailBars) : values;
                SeriesSummary summary = SummaryDistribution ? SeriesSummaryStatistics.CalculateDistribution(summaryValues, TrendBars) : SeriesSummaryStatistics.Calculate(summaryValues, TrendBars);
                writer.WriteStartObject(name);
                writer.WriteNumber("count", summary.Count);
                WriteNullable(writer, "latest", Round(summary.Latest));
                WriteNullable(writer, "mean", Round(summary.Mean));
                WriteNullable(writer, "stdDev", Round(summary.StdDev));
                WriteNullable(writer, "zScoreOfLatest", Round(summary.ZScoreOfLatest));
                WriteNullable(writer, "percentileRankOfLatest", Round(summary.PercentileRankOfLatest));
                WriteNullable(writer, "trendSlope", Round(summary.TrendSlope));
                writer.WriteNumber("trendBars", summary.TrendBars);
                if (SummaryBars is { } sentBars && windowBars is { } usedBars)
                {
                    writer.WriteNumber(SummaryBarsArgument, sentBars);
                    writer.WriteNumber(SummaryWindowBarsKey, usedBars);
                }

                if (SummaryDistribution)
                {
                    WriteNullable(writer, "median", Round(summary.Median));
                    WriteNullable(writer, "iqr", Round(summary.Iqr));
                    WriteNullable(writer, "mad", Round(summary.Mad));
                    WriteNullable(writer, "skewness", Round(summary.Skewness));
                    WriteNullable(writer, "excessKurtosis", Round(summary.ExcessKurtosis));
                    WriteNullable(writer, "robustTrendSlope", Round(summary.RobustTrendSlope));
                }

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

    /// <summary>The last <paramref name="count"/> entries of <paramref name="window"/> (a null entry is a bar like any other); the list itself when it already has that length.</summary>
    private static IReadOnlyList<decimal?> Tail(IReadOnlyList<decimal?> window, int count)
    {
        if (count >= window.Count)
        {
            return window;
        }

        var tail = new decimal?[count];
        int first = window.Count - count;
        for (int i = 0; i < count; i++)
        {
            tail[i] = window[first + i];
        }

        return tail;
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
