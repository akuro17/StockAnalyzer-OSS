using System;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>Optional limits for training jobs. Null inherits the existing process environment.</summary>
public sealed record TrainingResourceOverrides(int? MaxChannels = null, int? MaxTensorSizeMiB = null,
    int? MaxSamples = null)
{
    public const int Minimum = 1;
    public const int MaximumChannels = FeatureSpec.MaxChannels;
    public const int MaximumTensorSizeMiB = 256;
    public const int MaximumSamples = 10_000;
    public const int MiB = 1024 * 1024;
    public const int Float32Bytes = sizeof(float);

    public void Validate()
    {
        Check(MaxChannels, MaximumChannels, nameof(MaxChannels));
        Check(MaxTensorSizeMiB, MaximumTensorSizeMiB, nameof(MaxTensorSizeMiB));
        Check(MaxSamples, MaximumSamples, nameof(MaxSamples));
    }

    private static void Check(int? value, int maximum, string name)
    {
        if (value is { } number && (number < Minimum || number > maximum))
        {
            throw new InvalidOperationException($"{name} must be between {Minimum} and {maximum}.");
        }
    }
}
