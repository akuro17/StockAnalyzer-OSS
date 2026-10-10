using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class ShortBorrowFeeTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData MakeBar(int dayOffset, decimal open, decimal high, decimal low, decimal close)
    {
        return new CandleData(
            Timestamp: BaseTime.AddDays(dayOffset),
            Open: open,
            High: high,
            Low: low,
            Close: close,
            Volume: 10_000);
    }

    private static CandleData FlatBar(int dayOffset, decimal price) => MakeBar(dayOffset, price, price, price, price);

    private sealed class ScriptedStrategy : IBacktestStrategy
    {
        private readonly Dictionary<int, StrategyOrderRequest> _script;
        public string Name => "Scripted";
        public ScriptedStrategy(Dictionary<int, StrategyOrderRequest> script) => _script = script;
        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();
        public StrategyOrderRequest? Evaluate(StrategyContext context)
            => _script.TryGetValue(context.BarIndex, out StrategyOrderRequest req) ? req : null;
    }

    [Fact]
    public void ShortBorrowAnnualRate_Validation_EnforcesBounds()
    {
        var configDefault = new BacktestConfiguration();
        Assert.Equal(0m, configDefault.ShortBorrowAnnualRate);

        Assert.Throws<ArgumentOutOfRangeException>(() => new BacktestConfiguration { ShortBorrowAnnualRate = -0.01m });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BacktestConfiguration { ShortBorrowAnnualRate = 1.0001m });

        var configValid = new BacktestConfiguration { ShortBorrowAnnualRate = 0.05m };
        Assert.Equal(0.05m, configValid.ShortBorrowAnnualRate);
    }

    [Fact]
    public void OvernightShortPosition_IncursExactDailyBorrowFee_AndSatisfiesLedgerInvariants()
    {
        // 6 daily flat bars at $100
        var bars = ImmutableArray.Create(
            FlatBar(0, 100m),
            FlatBar(1, 100m),
            FlatBar(2, 100m),
            FlatBar(3, 100m),
            FlatBar(4, 100m),
            FlatBar(5, 100m));

        var input = new BacktestInput(
            bars,
            "TEST",
            TimeFrame.D1,
            BacktestInput.CurrentDataVersion,
            bars[0].Timestamp,
            bars[^1].Timestamp.AddDays(1),
            historyStartIndex: 0,
            tradingStartIndex: 0);

        decimal annualRate = 0.073m; // 7.3% per year -> exactly 0.073 / 365 = 0.0002 (0.02%) per day
        var config = new BacktestConfiguration
        {
            InitialCapital = 10_000m,
            CommissionFlat = 0m,
            CommissionPerUnit = 0m,
            SlippageRatio = 0m,
            InitialMarginRatio = 0.5m,
            MaintenanceMarginRatio = 0.25m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 10m, // 10 shares @ $100 = $1,000 position value ($500 initial margin)
            ShortBorrowAnnualRate = annualRate,
        };

        // Bar 0: ShortEntry Market order submitted at Step 7 -> fills at bar 1 Open ($100).
        // Bar 1: Position opened at Open (EntryBar = 1). At Close Step 5, EntryBar == i, so no borrow fee for bar 1. Cash = 9,500.
        // Bar 2: Held overnight (EntryBar = 1 < 2). Step 5 charges $0.20 borrow fee. Cash = 9,499.80.
        // Bar 3: Held overnight (EntryBar = 1 < 3). Step 5 charges $0.20 borrow fee. Cash = 9,499.60.
        // Bar 3: ShortExit Market order submitted at Step 7 -> fills at bar 4 Open ($100).
        // Bar 4: Closed at Open. At Close Step 5, Q == 0. Margin released. Cash = 9,499.60 + 500 = 9,999.60.
        // Bar 5: Flat. Cash = 9,999.60.
        var script = new Dictionary<int, StrategyOrderRequest>
        {
            [0] = new(SignalType.ShortEntry, OrderType.Market, null, null, Gtc, "entry"),
            [3] = new(SignalType.ShortExit, OrderType.Market, null, null, Gtc, "exit"),
        };

        var strategy = new ScriptedStrategy(script);
        var engine = new BacktestEngine(new IndicatorFactory());
        BacktestResult result = engine.Run(input, config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Single(result.Trades);

        Assert.Equal(6, result.EquityPoints.Length);
        Assert.Equal(10000m, result.EquityPoints[0].Cash);
        Assert.Equal(9500m, result.EquityPoints[1].Cash);
        Assert.Equal(9499.80m, result.EquityPoints[2].Cash);
        Assert.Equal(9499.60m, result.EquityPoints[3].Cash);
        Assert.Equal(9999.60m, result.EquityPoints[4].Cash);
        Assert.Equal(9999.60m, result.EquityPoints[5].Cash);

        // Verify all ledger invariants (PnL identity, equity matching ledger)
        LedgerAssertions.AllInvariants(input, config, result);
    }

    [Fact]
    public void IntradayShortPosition_IncursZeroBorrowFee()
    {
        // 3 daily bars: bar 0 flat, bar 1 spikes to trigger forced liquidation on entry bar, bar 2 flat
        var bars = ImmutableArray.Create(
            FlatBar(0, 100m),
            MakeBar(1, open: 100m, high: 130m, low: 100m, close: 100m),
            FlatBar(2, 100m));

        var input = new BacktestInput(
            bars,
            "TEST",
            TimeFrame.D1,
            BacktestInput.CurrentDataVersion,
            bars[0].Timestamp,
            bars[^1].Timestamp.AddDays(1),
            historyStartIndex: 0,
            tradingStartIndex: 0);

        var config = new BacktestConfiguration
        {
            InitialCapital = 1000m,
            CommissionFlat = 0m,
            CommissionPerUnit = 0m,
            SlippageRatio = 0m,
            InitialMarginRatio = 0.30m,
            MaintenanceMarginRatio = 0.20m,
            LiquidationPenaltyRatio = 0m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 20m, // 20 shares @ 100 = 2000
            ShortBorrowAnnualRate = 0.50m, // 50% high borrow rate
        };

        var script = new Dictionary<int, StrategyOrderRequest>
        {
            [0] = new(SignalType.ShortEntry, OrderType.Market, null, null, Gtc, "entry"), // fills bar 1 Open
        };

        var strategy = new ScriptedStrategy(script);
        var engine = new BacktestEngine(new IndicatorFactory());
        BacktestResult result = engine.Run(input, config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.True(trade.IsForcedLiquidation);
        Assert.Equal(1, trade.EntryBar);
        Assert.Equal(1, trade.ExitBar);
        Assert.Equal(0, trade.HoldingBars);

        // Verify that on the entry bar, borrow fee was 0!
        LedgerAssertions.AllInvariants(input, config, result);
    }

    [Fact]
    public void LongPosition_WithShortBorrowRate_IncursZeroBorrowFee()
    {
        // 5 daily bars
        var bars = ImmutableArray.Create(
            FlatBar(0, 100m),
            FlatBar(1, 100m),
            FlatBar(2, 100m),
            FlatBar(3, 100m),
            FlatBar(4, 100m));

        var input = new BacktestInput(
            bars,
            "TEST",
            TimeFrame.D1,
            BacktestInput.CurrentDataVersion,
            bars[0].Timestamp,
            bars[^1].Timestamp.AddDays(1),
            historyStartIndex: 0,
            tradingStartIndex: 0);

        var config = new BacktestConfiguration
        {
            InitialCapital = 10_000m,
            CommissionFlat = 0m,
            CommissionPerUnit = 0m,
            SlippageRatio = 0m,
            InitialMarginRatio = 0.5m,
            MaintenanceMarginRatio = 0.25m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 10m,
            ShortBorrowAnnualRate = 0.20m, // 20% borrow fee
        };

        // Long position: fills at bar 1 Open, exits at bar 3 Open
        var script = new Dictionary<int, StrategyOrderRequest>
        {
            [0] = new(SignalType.LongEntry, OrderType.Market, null, null, Gtc, "entry"),
            [2] = new(SignalType.LongExit, OrderType.Market, null, null, Gtc, "exit"),
        };

        var strategy = new ScriptedStrategy(script);
        var engine = new BacktestEngine(new IndicatorFactory());
        BacktestResult result = engine.Run(input, config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Single(result.Trades);

        // Long position: No borrow fee at all! Final cash == initial capital
        Assert.Equal(config.InitialCapital, result.EquityPoints[3].Cash);
        Assert.Equal(config.InitialCapital, result.EquityPoints[4].Cash);
        LedgerAssertions.AllInvariants(input, config, result);
    }

    [Fact]
    public void BorrowFeeBasisMode_Validation_EnforcesDefinedEnum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BacktestConfiguration
        {
            InitialCapital = 10_000m,
            SizingParameter = 1m,
            BorrowFeeBasisMode = (BorrowFeeBasisMode)999,
        }.Validate());
    }

    [Fact]
    public void ShortBorrowFee_MarkToMarketDaily_CalculatesFeesAgainstDailyClosingPrices()
    {
        // 5 daily bars with rising price for a Short position
        var bars = ImmutableArray.Create(
            FlatBar(0, 100m),
            FlatBar(1, 100m),
            FlatBar(2, 150m),
            FlatBar(3, 200m),
            FlatBar(4, 200m));

        var input = new BacktestInput(
            bars,
            "TEST",
            TimeFrame.D1,
            BacktestInput.CurrentDataVersion,
            bars[0].Timestamp,
            bars[^1].Timestamp.AddDays(1),
            historyStartIndex: 0,
            tradingStartIndex: 0);

        decimal annualRate = 0.073m; // 7.3% per year -> exactly 0.0002 (0.02%) per day

        var script = new Dictionary<int, StrategyOrderRequest>
        {
            [0] = new(SignalType.ShortEntry, OrderType.Market, null, null, Gtc, "entry"),
            [3] = new(SignalType.ShortExit, OrderType.Market, null, null, Gtc, "exit"),
        };

        var configFixed = new BacktestConfiguration
        {
            InitialCapital = 10_000m,
            CommissionFlat = 0m,
            CommissionPerUnit = 0m,
            SlippageRatio = 0m,
            InitialMarginRatio = 0.5m,
            MaintenanceMarginRatio = 0.25m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 10m,
            ShortBorrowAnnualRate = annualRate,
            BorrowFeeBasisMode = BorrowFeeBasisMode.EntryPriceFixed,
        };

        var configMtm = new BacktestConfiguration
        {
            InitialCapital = 10_000m,
            CommissionFlat = 0m,
            CommissionPerUnit = 0m,
            SlippageRatio = 0m,
            InitialMarginRatio = 0.5m,
            MaintenanceMarginRatio = 0.25m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 10m,
            ShortBorrowAnnualRate = annualRate,
            BorrowFeeBasisMode = BorrowFeeBasisMode.MarkToMarketDaily,
        };

        var engine = new BacktestEngine(new IndicatorFactory());
        BacktestResult resultFixed = engine.Run(input, configFixed, new ScriptedStrategy(script));
        BacktestResult resultMtm = engine.Run(input, configMtm, new ScriptedStrategy(script));

        Assert.Equal(RunStatus.Completed, resultFixed.Status);
        Assert.Equal(RunStatus.Completed, resultMtm.Status);

        // Fixed entry price ($100): Bar 2 fee = 10 * 100 * 0.0002 = 0.20, Bar 3 fee = 10 * 100 * 0.0002 = 0.20 -> Total = 0.40
        // MarkToMarketDaily: Bar 2 fee = 10 * 150 * 0.0002 = 0.30, Bar 3 fee = 10 * 200 * 0.0002 = 0.40 -> Total = 0.70
        // Initial capital 10,000, Short 10 @ 100 closed @ 200 -> Loss = -1,000
        // Fixed: 10,000 - 1,000 - 0.40 = 8,999.60
        // MtM:   10,000 - 1,000 - 0.70 = 8,999.30
        Assert.Equal(8999.60m, resultFixed.EquityPoints[^1].Cash);
        Assert.Equal(8999.30m, resultMtm.EquityPoints[^1].Cash);

        LedgerAssertions.AllInvariants(input, configFixed, resultFixed);
        LedgerAssertions.AllInvariants(input, configMtm, resultMtm);
    }
}
