using System;
using StockAnalyzer.Avalonia.Common;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Common;

public class TabMoveResolverTests
{
    [Theory]
    [InlineData(0, 0, 1, 100, TabMoveOutcome.SourceOutOfRange, 0)]
    [InlineData(1, 0, 5, 100, TabMoveOutcome.NoChange, 0)]
    [InlineData(4, -1, 2, 100, TabMoveOutcome.SourceOutOfRange, -1)]
    [InlineData(4, 4, 0, 100, TabMoveOutcome.SourceOutOfRange, 4)]
    [InlineData(4, 1, 1, 100, TabMoveOutcome.NoChange, 1)]
    [InlineData(4, 0, 2, 100, TabMoveOutcome.Move, 2)]
    [InlineData(4, 3, 0, 100, TabMoveOutcome.Move, 0)]
    [InlineData(4, 0, 99, 100, TabMoveOutcome.Move, 3)]
    [InlineData(4, 2, -5, 100, TabMoveOutcome.Move, 0)]
    [InlineData(200, 0, 150, 100, TabMoveOutcome.Move, 100)]
    [InlineData(200, 150, 0, 100, TabMoveOutcome.Move, 50)]
    [InlineData(4, 0, 3, 2, TabMoveOutcome.Move, 2)]
    [InlineData(4, 3, 0, 2, TabMoveOutcome.Move, 1)]
    [InlineData(4, 1, 2, 0, TabMoveOutcome.NoChange, 1)]
    [InlineData(4, 2, 3, 1, TabMoveOutcome.Move, 3)]
    public void Resolve_ReturnsExpectedOutcomeAndTarget(int count, int source, int requested, int maxDistance, TabMoveOutcome expectedOutcome, int expectedTarget)
    {
        var outcome = TabMoveResolver.Resolve(count, source, requested, maxDistance, out int resolved);

        Assert.Equal(expectedOutcome, outcome);
        Assert.Equal(expectedTarget, resolved);
    }

    [Theory]
    [InlineData(0, 0, 1, 100, TabMoveOutcome.SourceOutOfRange, 0, TabMoveClamps.None)]
    [InlineData(4, 1, 1, 100, TabMoveOutcome.NoChange, 1, TabMoveClamps.None)]
    [InlineData(4, 0, 2, 100, TabMoveOutcome.Move, 2, TabMoveClamps.None)]
    [InlineData(4, 0, 99, 100, TabMoveOutcome.Move, 3, TabMoveClamps.LoweredToLast)]
    [InlineData(4, 2, -5, 100, TabMoveOutcome.Move, 0, TabMoveClamps.RaisedToFirst)]
    [InlineData(200, 0, 150, 100, TabMoveOutcome.Move, 100, TabMoveClamps.DistanceLimited)]
    [InlineData(200, 150, 0, 100, TabMoveOutcome.Move, 50, TabMoveClamps.DistanceLimited)]
    [InlineData(4, 0, 3, 2, TabMoveOutcome.Move, 2, TabMoveClamps.DistanceLimited)]
    [InlineData(4, 1, 1000, 100, TabMoveOutcome.Move, 3, TabMoveClamps.DistanceLimited | TabMoveClamps.LoweredToLast)]
    [InlineData(4, 2, -1000, 100, TabMoveOutcome.Move, 0, TabMoveClamps.DistanceLimited | TabMoveClamps.RaisedToFirst)]
    [InlineData(4, 3, 9, 100, TabMoveOutcome.NoChange, 3, TabMoveClamps.LoweredToLast)]
    [InlineData(4, 0, -1, 100, TabMoveOutcome.NoChange, 0, TabMoveClamps.RaisedToFirst)]
    public void Resolve_WithClamps_ReportsWhichClampStepsApplied(int count, int source, int requested, int maxDistance, TabMoveOutcome expectedOutcome, int expectedTarget, TabMoveClamps expectedClamps)
    {
        var outcome = TabMoveResolver.Resolve(count, source, requested, maxDistance, out int resolved, out var clamps);

        Assert.Equal(expectedOutcome, outcome);
        Assert.Equal(expectedTarget, resolved);
        Assert.Equal(expectedClamps, clamps);
    }

    [Fact]
    public void Resolve_NegativeMaxDistance_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TabMoveResolver.Resolve(4, 0, 1, -1, out _));
    }

    [Fact]
    public void Resolve_ExtremeRequestedTarget_DoesNotOverflow()
    {
        var outcome = TabMoveResolver.Resolve(4, 1, int.MaxValue, 100, out int resolved);
        Assert.Equal(TabMoveOutcome.Move, outcome);
        Assert.Equal(3, resolved);

        outcome = TabMoveResolver.Resolve(4, 2, int.MinValue, 100, out resolved);
        Assert.Equal(TabMoveOutcome.Move, outcome);
        Assert.Equal(0, resolved);
    }
}
