namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Whether a threshold condition fires as price rises to meet it (Upward: price &gt;= threshold,
/// e.g. a Sell Limit, a Buy Stop, a Short-side margin liquidation) or falls to meet it
/// (Downward: price &lt;= threshold, e.g. a Buy Limit, a Sell Stop, a Long-side margin liquidation).
/// </summary>
public enum TouchDirection { Upward, Downward }

/// <summary>A point on the canonical Open-&gt;High-&gt;Low-&gt;Close path: <paramref name="Position"/> on the scale [0, 3] (0 = Open, 1 = High, 2 = Low, 3 = Close) and the market <paramref name="Price"/> there.</summary>
public readonly record struct PathPoint(decimal Position, decimal Price);

/// <summary>
/// Computes WHEN (not just whether) a price threshold is first touched along the P1 spec's fixed
/// canonical intrabar path Open-&gt;High-&gt;Low-&gt;Close (section 5.6 of
/// Y:\0915 Backtesting\01_P1_SimulationEngine.md — a fixed traversal order, not the bar's actual
/// unknown tick sequence). The result is a comparable "path position" in the range [0, 2]
/// (0 = at Open, 1 = at High, 2 = at Low; the Low-&gt;Close leg extends the scale to (2, 3], see <see cref="TryFindTouchFrom"/>) so that two competing candidate events on the same bar
/// (e.g. a pending exit order vs. a margin-liquidation threshold, per
/// Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5.6) can be ordered by whichever is smaller.
/// </summary>
public static class FillPathScanner
{
    /// <summary>
    /// The path position at which the threshold is first touched over the WHOLE bar, i.e. <see cref="TryFindTouchFrom"/> started at the Open vertex
    /// (position 0), or null if it never is. The result is on [0, 2]: an Open that already satisfies the condition is position 0, otherwise the touch is on the
    /// Open-&gt;High leg (Upward) or the High-&gt;Low leg (Downward) - see <see cref="TryFindTouchFrom"/> for why no other leg can hold a first touch on a valid bar.
    /// The comparable position lets two competing candidate events on the same bar (e.g. a pending exit order vs. a margin-liquidation threshold) be
    /// ordered by whichever is smaller.
    /// </summary>
    public static decimal? TryFindTouchPosition(decimal open, decimal high, decimal low, decimal close, decimal threshold, TouchDirection direction)
    {
        return TryFindTouchFrom(open, high, low, close, threshold, direction, OpenPoint(open))?.Position;
    }

    /// <summary>Path position of the Open vertex (start of every bar).</summary>
    private const decimal OpenPosition = 0m;

    /// <summary>Path position of the Close vertex (end of every bar).</summary>
    private const decimal ClosePosition = 3m;

    /// <summary>Number of straight legs on the path: Open-&gt;High, High-&gt;Low, Low-&gt;Close.</summary>
    private const int LegCount = 3;

    /// <summary>Vertex numbers of the path; leg <c>n</c> runs from vertex <c>n</c> to vertex <c>n + 1</c>, and any later vertex is the Close.</summary>
    private const int OpenVertex = 0;
    private const int HighVertex = 1;
    private const int LowVertex = 2;

    public static PathPoint OpenPoint(decimal open) => new(OpenPosition, open);

    public static PathPoint ClosePoint(decimal close) => new(ClosePosition, close);

    /// <summary>
    /// First point at or after <paramref name="start"/> (inclusive) where the price is at/through <paramref name="threshold"/> in
    /// <paramref name="direction"/>, or null if it never is on the rest of the path. When the condition already holds at <paramref name="start"/>
    /// the touch is the start point itself and its price is the (possibly better) market price there, exactly like a gap through the threshold at Open;
    /// otherwise the price at the touch is the threshold. Scanning from <c>(0, Open)</c> reproduces <see cref="TryFindTouchPosition"/>; a later start
    /// scans only the residual path, which is what an order that cannot fill before an earlier event (a reversal Entry behind its Exit, a new
    /// position's liquidation after its own fill) needs. Valid bars make each leg monotone, so a crossing can only be on a rising leg for an
    /// Upward touch and on a falling leg for a Downward touch.
    /// </summary>
    public static PathPoint? TryFindTouchFrom(decimal open, decimal high, decimal low, decimal close, decimal threshold, TouchDirection direction, PathPoint start)
    {
        bool upward = direction switch
        {
            TouchDirection.Upward => true,
            TouchDirection.Downward => false,
            _ => throw new System.ArgumentOutOfRangeException(nameof(direction), direction, "Unknown TouchDirection."),
        };

        if (Holds(start.Price, threshold, upward)) return start;

        int firstLeg = (int)System.Math.Min(System.Math.Floor(start.Position), LegCount - 1);
        for (int leg = firstLeg; leg < LegCount; leg++)
        {
            decimal from = VertexPrice(leg, open, high, low, close);
            decimal to = VertexPrice(leg + 1, open, high, low, close);
            if (!Holds(to, threshold, upward)) continue;

            // The start (and every earlier vertex) does not hold, so the threshold is crossed on this leg and from != to.
            decimal position = upward
                ? leg + ((threshold - from) / (to - from))
                : leg + ((from - threshold) / (from - to));
            return new PathPoint(position, threshold);
        }

        return null;
    }

    private static bool Holds(decimal price, decimal threshold, bool upward) => upward ? price >= threshold : price <= threshold;

    private static decimal VertexPrice(int vertex, decimal open, decimal high, decimal low, decimal close) => vertex switch
    {
        OpenVertex => open,
        HighVertex => high,
        LowVertex => low,
        _ => close,
    };
}
