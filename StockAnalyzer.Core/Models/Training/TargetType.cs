namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Learning objective a training job optimizes for. Wire strings (see <c>TrainingConfigJson</c>):
/// <c>classification</c> / <c>regression</c>, matching the ONNX <c>target_type</c> metadata key
/// (<c>onnx_meta.TARGET_TYPE</c> / <see cref="StockAnalyzer.Core.Models.PredictionModelMetadata"/>).
/// </summary>
/// <remarks>
/// <see cref="Classification"/> is the historical behavior: a 3-class Up/Down/Neutral head whose
/// output is a probability vector ordered by <c>class_order</c>. <see cref="Regression"/> predicts
/// a single continuous value - the log return over the forward horizon - and carries no class
/// order; it is added additively here and consumed by the later trainer / prediction steps of the
/// validation and target-definition feature.
/// </remarks>
public enum TargetType
{
    Classification = 0,
    Regression = 1,
}
