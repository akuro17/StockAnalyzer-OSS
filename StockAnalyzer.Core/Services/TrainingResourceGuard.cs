using System;
using System.Diagnostics;
using System.Globalization;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>Validates and applies one immutable per-run snapshot at the training boundary.</summary>
public static class TrainingResourceGuard
{
    public static void Validate(TrainingJobConfig config, TrainingResourceOverrides limits)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        config.Validate();
        if (config.FeatureMode != PredictionFeatureMode.ComposedFeatures) return;

        var spec = config.FeatureSpec!;
        if (!spec.IsValid(out var error, limits.MaxChannels ?? FeatureSpec.MaxChannels))
            throw new InvalidOperationException($"Training resource limit: {error}");
        if (limits.MaxTensorSizeMiB is { } maxMiB)
        {
            long bytes = checked((long)TrainingResourceOverrides.Float32Bytes * config.WindowSize * spec.Channels.Count);
            long maximum = checked((long)maxMiB * TrainingResourceOverrides.MiB);
            if (bytes > maximum)
                throw new InvalidOperationException($"Training tensor has {bytes} bytes, exceeding the {maxMiB} MiB limit.");
        }
    }

    public static void ApplyToChild(ProcessStartInfo startInfo, TrainingResourceOverrides limits)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if (limits.MaxChannels is { } channels)
            startInfo.Environment["SA_MAX_COMPOSED_CHANNELS"] = channels.ToString(CultureInfo.InvariantCulture);
        if (limits.MaxTensorSizeMiB is { } tensorMiB)
            startInfo.Environment["SA_MAX_COMPOSED_TENSOR_SIZE_MB"] = tensorMiB.ToString(CultureInfo.InvariantCulture);
        if (limits.MaxSamples is { } samples)
            startInfo.Environment["SA_MAX_COMPOSED_BATCH_SAMPLES"] = samples.ToString(CultureInfo.InvariantCulture);
    }
}
