namespace StockAnalyzer.Core.Models;

/// <summary>The meaning of an ONNX output tensor, independent of its training framework.</summary>
public enum PredictionOutputSemantic
{
    ClassProbabilities,
    LogReturn,
}

/// <summary>The raw output unit before any UI conversion.</summary>
public enum PredictionOutputUnit
{
    Probability,
    Dimensionless,
}

/// <summary>The mathematical meaning of an optional confidence value.</summary>
public enum ConfidenceType
{
    None,
    ClassProbability,
    CalibratedProbability,
    PredictionStdDev,
}

/// <summary>The label equation used during training.</summary>
public enum PredictionTargetFormula
{
    ThresholdedSimpleReturn,
    LogFutureCloseOverAnchorClose,
}

/// <summary>Validated, immutable semantic contract read from ONNX metadata_props.</summary>
public sealed record PredictionOutputContract(
    PredictionOutputSemantic Semantic,
    PredictionOutputUnit Unit,
    int HorizonBars,
    TimeframeType Timeframe,
    ConfidenceType ConfidenceType,
    PredictionTargetFormula TargetFormula);
