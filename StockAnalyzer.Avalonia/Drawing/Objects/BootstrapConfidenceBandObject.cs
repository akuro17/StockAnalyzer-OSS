using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using SkiaSharp;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// Bootstrap confidence band: the confidence interval of a statistic of the range (Mean, Median of the closes; StdDev, Sharpe of the simple returns) from the deterministic block bootstrap
/// (<see cref="BootstrapConfidenceBandValues"/> holds the calculation and the Data-tab rows). A price statistic (Mean, Median) is drawn as a box between the lower and upper bound with a line at the
/// estimate; a return statistic has no price level, so only the label at the top of the range shows it. A range longer than the kernel takes uses its newest bars and says so in the label.
/// </summary>
public sealed class BootstrapConfidenceBandObject : RangeAnalysisObjectBase
{
    private const byte BoxFillAlpha = 40;
    private const float BoundDashOn = 4f;
    private const float BoundDashOff = 3f;
    private const float EstimateLineScale = 1f;

    private BootstrapStatKind _statKind = BootstrapStatKind.Mean;
    private decimal _confidenceLevel = BootstrapConfidenceBandValues.DefaultConfidenceLevel;
    private int _resamples = BootstrapConfidenceBandValues.DefaultResamples;
    private int _blockLength = BootstrapConfidenceBandValues.DefaultBlockLength;

    private BootstrapBand _band = BootstrapBand.Empty;
    private string _overlayText = string.Empty;
    private IReadOnlyList<DrawingCalculatedValue> _cachedValues = Array.Empty<DrawingCalculatedValue>();
    private IndicatorColor _cachedValuesColor;
    private bool _hasCachedValues;

