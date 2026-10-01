using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models.Training;
using Microsoft.Extensions.Logging;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Cross-checks the model-contract <c>metadata_props</c> embedded by the training tooling
/// (<c>StockAnalyzer.Python/training/onnx_meta.py</c>) against the running inference
/// configuration. Tensor-shape validation alone cannot distinguish an
/// <see cref="PredictionFeatureMode.OhlcvMinMax"/> model from a
/// <see cref="PredictionFeatureMode.ZScoreStandardized"/> one (both are
/// <c>[batch, window, 5]</c>); a wrong feature mode would silently produce plausible-looking
/// but wrong predictions. Enforced keys: <c>feature_mode</c>, <c>window_size</c>,
/// <c>class_order</c>. Provenance keys — <c>model_contract_version</c>, <c>producer</c>,
/// <c>created_utc</c>, <c>prediction_horizon</c>, and the <c>training_*</c> / <c>validation_*</c>
/// calendar spans — are logged, never enforced. Any other key written by the training tooling is
/// ignored.
///
/// <para>
/// <c>target_type</c> (<c>classification</c> / <c>regression</c>; see
/// <c>onnx_meta.TARGET_TYPES</c>) selects the learning objective. A <c>regression</c> model
/// still carries <c>class_order</c> for a stable metadata key set, but its output index order
/// is meaningless, so this reader skips the <c>class_order</c> cross-check for it. An absent
/// key is treated as <c>classification</c> (models exported before the key existed); a present
/// but unrecognized value is a corrupt contract and is rejected.
/// </para>
/// </summary>
public static class PredictionModelMetadata
{
    /// <summary>Metadata key: training-side feature-mode wire string (see <see cref="ParseFeatureMode"/>).</summary>
    public const string FeatureModeKey = "feature_mode";
    /// <summary>
    /// Metadata key: JSON of the training-side <see cref="Training.FeatureSpec"/> for a
    /// <see cref="PredictionFeatureMode.ComposedFeatures"/> model. Enforced (not merely logged):
    /// two composed models of equal channel width are indistinguishable by tensor shape, so a
    /// wrong composition would silently produce plausible-looking but wrong predictions. Absent for
    /// every fixed-width feature mode.
    /// </summary>
    public const string FeatureSpecKey = "feature_spec";
    /// <summary>Relative required fixed-scaler sidecar for a generation.</summary>
    public const string ScalerReferenceKey = "scaler_ref";
    private const string NormalizationKey = "normalization";
    private const string FixedNormalization = "fixed_zscore";
    /// <summary>Metadata key: bar count the model's window was trained with.</summary>
    public const string WindowSizeKey = "window_size";
    /// <summary>Metadata key: comma-separated class labels in output-index order.</summary>
    public const string ClassOrderKey = "class_order";

    internal static bool HasValidClassOrder(string? classOrder)
    {
        if (classOrder is null) return false;
        var labels = classOrder.Split(',');
        return labels.Length > 0 && labels.All(label => !string.IsNullOrWhiteSpace(label)
            && label == label.Trim())
            && labels.Distinct(StringComparer.Ordinal).Count() == labels.Length;
    }
    /// <summary>
    /// Metadata key: learning-objective wire string, <c>classification</c> or <c>regression</c>
    /// (mirrors <c>onnx_meta.TARGET_TYPES</c> / <c>TrainingConfigJson</c>'s <c>target_type</c>).
    /// Absent =&gt; <see cref="TargetTypeClassification"/>.
    /// </summary>
    public const string TargetTypeKey = "target_type";
    /// <summary>Wire value of <see cref="TargetTypeKey"/> for the historical 3-class head.</summary>
    public const string TargetTypeClassification = "classification";
    /// <summary>Wire value of <see cref="TargetTypeKey"/> for a single-value regression head.</summary>
    public const string TargetTypeRegression = "regression";
    /// <summary>
    /// Metadata key: forward label horizon in bars the model was trained with (informational
    /// only; mirrors <c>onnx_meta.CONTRACT_KEYS</c>'s <c>prediction_horizon</c>, always present
    /// on a contract-carrying model). Not cross-checked against the running configuration: no
    /// runtime "expected horizon" setting currently exists to compare it to.
    /// </summary>
    public const string HorizonKey = "prediction_horizon";
    /// <summary>Effective chronological purge gap in bars, recorded by contract version 2.</summary>
    public const string PurgeGapKey = "com.stockanalyzer.training.purge_gap";
    public const string OutputSemanticKey = "com.stockanalyzer.output.semantic";
    public const string OutputUnitKey = "com.stockanalyzer.output.unit";
    public const string OutputHorizonKey = "com.stockanalyzer.output.horizon";
    public const string OutputTimeframeKey = "com.stockanalyzer.output.timeframe";
    public const string OutputConfidenceTypeKey = "com.stockanalyzer.output.confidence_type";
    public const string TargetFormulaKey = "com.stockanalyzer.output.target_formula";
    /// <summary>Metadata key: contract schema version (informational).</summary>
    public const string ContractVersionKey = "model_contract_version";
    /// <summary>Metadata key: producer/provenance string (informational).</summary>
    public const string ProducerKey = "producer";
    /// <summary>Metadata key: ISO-8601 UTC export timestamp (informational).</summary>
    public const string CreatedUtcKey = "created_utc";
    /// <summary>Metadata key: training-window start date (informational; empty when the dataset carried no dates).</summary>
    public const string TrainingStartKey = "training_start";
    /// <summary>Metadata key: training-window end date (informational; empty when the dataset carried no dates).</summary>
    public const string TrainingEndKey = "training_end";
    /// <summary>Metadata key: validation-window start date (informational; empty when the dataset carried no dates).</summary>
    public const string ValidationStartKey = "validation_start";
    /// <summary>Metadata key: validation-window end date (informational; empty when the dataset carried no dates).</summary>
    public const string ValidationEndKey = "validation_end";

