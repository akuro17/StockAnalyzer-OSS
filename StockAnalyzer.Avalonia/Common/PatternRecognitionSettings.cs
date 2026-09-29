namespace StockAnalyzer.Avalonia.Common;

public class PatternRecognitionSettings
{
    public int MinWindow { get; set; } = StockAnalyzer.Core.ChartConstants.PatternRecognitionDefaultMinWindow;
    public int MaxWindow { get; set; } = StockAnalyzer.Core.ChartConstants.PatternRecognitionDefaultMaxWindow;
    public int WindowStep { get; set; } = StockAnalyzer.Core.ChartConstants.PatternRecognitionDefaultWindowStep;
    public double DefaultThreshold { get; set; } = StockAnalyzer.Core.ChartConstants.PatternRecognitionDefaultThreshold;

    public void Validate()
    {
        if (MinWindow <= 0) throw new System.InvalidOperationException("PatternRecognitionSettings: MinWindow must be positive.");
        if (MaxWindow <= MinWindow) throw new System.InvalidOperationException("PatternRecognitionSettings: MaxWindow must be greater than MinWindow.");
        if (WindowStep <= 0) throw new System.InvalidOperationException("PatternRecognitionSettings: WindowStep must be positive.");
        if (DefaultThreshold <= 0 || DefaultThreshold > 1) throw new System.InvalidOperationException("PatternRecognitionSettings: DefaultThreshold must be between 0 and 1.");
    }
}
