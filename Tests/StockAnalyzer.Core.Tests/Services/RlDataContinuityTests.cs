using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlDataContinuityTests
{
    private static readonly DateTime Start = new(2010, 1, 4, 0, 0, 0, DateTimeKind.Utc);

    private static CandleData Bar(int dayOffset, decimal close)
        => new(Start.AddDays(dayOffset), close, close + 1m, close - 1m, close, 1_000);

    /// <summary>Weekday bars (a 3-day gap over each weekend) with the given closes.</summary>
    private static ImmutableArray<CandleData> Weekdays(params decimal[] closes)
    {
        var bars = ImmutableArray.CreateBuilder<CandleData>();
        int offset = 0;
        foreach (decimal close in closes)
        {
            while (Start.AddDays(offset).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) offset++;
            bars.Add(Bar(offset++, close));
        }
        return bars.ToImmutable();
    }

    [Fact]
    public void TheProfileIsTheLargestGapAndTheLargestAbsoluteCloseChangeWithTheDateOfTheLaterBar()
    {
        var bars = ImmutableArray.Create(Bar(0, 100m), Bar(1, 110m), Bar(21, 99m), Bar(22, 99m));
        var profile = RlDataContinuity.Measure(bars.AsSpan());
        Assert.Equal(20, profile.LargestGapDays);
        Assert.Equal(DateOnly.FromDateTime(Start.AddDays(21)), profile.GapDate);
        Assert.Equal(0.10m, profile.LargestChange);
        Assert.Equal(DateOnly.FromDateTime(Start.AddDays(1)), profile.ChangeDate);
        Assert.Equal(default, RlDataContinuity.Measure(ReadOnlySpan<CandleData>.Empty));
        Assert.Equal(0, RlDataContinuity.Measure(bars.AsSpan(0, 1)).LargestGapDays);
    }

    [Fact]
    public void ANormalHistoryAfterANormalHistoryGivesNoFinding()
    {
        var trained = RlDataContinuity.Measure(Weekdays(Enumerable.Range(0, 200).Select(i => 100m + (i % 7) - 3m).ToArray()).AsSpan());
        var shown = RlDataContinuity.Measure(Weekdays(Enumerable.Range(0, 100).Select(i => 120m + (i % 5) - 2m).ToArray()).AsSpan());
        Assert.Empty(RlDataContinuity.Find(trained, shown));
    }

    [Fact]
    public void AValueEqualToTheTrainedMaximumIsNotAFindingAndOnlyAStrictlyLargerValueIs()
    {
        var trained = RlDataContinuity.Measure(Weekdays(100m, 110m, 100m).AsSpan());
        Assert.Empty(RlDataContinuity.Find(trained, trained));
        var larger = RlDataContinuity.Measure(Weekdays(100m, 111m).AsSpan());
        var finding = Assert.Single(RlDataContinuity.Find(trained, larger));
        Assert.Equal(RlContinuityKind.CloseChange, finding.Kind);
        Assert.Equal(0.11m, finding.Observed);
        Assert.Equal(trained.LargestChange, finding.TrainedMaximum);
    }

    [Fact]
    public void ATwoToOneSplitJumpAndATwentyDayGapAreEachReportedWithTheirDatesAndTheTrainedMaximum()
    {
        var trained = RlDataContinuity.Measure(Weekdays(Enumerable.Range(0, 100).Select(i => 100m + (i % 3)).ToArray()).AsSpan());
        var split = ImmutableArray.Create(Bar(0, 100m), Bar(1, 101m), Bar(2, 50.5m), Bar(3, 51m));
        var jump = Assert.Single(RlDataContinuity.Find(trained, RlDataContinuity.Measure(split.AsSpan())));
        Assert.Equal((RlContinuityKind.CloseChange, 0.5m, DateOnly.FromDateTime(Start.AddDays(2))), (jump.Kind, jump.Observed, jump.Date));
        Assert.True(jump.TrainedMaximum < 0.05m);

        var gap = ImmutableArray.Create(Bar(0, 100m), Bar(1, 100m), Bar(21, 100m));
        var missing = Assert.Single(RlDataContinuity.Find(trained, RlDataContinuity.Measure(gap.AsSpan())));
        Assert.Equal((RlContinuityKind.CalendarGap, 20m, 3m, DateOnly.FromDateTime(Start.AddDays(21))), (missing.Kind, missing.Observed, missing.TrainedMaximum, missing.Date));
    }

    [Fact]
    public void ADropFromANonPositivePreviousCloseContributesNoChange()
    {
        var bars = ImmutableArray.Create(Bar(0, 0m), Bar(1, 100m));
        Assert.Equal(0m, RlDataContinuity.Measure(bars.AsSpan()).LargestChange);
    }
}
