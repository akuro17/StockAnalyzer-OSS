using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Whether a threshold condition fires as price rises to meet it (Upward: price &gt;= threshold,
/// e.g. a Sell Limit, a Buy Stop, a Short-side margin liquidation) or falls to meet it
/// (Downward: price &lt;= threshold, e.g. a Buy Limit, a Sell Stop, a Long-side margin liquidation).
/// </summary>
public enum TouchDirection { Upward, Downward }

/// <summary>A point on the canonical or deterministic OHLC path: <paramref name="Position"/> on the scale [0, 3] (0 = Open, 1 = High/Low, 2 = Low/High, 3 = Close) and the market <paramref name="Price"/> there.</summary>
public readonly record struct PathPoint(decimal Position, decimal Price);

/// <summary>
/// Computes WHEN (not just whether) a price threshold is first touched along the specified intrabar path
/// (CanonicalOHLC fixed O->H->L->C, or DeterministicCandleShape which adapts vertices based on candle shape
/// and conservative worst-case principle).
/// </summary>
public static class FillPathScanner
{
    /// <summary>
    /// The path position at which the threshold is first touched over the WHOLE bar, i.e. <see cref="TryFindTouchFrom"/> started at the Open vertex
    /// (position 0), or null if it never is.
    /// </summary>
    public static decimal? TryFindTouchPosition(
        decimal open, decimal high, decimal low, decimal close, decimal threshold, TouchDirection direction,
        IntrabarPathMode pathMode = IntrabarPathMode.CanonicalOHLC, TradeSide? heldPositionSide = null)
    {
        return TryFindTouchFrom(open, high, low, close, threshold, direction, OpenPoint(open), pathMode, heldPositionSide)?.Position;
    }

    /// <summary>Path position of the Open vertex (start of every bar).</summary>
    private const decimal OpenPosition = 0m;

    /// <summary>Path position of the Close vertex (end of every bar).</summary>
    private const decimal ClosePosition = 3m;

    /// <summary>Number of straight legs on the path: leg 0, leg 1, leg 2.</summary>
    private const int LegCount = 3;

    /// <summary>Vertex numbers of the path; leg <c>n</c> runs from vertex <c>n</c> to vertex <c>n + 1</c>, and any later vertex is the Close.</summary>
    private const int OpenVertex = 0;
    private const int HighVertex = 1;
    private const int LowVertex = 2;

    public static PathPoint OpenPoint(decimal open) => new(OpenPosition, open);

    public static PathPoint ClosePoint(decimal close) => new(ClosePosition, close);

    /// <summary>
    /// First point at or after <paramref name="start"/> (inclusive) where the price is at/through <paramref name="threshold"/> in
    /// <paramref name="direction"/>, or null if it never is on the rest of the path.
    /// </summary>
    public static PathPoint? TryFindTouchFrom(
        decimal open, decimal high, decimal low, decimal close, decimal threshold, TouchDirection direction, PathPoint start,
        IntrabarPathMode pathMode = IntrabarPathMode.CanonicalOHLC, TradeSide? heldPositionSide = null)
        => TryFindTouchCore(open, high, low, close, threshold, direction, start, IsBearishVertexPath(open, close, pathMode, heldPositionSide));

    /// <summary>
    /// <see cref="TryFindTouchPosition"/> on an explicitly chosen vertex order instead of the one <see cref="IntrabarPathMode"/> derives:
    /// <paramref name="lowFirst"/> true = Open -&gt; Low -&gt; High -&gt; Close, false = Open -&gt; High -&gt; Low -&gt; Close. Used to compare the two orders of an
    /// order-dependent bar; the configured mode is not consulted.
    /// </summary>
    public static decimal? TryFindTouchPositionOnOrder(
        decimal open, decimal high, decimal low, decimal close, decimal threshold, TouchDirection direction, bool lowFirst)
        => TryFindTouchFromOnOrder(open, high, low, close, threshold, direction, OpenPoint(open), lowFirst)?.Position;

    /// <summary><see cref="TryFindTouchFrom"/> on an explicitly chosen vertex order (see <see cref="TryFindTouchPositionOnOrder"/>).</summary>
    public static PathPoint? TryFindTouchFromOnOrder(
        decimal open, decimal high, decimal low, decimal close, decimal threshold, TouchDirection direction, PathPoint start, bool lowFirst)
        => TryFindTouchCore(open, high, low, close, threshold, direction, start, lowFirst);

    /// <summary>Whether the path with the given <paramref name="pathMode"/> and held side visits Low before High for this bar (the order every scan of the bar uses).</summary>
    public static bool VisitsLowFirst(decimal open, decimal close, IntrabarPathMode pathMode, TradeSide? heldPositionSide)
        => IsBearishVertexPath(open, close, pathMode, heldPositionSide);

    private static PathPoint? TryFindTouchCore(
        decimal open, decimal high, decimal low, decimal close, decimal threshold, TouchDirection direction, PathPoint start, bool isBearishPath)
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
            decimal from = VertexPrice(leg, open, high, low, close, isBearishPath);
            decimal to = VertexPrice(leg + 1, open, high, low, close, isBearishPath);
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

    private static bool IsBearishVertexPath(decimal open, decimal close, IntrabarPathMode pathMode, TradeSide? heldPositionSide)
    {
        if (pathMode == IntrabarPathMode.CanonicalOHLC) return false;

        // DeterministicCandleShape:
        // Bullish (close > open): O -> H -> L -> C (not bearish)
        if (close > open) return false;
        // Bearish (close < open): O -> L -> H -> C (bearish)
        if (close < open) return true;

        // Doji (close == open): conservative worst-case principle
        // Long held or Flat/null: adverse is down first (O -> L -> H -> C)
        // Short held: adverse is up first (O -> H -> L -> C)
        return heldPositionSide != TradeSide.Short;
    }

    private static decimal VertexPrice(int vertex, decimal open, decimal high, decimal low, decimal close, bool isBearishPath)
    {
        return vertex switch
        {
            OpenVertex => open,
            HighVertex => isBearishPath ? low : high,
            LowVertex => isBearishPath ? high : low,
            _ => close,
        };
    }
}
