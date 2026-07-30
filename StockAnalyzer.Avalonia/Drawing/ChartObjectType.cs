namespace StockAnalyzer.Avalonia.Drawing;

public enum ChartObjectType
{
    TrendLine,
    HorizontalLine,
    Rectangle,
    FibonacciRetracement,
    MeasurementRuler,
    Polyline,
    Text,
    ParallelChannel,

    RegressionTrend,
    FibonacciTimeZone,
    FibonacciArc,
    FibonacciCircle,
    FibonacciFan,
    FibonacciExpansion,
    FibonacciChannel,
    FibonacciSpiral,
    GannFan,
    GannBox,
    GannSquare,
    GannGrid,
    GannSquareOfNine,
    GannSquare144,
    GannWheel,
    Pitchfork,

    FixedRangeVolumeProfile,
    AnchoredVWAP,

    LongPosition,
    ShortPosition,
    GhostFeed,
    BarPattern,
    GeometricPattern,
    HarmonicPattern,
    
    CyclicLines,
    SineLine,
    TimeCycles,

    Triangle,
    Ellipse,

    AngleTool,
    VerticalLine,
    Ray,

    Arrow,
    PriceLabel,
    Callout,

    DtwProjection,
    TargetPriceProjection,
    ElliottWave,
    AutoElliottWave,

    General // Fallback
}
