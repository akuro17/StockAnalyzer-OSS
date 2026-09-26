using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// One cell of a <see cref="SeasonalityMonthlyReturnsTable"/>: the fractional return for one
/// (row, year), or <c>null</c> when it is undefined for that year — a missing month close, or a year
/// with no positive base close.
/// </summary>
public readonly record struct SeasonalityMonthlyReturnCell(decimal? Return)
{
    /// <summary>True when <see cref="Return"/> holds a computed value.</summary>
    public bool IsDefined => Return.HasValue;
}

/// <summary>
/// Fixed-shape month-by-year matrix of returns for the seasonality base symbol (series 0). Rows are
/// the twelve calendar months (index 0 = January .. 11 = December) followed by the annual row at
/// <see cref="AnnualRowIndex"/>; columns are the overlaid calendar years in <see cref="Years"/>
/// order. <see cref="RowAverages"/> is the arithmetic (simple) mean of each row's defined cells —
/// never a compounded or geometric average (confirmation Q2).
/// </summary>
public sealed class SeasonalityMonthlyReturnsTable
{
    /// <summary>Row index of the annual (year-start to year-end) return row.</summary>
    public const int AnnualRowIndex = 12;

    /// <summary>Total row count: twelve months plus the annual row.</summary>
    public const int RowCount = 13;

    private readonly SeasonalityMonthlyReturnCell[][] _rows;

    internal SeasonalityMonthlyReturnsTable(
        IReadOnlyList<int> years,
        SeasonalityMonthlyReturnCell[][] rows,
        decimal?[] rowAverages,
        uint startMonth = 1)
    {
        Years = years;
        _rows = rows;
        RowAverages = rowAverages;
        StartMonth = startMonth;
    }

    /// <summary>Overlaid calendar years, ascending (mirrors <see cref="SeasonalityChartResult.CalendarYears"/>).</summary>
    public IReadOnlyList<int> Years { get; }

    /// <summary>Starting month (1..12) for this table (1 = January, 4 = April).</summary>
    public uint StartMonth { get; } = 1;

    /// <summary>
    /// Arithmetic mean of the defined cells in each row; <c>null</c> for a row with no defined cell.
    /// Length is always <see cref="RowCount"/>.
    /// </summary>
    public IReadOnlyList<decimal?> RowAverages { get; }

    /// <summary>The cell for <paramref name="rowIndex"/> (0..12) and <paramref name="yearIndex"/> (into <see cref="Years"/>).</summary>
    public SeasonalityMonthlyReturnCell Cell(int rowIndex, int yearIndex) => _rows[rowIndex][yearIndex];

    /// <summary>The whole row for <paramref name="rowIndex"/> (0..12): one cell per year in <see cref="Years"/> order.</summary>
    public IReadOnlyList<SeasonalityMonthlyReturnCell> Row(int rowIndex) => _rows[rowIndex];

    /// <summary>An all-undefined table with no overlaid years, used when the result has no base-symbol trace.</summary>
    public static SeasonalityMonthlyReturnsTable Empty { get; } = new(
        Array.Empty<int>(),
        CreateRows(0),
        new decimal?[RowCount],
        1);

    internal static SeasonalityMonthlyReturnCell[][] CreateRows(int yearCount)
    {
        var rows = new SeasonalityMonthlyReturnCell[RowCount][];
        for (int r = 0; r < RowCount; r++)
        {
            rows[r] = yearCount == 0 ? Array.Empty<SeasonalityMonthlyReturnCell>() : new SeasonalityMonthlyReturnCell[yearCount];
        }

        return rows;
    }
}

/// <summary>
/// Aggregates a <see cref="SeasonalityChartResult"/> into a month-by-year table of returns for the
/// base symbol (series 0). This is a separate aggregation, not an extension of
/// <see cref="SeasonalityChartEngine.Analyze"/>: its purpose (monthly buckets) differs from the
/// engine's (per-year polar polylines), and the engine is left completely unchanged. All arithmetic
/// stays in the <see cref="decimal"/> domain (financial values).
/// </summary>
public static class SeasonalityMonthlyReturnsEngine
{
    private const int MonthsPerYear = 12;

    /// <summary>
    /// Builds the monthly-returns table from <paramref name="result"/>. Only series 0 (the base
    /// symbol) is read — comparison / indicator series no longer exist in the redesigned chart.
    /// Returns <see cref="SeasonalityMonthlyReturnsTable.Empty"/> when there is no base-symbol trace.
    /// </summary>
    public static SeasonalityMonthlyReturnsTable Build(SeasonalityChartResult result)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        IReadOnlyList<int> years = result.CalendarYears;

        var traceByYear = new Dictionary<int, SeasonalityYearTrace>(years.Count);
        foreach (SeasonalityYearTrace trace in result.Traces)
        {
            if (trace.SeriesId == 0)
            {
                traceByYear[trace.CalendarYear] = trace;
            }
        }

        if (years.Count == 0 || traceByYear.Count == 0)
        {
            return SeasonalityMonthlyReturnsTable.Empty;
        }

