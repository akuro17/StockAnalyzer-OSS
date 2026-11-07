using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Rendering;

/// <summary>A series that is drawn: its name in the indicator result and its values for the drawn window (one per drawn bar, null where there is no value).</summary>
public sealed record AiChartSelectedSeries(string Name, IReadOnlyList<decimal?> Values);

/// <summary>
/// Chooses which output series of an indicator result an AI chart image draws, and cuts them to the drawn window (rule R-3 of plan 15: warm-up values never reach the picture
/// or the price range). A series is left out when the chart leaves it out (signal series, the series of <see cref="IndicatorChartSuppression"/>, the up-trend flag),
/// when the chart draws it as histogram bars (the name contains "Histogram"), or when its values equal those of a series already chosen (an alias of the main series).
/// At most <see cref="AiChartImageStyle.MaxSeriesPerIndicator"/> are kept, in the order of the result (the main series first).
/// </summary>
public static class AiChartSeriesSelector
{
    /// <summary>
    /// The drawn series. <paramref name="windowStart"/> is the index of the first drawn bar inside the result and <paramref name="windowCount"/> the number of drawn bars;
    /// a result shorter than the window gives null values for the missing bars. A failed result gives no series.
    /// </summary>
    public static IReadOnlyList<AiChartSelectedSeries> Select(IndicatorType type, IIndicatorResult result, int windowStart, int windowCount)
    {
        var selected = new List<AiChartSelectedSeries>();
        if (!result.IsSuccessful || windowCount <= 0)
        {
            return selected;
        }

        foreach (string name in result.SeriesNames)
        {
            if (selected.Count >= AiChartImageStyle.MaxSeriesPerIndicator)
            {
                break;
            }

            if (IsLeftOut(type, name))
            {
                continue;
            }

            IReadOnlyList<decimal?> window = Window(result.GetSeries(name), windowStart, windowCount);
            if (window.All(v => v is null) || selected.Any(s => SameValues(s.Values, window)))
            {
                continue;
            }

            selected.Add(new AiChartSelectedSeries(name, window));
        }

        return selected;
    }

    /// <summary>True for a series name that the image never draws as a line.</summary>
    public static bool IsLeftOut(IndicatorType type, string seriesName)
        => seriesName == IndicatorSeriesNames.UpTrendFlag
           || IndicatorSeriesNames.IsSignalSeries(seriesName)
           || seriesName.Contains("Histogram", StringComparison.OrdinalIgnoreCase)
           || IndicatorChartSuppression.IsSuppressed(type, seriesName);

    private static IReadOnlyList<decimal?> Window(IReadOnlyList<decimal?> series, int start, int count)
    {
        var window = new decimal?[count];
        for (int i = 0; i < count; i++)
        {
            int index = start + i;
            window[i] = index >= 0 && index < series.Count ? series[index] : null;
        }

        return window;
    }

    private static bool SameValues(IReadOnlyList<decimal?> a, IReadOnlyList<decimal?> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}
