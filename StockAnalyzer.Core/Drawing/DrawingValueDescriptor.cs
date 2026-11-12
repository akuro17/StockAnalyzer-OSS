using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// One drawing tool whose Data-tab values can be calculated without a chart.
/// </summary>
/// <param name="Name">Equals the <c>ChartObjectType</c> member name of the tool (ordinal).</param>
/// <param name="PointCount">Number of control points the tool needs: 1, 2 or 3.</param>
/// <param name="Calculate">The calculation; the single definition of the tool's values.</param>
public sealed record DrawingValueDescriptor(
    string Name,
    int PointCount,
    Func<DrawingValueRequest, IReadOnlyList<DrawingCalculatedValue>> Calculate);
