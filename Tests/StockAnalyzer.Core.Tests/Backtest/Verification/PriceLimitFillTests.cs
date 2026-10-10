#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// Price limit (stop-high / stop-low / flat halt) fill rejection and carry-over tests for Task 6.
/// Verifies PriceLimitFillMode.AlwaysFill (default) vs PriceLimitFillMode.RejectIfAtLimitPrice.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class PriceLimitFillTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData Bar(int dayOffset, decimal open, decimal high, decimal low, decimal close, long volume = 10_000)
        => new(Bar0.AddDays(dayOffset), open, high, low, close, volume);

    private static BacktestInput MakeInput(ImmutableArray<CandleData> bars)
        => new(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, 0);

    private static StrategyOrderRequest Req(SignalType type, OrderType orderType = OrderType.Market, decimal? limitPrice = null, decimal? stopPrice = null)
        => new(type, orderType, limitPrice, stopPrice, Gtc, "test");

    private static BacktestConfiguration Config(PriceLimitFillMode mode)
        => new()
        {
            InitialCapital = 10_000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 10m,
            PriceLimitFillMode = mode,
        };

    [Fact]
    public void Configuration_PriceLimitFillMode_ValidatesDefinedEnum()
    {
        var configValid = Config(PriceLimitFillMode.RejectIfAtLimitPrice);
        configValid.Validate();

        var configInvalid = new BacktestConfiguration
        {
            InitialCapital = 10_000m,
            SizingParameter = 10m,
            PriceLimitFillMode = (PriceLimitFillMode)999,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => configInvalid.Validate());
    }

    [Fact]
    public void AlwaysFill_DefaultFillsEvenOnFlatBar()
    {
        // Order submitted at [0]. Bar 1 has High == Low.
        // Under AlwaysFill, order fills on bar 1 despite flat bar.
        var bars = ImmutableArray.Create(
            Bar(0, 100m, 102m, 98m, 100m),
            Bar(1, 100m, 100m, 100m, 100m),
            Bar(2, 102m, 105m, 100m, 104m)
        );

        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
        });

        BacktestResult result = VerificationHarness.Run(MakeInput(bars), Config(PriceLimitFillMode.AlwaysFill), strategy);

        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(1, fill.BarIndex);
        Assert.Equal(100m, fill.Price);
    }

    [Fact]
    public void FlatBar_HighEqualsLow_RejectIfAtLimitPrice_CarriesOrderOver()
    {
        // Order submitted at [0]. Bar 1 has High == Low (100).
        // Under RejectIfAtLimitPrice, order cannot fill on bar 1, carries over to bar 2.
        var bars = ImmutableArray.Create(
            Bar(0, 100m, 102m, 98m, 100m),
            Bar(1, 100m, 100m, 100m, 100m),
            Bar(2, 102m, 105m, 100m, 104m)
        );

        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
        });

        BacktestResult result = VerificationHarness.Run(MakeInput(bars), Config(PriceLimitFillMode.RejectIfAtLimitPrice), strategy);

        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(2, fill.BarIndex);
        Assert.Equal(102m, fill.Price);
    }

    [Fact]
    public void StopHighBar_CloseEqualsHigh_RejectIfAtLimitPrice_BlocksBuyAtLimit_CarriesOver()
    {
        // Order submitted at [0]. Buy Stop at 110.
        // Bar 1: Open 100, High 110, Low 100, Close 110. (Close == High == 110). Buy at 110 is blocked, carries over to bar 2.
        // Bar 2: Open 110, High 115, Low 108, Close 112. Fills on bar 2.
        var bars = ImmutableArray.Create(
            Bar(0, 100m, 102m, 98m, 100m),
            Bar(1, 100m, 110m, 100m, 110m),
            Bar(2, 110m, 115m, 108m, 112m)
        );

        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Stop, stopPrice: 110m),
        });

        BacktestResult result = VerificationHarness.Run(MakeInput(bars), Config(PriceLimitFillMode.RejectIfAtLimitPrice), strategy);

        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(2, fill.BarIndex);
    }

    [Fact]
    public void StopHighBar_CloseEqualsHigh_RejectIfAtLimitPrice_AllowsSell()
    {
        // Bar 0: Long entered at Close of [0]. Fills on bar 1 at 100.
        // At Close of bar 1, LongExit (Sell Limit at 110) is submitted.
        // Bar 2: Open 100, High 110, Low 100, Close 110 (Close == High).
        // Long exit (Sell Limit at 110) should execute on bar 2 because selling at stop-high is permitted.
        var bars = ImmutableArray.Create(
            Bar(0, 100m, 102m, 98m, 100m),
            Bar(1, 100m, 102m, 98m, 100m),
            Bar(2, 100m, 110m, 100m, 110m)
        );

        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
            [1] = Req(SignalType.LongExit, OrderType.Limit, limitPrice: 110m),
        });

        BacktestResult result = VerificationHarness.Run(MakeInput(bars), Config(PriceLimitFillMode.RejectIfAtLimitPrice), strategy);

        Assert.Equal(2, result.Fills.Length);
        Assert.Equal(1, result.Fills[0].BarIndex);
        Assert.Equal(2, result.Fills[1].BarIndex);
        Assert.Equal(OrderSide.Sell, result.Fills[1].Side);
        Assert.Equal(110m, result.Fills[1].Price);
    }

    [Fact]
    public void StopLowBar_CloseEqualsLow_RejectIfAtLimitPrice_BlocksSellAtLimit_CarriesOver()
    {
        // Bar 0: Long entered at Close of [0]. Fills on bar 1 at 100.
        // At Close of bar 1, LongExit (Sell Stop at 90) is submitted.
        // Bar 2: Open 100, High 100, Low 90, Close 90 (Close == Low). Stop touches 90, but selling at limit-low is blocked. Carries over.
        // Bar 3: Open 90, High 95, Low 88, Close 92. Fills on bar 3.
        var bars = ImmutableArray.Create(
            Bar(0, 100m, 102m, 98m, 100m),
            Bar(1, 100m, 102m, 98m, 100m),
            Bar(2, 100m, 100m, 90m, 90m),
            Bar(3, 90m, 95m, 88m, 92m)
        );

        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
            [1] = Req(SignalType.LongExit, OrderType.Stop, stopPrice: 90m),
        });

        BacktestResult result = VerificationHarness.Run(MakeInput(bars), Config(PriceLimitFillMode.RejectIfAtLimitPrice), strategy);

        Assert.Equal(2, result.Fills.Length);
        Assert.Equal(1, result.Fills[0].BarIndex);
        Assert.Equal(3, result.Fills[1].BarIndex); // Executed on bar 3, not bar 2!
    }

    [Fact]
    public void MarketOnClose_AtLimit_CarriesOverToNextBar()
    {
        // Bar 0: Buy MOC submitted at Close of [0].
        // Bar 1: Open 100, High 110, Low 100, Close 110 (Close == High). Buy MOC is blocked on bar 1, delayed to bar 2.
        // Bar 2: Open 110, High 115, Low 108, Close 112 (Close != High). Fills at Close of bar 2.
        var bars = ImmutableArray.Create(
            Bar(0, 100m, 102m, 98m, 100m),
            Bar(1, 100m, 110m, 100m, 110m),
            Bar(2, 110m, 115m, 108m, 112m)
        );

        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.MarketOnClose),
        });

        BacktestResult result = VerificationHarness.Run(MakeInput(bars), Config(PriceLimitFillMode.RejectIfAtLimitPrice), strategy);

        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(2, fill.BarIndex);
        Assert.Equal(112m, fill.Price);
    }
}
