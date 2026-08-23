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
    CatenaryCurve,
    NurbsTrendCurve,
    NurbsConic,
    NurbsEllipse,
    NurbsParabola,
    NurbsHyperbola,
    CurveTrend,
    CurveChannel,

    RegressionTrend,
    RangeSpline,
    FibonacciTimeZone,
    FibonacciArc,
    FibonacciCircle,
    FibonacciFan,
    FibonacciExpansion,
    FibonacciChannel,
    FibonacciSpiral,
    FibonacciEllipse,
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
    EllipseAnnulus,

    AngleTool,
    VerticalLine,
    Ray,

    Arrow,
    PriceLabel,
    Callout,
    LineText,
    CurveLineText,

    DtwProjection,
    KalmanFilterProjection,
    TargetPriceProjection,
    ElliottWave,
    AutoElliottWave,
    NurbsConicArc,

    General // Fallback
}
