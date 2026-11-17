using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>One projected target: its short name, its price and the index (0..2) of the control point it hangs from.</summary>
public readonly record struct ProjectionTargetResult(string Name, decimal Price, int AnchorIndex);

/// <summary>Which of the five projections are shown. <see cref="All"/> is how the GUI object starts (the zero value of this struct shows none).</summary>
public readonly record struct ProjectionTargetSelection(
    bool ShowEqualDistance,
    bool ShowEqualPercentage,
    bool ShowDoubleReturn,
    bool ShowEProjection,
    bool ShowNTProjection)
{
    /// <summary>All five projections.</summary>
    public static ProjectionTargetSelection All { get; } = new(true, true, true, true, true);
}

/// <summary>
/// Target price projection from three control points A=P1, B=P2, C=P3 (the single definition; the GUI object and the MCP tool both call it):
/// V = B + (B - C), N = C + (B - A), % = C * (B / A) (not for A = 0), E = B + (B - A), NT = C + (C - A). The order is the order the GUI draws them.
/// </summary>
public static class TargetPriceProjectionValues
{
    public const string DoubleReturnName = "V";
    public const string EqualDistanceName = "N";
    public const string EqualPercentageName = "%";
    public const string ExpansionName = "E";
    public const string SecondaryName = "NT";

    /// <summary>The most targets one projection can have (all five).</summary>
    public const int MaxTargets = 5;

    /// <summary>The enabled targets in drawing order. Fewer than three points give none.</summary>
    public static IReadOnlyList<ProjectionTargetResult> Targets(IReadOnlyList<DrawingAnchor> points, ProjectionTargetSelection selection)
    {
        if (points.Count < 3)
        {
            return Array.Empty<ProjectionTargetResult>();
        }

        var buffer = new ProjectionTargetResult[MaxTargets];
        int count = Targets(points[0].Price, points[1].Price, points[2].Price, selection, buffer);
        return new ArraySegment<ProjectionTargetResult>(buffer, 0, count);
    }

    /// <summary>
    /// The enabled targets in drawing order, written to <paramref name="destination"/> (at least <see cref="MaxTargets"/> long); returns how many.
    /// Allocation free: the GUI calls it for every frame.
    /// </summary>
    /// <param name="a">Price of the first control point (A).</param>
    /// <param name="b">Price of the second control point (B).</param>
    /// <param name="c">Price of the third control point (C).</param>
    public static int Targets(decimal a, decimal b, decimal c, ProjectionTargetSelection selection, Span<ProjectionTargetResult> destination)
    {
        int count = 0;
        if (selection.ShowDoubleReturn)
        {
            destination[count++] = new ProjectionTargetResult(DoubleReturnName, b + (b - c), 2);
        }

        if (selection.ShowEqualDistance)
        {
            destination[count++] = new ProjectionTargetResult(EqualDistanceName, c + (b - a), 2);
        }

        if (selection.ShowEqualPercentage && a != 0m)
        {
            destination[count++] = new ProjectionTargetResult(EqualPercentageName, c * (b / a), 2);
        }

        if (selection.ShowEProjection)
        {
            destination[count++] = new ProjectionTargetResult(ExpansionName, b + (b - a), 1);
        }

        if (selection.ShowNTProjection)
        {
            destination[count++] = new ProjectionTargetResult(SecondaryName, c + (c - a), 2);
        }

        return count;
    }

    public static IReadOnlyList<DrawingCalculatedValue> Calculate(IReadOnlyList<DrawingAnchor> points, decimal? currentPrice, IndicatorColor color, ProjectionTargetSelection selection)
    {
        IReadOnlyList<ProjectionTargetResult> targets = Targets(points, selection);
        var values = new List<DrawingCalculatedValue>(targets.Count);
        foreach (ProjectionTargetResult target in targets)
        {
            string formatted;
            if (currentPrice.HasValue)
            {
                decimal diff = target.Price - currentPrice.Value;
                string diffSign = diff >= 0 ? "+" : string.Empty;
                formatted = string.Create(DrawingValueFormat.Culture, $"{target.Price:F3} ({diffSign}{diff:F3})");
            }
            else
            {
                formatted = string.Create(DrawingValueFormat.Culture, $"{target.Price:F3}");
            }

            string label = target.Name switch
            {
                DoubleReturnName => "Target V (Double Return)",
                EqualDistanceName => "Target N (Equal Distance)",
                EqualPercentageName => "Target % (Equal Ratio)",
                ExpansionName => "Target E (Expansion)",
                SecondaryName => "Target NT (Secondary)",
                _ => $"Target {target.Name}",
            };

            values.Add(new DrawingCalculatedValue($"Target_{target.Name}", label, target.Price, formatted, color));
        }

        return values;
    }
}
