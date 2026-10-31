using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Services.Backtest.Optimization;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Optimization;

/// <summary>Plan 04 / T3, owner decisions D-04c = A and D-04f = A: the ratio of the two simple means, no threshold.</summary>
public sealed class WalkForwardEfficiencyTests
{
    private static WalkForwardEfficiencyResult Compute(params (decimal? InSample, decimal? OutOfSample)[] windows) => WalkForwardEfficiency.Compute(windows);

    [Fact]
    public void TheEfficiencyIsTheMeanOutOfSampleOverTheMeanInSample()
    {
        WalkForwardEfficiencyResult result = Compute((1.0m, 0.5m), (2.0m, 1.5m), (3.0m, 1.0m));

        Assert.Equal(2.0m, result.MeanInSampleBarSharpe);
        Assert.Equal(1.0m, result.MeanOutOfSampleBarSharpe);
        Assert.Equal(0.5m, result.Value);
        Assert.Null(result.Reason);
        Assert.Equal(3, result.WindowCount);
    }

    [Fact]
    public void AWindowWithOnlyOneValidSideIsNotUsedForEitherMean()
    {
        WalkForwardEfficiencyResult result = Compute((1.0m, 0.5m), (9.0m, null), (null, 9.0m), (3.0m, 1.5m));

        Assert.Equal(2, result.WindowCount);
        Assert.Equal(2.0m, result.MeanInSampleBarSharpe);
        Assert.Equal(1.0m, result.MeanOutOfSampleBarSharpe);
        Assert.Equal(0.5m, result.Value);
    }

    [Fact]
    public void NoQualifyingWindowGivesNullWithTheReason()
    {
        foreach (WalkForwardEfficiencyResult result in new[] { Compute(), Compute((null, null)), Compute((1m, null), (null, 1m)) })
        {
            Assert.Null(result.Value);
            Assert.Equal(WalkForwardEfficiency.NoQualifyingWindowReason, result.Reason);
            Assert.Equal(0, result.WindowCount);
            Assert.Null(result.MeanInSampleBarSharpe);
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.4)]
    public void ANonPositiveInSampleMeanGivesNullWithTheReasonButKeepsTheMeans(double inSample)
    {
        WalkForwardEfficiencyResult result = Compute(((decimal)inSample, 0.3m));

        Assert.Null(result.Value);
        Assert.Equal(WalkForwardEfficiency.NonPositiveInSampleMeanReason, result.Reason);
        Assert.Equal((decimal)inSample, result.MeanInSampleBarSharpe);
        Assert.Equal(0.3m, result.MeanOutOfSampleBarSharpe);
        Assert.Equal(1, result.WindowCount);
    }

    [Fact]
    public void ThereIsNoThresholdOnTheDenominator()
    {
        Assert.Equal(10_000_000m, Compute((0.0000001m, 1m)).Value);
    }

    [Fact]
    public void ANegativeOutOfSampleMeanGivesANegativeEfficiency()
    {
        Assert.Equal(-0.5m, Compute((2m, -1m)).Value);
    }

    [Fact]
    public void ANullSequenceIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => WalkForwardEfficiency.Compute((IEnumerable<(decimal?, decimal?)>)null!));
    }
}
