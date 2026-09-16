namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Whether a threshold condition fires as price rises to meet it (Upward: price &gt;= threshold,
/// e.g. a Sell Limit, a Buy Stop, a Short-side margin liquidation) or falls to meet it
/// (Downward: price &lt;= threshold, e.g. a Buy Limit, a Sell Stop, a Long-side margin liquidation).
/// </summary>
public enum TouchDirection { Upward, Downward }

/// <summary>
/// Computes WHEN (not just whether) a price threshold is first touched along the P1 spec's fixed
/// canonical intrabar path Open-&gt;High-&gt;Low-&gt;Close (section 5.6 of
/// Y:\0915 Backtesting\01_P1_SimulationEngine.md — a fixed traversal order, not the bar's actual
/// unknown tick sequence). The result is a comparable "path position" in the range [0, 2]
/// (0 = at Open, 1 = at High, 2 = at Low) so that two competing candidate events on the same bar
/// (e.g. a pending exit order vs. a margin-liquidation threshold, per
/// Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5.6) can be ordered by whichever is smaller.
/// </summary>
public static class FillPathScanner
{
    /// <summary>
    /// Because <see cref="Models.CandleData.IsValid"/> guarantees Open/Close are within [Low, High]
    /// (i.e. High is the bar's maximum and Low is its minimum), the Open-&gt;High leg is always
    /// non-decreasing and the High-&gt;Low leg is always non-increasing. Consequently:
    /// - An Upward condition (price &gt;= threshold) can only first become true at Open, or partway
    ///   through the Open-&gt;High leg. The subsequent High-&gt;Low and Low-&gt;Close legs never exceed
    ///   High again, so they cannot be where an Upward threshold above Open is first reached.
    /// - A Downward condition (price &lt;= threshold) can only first become true at Open, or partway
    ///   through the High-&gt;Low leg. The Low-&gt;Close leg never goes below Low again, so it cannot be
    ///   where a Downward threshold below Open is first reached.
    /// This lets the scan check only two candidate legs instead of interpolating a general polyline,
    /// while still producing a path position usable to order competing candidates against each other.
    /// Division by the leg span cannot be by zero here: a zero-span leg only occurs when
    /// Open == High == Low == Close, in which case the immediate Open check above always resolves
    /// the touch first and the divide is never reached.
    /// </summary>
    public static decimal? TryFindTouchPosition(decimal open, decimal high, decimal low, decimal close, decimal threshold, TouchDirection direction)
    {
        _ = close;
        return direction switch
        {
            TouchDirection.Upward => TryFindUpwardTouch(open, high, threshold),
            TouchDirection.Downward => TryFindDownwardTouch(open, high, low, threshold),
            _ => throw new System.ArgumentOutOfRangeException(nameof(direction), direction, "Unknown TouchDirection."),
        };
    }

    private static decimal? TryFindUpwardTouch(decimal open, decimal high, decimal threshold)
    {
        if (open >= threshold) return 0m;
        if (high >= threshold) return (threshold - open) / (high - open);
        return null;
    }

    private static decimal? TryFindDownwardTouch(decimal open, decimal high, decimal low, decimal threshold)
    {
        if (open <= threshold) return 0m;
        if (low <= threshold) return 1m + (high - threshold) / (high - low);
        return null;
    }
}
