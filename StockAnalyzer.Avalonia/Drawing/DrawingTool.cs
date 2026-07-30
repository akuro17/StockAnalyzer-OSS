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
    
    // Shapes
    Rectangle,
    Triangle,
    Ellipse,
    
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
    ElliottWave,
    AutoElliottWave,
    HarmonicPattern,
    GeometricPattern,
    
    // Analysis
    RegressionTrend,
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
    Callout
}
