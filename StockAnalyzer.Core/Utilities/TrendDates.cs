using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utilities;

/// <summary>
/// The reversal date, the update date and the number of calendar days between them for one element of a chart that is not a time series.
/// Every part is null when it is not defined (see <see cref="TrendDates.Describe"/>).
/// </summary>
public readonly record struct TrendDateSpan(DateTime? ReversalDate, DateTime? UpdateDate, int? Days)
{
    public static readonly TrendDateSpan None = new(null, null, null);
}

/// <summary>
/// The single definition of the "reversal date", "update date" and "days since the reversal" of Renko, Point and Figure, Kagi and Three Line Break.
/// <list type="bullet">
/// <item>Run: the elements that go in the same direction (up when <see cref="CoreCandleData.IsBullish"/>, that is Close &gt;= Open). Renko / Three Line Break: consecutive elements of one direction;
/// Point and Figure: one column; Kagi: one column of <see cref="KagiColumns"/>.</item>
/// <item>Reversal date: the date on which the first element of the run was created (the confirmation date of the reversal), empty for the first run.</item>
/// <item>Update date: the date of the last update of the element (Renko / Three Line Break: its creation date; Point and Figure / Kagi: the last candle that extended the column).</item>
/// <item>Days: calendar days from the reversal date to the update date (the date parts only, so the time of day and the timeframe do not change the unit).</item>
/// </list>
/// </summary>
public static class TrendDates
{
    /// <summary>
    /// Describes the element at <paramref name="index"/> of the drawn <paramref name="series"/>. Point and Figure and Kagi need
    /// <paramref name="dates"/> (one pair per element); without them, or when their count differs from the series, nothing is described. Never throws, does not change
    /// its input and does not allocate.
    /// </summary>
    public static TrendDateSpan Describe(
        IReadOnlyList<CoreCandleData>? series, IReadOnlyList<ElementDates>? dates, int index, ChartType chartType)
    {
        if (series == null || series.Count == 0 || index < 0 || index >= series.Count)
        {
            return TrendDateSpan.None;
        }

        DateTime update;
        DateTime? reversal;
        switch (chartType)
        {
            case ChartType.Renko:
            case ChartType.ThreeLineBreak:
                update = series[index].Timestamp;
                reversal = RunStartDate(series, index);
                break;

            case ChartType.PointAndFigure:
                if (!HasDatesFor(series, dates))
                {
                    return TrendDateSpan.None;
                }

                update = dates![index].End;
                reversal = index >= 1 ? dates[index].Start : null;
                break;

            case ChartType.Kagi:
                if (!HasDatesFor(series, dates))
                {
                    return TrendDateSpan.None;
                }

                var (start, end) = KagiColumns.ColumnBounds(series, index);
                update = dates![end].End;
                reversal = start >= 1 && !IsFlatAnchor(series, start - 1) ? dates[start].Start : null;
                break;

            default:
                return TrendDateSpan.None;
        }

        if (reversal == null)
        {
            return new TrendDateSpan(null, update, null);
        }

        int days = (update.Date - reversal.Value.Date).Days;
        return new TrendDateSpan(reversal, update, days < 0 ? null : days);
    }

    private static bool HasDatesFor(IReadOnlyList<CoreCandleData> series, IReadOnlyList<ElementDates>? dates) =>
        dates != null && dates.Count == series.Count;

    /// <summary>The flat first segment of a Kagi chart (the anchor at the starting close), which belongs to no trend.</summary>
    private static bool IsFlatAnchor(IReadOnlyList<CoreCandleData> series, int index) =>
        index == 0 && series[0].Open == series[0].Close;

    /// <summary>The creation date of the first element of the run that contains <paramref name="index"/>; null when the run is the first one of the series.</summary>
    private static DateTime? RunStartDate(IReadOnlyList<CoreCandleData> series, int index)
    {
        bool isUp = series[index].IsBullish;
        int runStart = index;
        while (runStart > 0 && series[runStart - 1].IsBullish == isUp)
        {
            runStart--;
        }

        return runStart >= 1 ? series[runStart].Timestamp : null;
    }
}
