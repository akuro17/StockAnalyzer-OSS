namespace StockAnalyzer.Core.Models.Rendering;

/// <summary>
/// Declarative rendering primitive types supported by the chart indicator rendering backend.
/// Explicit integer ordinals are wire-contract pinned and append-only.
/// </summary>
public enum SeriesPrimitiveType
{
    Line = 0,
    DashedLine = 1,
    Dot = 2,
    Histogram = 3,
    StepLine = 4,
    HorizontalSegment = 5,
    Spline = 6,
    Area = 7,
    BandCloud = 8,
    FloatingBar = 9,
    PositiveNegativeArea = 10,
    StackedArea = 11,
    Bubble = 12,
    MarkerSymbol = 13,
    ColorStrip = 14,
    DynamicStyling = 15,
    Vector = 16,
    Waterfall = 17,
    Lollipop = 18
}

public enum StepAlignmentMode
{
    After = 0,
    Before = 1,
    Mid = 2
}

public enum NullGapPolicy
{
    BreakPath = 0,
    ConnectThrough = 1
}

public enum AreaBaselineMode
{
    PanelBottom = 0,
    ScalarValue = 1,
    ReferenceSeries = 2
}

public enum StackingSignMode
{
    ZeroBaselineSplit = 0,
    NonNegativeClamp = 1,
    RawCumulative = 2
}

public enum StackingNullMode
{
    TreatAsZero = 0,
    BreakAllLayers = 1
}

public enum MarkerSymbolShape
{
    Circle = 0,
    TriangleUp = 1,
    TriangleDown = 2,
    Square = 3,
    Cross = 4,
    Plus = 5,
    Diamond = 6
}

public enum ColorStripScaleMode
{
    Sequential = 0,
    Diverging = 1,
    DiscreteState = 2
}

public enum ColorStripAnchor
{
    Bottom = 0,
    Top = 1,
    FullPanel = 2
}

public enum DynamicStyleMetric
{
    Value = 0,
    Slope = 1,
    Delta = 2,
    AuxiliarySeries = 3
}

public enum VectorInputMode
{
    PriceSpaceEndpoints = 0,
    BarAnchoredPolar = 1
}

public enum WaterfallBarKind
{
    Delta = 0,
    Subtotal = 1,
    Total = 2
}
