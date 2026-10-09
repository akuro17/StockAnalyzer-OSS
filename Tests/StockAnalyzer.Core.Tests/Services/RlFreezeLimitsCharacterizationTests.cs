using System;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Pins the rollout-buffer inequality of <see cref="RlTrainingValidation.FreezeLimits"/> (continuous branch) before the byte formula
/// became a shared internal function used by the portfolio limits as well. Expected numbers are computed here by hand.
/// </summary>
public sealed class RlFreezeLimitsCharacterizationTests
{
    /// <summary>(512 rollout + 64 batch) rows of (observation + 2 action + 6 auxiliary) float32 values, plus two IPC frames.</summary>
    private static long ExpectedContinuousBytes()
    {
        long rows = 512 + 64;
        long observation = 64 * 5 + RlContract.AccountFieldCount;
        return rows * (observation * 4 + (2 + 6) * 4) + 2L * 1024 * 1024;
    }

    [Fact]
    public void ContinuousReplayBoundaryIsTheDocumentedInequality()
    {
        long bytes = ExpectedContinuousBytes();
        int fits = (int)((bytes + TrainingResourceOverrides.MiB - 1) / TrainingResourceOverrides.MiB);
        _ = RlTrainingValidation.FreezeLimits(new TrainingResourceOverrides(MaxTensorSizeMiB: fits), continuous: true);
        Assert.Throws<InvalidOperationException>(() => RlTrainingValidation.FreezeLimits(new TrainingResourceOverrides(MaxTensorSizeMiB: fits - 1), continuous: true));
    }

    [Fact]
    public void DiscreteReplayBoundaryIsUnchanged()
    {
        long rowBytes = RlContract.ObservationLength * 4L * 2 + 8 + 4 * 3L;
        long bytes = (RlContract.ReplayCapacity + RlContract.BatchSize) * rowBytes + RlContract.BatchSize * 8L + 2L * 1024 * 1024;
        int fits = (int)((bytes + TrainingResourceOverrides.MiB - 1) / TrainingResourceOverrides.MiB);
        _ = RlTrainingValidation.FreezeLimits(new TrainingResourceOverrides(MaxTensorSizeMiB: fits));
        Assert.Throws<InvalidOperationException>(() => RlTrainingValidation.FreezeLimits(new TrainingResourceOverrides(MaxTensorSizeMiB: fits - 1)));
    }
}
