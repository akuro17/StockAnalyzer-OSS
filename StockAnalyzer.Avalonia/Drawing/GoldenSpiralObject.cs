namespace StockAnalyzer.Avalonia.Drawing;

using SkiaSharp;
using StockAnalyzer.Core.Models;

/// <summary>
/// Golden logarithmic spiral: r(theta) = r0 * phi^(2 * theta / pi).
/// </summary>
public sealed class GoldenSpiralObject : SpiralObjectBase
{
    private static readonly double GoldenGrowthRate = System.Math.Log(BezierSplineMath.GoldenRatioPhi) / (System.Math.PI / 2.0);

    public override ChartObjectType Type => ChartObjectType.GoldenSpiral;

    public GoldenSpiralObject() { }
    public GoldenSpiralObject(ChartPoint center, ChartPoint start) : base(center, start) { }

    protected override void BuildPath(SKPoint center, SKPoint start)
        => SpiralMath.BuildLogarithmicPath(_cachedPath, center, start, Direction, GoldenGrowthRate, MaxTurns, BezierSplineMath.DefaultMaxRadius);

    protected override bool HitTestPath(SKPoint target, SKPoint center, SKPoint start, double tolerance)
        => SpiralMath.HitTestLogarithmic(target, center, start, Direction, GoldenGrowthRate, MaxTurns, BezierSplineMath.DefaultMaxRadius, tolerance);
}
