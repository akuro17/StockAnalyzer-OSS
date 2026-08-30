namespace StockAnalyzer.Avalonia.Drawing;

public enum DrawingTool
{
    Pointer,
    Ruler,
    Eraser,
    Text,
    
    // Lines
    TrendLine,
    HorizontalLine,
    VerticalLine,
    Ray,
    Polyline,
    Arrow,
    CatenaryCurve,
    CurveTrend,
    NurbsTrendCurve,
    NurbsParabola,
    NurbsHyperbola,
    NurbsConicArc,

    // Shapes
    Rectangle,
    Triangle,
    Ellipse,
    EllipseAnnulus,
    NurbsConic,
    NurbsEllipse,
    
    // Fibonacci
    FibonacciRetracement,
    FibonacciTimeZone,
    FibonacciFan,
    FibonacciArc,
    FibonacciCircle,
    FibonacciExpansion,
    FibonacciChannel,
    FibonacciSpiral,
    FibonacciEllipse,
    
    // Gann
    GannFan,
    GannBox,
    GannSquare,
    GannGrid,
    GannSquareOfNine,
    GannSquare144,
    GannWheel,
    
    // Patterns
    Pitchfork,
    ParallelChannel,
    CurveChannel,
    AutoElliottWave,
    HarmonicPattern,
    GeometricPattern,
    
    // Analysis
    RegressionTrend,
    RangeSpline,
    FixedRangeVolumeProfile,
    AnchoredVWAP,
    FftSpectrum,
    DtwProjection,
    KalmanFilterProjection,
    TargetPriceProjection,
    
    // Prediction
    LongPosition,
    ShortPosition,
    GhostFeed,
    BarPattern,
    
    // Cycle
    CyclicLines,
    SineLine,
    TimeCycles,
    
    // Other
    AngleTool,
    PriceLabel,
    Callout,
    LineText,
    CurveLineText
}
