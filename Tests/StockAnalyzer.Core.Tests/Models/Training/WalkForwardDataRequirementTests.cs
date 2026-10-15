using System;
using System.IO;
using System.Text.Json;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>
/// Cross-checks <see cref="WalkForwardDataRequirement.MinimumRawBars"/> against values obtained
/// by calling the real Python <c>dataset.walk_forward_split</c> directly (brute-forcing the
/// smallest raw bar count that yields a non-empty fold) - see
/// docs/sa_step_log_OnnxTrainingFoundation, 2026-08-29, "Empty train or val split"
/// investigations, for the verification transcript. Do not "simplify" these expected values
/// without re-running that cross-check; the formula is a closed-form derivation of an integer
/// floor-division loop and is easy to get subtly wrong at small window/horizon values (see the
/// window=horizon=1 case below, which caught an earlier off-by-one in the derivation).
/// </summary>
public class WalkForwardDataRequirementTests
{
    [Theory]
    [InlineData(25, 5, 184)]
    [InlineData(75, 5, 534)] // The former default WindowSize with the typical Horizon (documented example).
    [InlineData(60, 10, 464)]
    [InlineData(10, 1, 51)]
    [InlineData(1, 1, 8)] // Smallest possible window/horizon; caught an earlier derivation bug (fold_size == 0 guard).
    [InlineData(30, 30, 394)]
    [InlineData(5, 5, 44)]
    [InlineData(50, 20, 464)]
    [InlineData(20, 5, 149)]
    [InlineData(100, 1, 681)]
    [InlineData(1, 100, 681)] // Symmetric in window/horizon (gap = window + horizon - 1).
    [InlineData(150, 25, 1199)]
    public void MinimumRawBars_MatchesRealWalkForwardSplitBruteForce(int windowSize, int horizon, int expected)
    {
        Assert.Equal(expected, WalkForwardDataRequirement.MinimumRawBars(windowSize, horizon));
    }

