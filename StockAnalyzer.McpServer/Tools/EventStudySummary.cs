using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// The descriptive statistics of the forward returns of the events of sa_extract_signal_events (<c>includeEventStudy</c>). Pure: it reads the forward returns the tool already
/// calculated (ratios, 0.05 = 5%, the unit of <c>forwardReturns</c>) and never looks at a bar. Mean, median and the population standard deviation come from
/// <see cref="SeriesSummaryStatistics.CalculateDistribution"/>; only the smallest, the largest and the share of positive returns are defined here. Everything is decimal.
/// </summary>
internal static class EventStudySummary
{
    /// <summary>The fixed note written with every event study: the events of EveryTrueBar overlap, so the figures describe the sample and test nothing.</summary>
    public const string OverlapNote = "Events of the EveryTrueBar mode overlap in time (consecutive bars share their forward windows); the figures are descriptive statistics of the forward returns, not a significance test.";

    /// <summary>The figures of one horizon on one basis. Every figure but the two counts is null when there is no value (and <see cref="StdDev"/> also below two values).</summary>
    internal sealed record Stats(int Count, int MissingCount, decimal? Mean, decimal? Median, decimal? StdDev, decimal? Min, decimal? Max, decimal? PositiveRatio);

    /// <summary>
    /// The figures of <paramref name="returns"/> (one entry per event, in event order, null where the horizon is not available) for a call that found
    /// <paramref name="eventCount"/> events. <c>MissingCount</c> is <paramref name="eventCount"/> minus the number of values.
    /// </summary>
    public static Stats Summarize(IReadOnlyList<decimal?> returns, int eventCount)
    {
        ArgumentNullException.ThrowIfNull(returns);
        decimal[] values = returns.Where(r => r.HasValue).Select(r => r!.Value).ToArray();
        if (values.Length == 0)
        {
            return new Stats(0, eventCount, null, null, null, null, null, null);
        }

        // The population standard deviation (divisor n, formula 0052) and the type 7 median are the Core definitions; the window is the whole list, so the trend window is irrelevant.
        SeriesSummary distribution = SeriesSummaryStatistics.CalculateDistribution(values.Select(v => (decimal?)v).ToArray(), SeriesSummaryStatistics.MinTrendBars);
        int positive = values.Count(v => v > 0m);
        return new Stats(
            values.Length,
            eventCount - values.Length,
            distribution.Mean,
            distribution.Median,
            distribution.StdDev,
            values.Min(),
            values.Max(),
            (decimal)positive / values.Length);
    }

    /// <summary>
    /// Writes the <c>eventStudy</c> property: the event count, the fixed note, the look-ahead flag and, for each basis, the figures of every horizon in the order of
    /// <paramref name="horizons"/> (keys are the horizons as invariant integers, as in <c>forwardReturns</c>). <paramref name="fromClose"/> and <paramref name="fromNextOpen"/>
    /// hold one dictionary per event, in event order.
    /// </summary>
    public static void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<int> horizons,
        IReadOnlyList<IReadOnlyDictionary<string, decimal?>> fromClose,
        IReadOnlyList<IReadOnlyDictionary<string, decimal?>> fromNextOpen)
    {
        int eventCount = fromClose.Count;
        writer.WriteStartObject("eventStudy");
        writer.WriteNumber("eventCount", eventCount);
        writer.WriteString("overlapNote", OverlapNote);
        writer.WriteBoolean("lookAheadDiagnostic", true);
        WriteBasis(writer, "fromClose", horizons, fromClose, eventCount);
        WriteBasis(writer, "fromNextOpen", horizons, fromNextOpen, eventCount);
        writer.WriteEndObject();
    }

    private static void WriteBasis(Utf8JsonWriter writer, string name, IReadOnlyList<int> horizons, IReadOnlyList<IReadOnlyDictionary<string, decimal?>> perEvent, int eventCount)
    {
        writer.WriteStartObject(name);
        foreach (int horizon in horizons)
        {
            string key = horizon.ToString(CultureInfo.InvariantCulture);
            Stats stats = Summarize(perEvent.Select(e => e.TryGetValue(key, out decimal? value) ? value : null).ToArray(), eventCount);
            writer.WriteStartObject(key);
            writer.WriteNumber("count", stats.Count);
            writer.WriteNumber("missingCount", stats.MissingCount);
            WriteNullable(writer, "mean", stats.Mean);
            WriteNullable(writer, "median", stats.Median);
            WriteNullable(writer, "stdDev", stats.StdDev);
            WriteNullable(writer, "min", stats.Min);
            WriteNullable(writer, "max", stats.Max);
            WriteNullable(writer, "positiveRatio", stats.PositiveRatio);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
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
