using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Execution;

/// <summary>
/// Execution and validation policy governing sandboxed ephemeral models and training requests.
/// Allows decoupling MCP-specific fixed contracts from general GUI sandboxed execution.
/// </summary>
public sealed record EphemeralExecutionPolicy
{
    /// <summary>
    /// When true, verifies that request and model parameters conform strictly to <see cref="EphemeralFixedValues.Current"/>.
    /// </summary>
    public bool EnforceMcpFixedValues { get; init; }

    /// <summary>
    /// If non-null, restricts models and requests to this specific timeframe.
    /// </summary>
    public TrainingTimeframe? RequiredTimeframe { get; init; }

    /// <summary>
    /// If non-null, restricts models and requests to this specific feature mode.
    /// </summary>
    public PredictionFeatureMode? RequiredFeatureMode { get; init; }

    /// <summary>
    /// Whether models with fixed scalers are accepted.
    /// </summary>
    public bool AllowFixedScaler { get; init; }

    /// <summary>
    /// Whether models with Platt scaling / calibration are accepted.
    /// </summary>
    public bool AllowCalibration { get; init; }

    /// <summary>
    /// Whether Combinatorial Purged Cross-Validation is permitted for training.
    /// </summary>
    public bool AllowCpcv { get; init; }

    /// <summary>
    /// Whether ensemble models (bagging / stacking) are permitted for training.
    /// </summary>
    public bool AllowEnsemble { get; init; }

    /// <summary>
    /// Whether fine-tuning / warm-start initialization is permitted for training.
    /// </summary>
    public bool AllowNonFreshInitialization { get; init; }

    /// <summary>
    /// Full-capability policy matching the GUI training wizard and inference capabilities.
    /// </summary>
    public static EphemeralExecutionPolicy GuiEquivalent { get; } = new()
    {
        EnforceMcpFixedValues = false,
        RequiredTimeframe = null,
        RequiredFeatureMode = null,
        AllowFixedScaler = true,
        AllowCalibration = true,
        AllowCpcv = true,
        AllowEnsemble = true,
        AllowNonFreshInitialization = true,
    };

    /// <summary>
    /// Current MCP server restriction profile: enforces <see cref="EphemeralFixedValues.Current"/> baseline with opt-in scaling, calibration, and CPCV.
    /// </summary>
    public static EphemeralExecutionPolicy McpFixed { get; } = new()
    {
        EnforceMcpFixedValues = true,
        RequiredTimeframe = EphemeralFixedValues.Current.Timeframe,
        RequiredFeatureMode = EphemeralFixedValues.Current.FeatureMode,
        AllowFixedScaler = true,
        AllowCalibration = true,
        AllowCpcv = true,
        AllowEnsemble = EphemeralFixedValues.Current.EnsembleMethod != TrainingEnsembleMethod.None,
        AllowNonFreshInitialization = EphemeralFixedValues.Current.InitializationMode != TrainingInitializationMode.Fresh,
    };
}
