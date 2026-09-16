using System;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>
/// Cross-checks <see cref="WalkForwardDataRequirement.MinimumRawBars"/> against values obtained
/// by calling the real Python <c>dataset.walk_forward_split</c> directly (brute-forcing the
/// smallest raw bar count that yields a non-empty fold) - see
/// Y:\Temp\sa_step_log_OnnxTrainingFoundation.md, 2026-08-29, "Empty train or val split"
/// investigations, for the verification transcript. Do not "simplify" these expected values
/// without re-running that cross-check; the formula is a closed-form derivation of an integer
/// floor-division loop and is easy to get subtly wrong at small window/horizon values (see the
/// window=horizon=1 case below, which caught an earlier off-by-one in the derivation).
/// </summary>
public class WalkForwardDataRequirementTests
{
    [Theory]
    [InlineData(25, 5, 184)]
    [InlineData(75, 5, 534)] // TrainingWizardViewModel's actual default WindowSize/typical Horizon.
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
}
