namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Specifies where ONNX training artifacts and sidecars are written and referenced.
/// </summary>
public enum OnnxArtifactStorageMode
{
    /// <summary>
    /// Persisted directly to &lt;DataRoot&gt;/TrainingArtifacts/TW/ and recorded in experiment history.
    /// </summary>
    Artifacts = 0,

    /// <summary>
    /// Stored in an ephemeral %Temp% sandbox directory and omitted from persistent history until explicitly deployed.
    /// </summary>
    Temp = 1,
}