    private readonly SKPaint _boxPaint = new() { Style = SKPaintStyle.Fill };
    private readonly SKPathEffect _boundDash = SKPathEffect.CreateDash(new[] { BoundDashOn, BoundDashOff }, 0f);
    private readonly SKPaint _boundPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _estimatePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };

    public BootstrapConfidenceBandObject()
    {
        _boundPaint.PathEffect = _boundDash;
    }

    public BootstrapConfidenceBandObject(ChartPoint start, ChartPoint end)
        : this()
    {
        Points.Add(start);
        Points.Add(end);
    }

    public override ChartObjectType Type => ChartObjectType.BootstrapConfidenceBand;

    /// <summary>The statistic of the range: Mean and Median of the closes, StdDev and Sharpe of the simple returns.</summary>
    [Category("Parameters")]
    [DisplayName("Statistic")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public BootstrapStatKind StatKind
    {
        get => _statKind;
        set => _statKind = Enum.IsDefined(value) ? value : BootstrapStatKind.Mean;
    }

    /// <summary>Confidence level of the interval (0.80 .. 0.99).</summary>
    [Category("Parameters")]
    [DisplayName("Confidence Level")]
    [Range((double)BootstrapConfidenceBandValues.MinConfidenceLevel, (double)BootstrapConfidenceBandValues.MaxConfidenceLevel)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public decimal ConfidenceLevel
    {
        get => _confidenceLevel;
        set => _confidenceLevel = Math.Clamp(value, BootstrapConfidenceBandValues.MinConfidenceLevel, BootstrapConfidenceBandValues.MaxConfidenceLevel);
    }

    /// <summary>Number of bootstrap resamples.</summary>
    [Category("Parameters")]
    [DisplayName("Resamples")]
    [Range(BootstrapConfidenceBandValues.MinResamples, BootstrapConfidenceBandValues.MaxResamples)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public int Resamples
    {
        get => _resamples;
        set => _resamples = Math.Clamp(value, BootstrapConfidenceBandValues.MinResamples, BootstrapConfidenceBandValues.MaxResamples);
    }

    /// <summary>Length of the resampled blocks in bars (at most half of the series).</summary>
    [Category("Parameters")]
    [DisplayName("Block Length")]
    [Range(BootstrapConfidenceBandValues.MinBlockLength, BootstrapConfidenceBandValues.MaxBlockLength)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public int BlockLength
    {
        get => _blockLength;
        set => _blockLength = Math.Clamp(value, BootstrapConfidenceBandValues.MinBlockLength, BootstrapConfidenceBandValues.MaxBlockLength);
    }

    /// <summary>Circular blocks of a fixed length, or the stationary bootstrap with random block lengths.</summary>
    [Category("Parameters")]
    [DisplayName("Block Mode")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public BootstrapBlockMode BlockMode { get; set; } = BootstrapBlockMode.CircularBlock;

    /// <summary>The cached band (tests read it; it is not persisted).</summary>
    internal BootstrapBand Band
    {
        get
        {
            lock (SyncRoot)
            {
                return _band;
            }
        }
    }

    protected override string? OverlayText => _overlayText;

    protected override void ComputeRange(IReadOnlyList<CoreCandleData> candles, int firstIndex, int lastIndex)
    {
        _band = BootstrapConfidenceBandValues.Calculate(candles, firstIndex, lastIndex, _statKind, _confidenceLevel, _resamples, _blockLength, BlockMode);
        _overlayText = BootstrapConfidenceBandValues.Label(_band);
        _hasCachedValues = false;
    }

    protected override void ClearResult()
    {
        _band = BootstrapBand.Empty;
        _overlayText = string.Empty;
        _hasCachedValues = false;
    }

    protected override void RenderResult(SKCanvas canvas, ICoordinateTransform transform, float leftX, float rightX, SKColor color)
    {
        if (!_band.IsOk || !_band.IsPriceLevel)
        {
            return;
        }

        if (!TryGetBox(transform, out float left, out float right, out float top, out float bottom, out float estimateY))
        {
            return;
        }

        _boxPaint.Color = color.WithAlpha(BoxFillAlpha);
        canvas.DrawRect(new SKRect(left, top, right, bottom), _boxPaint);

        _boundPaint.Color = color;
        _boundPaint.StrokeWidth = (float)Math.Max(1.0, Thickness * 0.75);
        canvas.DrawLine(left, top, right, top, _boundPaint);
        canvas.DrawLine(left, bottom, right, bottom, _boundPaint);

        _estimatePaint.Color = color;
        _estimatePaint.StrokeWidth = (float)Thickness * EstimateLineScale;
        canvas.DrawLine(left, estimateY, right, estimateY, _estimatePaint);
    }

    protected override IReadOnlyList<DrawingCalculatedValue> BuildValues(IndicatorColor color)
    {
        if (!_hasCachedValues || _cachedValuesColor != color)
        {
            _cachedValues = BootstrapConfidenceBandValues.Calculate(_band, color);
            _cachedValuesColor = color;
            _hasCachedValues = true;
        }

        return _cachedValues;
    }

    protected override bool HitTestResult(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance)
    {
        if (!_band.IsOk || !_band.IsPriceLevel || !TryGetBox(transform, out float left, out float right, out float top, out float bottom, out _))
        {
            return false;
        }

        return screenPoint.X >= left - tolerance && screenPoint.X <= right + tolerance
            && screenPoint.Y >= Math.Min(top, bottom) - tolerance && screenPoint.Y <= Math.Max(top, bottom) + tolerance;
    }

    protected override void DisposeNativeResources()
    {
        _boxPaint.Dispose();
        _boundPaint.Dispose();
        _estimatePaint.Dispose();
        _boundDash.Dispose();
    }

    /// <summary>The screen box of the interval: from the first used bar to the last bar of the range, between the upper and the lower bound.</summary>
    private bool TryGetBox(ICoordinateTransform transform, out float left, out float right, out float top, out float bottom, out float estimateY)
    {
        global::Avalonia.Point start = transform.ChartToScreen(new ChartPoint(_band.WindowStart, _band.Upper));
        global::Avalonia.Point end = transform.ChartToScreen(new ChartPoint(_band.WindowEnd, _band.Lower));
        global::Avalonia.Point middle = transform.ChartToScreen(new ChartPoint(_band.WindowStart, _band.Estimate));
        left = (float)Math.Min(start.X, end.X);
        right = (float)Math.Max(start.X, end.X);
        top = (float)start.Y;
        bottom = (float)end.Y;
        estimateY = (float)middle.Y;
        return float.IsFinite(left) && float.IsFinite(right) && float.IsFinite(top) && float.IsFinite(bottom) && float.IsFinite(estimateY);
    }
}
