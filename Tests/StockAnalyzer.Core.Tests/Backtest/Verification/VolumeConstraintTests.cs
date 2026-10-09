using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// TASK-BT-01 verification: Volume constraint modes (RejectIfExceeds, ClampAndCancelRemainder),
/// zero-volume bar rejections, intrabar volume pool FIFO allocation, and configuration validation.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class VolumeConstraintTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData MakeBar(int dayOffset, decimal open, decimal high, decimal low, decimal close, long volume)
        => new(Bar0.AddDays(dayOffset), open, high, low, close, volume);

    private static BacktestInput MakeInput(ImmutableArray<CandleData> bars)
        => new(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, 0);

    private static StrategyOrderRequest Req(SignalType type, OrderType orderType = OrderType.Market)
        => new(type, orderType, null, null, Gtc, "test");

    [Fact]
    public void Configuration_ParticipationRatio_ValidationRulesEnforced()
    {
        var configValid = new BacktestConfiguration
        {
            InitialCapital = 1000m,
            SizingParameter = 1m,
            MaxVolumeParticipationRatio = 0.5m,
        };
        configValid.Validate(); // Does not throw

        Assert.Throws<ArgumentOutOfRangeException>(() => new BacktestConfiguration
        {
            InitialCapital = 1000m,
            SizingParameter = 1m,
            MaxVolumeParticipationRatio = 0.0m,
        });

        Assert.Throws<ArgumentOutOfRangeException>(() => new BacktestConfiguration
        {
            InitialCapital = 1000m,
            SizingParameter = 1m,
            MaxVolumeParticipationRatio = -0.1m,
        });

        Assert.Throws<ArgumentOutOfRangeException>(() => new BacktestConfiguration
        {
            InitialCapital = 1000m,
            SizingParameter = 1m,
            MaxVolumeParticipationRatio = 1.05m,
        });
    }

    [Fact]
    public void RejectIfExceeds_OrderQuantityExceedsVolumeCeiling_RejectsOrder()
    {
        // Bar volume = 1000, ratio = 0.1 => max participation = 100 shares.
        // Requested order qty = 150 shares.
        var bars = ImmutableArray.Create(
            MakeBar(0, 100m, 100m, 100m, 100m, 1000),
            MakeBar(1, 100m, 100m, 100m, 100m, 1000));

        var config = new BacktestConfiguration
        {
            InitialCapital = 100_000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 150m,
            MaxVolumeParticipationRatio = 0.1m,
            VolumeConstraintMode = VolumeConstraintMode.RejectIfExceeds,
        };

        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
        });

        BacktestResult result = VerificationHarness.CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Empty(result.Fills);
        Assert.Empty(result.Trades);
        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal(RejectedReason.InsufficientVolume, order.RejectedReason);
    }

    [Fact]
    public void ClampAndCancelRemainder_OrderQuantityExceedsVolumeCeiling_ClampsQuantityAndCancelsRemainder()
    {
        // Bar volume = 1000, ratio = 0.1 => ceiling = 100 shares.
        // Requested order qty = 250 shares.
        var bars = ImmutableArray.Create(
            MakeBar(0, 100m, 100m, 100m, 100m, 1000),
            MakeBar(1, 100m, 100m, 100m, 100m, 1000),
            MakeBar(2, 100m, 100m, 100m, 100m, 1000));

        var config = new BacktestConfiguration
        {
            InitialCapital = 100_000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 250m,
            MaxVolumeParticipationRatio = 0.1m,
            VolumeConstraintMode = VolumeConstraintMode.ClampAndCancelRemainder,
        };

        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
        });

        BacktestResult result = VerificationHarness.CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(100m, fill.Quantity); // Clamped to 100 shares
        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Filled, order.Status);
        // Position Q is exactly clamped quantity
        Assert.Equal(100m, result.EquityPoints[^1].MarketValue / 100m);
    }

    [Fact]
    public void ZeroVolumeBar_ImmediateRejection()
    {
        // Bar volume = 0 => available volume = 0.
        var bars = ImmutableArray.Create(
            MakeBar(0, 100m, 100m, 100m, 100m, 1000),
            MakeBar(1, 100m, 100m, 100m, 100m, 0)); // Day 1 volume is zero

        var config = new BacktestConfiguration
        {
            InitialCapital = 100_000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 50m,
            MaxVolumeParticipationRatio = 0.5m,
            VolumeConstraintMode = VolumeConstraintMode.ClampAndCancelRemainder,
        };

        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
        });

        BacktestResult result = VerificationHarness.CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Empty(result.Fills);
        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal(RejectedReason.InsufficientVolume, order.RejectedReason);
    }

    [Fact]
    public void IntrabarVolumePool_ExitConsumesFirst_RemainingAllocatedToEntry()
    {
        // Bar volume = 1500, ratio = 0.1 => total available volume = 150 shares.
        // Holding Long 100 shares.
        // On Bar 2, reversal: Exit Long (100 shares) + Entry Short (100 shares).
        // Exit consumes 100 shares first => remaining available = 50 shares.
        // In ClampAndCancelRemainder mode: Short entry requests 100, but only 50 available.
        // Short entry is clamped to 50 shares!
        var bars = ImmutableArray.Create(
            MakeBar(0, 100m, 100m, 100m, 100m, 5000),
            MakeBar(1, 100m, 100m, 100m, 100m, 5000),
            MakeBar(2, 100m, 100m, 100m, 100m, 1500),
            MakeBar(3, 100m, 100m, 100m, 100m, 5000));

        var config = new BacktestConfiguration
        {
            InitialCapital = 100_000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 100m,
            MaxVolumeParticipationRatio = 0.1m,
            VolumeConstraintMode = VolumeConstraintMode.ClampAndCancelRemainder,
        };

        // Custom reversal strategy
        var strategy = new ReversalTestStrategy();

        BacktestResult result = VerificationHarness.CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        // Fills:
        // Fill 0: Bar 1 Long Entry, 100 shares
        // Fill 1: Bar 2 Long Exit, 100 shares (consumes 100 out of 150 pool)
        // Fill 2: Bar 2 Short Entry, 50 shares (clamped from 100 to remaining 50)
        Assert.Equal(3, result.Fills.Length);
        Assert.Equal(100m, result.Fills[0].Quantity);
        Assert.Equal(100m, result.Fills[1].Quantity);
        Assert.Equal(50m, result.Fills[2].Quantity);
    }

    private sealed class ReversalTestStrategy : IBacktestStrategy
    {
        public string Name => "ReversalTestStrategy";
        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();

        public StrategyOrderRequest? Evaluate(StrategyContext context)
        {
            if (context.BarIndex == 0)
                return new StrategyOrderRequest(SignalType.LongEntry, OrderType.Market, null, null, Gtc, "enter long");
            if (context.BarIndex == 1)
                return new StrategyOrderRequest(SignalType.ShortEntry, OrderType.Market, null, null, Gtc, "reversal short entry");
            return null;
        }

        public StrategyOrderRequest? EvaluateExit(StrategyContext context)
        {
            if (context.BarIndex == 1)
                return new StrategyOrderRequest(SignalType.LongExit, OrderType.Market, null, null, Gtc, "reversal long exit");
            return null;
        }
    }
}
