namespace StockAnalyzer.Avalonia.Drawing;

public enum DrawingTool
{
    Pointer,
    Ruler,
    Eraser,
    Text,
    Information,
    
    // Lines
    TrendLine,
    HorizontalLine,
    VerticalLine,
    Ray,
    Polyline,
    Arrow,
    ClothoidArrow,
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
    ClothoidZone,
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

    // Spirals
    GoldenSpiral,
    LogarithmicSpiral,
    ArchimedeanSpiral,
    
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
    TimeAtPrice,
    AnchoredVWAP,
    FftSpectrum,
    DtwProjection,
    KalmanFilterProjection,
    ClothoidCurveProjection,
    ArimaProjection,
    FftProjection,
    HmmProjection,
    TargetPriceProjection,
    PearsonProjection,
    FrechetProjection,
    SsaProjection,
    SsaMultiComponent,
    SsaSupportResistance,
    SsaStructuralPivots,
    SsaDynamicEnvelopes,
    SsaProjectedTargets,
    SsaAnomalyHighlight,
    
    // Prediction
    LongPosition,
    ShortPosition,
    GhostFeed,
    BarPattern,
    
    // Cycle
    CyclicLines,
    IchimokuTimeLines,
    SineLine,
    TimeCycles,
    AutoTimeCycle,
    
    // Other
    AngleTool,
    PriceLabel,
    Callout,
    ClothoidCallout,
    LineText,
    CurveLineText,
    Icon,

    // Hough Transform
    HoughAutoLines,
    HoughParabolicCurve,
    HoughKeyLevels,
    HoughResonantFan,
    HoughMagneticLine,
    Freehand,
    ClassicPivotPoints
}
