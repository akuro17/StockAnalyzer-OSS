using System;
using System.Diagnostics;
using System.Globalization;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>Validates and applies one immutable per-run snapshot at the training boundary.</summary>
public static class TrainingResourceGuard
{
    public static void Validate(TrainingJobConfig config, TrainingResourceOverrides limits)
        => Validate(config, Resolve(limits));

    public static TrainingResourceLimits Resolve(TrainingResourceOverrides limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        int maxChannels = EffectiveLimit(limits.MaxChannels, "SA_MAX_COMPOSED_CHANNELS",
            TrainingResourceOverrides.MaximumChannels);
        int maxMiB = EffectiveLimit(limits.MaxTensorSizeMiB, "SA_MAX_COMPOSED_TENSOR_SIZE_MB",
            TrainingResourceOverrides.MaximumTensorSizeMiB);
        int maxSamples = EffectiveLimit(limits.MaxSamples, "SA_MAX_COMPOSED_BATCH_SAMPLES",
            TrainingResourceOverrides.MaximumSamples);
        var resolved = new TrainingResourceLimits(TrainingResourceLimits.CurrentContractVersion,
            maxChannels, maxMiB, maxSamples, TrainingResourceOverrides.MaximumEvaluationFolds,
            TrainingResourceOverrides.MaximumFeatureLag, TrainingResourceOverrides.MaximumEnsembleMembers,
            checked(TrainingResourceOverrides.MaximumRunDurationMinutes * 60));
        resolved.Validate();
        return resolved;
    }

    public static void Validate(TrainingJobConfig config, TrainingResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        config.Validate();
        if (!config.FixedZScore && config.FeatureMode != PredictionFeatureMode.ComposedFeatures) return;

        long channels = config.ExpandedFeatureChannelCount();
        if (channels > limits.MaxChannels)
            throw new InvalidOperationException($"Training resource limit: expanded channels exceed {limits.MaxChannels}.");
        long bytes = checked((long)TrainingResourceOverrides.Float32Bytes * config.WindowSize * channels);
        long maximum = checked((long)limits.MaxTensorSizeMiB * TrainingResourceOverrides.MiB);
        if (bytes > maximum)
            throw new InvalidOperationException($"Training tensor has {bytes} bytes, exceeding the {limits.MaxTensorSizeMiB} MiB limit.");
    }

    public static void ApplyToChild(ProcessStartInfo startInfo, TrainingResourceOverrides limits)
        => ApplyToChild(startInfo, Resolve(limits));

    public static void ApplyToChild(ProcessStartInfo startInfo, TrainingResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        startInfo.Environment["SA_MAX_COMPOSED_CHANNELS"] = limits.MaxChannels.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["SA_MAX_COMPOSED_TENSOR_SIZE_MB"] = limits.MaxTensorSizeMiB.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["SA_MAX_COMPOSED_BATCH_SAMPLES"] = limits.MaxSamples.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["SA_MAX_EVALUATION_FOLDS"] = limits.MaxEvaluationFolds.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["SA_MAX_FEATURE_LAG"] = limits.MaxFeatureLag.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["SA_MAX_ENSEMBLE_MEMBERS"] = limits.MaxEnsembleMembers.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["SA_MAX_RUN_DURATION_SECONDS"] = limits.MaxRunDurationSeconds.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["SA_TRAINING_RESOURCE_CONTRACT_VERSION"] = limits.ContractVersion.ToString(CultureInfo.InvariantCulture);
    }

    private static int EffectiveLimit(int? configured, string environmentName, int maximum)
    {
        if (configured is { } value) return value;
        var raw = Environment.GetEnvironmentVariable(environmentName);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inherited)
            && inherited is >= TrainingResourceOverrides.Minimum && inherited <= maximum
            ? inherited : maximum;
    }
}