    private static JsonDocument RequirementParity()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "Tests", "TrainingRequirementParity.json")));
    }

    [Fact]
    public void MinimumRawBarsForTraining_MatchesTheRealPythonFoldValidation()
    {
        // Tests/TrainingRequirementParity.json is written by generate_training_requirement_parity.py, which calls the real
        // dataset.independent_fold_data for every outer fold (inner split, optional calibration carve) and every bar count.
        using var parity = RequirementParity();
        var cases = parity.RootElement.GetProperty("cases");
        Assert.True(cases.GetArrayLength() >= 10);
        foreach (var item in cases.EnumerateArray())
        {
            int? gap = item.GetProperty("gap").ValueKind == JsonValueKind.Null ? null : item.GetProperty("gap").GetInt32();
            var actual = WalkForwardDataRequirement.MinimumRawBarsForTraining(
                item.GetProperty("window").GetInt32(), item.GetProperty("horizon").GetInt32(), item.GetProperty("splits").GetInt32(),
                gap, item.GetProperty("max_lag").GetInt32(), item.GetProperty("calibration").GetBoolean());
            Assert.True(item.GetProperty("minimum_raw_bars").GetInt32() == actual, $"{item}: C# {actual}");
        }
    }

    [Theory]
    [InlineData(75, 5)]
    [InlineData(20, 5)]
    [InlineData(1, 1)]
    public void MinimumRawBarsForTraining_IsNeverBelowTheLastFoldRequirement_AndGrowsWithCalibration(int window, int horizon)
    {
        var lastFold = WalkForwardDataRequirement.MinimumRawBars(window, horizon);
        var whole = WalkForwardDataRequirement.MinimumRawBarsForTraining(window, horizon);
        Assert.True(whole >= lastFold, $"{whole} < {lastFold}");
        Assert.True(WalkForwardDataRequirement.MinimumRawBarsForTraining(window, horizon, calibrationSegment: true) >= whole);
    }

    [Fact]
    public void MinimumRawBarsForTraining_TheDefaultMonthlyCaseTheWizardReported_NeedsMoreThan551Bars()
    {
        // AAPL monthly history = 551 bars; window 75, horizon 5, 5 splits stopped with "no usable inner validation fold".
        Assert.True(WalkForwardDataRequirement.MinimumRawBarsForTraining(75, 5) > 551);
        Assert.True(WalkForwardDataRequirement.MinimumRawBars(75, 5) < 551); // the earlier hint (534) understated it
    }

    [Theory]
    [InlineData(0, 5, 5, 0)]
    [InlineData(5, 0, 5, 0)]
    [InlineData(5, 5, 0, 0)]
    [InlineData(5, 5, 11, 0)] // above TrainingResourceOverrides.MaximumEvaluationFolds
    [InlineData(5, 5, 5, -1)]
    public void MinimumRawBarsForTraining_InvalidInput_Throws(int window, int horizon, int splits, int lag)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WalkForwardDataRequirement.MinimumRawBarsForTraining(window, horizon, splits, null, lag));
    }

    [Fact]
    public void MinimumRawBarsForTraining_OverLimitInput_ThrowsOverflowQuicklyInsteadOfSearching()
    {
        Assert.Throws<OverflowException>(() => WalkForwardDataRequirement.MinimumRawBarsForTraining(25, 5, gap: int.MaxValue));
        Assert.Throws<OverflowException>(() => WalkForwardDataRequirement.MinimumRawBarsForTraining(int.MaxValue, int.MaxValue));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(5, 0)]
    [InlineData(5, -1)]
    public void MinimumRawBars_NonPositiveWindowOrHorizon_Throws(int windowSize, int horizon)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WalkForwardDataRequirement.MinimumRawBars(windowSize, horizon));
    }

    [Theory]
    [InlineData(25, 5)]
    [InlineData(75, 5)]
    [InlineData(60, 10)]
    [InlineData(10, 1)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    public void MinimumRawBars_ExplicitDefaultGap_MatchesImplicitDefault(int windowSize, int horizon)
    {
        // Passing gap = window + horizon - 1 explicitly must be identical to omitting it: the
        // closed form always uses that value for the raw-vs-windowed offset; the gap argument
        // only feeds fold formation.
        Assert.Equal(
            WalkForwardDataRequirement.MinimumRawBars(windowSize, horizon),
            WalkForwardDataRequirement.MinimumRawBars(
                windowSize, horizon, WalkForwardDataRequirement.DefaultSplitCount, gap: windowSize + horizon - 1));
    }

    [Fact]
    public void MinimumRawBars_SmallerGapNeedsFewerBars_LargerGapNeedsMoreBars()
    {
        const int window = 25;
        const int horizon = 5;
        var atDefaultGap = WalkForwardDataRequirement.MinimumRawBars(window, horizon); // gap = 29

        var atZeroGap = WalkForwardDataRequirement.MinimumRawBars(
            window, horizon, WalkForwardDataRequirement.DefaultSplitCount, gap: 0);
        var atLargeGap = WalkForwardDataRequirement.MinimumRawBars(
            window, horizon, WalkForwardDataRequirement.DefaultSplitCount, gap: 100);

        Assert.True(atZeroGap < atDefaultGap, $"gap=0 ({atZeroGap}) should need fewer bars than the default ({atDefaultGap}).");
        Assert.True(atLargeGap > atDefaultGap, $"gap=100 ({atLargeGap}) should need more bars than the default ({atDefaultGap}).");
    }

    [Fact]
    public void MinimumRawBars_ZeroGap_IsAccepted()
    {
        Assert.True(WalkForwardDataRequirement.MinimumRawBars(25, 5, gap: 0) > 0);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-30)]
    public void MinimumRawBars_NegativeGap_Throws(int gap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WalkForwardDataRequirement.MinimumRawBars(25, 5, WalkForwardDataRequirement.DefaultSplitCount, gap));
    }

    [Fact]
    public void MinimumRawBars_ExplicitMaxLagAddsRequiredHistoryWithoutChangingGap()
    {
        int baseline = WalkForwardDataRequirement.MinimumRawBars(25, 5, gap: 0);
        Assert.Equal(baseline + 20,
            WalkForwardDataRequirement.MinimumRawBars(25, 5, gap: 0, maxLag: 20));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WalkForwardDataRequirement.MinimumRawBars(25, 5, maxLag: -1));
    }

    [Fact]
    public void MinimumRawBars_ExactIntLimitIsReturned_AndLagBeyondItIsRejected()
    {
        var window = int.MaxValue - (WalkForwardDataRequirement.DefaultSplitCount + 1);
        Assert.Equal(int.MaxValue, WalkForwardDataRequirement.MinimumRawBars(window, 1, gap: 0));
        Assert.Throws<OverflowException>(() =>
            WalkForwardDataRequirement.MinimumRawBars(window, 1, gap: 0, maxLag: 1));
    }

    [Theory]
    [InlineData(25, 5, 5, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue, 5, 0)]
    [InlineData(1, 1, int.MaxValue, 0)]
    public void MinimumRawBars_OverLimitIntermediatesNeverWrapOrIterateThroughAllSplits(
        int window, int horizon, int splits, int gap)
    {
        Assert.Throws<OverflowException>(() =>
            WalkForwardDataRequirement.MinimumRawBars(window, horizon, splits, gap));
    }
}
