using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// One drawing tool whose Data-tab values can be calculated without a chart.
/// </summary>
/// <param name="Name">Equals the <c>ChartObjectType</c> member name of the tool (ordinal).</param>
/// <param name="PointCount">Number of control points the tool needs: 1, 2 or 3.</param>
/// <param name="Calculate">The calculation; the single definition of the tool's values.</param>
/// <param name="StoredTypeName">
/// The name a saved drawing document gives the chart object of this tool (<c>DrawingObjectRecord.TypeName</c>): the CLR class name of the object in StockAnalyzer.Avalonia,
/// which is not the tool name for every tool (AngleTool is AngleObject, AnchoredVWAP is AnchoredVwapObject) and is one class for both LongPosition and ShortPosition.
/// A guard test in StockAnalyzer.Avalonia.Tests pins every name to the real class.
/// </param>
/// <param name="RequiresCandles">True for the tools calculated from bars (RegressionTrend, AnchoredVWAP, ClassicPivotPoints): the request must carry <see cref="DrawingValueRequest.Candles"/>.</param>
public sealed record DrawingValueDescriptor(
    string Name,
    int PointCount,
    Func<DrawingValueRequest, IReadOnlyList<DrawingCalculatedValue>> Calculate,
    string StoredTypeName,
    bool RequiresCandles = false);
