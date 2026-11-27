using System;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Centralized factory for ONNX Runtime <see cref="SessionOptions"/>, separating inference-time configuration from
/// lightweight metadata-inspection sessions.
/// </summary>
public static class OnnxSessionOptionsFactory
{
    private const string IntraOpAllowSpinningConfigKey = "session.intra_op.allow_spinning";
    private const string SpinEnabledValue = "1";
    private const string SpinDisabledValue = "0";

    /// <summary>
    /// Creates configured <see cref="SessionOptions"/> for inference pipelines.
    /// Defaults to CPU <c>ORT_SEQUENTIAL</c> with <c>IntraOpNumThreads = 1</c> and spinning disabled (measured fastest on batch-1 chart models).
    /// Future GPU execution providers (Win DirectML / macOS CoreML / Linux CUDA) may branch here if single-inference P50 exceeds 5.0 ms or batch &gt;= 32 is introduced.
    /// </summary>
    public static SessionOptions CreateInferenceOptions(IStockAnalyzerSettings? settings = null)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        int? intraOp = settings is null ? PredictionSettings.DefaultIntraOpNumThreads : settings.PredictionIntraOpNumThreads;
        if (intraOp is { } intraVal)
        {
            if (intraVal < PredictionSettings.MinNumThreads || intraVal > PredictionSettings.MaxIntraOpNumThreads)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(settings),
                    intraVal,
                    $"PredictionIntraOpNumThreads must be between {PredictionSettings.MinNumThreads} and {PredictionSettings.MaxIntraOpNumThreads} when specified.");
            }
            options.IntraOpNumThreads = intraVal;
        }

        int? interOp = settings is null ? PredictionSettings.DefaultInterOpNumThreads : settings.PredictionInterOpNumThreads;
        if (interOp is { } interVal)
        {
            if (interVal < PredictionSettings.MinNumThreads || interVal > PredictionSettings.MaxInterOpNumThreads)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(settings),
                    interVal,
                    $"PredictionInterOpNumThreads must be between {PredictionSettings.MinNumThreads} and {PredictionSettings.MaxInterOpNumThreads} when specified.");
            }
            options.InterOpNumThreads = interVal;
        }

        bool allowSpinning = settings?.PredictionAllowIntraOpSpinning ?? PredictionSettings.DefaultAllowIntraOpSpinning;
        options.AddSessionConfigEntry(IntraOpAllowSpinningConfigKey, allowSpinning ? SpinEnabledValue : SpinDisabledValue);
        return options;
    }

    /// <summary>
    /// Creates lightweight <see cref="SessionOptions"/> for metadata-only inspection (node names, custom metadata, input/output shapes)
    /// without graph optimization passes or multi-thread pool allocation.
    /// </summary>
    public static SessionOptions CreateMetadataInspectionOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = PredictionSettings.DefaultIntraOpNumThreads,
            InterOpNumThreads = PredictionSettings.DefaultInterOpNumThreads,
        };
        options.AddSessionConfigEntry(IntraOpAllowSpinningConfigKey, SpinDisabledValue);
        return options;
    }
}
