using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing.Behaviors;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Central registry mapping DrawingTool enum values to their behavior implementations.
/// Uses a Dictionary for O(1) lookup, replacing the former 500+ line if/else-if chain.
/// </summary>
public static class DrawingToolBehaviorRegistry
{
    private static readonly Dictionary<DrawingTool, IDrawingToolBehavior> _behaviors = new()
    {
        // --- Lines ---
        [DrawingTool.TrendLine] = new TrendLineBehavior(),
        [DrawingTool.HorizontalLine] = new HorizontalLineBehavior(),
        [DrawingTool.VerticalLine] = new VerticalLineBehavior(),
        [DrawingTool.Ray] = new RayBehavior(),
        [DrawingTool.Arrow] = new ArrowBehavior(),

        // --- Shapes ---
        [DrawingTool.Rectangle] = new RectangleBehavior(),
        [DrawingTool.Ellipse] = new EllipseBehavior(),
        [DrawingTool.Triangle] = new TriangleBehavior(),

        // --- Fibonacci ---
        [DrawingTool.FibonacciRetracement] = new FibRetracementBehavior(),
        [DrawingTool.FibonacciTimeZone] = new FibTimeZoneBehavior(),
        [DrawingTool.FibonacciArc] = new FibArcBehavior(),
        [DrawingTool.FibonacciCircle] = new FibCircleBehavior(),
        [DrawingTool.FibonacciFan] = new FibFanBehavior(),
        [DrawingTool.FibonacciSpiral] = new FibSpiralBehavior(),
        [DrawingTool.FibonacciExpansion] = new FibExpansionBehavior(),
        [DrawingTool.FibonacciChannel] = new FibChannelBehavior(),

        // --- Gann ---
        [DrawingTool.GannFan] = new GannFanBehavior(),
        [DrawingTool.GannBox] = new GannBoxBehavior(),
        [DrawingTool.GannSquare] = new GannSquareBehavior(),
        [DrawingTool.GannGrid] = new GannGridBehavior(),
        [DrawingTool.GannSquareOfNine] = new GannSquareOfNineBehavior(),
        [DrawingTool.GannSquare144] = new GannSquare144Behavior(),
        [DrawingTool.GannWheel] = new GannWheelBehavior(),

        // --- Patterns ---
        [DrawingTool.Pitchfork] = new PitchforkBehavior(),
        [DrawingTool.ParallelChannel] = new ParallelChannelBehavior(),
        [DrawingTool.Polyline] = new PolylineBehavior(),
        [DrawingTool.ElliottWave] = new PolylineBehavior(PolylineLabelType.Numeric),
        [DrawingTool.AutoElliottWave] = new AutoElliottWaveBehavior(),
        [DrawingTool.HarmonicPattern] = new HarmonicPatternBehavior(),
        [DrawingTool.BarPattern] = new BarPatternBehavior(),
        [DrawingTool.GeometricPattern] = new GeometricPatternBehavior(),

        // --- Analysis ---
        [DrawingTool.RegressionTrend] = new RegressionTrendBehavior(),
        [DrawingTool.FixedRangeVolumeProfile] = new FixedRangeVolumeBehavior(),
        [DrawingTool.AnchoredVWAP] = new AnchoredVwapBehavior(),
        [DrawingTool.AngleTool] = new AngleBehavior(),
        [DrawingTool.PriceLabel] = new PriceLabelBehavior(),
        [DrawingTool.Callout] = new CalloutBehavior(),
        [DrawingTool.DtwProjection] = new DtwProjectionBehavior(),
        [DrawingTool.TargetPriceProjection] = new TargetPriceProjectionBehavior(),

        // --- Prediction ---
        [DrawingTool.LongPosition] = new LongShortPositionBehavior(isLong: true),
        [DrawingTool.ShortPosition] = new LongShortPositionBehavior(isLong: false),
        [DrawingTool.GhostFeed] = new GhostFeedBehavior(),

        // --- Cycles ---
        [DrawingTool.CyclicLines] = new CyclicLinesBehavior(),
        [DrawingTool.SineLine] = new SineLineBehavior(),
        [DrawingTool.TimeCycles] = new TimeCyclesBehavior(),
    };

    /// <summary>
    /// Attempts to get the behavior for a given drawing tool.
    /// Returns null for non-drawing tools (Pointer, Ruler, Eraser, Text).
    /// </summary>
    public static IDrawingToolBehavior? GetBehavior(DrawingTool tool)
    {
        _behaviors.TryGetValue(tool, out var behavior);
        return behavior;
    }
}
