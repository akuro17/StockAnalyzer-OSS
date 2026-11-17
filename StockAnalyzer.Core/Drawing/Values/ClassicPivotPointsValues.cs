using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>The seven classic pivot levels of one anchor and the period they are drawn over.</summary>
public readonly record struct ClassicPivotLevels(
    decimal Pivot,
    decimal R1,
    decimal R2,
    decimal R3,
    decimal S1,
    decimal S2,
    decimal S3,
    DateTime PeriodStart,
    DateTime PeriodEnd);

/// <summary>
/// Classic pivot points (the single definition; the GUI object and the MCP tool both call it). The levels come from the high, low and close of the preceding
/// period: P = (H + L + C) / 3, R1 = 2P - L, S1 = 2P - H, R2 = P + (H - L), S2 = P - (H - L), R3 (HBOP) = R1 + (H - L), S3 (LBOP) = S1 - (H - L).
/// </summary>
public static class ClassicPivotPointsValues
{
    /// <summary>The period (day, week from Monday, month) the anchor time is in: [start, end).</summary>
    public static void GetPeriodBoundaries(DateTime anchor, PivotPeriod period, out DateTime start, out DateTime end)
    {
        DateTime anchorDate = anchor.Date;
        switch (period)
        {
            case PivotPeriod.Week:
                int diff = anchor.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)anchor.DayOfWeek - 1;
                start = anchorDate.AddDays(-diff);
                end = start.AddDays(7);
                break;

            case PivotPeriod.Month:
                start = new DateTime(anchor.Year, anchor.Month, 1, 0, 0, 0, anchor.Kind);
                end = start.AddMonths(1);
                break;

            case PivotPeriod.Day:
            default:
                start = anchorDate;
                end = anchorDate.AddDays(1);
                break;
        }
    }

    /// <summary>
    /// The levels of the anchor: the preceding period is the one of the latest bar before the start of the period the anchor is in.
    /// False when no bar is before it, or the preceding period has no valid range (high below low, or a low of zero or less).
    /// </summary>
    public static bool TryCompute(DateTime anchor, IReadOnlyList<CoreCandleData> candles, PivotPeriod period, out ClassicPivotLevels levels)
    {
        GetPeriodBoundaries(anchor, period, out DateTime periodStart, out DateTime periodEnd);
        if (!TryAggregatePreviousPeriod(candles, periodStart, period, out decimal high, out decimal low, out decimal close))
        {
            levels = default;
            return false;
        }

        decimal pivot = (high + low + close) / 3m;
        decimal r1 = 2m * pivot - low;
        decimal s1 = 2m * pivot - high;
        decimal diff = high - low;
        levels = new ClassicPivotLevels(pivot, r1, pivot + diff, r1 + diff, s1, pivot - diff, s1 - diff, periodStart, periodEnd);
        return true;
    }

    /// <summary>The Data-tab values of the levels. The period and the line extension are listed first as text items.</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(ClassicPivotLevels levels, PivotPeriod period, PivotLineExtendMode extendMode, IndicatorColor color)
    {
        return new DrawingCalculatedValue[]
        {
            new("Period", "Period", null, period.ToString(), color),
            new("ExtendLines", "Extend Lines", null, extendMode.ToString(), color),
            Level("HBOP", "HBOP (R3)", levels.R3, color),
            Level("R2", "R2", levels.R2, color),
            Level("R1", "R1", levels.R1, color),
            Level("Pivot", "Pivot (P)", levels.Pivot, color),
            Level("S1", "S1", levels.S1, color),
            Level("S2", "S2", levels.S2, color),
            Level("LBOP", "LBOP (S3)", levels.S3, color),
        };
    }

    /// <summary>The values calculated from bars (<paramref name="candles"/> oldest first); no usable preceding period gives no values.</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DateTime anchor, IReadOnlyList<CoreCandleData> candles, PivotPeriod period, PivotLineExtendMode extendMode, IndicatorColor color)
        => TryCompute(anchor, candles, period, out ClassicPivotLevels levels)
            ? Calculate(levels, period, extendMode, color)
            : Array.Empty<DrawingCalculatedValue>();

    private static DrawingCalculatedValue Level(string key, string label, decimal price, IndicatorColor color)
        => new(key, label, price, string.Create(DrawingValueFormat.Culture, $"{price:F3}"), color);

    private static bool TryAggregatePreviousPeriod(
        IReadOnlyList<CoreCandleData> candles,
        DateTime currentPeriodStart,
        PivotPeriod period,
        out decimal high,
        out decimal low,
        out decimal close)
    {
        high = decimal.MinValue;
        low = decimal.MaxValue;
        close = 0m;

        if (candles == null || candles.Count == 0)
        {
            return false;
        }

        // 1. The latest candle strictly before the current period start.
        int lastIdx = -1;
        for (int i = candles.Count - 1; i >= 0; i--)
        {
            if (candles[i].Timestamp < currentPeriodStart)
            {
                lastIdx = i;
                break;
            }
        }

        if (lastIdx < 0)
        {
            return false;
        }

        CoreCandleData latestPrevCandle = candles[lastIdx];
        GetPeriodBoundaries(latestPrevCandle.Timestamp, period, out DateTime rangeStart, out DateTime rangeEnd);

        close = latestPrevCandle.Close;
        int count = 0;

        // 2. Backwards scan with an early exit at the start of the preceding period.
        for (int i = lastIdx; i >= 0; i--)
        {
            CoreCandleData c = candles[i];
            if (c.Timestamp < rangeStart)
            {
                break;
            }

            if (c.Timestamp < rangeEnd)
            {
                if (c.High > high)
                {
                    high = c.High;
                }

                if (c.Low < low)
                {
                    low = c.Low;
                }

                count++;
            }
        }

        // Defensive guard: a non-empty set of candles and a positive valid range (high >= low > 0).
        return count != 0 && high >= low && low > 0m;
    }
}
