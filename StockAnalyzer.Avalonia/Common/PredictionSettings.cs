namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Strongly-typed configuration for AI Prediction (ONNX).
/// Bound from the "Prediction" section of appsettings.json via IOptions.
/// </summary>
public class PredictionSettings
{
    public string ModelPath { get; set; } = "Models/trend_predictor.onnx";
    public int WindowSize { get; set; } = 10;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelPath)) throw new System.InvalidOperationException("PredictionSettings: ModelPath cannot be empty.");
        if (WindowSize <= 0) throw new System.InvalidOperationException("PredictionSettings: WindowSize must be positive.");
    }
}
