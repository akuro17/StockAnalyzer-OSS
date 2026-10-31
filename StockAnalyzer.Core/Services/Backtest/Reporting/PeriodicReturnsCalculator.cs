using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// Return of one calendar month (<see cref="Month"/> 1..12) or year (<see cref="Month"/> null). <see cref="ReturnRatio"/> is the unitless
/// ratio <c>E_end / E_start - 1</c> (0.05 = +5%), or null with <see cref="Reason"/> when E_start is not positive.
/// <see cref="IsPartial"/> is true when the evaluation period starts after the first day of this period or ends before its last day.
/// </summary>
public readonly record struct PeriodicReturn(int Year, int? Month, decimal? ReturnRatio, string? Reason, bool IsPartial);

public readonly record struct PeriodicReturns(ImmutableArray<PeriodicReturn> Monthly, ImmutableArray<PeriodicReturn> Yearly);

/// <summary>
/// Pure monthly and yearly returns of the evaluated equity series. E_end is the equity of the last evaluated bar of the period; E_start is
/// the E_end of the previous period that has an evaluated bar, and for the first period the initial capital. Month membership is
/// <see cref="TimeFrameAggregator.GetPeriodKey"/> (MN1); a year is the calendar year of the bar date.
/// </summary>
public static class PeriodicReturnsCalculator
{
    /// <summary>Reason code of a null return: the starting equity was not positive, so a ratio is meaningless.</summary>
    public const string NonPositiveStartEquityReason = "NonPositiveStartEquity";

    /// <param name="initialCapital">E_0, the start equity of the first period.</param>
    /// <param name="points">Evaluated points only (warm-up removed), chronological.</param>
    /// <param name="evaluationStart">First day of the requested evaluation period; decides whether the first period is partial.</param>
    /// <param name="evaluationEnd">Last day of the requested evaluation period; decides whether the last period is partial.</param>
    public static PeriodicReturns Calculate(
        decimal initialCapital,
        ImmutableArray<EquityPoint> points,
        DateTime evaluationStart,
        DateTime evaluationEnd)
    {
        if (points.IsDefaultOrEmpty)
        {
            return new PeriodicReturns(ImmutableArray<PeriodicReturn>.Empty, ImmutableArray<PeriodicReturn>.Empty);
        }

        ImmutableArray<PeriodicReturn>.Builder monthly = ImmutableArray.CreateBuilder<PeriodicReturn>();
        ImmutableArray<PeriodicReturn>.Builder yearly = ImmutableArray.CreateBuilder<PeriodicReturn>();
        decimal monthStart = initialCapital;
        decimal yearStart = initialCapital;
        for (int i = 0; i < points.Length; i++)
        {
            DateTime date = points[i].Timestamp.Date;
            bool lastOfMonth = i == points.Length - 1 || MonthKey(points[i + 1].Timestamp) != MonthKey(date);
            bool lastOfYear = i == points.Length - 1 || points[i + 1].Timestamp.Year != date.Year;
            decimal end = points[i].Equity;

            if (lastOfMonth)
            {
                monthly.Add(Build(date.Year, date.Month, monthStart, end, IsPartialMonth(date, evaluationStart, evaluationEnd)));
                monthStart = end;
            }

            if (lastOfYear)
            {
                yearly.Add(Build(date.Year, null, yearStart, end, IsPartialYear(date, evaluationStart, evaluationEnd)));
                yearStart = end;
            }
        }

        return new PeriodicReturns(monthly.ToImmutable(), yearly.ToImmutable());
    }

    private static int MonthKey(DateTime timestamp) => TimeFrameAggregator.GetPeriodKey(timestamp, TimeFrame.MN1);

    private static PeriodicReturn Build(int year, int? month, decimal start, decimal end, bool isPartial) =>
        start > 0m
            ? new PeriodicReturn(year, month, end / start - 1m, null, isPartial)
            : new PeriodicReturn(year, month, null, NonPositiveStartEquityReason, isPartial);

    private static bool IsPartialMonth(DateTime periodDate, DateTime evaluationStart, DateTime evaluationEnd) =>
        (MonthKey(evaluationStart) == MonthKey(periodDate) && evaluationStart.Date > TimeFrameAggregator.PeriodStart(periodDate, TimeFrame.MN1))
        || (MonthKey(evaluationEnd) == MonthKey(periodDate) && evaluationEnd.Date < TimeFrameAggregator.PeriodEnd(periodDate, TimeFrame.MN1));

    private static bool IsPartialYear(DateTime periodDate, DateTime evaluationStart, DateTime evaluationEnd) =>
        (evaluationStart.Year == periodDate.Year && evaluationStart.Date > new DateTime(periodDate.Year, 1, 1))
        || (evaluationEnd.Year == periodDate.Year && evaluationEnd.Date < new DateTime(periodDate.Year, 12, 31));
}
