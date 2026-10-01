using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// Exposure population and same-bar round trips (Y:\Temp\sa_implementation_plan_BacktestExposureSemantics.md, T5 + T6):
/// the denominator starts at max(HistoryStartIndex, TradingStartIndex), and a trade opened and closed on the same bar counts as one
/// in-market bar even though its Close snapshot is flat.
/// </summary>
public class ExposureSemanticsTests
{
    private static readonly ImmutableArray<BacktestTrade> NoTrades = ImmutableArray<BacktestTrade>.Empty;

    private static EquityPoint Point(int index, bool held) => new(
        index, ReportTestHelpers.BaseUtc.AddDays(index), 100m, 100m, MarketValue: held ? 50m : 0m, HeldMargin: 0m);

    private static ImmutableArray<EquityPoint> Points(params bool[] held) =>
        held.Select((flag, index) => Point(index, flag)).ToImmutableArray();

    private static BacktestTrade SameBarTrade(int bar) => ReportTestHelpers.Trade(1m, entryBar: bar, exitBar: bar);

    // ---- T5: options ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Options_TradingStartDefaultsToHistoryStart()
    {
        BacktestReportOptions options = ReportTestHelpers.Options(historyStartIndex: 3);

        Assert.Null(options.TradingStartIndex);
        Assert.Equal(3, options.EffectiveTradingStartIndex);
    }

