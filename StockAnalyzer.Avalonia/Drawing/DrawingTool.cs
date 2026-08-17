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
    
    // Shapes
    Rectangle,
    Triangle,
    Ellipse,
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
    ElliottWave,
    AutoElliottWave,
    HarmonicPattern,
    GeometricPattern,
    
    // Analysis
    RegressionTrend,
    RangeSpline,
    FixedRangeVolumeProfile,
    AnchoredVWAP,
    DtwProjection,
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
