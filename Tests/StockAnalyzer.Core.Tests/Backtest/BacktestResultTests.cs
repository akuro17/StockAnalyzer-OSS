using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class BacktestResultTests
{
    private static BacktestConfiguration ValidConfig() => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
    };

    [Fact]
    public void CreateEmpty_PreservesCallerConfiguration()
    {
        var config = ValidConfig();
        var result = BacktestResult.CreateEmpty(config);

        Assert.Same(config, result.Configuration);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Empty(result.Orders);
        Assert.Empty(result.Fills);
        Assert.Empty(result.Trades);
        Assert.Empty(result.EquityPoints);
        Assert.Empty(result.Signals);
        Assert.NotNull(result.ReproducibilityHash);
        Assert.Equal(32, result.ReproducibilityHash.Length); // SHA-256 = 32 bytes
        Assert.True(result.IsInsufficientData);
    }

    [Fact]
    public void Constructor_NormalRun_IsInsufficientDataIsFalse()
    {
        var result = new BacktestResult(
            ImmutableArray<BacktestOrder>.Empty,
            ImmutableArray<BacktestFill>.Empty,
            ImmutableArray<BacktestTrade>.Empty,
            ImmutableArray<EquityPoint>.Empty,
            ImmutableArray<BacktestSignal>.Empty,
            ValidConfig(),
            RunStatus.Completed,
            strategyName: "test",
            reproducibilityHash: new byte[32],
            isInsufficientData: false);

        Assert.False(result.IsInsufficientData);
    }

    [Fact]
    public void CreateEmpty_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BacktestResult.CreateEmpty(null!));
    }

    [Fact]
    public void Constructor_NullConfiguration_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BacktestResult(
            ImmutableArray<BacktestOrder>.Empty,
            ImmutableArray<BacktestFill>.Empty,
            ImmutableArray<BacktestTrade>.Empty,
            ImmutableArray<EquityPoint>.Empty,
            ImmutableArray<BacktestSignal>.Empty,
            configuration: null!,
            RunStatus.Completed,
            strategyName: "test",
            reproducibilityHash: new byte[32],
            isInsufficientData: false));
    }

    [Fact]
    public void Constructor_DefaultImmutableArrays_NormalizedToEmpty()
    {
        var result = new BacktestResult(
            default,
            default,
            default,
            default,
            default,
            ValidConfig(),
            RunStatus.Completed,
            strategyName: "test",
            reproducibilityHash: new byte[32],
            isInsufficientData: false);

        Assert.Empty(result.Orders);
        Assert.Empty(result.Fills);
        Assert.Empty(result.Trades);
        Assert.Empty(result.EquityPoints);
        Assert.Empty(result.Signals);
    }
}