    /// <summary>
    /// The contract keys this reader knows. Used only to distinguish a partial/corrupt
    /// contract (a known key present, <see cref="FeatureModeKey"/> absent) from an unrelated
    /// <c>metadata_props</c> entry stamped by some other tool.
    /// </summary>
    private static readonly string[] KnownContractKeys =
    {
        FeatureModeKey, FeatureSpecKey, ScalerReferenceKey, NormalizationKey, "lags", "clip_sigma",
        WindowSizeKey, ClassOrderKey, TargetTypeKey, HorizonKey, PurgeGapKey, ContractVersionKey,
        OutputSemanticKey, OutputUnitKey, OutputHorizonKey, OutputTimeframeKey,
        OutputConfidenceTypeKey, TargetFormulaKey,
        ProducerKey, CreatedUtcKey, TrainingStartKey, TrainingEndKey, ValidationStartKey, ValidationEndKey,
    };

    /// <summary>
    /// Reads <see cref="TargetTypeKey"/>: <see langword="true"/> only for an explicit
    /// <see cref="TargetTypeRegression"/> value. An absent key maps to classification; a
    /// present but unrecognized value throws <see cref="InvalidOperationException"/>.
    /// </summary>
    private static bool IsRegressionTarget(IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue(TargetTypeKey, out var raw))
        {
            return false;
        }

