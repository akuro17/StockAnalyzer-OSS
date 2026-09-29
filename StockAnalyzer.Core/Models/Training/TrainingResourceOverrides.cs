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
    public const int MaximumEvaluationFolds = 10;
    public const int MaximumEnsembleMembers = 8;
    public const int MaximumFeatureLag = 512;
    public const int MaximumRunDurationMinutes = 120;
    public const int MiB = 1024 * 1024;
    public const int Float32Bytes = sizeof(float);

    public static bool ValidLags(System.Collections.Generic.IReadOnlyList<int>? lags, out string? error)
    {
        if (lags is null) { error = "Lags cannot be null; omit the property for no lags."; return false; }
        var seen = new System.Collections.Generic.HashSet<int>();
        foreach (int lag in lags)
        {
            if (lag < 1 || lag > MaximumFeatureLag || !seen.Add(lag))
            {
                error = $"Lags must be distinct bars in [1,{MaximumFeatureLag}], preserving input order.";
                return false;
            }
        }
        error = null;
        return true;
    }

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
