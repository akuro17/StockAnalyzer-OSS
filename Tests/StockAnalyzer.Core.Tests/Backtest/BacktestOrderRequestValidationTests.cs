#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Fix T1 (the P1 correctness plan, finding R8): a malformed strategy request
/// is diagnosed at the bar it is submitted on - including the last bar, where it never reaches a fill evaluation.
/// </summary>
public class BacktestOrderRequestValidationTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData FlatBar(int dayOffset, decimal price) => new(Bar0.AddDays(dayOffset), price, price, price, price, 1000);

    private static ImmutableArray<CandleData> FlatBars(int count)
    {
        var builder = ImmutableArray.CreateBuilder<CandleData>(count);
        for (int i = 0; i < count; i++) builder.Add(FlatBar(i, 100m));
        return builder.MoveToImmutable();
    }

    private static BacktestConfiguration MakeConfig() => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
    };

    private static BacktestResult Run(int barCount, Dictionary<int, StrategyOrderRequest> script)
        => VerificationHarness.CreateEngine().Run(
            new BacktestInput(FlatBars(barCount), "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(barCount + 1), 0, 0),
            MakeConfig(), new ScriptedStrategy(script));

    private static StrategyOrderRequest Req(SignalType type, OrderType orderType, decimal? limitPrice = null, decimal? stopPrice = null, TimeInForce? tif = null)
        => new(type, orderType, limitPrice, stopPrice, tif ?? Gtc, "r");

    public static IEnumerable<object?[]> MalformedRequests() => new[]
    {
        new object?[] { OrderType.Limit, null, null, nameof(StrategyOrderRequest.LimitPrice) },
        new object?[] { OrderType.Stop, null, null, nameof(StrategyOrderRequest.StopPrice) },
        new object?[] { OrderType.StopLimit, null, 95m, nameof(StrategyOrderRequest.LimitPrice) },
        new object?[] { OrderType.StopLimit, 95m, null, nameof(StrategyOrderRequest.StopPrice) },
        new object?[] { OrderType.MarketOnClose, 100m, null, nameof(StrategyOrderRequest.LimitPrice) },
        new object?[] { OrderType.MarketOnClose, null, 100m, nameof(StrategyOrderRequest.StopPrice) },
    };

    [Theory]
    [MemberData(nameof(MalformedRequests))]
    public void MalformedRequest_ThrowsAtSubmittedBar_NamingBarAndField(OrderType orderType, decimal? limitPrice, decimal? stopPrice, string expectedField)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => Run(3, new Dictionary<int, StrategyOrderRequest>
        {
            [1] = Req(SignalType.LongEntry, orderType, limitPrice, stopPrice),
        }));

        Assert.Equal(expectedField, ex.ParamName);
        Assert.Contains("bar 1", ex.Message);
        Assert.Contains(expectedField, ex.Message);
    }

    [Fact]
    public void InvalidMarketOnClose_SubmittedOnTheLastBar_IsNoLongerSilentlyDropped()
    {
        Assert.Throws<ArgumentException>(() => Run(3, new Dictionary<int, StrategyOrderRequest>
        {
            [2] = Req(SignalType.LongEntry, OrderType.MarketOnClose, limitPrice: 100m),
        }));
    }

    [Fact]
    public void GtdExpiryBeforeEarliestFillBar_Throws_ButEqualToEarliestFillBar_IsAccepted()
    {
        // Submitted at bar 1 => earliest fill bar 2.
        ArgumentException ex = Assert.Throws<ArgumentException>(() => Run(4, new Dictionary<int, StrategyOrderRequest>
        {
            [1] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 95m, tif: new TimeInForce(false, 1)),
        }));
        Assert.Equal(nameof(StrategyOrderRequest.TimeInForce), ex.ParamName);
        Assert.Contains("bar 1", ex.Message);

        BacktestResult accepted = Run(4, new Dictionary<int, StrategyOrderRequest>
        {
            [1] = Req(SignalType.LongEntry, OrderType.Market, tif: new TimeInForce(false, 2)),
        });
        Assert.Equal(OrderStatus.Filled, Assert.Single(accepted.Orders).Status);
    }

    [Fact]
    public void GtcOrder_UnusedExpiryBar_IsNeverChecked()
    {
        BacktestResult result = Run(3, new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market, tif: new TimeInForce(true, 0)),
        });

        Assert.Equal(OrderStatus.Filled, Assert.Single(result.Orders).Status);
    }

    [Fact]
    public void ExtraneousPrices_OnOrderTypesThatIgnoreThem_StayAccepted()
    {
        BacktestResult result = Run(3, new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market, limitPrice: 1m, stopPrice: 2m),
        });

        Assert.Equal(OrderStatus.Filled, Assert.Single(result.Orders).Status);
    }

    [Fact]
    public void SignalDiscardedBecauseItsSlotIsOccupied_IsNotValidated()
    {
        // bar0 Limit far below never fills and keeps the Entry slot occupied; bar1's malformed MOC Entry is discarded as before.
        BacktestResult result = Run(3, new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 50m),
            [1] = Req(SignalType.LongEntry, OrderType.MarketOnClose, limitPrice: 100m),
        });

        Assert.Single(result.Orders);
        Assert.Equal(2, result.Signals.Length);
    }
}