        return (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            TargetTypeClassification or "" => false,
            TargetTypeRegression => true,
            _ => throw new InvalidOperationException(
                $"ONNX model metadata '{TargetTypeKey}' = '{raw}' is not a recognized target type " +
                $"('{TargetTypeClassification}' or '{TargetTypeRegression}')."),
        };
    }

    /// <summary>
    /// Reads the framework-neutral output contract. Every loadable model must be self-describing.
    /// </summary>
    public static PredictionOutputContract ReadOutputContract(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            throw new InvalidOperationException("ONNX model has no output semantic contract; re-export with current tooling.");
        if (!metadata.TryGetValue(ContractVersionKey, out var version) || version != "2")
            throw new InvalidOperationException("ONNX output semantic contract requires model contract version 2.");

        static string Required(IReadOnlyDictionary<string, string> values, string key)
        {
            if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"ONNX model contract version 2 requires '{key}'.");
            return value;
        }

        var semantic = Required(metadata, OutputSemanticKey) switch
        {
            "class_probabilities" => PredictionOutputSemantic.ClassProbabilities,
            "log_return" => PredictionOutputSemantic.LogReturn,
            _ => throw new InvalidOperationException("Unknown ONNX output semantic."),
        };
        var unit = Required(metadata, OutputUnitKey) switch
        {
            "probability" => PredictionOutputUnit.Probability,
            "dimensionless" => PredictionOutputUnit.Dimensionless,
            _ => throw new InvalidOperationException("Unknown ONNX output unit."),
        };
        var timeframe = Required(metadata, OutputTimeframeKey) switch
        {
            "daily" => TimeframeType.Daily,
            "weekly" => TimeframeType.Weekly,
            "monthly" => TimeframeType.Monthly,
            _ => throw new InvalidOperationException("Unknown ONNX output timeframe."),
        };
        var confidenceType = Required(metadata, OutputConfidenceTypeKey) switch
        {
            "none" => ConfidenceType.None,
            "class_probability" => ConfidenceType.ClassProbability,
            "calibrated_probability" => ConfidenceType.CalibratedProbability,
            "prediction_std_dev" => ConfidenceType.PredictionStdDev,
            _ => throw new InvalidOperationException("Unknown ONNX confidence type."),
        };
        var targetFormula = Required(metadata, TargetFormulaKey) switch
        {
            "thresholded_simple_return" => PredictionTargetFormula.ThresholdedSimpleReturn,
            "log_future_close_over_anchor_close" => PredictionTargetFormula.LogFutureCloseOverAnchorClose,
            _ => throw new InvalidOperationException("Unknown ONNX target formula."),
        };
        var horizonRaw = Required(metadata, OutputHorizonKey);
        if (!int.TryParse(horizonRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var horizon)
            || horizon <= 0 || horizon.ToString(CultureInfo.InvariantCulture) != horizonRaw
            || !metadata.TryGetValue(HorizonKey, out var oldHorizon) || oldHorizon != horizonRaw)
            throw new InvalidOperationException("ONNX output horizon must be a positive bar count matching prediction_horizon.");

        var targetType = Required(metadata, TargetTypeKey);
        bool classification = semantic == PredictionOutputSemantic.ClassProbabilities;
        if (classification != (targetType == TargetTypeClassification)
            || (!classification && targetType != TargetTypeRegression)
            || (classification && (unit != PredictionOutputUnit.Probability
                || confidenceType != ConfidenceType.ClassProbability
                || targetFormula != PredictionTargetFormula.ThresholdedSimpleReturn))
            || (!classification && (unit != PredictionOutputUnit.Dimensionless
                || confidenceType != ConfidenceType.None
                || targetFormula != PredictionTargetFormula.LogFutureCloseOverAnchorClose)))
            throw new InvalidOperationException("ONNX output semantic, unit, confidence, formula and target_type disagree.");

        return new PredictionOutputContract(semantic, unit, horizon, timeframe, confidenceType, targetFormula);
    }

    /// <summary>
    /// Maps a training-side <c>feature_mode</c> wire string (the values of
    /// <c>dataset.FEATURE_MODES</c> / <c>onnx_meta.py</c>) to the C# enum. Returns
    /// <see langword="null"/> for an unrecognized value.
    /// </summary>
    public static PredictionFeatureMode? ParseFeatureMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "ohlcv_minmax" => PredictionFeatureMode.OhlcvMinMax,
            "log_return" => PredictionFeatureMode.LogReturn,
            "zscore" => PredictionFeatureMode.ZScoreStandardized,
            "zscore_joint" => PredictionFeatureMode.ZScoreOhlcvJoint,
            "log_return_ohlc" => PredictionFeatureMode.LogReturnOhlc,
            "composed_features" => PredictionFeatureMode.ComposedFeatures,
            _ => null,
        };
    }

    /// <summary>Reject partial fixed-preprocessing markers before any loader selects a preprocessing path.</summary>
    public static bool RequiresFixedScaler(IReadOnlyDictionary<string, string> metadata)
    {
        metadata.TryGetValue(NormalizationKey, out var normalization);
        bool isFixed = string.Equals(normalization, FixedNormalization, StringComparison.Ordinal);
        bool hasReference = metadata.TryGetValue(ScalerReferenceKey, out var reference);
        bool hasLags = metadata.ContainsKey("lags");
        bool hasClip = metadata.ContainsKey("clip_sigma");
        if (isFixed)
        {
            if (!hasReference || string.IsNullOrWhiteSpace(reference) || !hasLags || !hasClip)
                throw new InvalidOperationException("Fixed z-score metadata requires scaler_ref, lags and clip_sigma.");
            return true;
        }
        if (hasReference || hasLags || hasClip)
            throw new InvalidOperationException("Fixed preprocessing markers require normalization=fixed_zscore.");
        if (metadata.TryGetValue(FeatureSpecKey, out var featureSpecRaw))
        {
            try
            {
                using var document = JsonDocument.Parse(featureSpecRaw);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Composed feature_spec metadata must be an object.");
                if (document.RootElement.TryGetProperty("lags", out var nestedLags))
                {
                    if (nestedLags.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("Composed lags must be an array and require fixed z-score.");
                    if (nestedLags.GetArrayLength() > 0)
                        throw new InvalidOperationException("Composed lags require normalization=fixed_zscore and a scaler.");
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Composed feature_spec metadata is invalid JSON.", ex);
            }
        }
        if (normalization is not null && normalization != metadata.GetValueOrDefault(FeatureModeKey))
            throw new InvalidOperationException("ONNX normalization disagrees with feature_mode.");
        return false;
    }

    /// <summary>
    /// Validates <paramref name="metadata"/> against the configured inference contract.
    /// A <see langword="null"/> or empty map is rejected. A non-empty map that carries a known
    /// contract key but omits <see cref="FeatureModeKey"/> is a partial/corrupt contract
    /// and is rejected. Every present validated key that disagrees with configuration
    /// throws <see cref="InvalidOperationException"/>, which the caller routes to the
    /// permanent-initialization-failure path. When <see cref="TargetTypeKey"/> is
    /// <see cref="TargetTypeRegression"/> the <see cref="ClassOrderKey"/> cross-check is
    /// skipped (a regression head has no meaningful class order).
    /// </summary>
    /// <param name="expectedFeatureSpec">
    /// Canonical JSON of the configured <see cref="Training.FeatureSpec"/> for a
    /// <see cref="PredictionFeatureMode.ComposedFeatures"/> model, or <see langword="null"/> when the
    /// running configuration has no composed spec. When both this and the model's
    /// <see cref="FeatureSpecKey"/> are present and not JSON-equivalent, validation throws.
    /// </param>
    public static void Validate(
        IReadOnlyDictionary<string, string>? metadata,
        PredictionFeatureMode expectedFeatureMode,
        int expectedWindowSize,
        IReadOnlyList<string> expectedClassLabels,
        ILogger logger,
        string? expectedFeatureSpec = null)
    {
        if (metadata is null || metadata.Count == 0)
            throw new InvalidOperationException("ONNX model has no output semantic contract; re-export with current tooling.");

        if (!metadata.ContainsKey(FeatureModeKey))
        {
            foreach (var key in KnownContractKeys)
            {
                if (metadata.ContainsKey(key))
                {
                    throw new InvalidOperationException(
                        $"ONNX model metadata carries contract key '{key}' but is missing '{FeatureModeKey}'; " +
                        "the metadata_props map is a partial or corrupt contract. Re-export the model with current tooling.");
                }
            }
        }

        RequiresFixedScaler(metadata);

        if (metadata.TryGetValue(ContractVersionKey, out var contractVersion)
            && contractVersion == "2")
        {
            if (!metadata.TryGetValue(PurgeGapKey, out var purgeGapRaw)
                || !int.TryParse(purgeGapRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var purgeGap)
                || purgeGap < 0
                || purgeGap.ToString(CultureInfo.InvariantCulture) != purgeGapRaw)
            {
                throw new InvalidOperationException(
                    $"ONNX model contract version 2 requires '{PurgeGapKey}' as a canonical non-negative bar count.");
            }
        }

        if (metadata.TryGetValue(FeatureModeKey, out var featureModeRaw))
        {
            var parsed = ParseFeatureMode(featureModeRaw);
            if (parsed is null)
            {
                throw new InvalidOperationException(
                    $"ONNX model metadata '{FeatureModeKey}' = '{featureModeRaw}' is not a recognized feature mode.");
            }

            if (parsed.Value != expectedFeatureMode)
            {
                throw new InvalidOperationException(
                    $"ONNX model was trained with feature mode '{featureModeRaw}' ({parsed.Value}) " +
                    $"but the configured PredictionFeatureMode is {expectedFeatureMode}.");
            }
        }

        if (metadata.TryGetValue(WindowSizeKey, out var windowRaw))
        {
            if (!int.TryParse(windowRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var modelWindow))
            {
                throw new InvalidOperationException(
                    $"ONNX model metadata '{WindowSizeKey}' = '{windowRaw}' is not an integer.");
            }

            if (modelWindow != expectedWindowSize)
            {
                throw new InvalidOperationException(
                    $"ONNX model was trained with window size {modelWindow} " +
                    $"but the configured PredictionWindowSize is {expectedWindowSize}.");
            }
        }

        if (metadata.TryGetValue(FeatureSpecKey, out var featureSpecRaw))
        {
            if (expectedFeatureSpec is null)
            {
                throw new InvalidOperationException(
                    "ONNX model metadata carries a 'feature_spec' (a composed-features contract) " +
                    "but the running configuration supplies no FeatureSpec to validate it against.");
            }

            if (!FeatureSpecEquivalent(featureSpecRaw, expectedFeatureSpec))
            {
                throw new InvalidOperationException(
                    $"ONNX model was trained with feature_spec {featureSpecRaw} " +
                    $"but the configured FeatureSpec is {expectedFeatureSpec}.");
            }
        }
        else if (expectedFeatureSpec is not null)
        {
            throw new InvalidOperationException(
                "The running configuration supplies a composed FeatureSpec but the ONNX model " +
                "metadata carries no 'feature_spec' key; re-export the model with current tooling.");
        }

        bool isRegression = IsRegressionTarget(metadata);

        if (!isRegression && metadata.TryGetValue(ClassOrderKey, out var classOrderRaw))
        {
            var modelClasses = classOrderRaw.Split(
                ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (!ClassesMatch(modelClasses, expectedClassLabels))
            {
                throw new InvalidOperationException(
                    $"ONNX model class order [{string.Join(", ", modelClasses)}] does not match " +
                    $"the configured PredictionClassLabels [{string.Join(", ", expectedClassLabels)}].");
            }
        }

        _ = ReadOutputContract(metadata);

        logger.LogInformation(
            "ONNX model contract validated (version {Version}, target {TargetType}, horizon {Horizon}, producer {Producer}, created {Created}, " +
            "training {TrainingStart}..{TrainingEnd}, validation {ValidationStart}..{ValidationEnd}).",
            metadata.TryGetValue(ContractVersionKey, out var version) ? version : "?",
            metadata.TryGetValue(TargetTypeKey, out var targetType) ? targetType : TargetTypeClassification,
            metadata.TryGetValue(HorizonKey, out var horizon) ? horizon : "?",
            metadata.TryGetValue(ProducerKey, out var producer) ? producer : "?",
            metadata.TryGetValue(CreatedUtcKey, out var created) ? created : "?",
            metadata.TryGetValue(TrainingStartKey, out var trainingStart) ? trainingStart : "?",
            metadata.TryGetValue(TrainingEndKey, out var trainingEnd) ? trainingEnd : "?",
            metadata.TryGetValue(ValidationStartKey, out var validationStart) ? validationStart : "?",
            metadata.TryGetValue(ValidationEndKey, out var validationEnd) ? validationEnd : "?");
    }

    /// <summary>
    /// Compare validated feature contracts, treating an omitted lag list as empty while preserving
    /// channel and lag order. Legacy composed models were exported before the lag field existed.
    /// </summary>
    private static bool FeatureSpecEquivalent(string? modelValue, string? expectedValue)
    {
        try
        {
            var model = JsonSerializer.Deserialize<FeatureSpec>(modelValue ?? "null", TrainingConfigJson.Options);
            var expected = JsonSerializer.Deserialize<FeatureSpec>(expectedValue ?? "null", TrainingConfigJson.Options);
            if (model is null || expected is null || !model.IsValid(out _) || !expected.IsValid(out _)
                || model.SchemaVersion != expected.SchemaVersion
                || !model.Lags!.SequenceEqual(expected.Lags!)
                || model.Channels.Count != expected.Channels.Count)
                return false;
            for (int index = 0; index < model.Channels.Count; index++)
            {
                var actualChannel = model.Channels[index];
                var expectedChannel = expected.Channels[index];
                if (actualChannel.Kind != expectedChannel.Kind || actualChannel.Price != expectedChannel.Price
                    || actualChannel.Indicator != expectedChannel.Indicator
                    || actualChannel.Normalization != expectedChannel.Normalization)
                    return false;
                if (actualChannel.Kind == FeatureChannelKind.Indicator
                    && (actualChannel.Params!.Count != expectedChannel.Params!.Count
                        || actualChannel.Params.Any(pair => !expectedChannel.Params.TryGetValue(pair.Key, out var value)
                            || value != pair.Value)))
                    return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ClassesMatch(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
