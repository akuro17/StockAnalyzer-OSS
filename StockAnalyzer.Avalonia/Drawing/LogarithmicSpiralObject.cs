namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using SkiaSharp;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;

/// <summary>
/// Logarithmic spiral: r(theta) = r0 * exp(GrowthRate * theta).
/// </summary>
public sealed class LogarithmicSpiralObject : SpiralObjectBase
{
    private double _growthRate = SpiralMath.DefaultLogarithmicGrowthRate;
    private bool _allowDegenerateCurve;

    public override ChartObjectType Type => ChartObjectType.LogarithmicSpiral;

    [Category("Geometry")]
    [DisplayName("Growth Rate")]
    [Range(0.0, SpiralMath.MaximumLogarithmicGrowthRate)]
    [Display(Order = 30)]
    [ParameterTag(DrawingParameterTags.Geometry)]
    public double GrowthRate
    {
        get => _growthRate;
        set
        {
            if (value < 0.0 || value > SpiralMath.MaximumLogarithmicGrowthRate || !double.IsFinite(value) || (value == 0.0 && !_allowDegenerateCurve)) throw new ArgumentOutOfRangeException(nameof(value));
            _growthRate = value;
        }
    }

    [JsonPropertyOrder(-1)]
    [Category("Geometry")]
    [DisplayName("Allow Degenerate Curve")]
    [Display(Order = 40)]
    [ParameterTag(DrawingParameterTags.Geometry)]
    public bool AllowDegenerateCurve
    {
        get => _allowDegenerateCurve;
        set
        {
            if (!value && _growthRate == 0.0) throw new ArgumentOutOfRangeException(nameof(value));
            _allowDegenerateCurve = value;
        }
    }

    public LogarithmicSpiralObject() { }
    public LogarithmicSpiralObject(ChartPoint center, ChartPoint start) : base(center, start) { }

    protected override void BuildPath(SKPoint center, SKPoint start)
        => SpiralMath.BuildLogarithmicPath(_cachedPath, center, start, Direction, GrowthRate, MaxTurns, BezierSplineMath.DefaultMaxRadius);

    protected override bool HitTestPath(SKPoint target, SKPoint center, SKPoint start, double tolerance)
        => SpiralMath.HitTestLogarithmic(target, center, start, Direction, GrowthRate, MaxTurns, BezierSplineMath.DefaultMaxRadius, tolerance);
}
