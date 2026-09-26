namespace StockAnalyzer.Avalonia.Drawing;

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using SkiaSharp;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;

/// <summary>
/// Archimedean spiral: r(theta) = r0 + Pitch * theta / (2 * pi), where Pitch is px per turn.
/// </summary>
public sealed class ArchimedeanSpiralObject : SpiralObjectBase
{
    private double _pitchPixels = SpiralMath.DefaultArchimedeanPitchPixels;

    public override ChartObjectType Type => ChartObjectType.ArchimedeanSpiral;

    [Category("Geometry")]
    [DisplayName("Pitch (px/turn)")]
    [Range(0.0, SpiralMath.MaximumArchimedeanPitchPixels)]
    [Display(Order = 30)]
    [ParameterTag(DrawingParameterTags.Geometry)]
    public double PitchPixels
    {
        get => _pitchPixels;
        set
        {
            if (value < 0.0 || value > SpiralMath.MaximumArchimedeanPitchPixels || !double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _pitchPixels = value;
        }
    }

    public ArchimedeanSpiralObject() { }
    public ArchimedeanSpiralObject(ChartPoint center, ChartPoint start) : base(center, start) { }

    protected override void BuildPath(SKPoint center, SKPoint start)
        => SpiralMath.BuildArchimedeanPath(_cachedPath, center, start, Direction, PitchPixels, MaxTurns, BezierSplineMath.DefaultMaxRadius);

    protected override bool HitTestPath(SKPoint target, SKPoint center, SKPoint start, double tolerance)
        => SpiralMath.HitTestArchimedean(target, center, start, Direction, PitchPixels, MaxTurns, BezierSplineMath.DefaultMaxRadius, tolerance);
}