    [Fact]
    public void Options_TradingStartBeforeHistoryStart_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReportTestHelpers.Options(historyStartIndex: 3, tradingStartIndex: 2));
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(3, 8)]
    public void Options_TradingStartAtOrAfterHistoryStart_IsAccepted(int history, int trading)
    {
        Assert.Equal(trading, ReportTestHelpers.Options(historyStartIndex: history, tradingStartIndex: trading).EffectiveTradingStartIndex);
    }

    // ---- T5: denominator --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Exposure_WarmupBarsBetweenHistoryAndTradingStart_DoNotDiluteTheDenominator()
    {
        // 2 warm-up bars (History 0 .. Trading 2), then 4 bars all held: Exposure is exactly 1, not 4/6.
        ImmutableArray<EquityPoint> points = Points(false, false, true, true, true, true);

        var (timeInMarket, exposure) = ExposureMetricsCalculator.Compute(points, NoTrades, historyStartIndex: 0, tradingStartIndex: 2);

        Assert.Equal(4m, timeInMarket.Value);
        Assert.Equal(1m, exposure.Value);
    }

    [Fact]
    public void Exposure_WhenTradingStartEqualsHistoryStart_IsUnchangedFromTheBarCount()
    {
        ImmutableArray<EquityPoint> points = Points(false, false, true, true, true, true);

        var (timeInMarket, exposure) = ExposureMetricsCalculator.Compute(points, NoTrades, historyStartIndex: 0, tradingStartIndex: 0);

        Assert.Equal(4m, timeInMarket.Value);
        Assert.Equal(4m / 6m, exposure.Value);
    }

    [Fact]
    public void Exposure_ChangingOnlyHistoryStart_LeavesItUnchangedWhenTradingStartIsFixed()
    {
        ImmutableArray<EquityPoint> points = Points(false, true, false, true, true, false);

        var lowHistory = ExposureMetricsCalculator.Compute(points, NoTrades, historyStartIndex: 0, tradingStartIndex: 3);
        var highHistory = ExposureMetricsCalculator.Compute(points, NoTrades, historyStartIndex: 2, tradingStartIndex: 3);

        Assert.Equal(lowHistory, highHistory);
        Assert.Equal(2m, lowHistory.TimeInMarket.Value);
        Assert.Equal(2m / 3m, lowHistory.Exposure.Value);
    }

    [Fact]
    public void Exposure_TradingStartAtOrBeyondTheLastBar_IsInsufficientData()
    {
        ImmutableArray<EquityPoint> points = Points(true, true, true);

        foreach (int trading in new[] { 3, 5 })
        {
            var (timeInMarket, exposure) = ExposureMetricsCalculator.Compute(points, NoTrades, historyStartIndex: 0, tradingStartIndex: trading);

            Assert.Equal(MetricStatus.InsufficientData, timeInMarket.Status);
            Assert.Equal(MetricReason.EmptyInput, timeInMarket.Reason);
            Assert.Equal(MetricStatus.InsufficientData, exposure.Status);
            Assert.Equal(MetricUnit.ExposureRatio, exposure.Unit);
        }
    }

    [Fact]
    public void Generator_UsesTheOptionsTradingStart_ForExposureOnly()
    {
        var pointsBuilder = ImmutableArray.CreateBuilder<EquityPoint>();
        decimal[] equity = { 101m, 102m, 101m, 103m, 104m, 106m };
        for (int i = 0; i < equity.Length; i++)
        {
            pointsBuilder.Add(new EquityPoint(i, ReportTestHelpers.BaseUtc.AddDays(i), equity[i], equity[i], i >= 2 ? 50m : 0m, 0m));
        }
        BacktestResult result = ReportTestHelpers.BuildResult(100m, equity);
        result = new BacktestResult(
            result.Orders, result.Fills, result.Trades, pointsBuilder.ToImmutable(), result.Signals, result.Configuration, result.Status,
            result.StrategyName, result.ReproducibilityHash, result.IsInsufficientData);

        BacktestReport atHistory = new BacktestReportGenerator().Generate(result, ReportTestHelpers.Options());
        BacktestReport atTrading = new BacktestReportGenerator().Generate(result, ReportTestHelpers.Options(tradingStartIndex: 2));

        Assert.Equal(4m / 6m, atHistory.Exposure!.Value.Value);
        Assert.Equal(1m, atTrading.Exposure!.Value.Value);
        Assert.Equal(4m, atTrading.TimeInMarket!.Value.Value);
        // Every other statistic keeps the HistoryStartIndex sample.
        Assert.Equal(atHistory.TotalPnL, atTrading.TotalPnL);
        Assert.Equal(atHistory.LongestDrawdownDuration, atTrading.LongestDrawdownDuration);
        Assert.Equal(atHistory.BarSharpe, atTrading.BarSharpe);
        BacktestReportValidator.Validate(atTrading);
    }

    // ---- T6: same-bar round trips -----------------------------------------------------------------------------------------------------

    [Fact]
    public void SameBarTrade_OnAFlatBar_IsCountedAsOneInMarketBar()
    {
        // The forced-liquidation-on-the-entry-bar shape: the Close snapshot of bar 1 is flat, yet the trade lived on that bar.
        ImmutableArray<EquityPoint> points = Points(false, false, false);

        var (timeInMarket, exposure) = ExposureMetricsCalculator.Compute(
            points, ImmutableArray.Create(SameBarTrade(1)), historyStartIndex: 0, tradingStartIndex: 0);

        Assert.Equal(1m, timeInMarket.Value);
        Assert.Equal(1m / 3m, exposure.Value);
    }

    [Fact]
    public void SameBarTrade_OnABarThatAlsoHoldsAPositionAtClose_IsCountedOnce()
    {
        // A same-bar round trip followed by a new position that stays open at the Close: still one bar.
        ImmutableArray<EquityPoint> points = Points(false, true, true);

        var (timeInMarket, _) = ExposureMetricsCalculator.Compute(
            points, ImmutableArray.Create(SameBarTrade(1)), historyStartIndex: 0, tradingStartIndex: 0);

        Assert.Equal(2m, timeInMarket.Value);
    }

    [Fact]
    public void SeveralSameBarTradesOnTheSameBar_AreCountedOnce()
    {
        ImmutableArray<EquityPoint> points = Points(false, false);

        var (timeInMarket, _) = ExposureMetricsCalculator.Compute(
            points, ImmutableArray.Create(SameBarTrade(0), SameBarTrade(0)), historyStartIndex: 0, tradingStartIndex: 0);

        Assert.Equal(1m, timeInMarket.Value);
    }

    [Fact]
    public void NoSameBarTrades_LeavesTheSnapshotCountUnchanged()
    {
        ImmutableArray<EquityPoint> points = Points(false, true, true, false);
        BacktestTrade multiBar = ReportTestHelpers.Trade(1m, entryBar: 1, exitBar: 3);

        var (timeInMarket, exposure) = ExposureMetricsCalculator.Compute(
            points, ImmutableArray.Create(multiBar), historyStartIndex: 0, tradingStartIndex: 0);

        Assert.Equal(2m, timeInMarket.Value);
        Assert.Equal(0.5m, exposure.Value);
    }

    [Fact]
    public void SameBarTradesOutsideTheSampleRange_AreIgnored()
    {
        ImmutableArray<EquityPoint> points = Points(false, false, false, false);

        var (timeInMarket, _) = ExposureMetricsCalculator.Compute(
            points,
            ImmutableArray.Create(SameBarTrade(0), SameBarTrade(1), SameBarTrade(4), SameBarTrade(9)), // 1 = just before the start, 4 = one past the last point
            historyStartIndex: 0, tradingStartIndex: 2);

        Assert.Equal(0m, timeInMarket.Value);
    }

    [Fact]
    public void ForcedLiquidationOnTheEntryBar_MakesExposurePositive_AndExposureAdjustedCagrDefined()
    {
        // Real engine: the Long fills at bar 1's Open and is force-liquidated on the same bar (BacktestEngineEntryBarLifetimeTests scenario).
        BacktestResult result = RunForcedLiquidationOnEntryBar();

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(trade.EntryBar, trade.ExitBar);
        Assert.Equal(0m, result.EquityPoints[trade.EntryBar].MarketValue);   // the Close snapshot alone would say "never in the market"
        Assert.Equal(0m, result.EquityPoints[trade.EntryBar].HeldMargin);

        BacktestReport report = new BacktestReportGenerator().Generate(result, ReportTestHelpers.Options());

        Assert.Equal(1m, report.TimeInMarket!.Value.Value);
        Assert.Equal(1m / result.EquityPoints.Length, report.Exposure!.Value.Value);
        Assert.NotEqual(MetricReason.ZeroDivisor, report.ExposureAdjustedCAGR!.Value.Reason);
        BacktestReportValidator.Validate(report);
    }

    private static BacktestResult RunForcedLiquidationOnEntryBar()
    {
        var bars = ImmutableArray.Create(
            Candle(0, 100m, 100m, 100m, 100m), Candle(1, 100m, 100m, 60m, 100m), Candle(2, 100m, 100m, 100m, 100m));
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, ReportTestHelpers.BaseUtc, ReportTestHelpers.BaseUtc.AddDays(4), 0, 0);
        var config = new BacktestConfiguration
        {
            InitialCapital = 1000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 20m,
            InitialMarginRatio = 0.3m,
            MaintenanceMarginRatio = 0.2m,
        };
        var entry = new System.Collections.Generic.Dictionary<int, StockAnalyzer.Core.Services.Backtest.Engine.StrategyOrderRequest>
        {
            [0] = new(SignalType.LongEntry, OrderType.Market, null, null, new TimeInForce(true, -1), "r"),
        };
        return Verification.VerificationHarness.CreateEngine().Run(input, config, new Verification.ScriptedStrategy(entry));
    }

    private static CandleData Candle(int day, decimal open, decimal high, decimal low, decimal close) =>
        new(ReportTestHelpers.BaseUtc.AddDays(day), open, high, low, close, 1000);
}
