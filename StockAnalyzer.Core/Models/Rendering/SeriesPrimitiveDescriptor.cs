using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Rendering;

/// <summary>
/// Immutable declarative rendering descriptor attached to an indicator result via
/// <see cref="Indicators.IPrimitiveAwareIndicatorResult"/>.
/// Visual-only descriptor: MUST NOT affect <see cref="CoreIndicatorSettings.MathematicalVersion"/>.
/// </summary>
public sealed record SeriesPrimitiveDescriptor
{
    public required SeriesPrimitiveType PrimitiveType { get; init; }
    public required string PrimarySeriesName { get; init; }

    /// <summary>
    /// Optional secondary series name: BandCloud lower bound, FloatingBar high value,
    /// Bubble size, Vector end Y or polar angle, Waterfall RunningStart, or Area reference baseline.
    /// </summary>
    public string? SecondarySeriesName { get; init; }

    /// <summary>
    /// Optional tertiary series name: Vector end bar delta or polar pixel length,
    /// DynamicStyling auxiliary driver, or Waterfall bar kind ordinal (<see cref="WaterfallBarKind"/>).
    /// </summary>
    public string? TertiarySeriesName { get; init; }

    /// <summary>
    /// Ordered layer series names (bottom-to-top) for <see cref="SeriesPrimitiveType.StackedArea"/>.
    /// </summary>
    public IReadOnlyList<string>? StackedSeriesNames { get; init; }

    // Common visual styling parameters (Visual-Only)
    public CoreLineStyle LineStyle { get; init; } = CoreLineStyle.Solid;
    public NullGapPolicy NullPolicy { get; init; } = NullGapPolicy.BreakPath;
    public int ZOrderOffset { get; init; } = 0;

    // StepLine / HorizontalSegment / Spline
    public StepAlignmentMode StepMode { get; init; } = StepAlignmentMode.After;
    public float SegmentWidthRatio { get; init; } = 0.8f;
    public double SplineTension { get; init; } = 0.5;

    // Area / BandCloud / PositiveNegativeArea / StackedArea
    public AreaBaselineMode BaselineMode { get; init; } = AreaBaselineMode.ScalarValue;
    public decimal BaselineValue { get; init; } = 0m;
    public byte FillAlpha { get; init; } = 64;
    public bool DrawBorderStroke { get; init; } = true;
    public StackingSignMode StackingMode { get; init; } = StackingSignMode.ZeroBaselineSplit;
    public StackingNullMode StackingNullPolicy { get; init; } = StackingNullMode.TreatAsZero;

    // Bubble
    public float BubbleMinRadiusPx { get; init; } = 2.0f;
    public float BubbleMaxRadiusPx { get; init; } = 16.0f;
    public bool BubbleSortDescending { get; init; } = true;
    public bool BubbleDrawZeroSize { get; init; } = false;
    public bool BubbleAllowSignedSize { get; init; } = false;

    // MarkerSymbol / Lollipop
    public MarkerSymbolShape MarkerShape { get; init; } = MarkerSymbolShape.Circle;
    public float MarkerRadiusPx { get; init; } = 4.0f;
    public bool MarkerFilled { get; init; } = true;
    public float MarkerYOffsetPx { get; init; } = 0.0f;
    public float LollipopStemWidthPx { get; init; } = 1.0f;

    // ColorStrip
    public ColorStripScaleMode StripScaleMode { get; init; } = ColorStripScaleMode.Diverging;
    public ColorStripAnchor StripAnchor { get; init; } = ColorStripAnchor.Bottom;
    public float StripHeightPx { get; init; } = 12.0f;
    public byte StripAlpha { get; init; } = 255;
    public int StripQuantizationSteps { get; init; } = 0;

    // DynamicStyling
    public DynamicStyleMetric StyleMetric { get; init; } = DynamicStyleMetric.Slope;
    public float MinStrokeWidthPx { get; init; } = 1.0f;
    public float MaxStrokeWidthPx { get; init; } = 4.0f;
    public byte MinDynamicAlpha { get; init; } = 80;
    public byte MaxDynamicAlpha { get; init; } = 255;

    // Vector
    public VectorInputMode VectorMode { get; init; } = VectorInputMode.PriceSpaceEndpoints;
    public float VectorHeadSizePx { get; init; } = 8.0f;
    public float VectorMaxLengthPx { get; init; } = 200.0f;

    // Waterfall
    public bool WaterfallDrawConnectors { get; init; } = true;

    // Optional fixed normalization domain bounds for Bubble / ColorStrip / DynamicStyling.
    // When both are set with ScaleDomainMax > ScaleDomainMin, viewport extrema scanning is skipped.
    public decimal? ScaleDomainMin { get; init; } = null;
    public decimal? ScaleDomainMax { get; init; } = null;
}