        uint startMonth = result.Parameters.StartMonth;
        SeasonalityMonthlyReturnCell[][] rows = SeasonalityMonthlyReturnsTable.CreateRows(years.Count);

        for (int yearIndex = 0; yearIndex < years.Count; yearIndex++)
        {
            if (traceByYear.TryGetValue(years[yearIndex], out SeasonalityYearTrace? trace))
            {
                PopulateYearColumn(trace, rows, yearIndex, startMonth);
            }
        }

        var averages = new decimal?[SeasonalityMonthlyReturnsTable.RowCount];
        for (int row = 0; row < averages.Length; row++)
        {
            averages[row] = ArithmeticMean(rows[row]);
        }

        return new SeasonalityMonthlyReturnsTable(years, rows, averages, startMonth);
    }

    private static void PopulateYearColumn(
        SeasonalityYearTrace trace,
        SeasonalityMonthlyReturnCell[][] rows,
        int yearIndex,
        uint startMonth = 1)
    {
        IReadOnlyList<SeasonalitySample> samples = trace.Samples;

        decimal? yearBaseClose = ResolveYearBaseClose(samples);
        if (yearBaseClose is null)
        {
            return; // no positive base close -> the whole year column stays undefined (definition D-C step 1)
        }

        // Last available close per month (1..12); index 0 is unused.
        Span<decimal> monthClose = stackalloc decimal[MonthsPerYear + 1];
        Span<bool> monthHasClose = stackalloc bool[MonthsPerYear + 1];
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Value is { } close)
            {
                int month = samples[i].Timestamp.Month;
                monthClose[month] = close;
                monthHasClose[month] = true;
            }
        }

        for (int step = 0; step < MonthsPerYear; step++)
        {
            int month = (int)(((startMonth - 1 + step) % MonthsPerYear) + 1);
            if (!monthHasClose[month])
            {
                continue; // month has no close -> cell undefined, not zero (definition D-C step 2)
            }

            decimal previousClose = ResolvePreviousMonthEndClose(step, startMonth, yearBaseClose.Value, monthClose, monthHasClose);
            if (previousClose == 0m)
            {
                continue; // guard: a zero prior close would divide by zero
            }

            decimal monthlyReturn = (monthClose[month] / previousClose) - 1m;
            rows[step][yearIndex] = new SeasonalityMonthlyReturnCell(monthlyReturn);
        }

        rows[SeasonalityMonthlyReturnsTable.AnnualRowIndex][yearIndex] =
            new SeasonalityMonthlyReturnCell(ResolveAnnualReturn(samples));
    }

    private static decimal? ResolveYearBaseClose(IReadOnlyList<SeasonalitySample> samples)
    {
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Value is { } close && close > 0m)
            {
                return close; // first positive close, matching the engine's PercentVsYearStart base (decision D2)
            }
        }

        return null;
    }

    private static decimal ResolvePreviousMonthEndClose(
        int step,
        uint startMonth,
        decimal yearBaseClose,
        ReadOnlySpan<decimal> monthClose,
        ReadOnlySpan<bool> monthHasClose)
    {
        // The nearest earlier month in the fiscal sequence that has a close;
        // the first month of the cycle (step 0) falls back to yearBaseClose.
        for (int earlierStep = step - 1; earlierStep >= 0; earlierStep--)
        {
            int earlierMonth = (int)(((startMonth - 1 + earlierStep) % MonthsPerYear) + 1);
            if (monthHasClose[earlierMonth])
            {
                return monthClose[earlierMonth];
            }
        }

        return yearBaseClose;
    }

    /// <summary>
    /// One year trace's overall change: the last valid sample's cumulative <see cref="SeasonalitySample.RateOfChange"/>
    /// (year-start to year-end, definition D-D). This is the value shown in the table's annual row and,
    /// for series 0, next to the year in the plot legend. <c>null</c> when the year has no valid sample.
    /// Single source of truth so the legend never re-derives the rule.
    /// </summary>
    public static decimal? AnnualReturn(SeasonalityYearTrace trace)
    {
        if (trace is null)
        {
            throw new ArgumentNullException(nameof(trace));
        }

        return ResolveAnnualReturn(trace.Samples);
    }

    private static decimal? ResolveAnnualReturn(IReadOnlyList<SeasonalitySample> samples)
    {
        for (int i = samples.Count - 1; i >= 0; i--)
        {
            SeasonalitySample sample = samples[i];
            if (sample.Status == SeasonalitySampleStatus.Valid && sample.RateOfChange is { } rateOfChange)
            {
                return rateOfChange; // last valid sample's cumulative change = year-start -> year-end (definition D-D)
            }
        }

        return null;
    }

    private static decimal? ArithmeticMean(ReadOnlySpan<SeasonalityMonthlyReturnCell> cells)
    {
        decimal sum = 0m;
        int count = 0;
        foreach (SeasonalityMonthlyReturnCell cell in cells)
        {
            if (cell.Return is { } value)
            {
                sum += value;
                count++;
            }
        }

        return count == 0 ? null : sum / count;
    }
}
