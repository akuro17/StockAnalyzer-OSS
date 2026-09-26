using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.Verification.LedgerAssertions;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// L2 warm-up protection and TradingStartIndex boundary, characterizing the CURRENT engine: the strategy is evaluated only when
/// <c>i &gt;= TradingStartIndex</c> (inclusive), and an indicator that is still warming up yields null, which a condition treats as "false".
/// Data: rising ramp Close = 100 + i on flat bars, so "Close &gt; SMA(5)" is true on every bar where the SMA is defined (index 4 onward).
/// </summary>
[Trait("Category", "BacktestVerification")]
public class L2_WarmupAndTradingStartTests
{
    private const int BarCount = 10;
    private const decimal RampStart = 100m;
    private const int Period = VerificationParameters.SmaPeriod;
    private const int FirstValidSmaBar = Period - 1;

    private static ImmutableArray<CandleData> RampBars()
        => SyntheticBars.FlatSeries(Enumerable.Range(0, BarCount).Select(i => RampStart + i).ToArray());

    private static BacktestResult Run(int historyStartIndex, int tradingStartIndex, out BacktestInput input)
    {
        ImmutableArray<CandleData> bars = RampBars();
        input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            SyntheticBars.Start, SyntheticBars.Start.AddDays(bars.Length + 1), historyStartIndex, tradingStartIndex);
        return VerificationHarness.Run(input, VerificationHarness.MakeConfig(), VerificationHarness.SmaTrendStrategy(Period));
    }

    [Fact]
    public void TradingStartZero_NoSignalUntilTheSmaHasItsFirstValue_ThenEntryFillsOnTheNextOpen()
    {
        BacktestResult r = Run(0, 0, out BacktestInput input);

        BacktestSignal signal = Assert.Single(r.Signals);
        Assert.Equal(SignalType.LongEntry, signal.Type);
        Assert.Equal(FirstValidSmaBar, signal.BarIndex);           // bar 4: first bar with a defined SMA(5)

        BacktestFill fill = Assert.Single(r.Fills);
        Assert.Equal(FirstValidSmaBar + 1, fill.BarIndex);         // bar 5
        Eq(RampStart + FirstValidSmaBar + 1, fill.Price, "fill price = Open of bar 5");

        Assert.Equal(BarCount, r.EquityPoints.Length);             // warm-up bars are still recorded
        Assert.All(r.EquityPoints.Take(FirstValidSmaBar + 1), p => Eq(1_000_000m, p.Equity, "Equity during warm-up"));
        AllInvariants(input, VerificationHarness.MakeConfig(), r);
    }

    [Fact]
    public void TradingStartAfterWarmup_FirstSignalIsExactlyOnTheTradingStartBar_Inclusive()
    {
        // SMA is valid from bar 4 and the condition is true, but trading is barred until bar 7.
        const int tradingStart = 7;
        BacktestResult r = Run(0, tradingStart, out BacktestInput input);

        Assert.All(r.Signals, s => Assert.True(s.BarIndex >= tradingStart));
        BacktestSignal signal = Assert.Single(r.Signals);
        Assert.Equal(tradingStart, signal.BarIndex);               // the TradingStart bar itself is INCLUDED
        BacktestFill fill = Assert.Single(r.Fills);
        Assert.Equal(tradingStart + 1, fill.BarIndex);
        Eq(RampStart + tradingStart + 1, fill.Price, "fill price = Open of bar 8");
        AllInvariants(input, VerificationHarness.MakeConfig(), r);
    }

    [Fact]
    public void TradingStartEqualToBarCount_StrategyNeverRuns_EquityStaysInitial()
    {
        BacktestResult r = Run(0, BarCount, out BacktestInput input);

        Assert.Empty(r.Signals);
        Assert.Empty(r.Orders);
        Assert.Empty(r.Fills);
        Assert.Empty(r.Trades);
        Assert.Equal(BarCount, r.EquityPoints.Length);
        Assert.All(r.EquityPoints, p => Eq(1_000_000m, p.Equity, "Equity"));
        AllInvariants(input, VerificationHarness.MakeConfig(), r);
    }

    [Fact]
    public void TradingStartOnTheLastBar_SignalIsRecorded_ButTheOrderNeverFills()
    {
        BacktestResult r = Run(0, BarCount - 1, out BacktestInput input);

        BacktestSignal signal = Assert.Single(r.Signals);
        Assert.Equal(BarCount - 1, signal.BarIndex);
        Assert.Empty(r.Fills);
        BacktestOrder order = Assert.Single(r.Orders);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(ExpiredReason.EndOfData, order.ExpiredReason);
        AllInvariants(input, VerificationHarness.MakeConfig(), r);
    }

    [Fact]
    public void HistoryStartInsideTheData_EngineGatesOrdersByTradingStartOnly_AndStillRecordsEveryBar()
    {
        // The engine only validates HistoryStartIndex; the report layer applies it (see L2_ReportPeriodBoundaryTests).
        const int start = 6;
        BacktestResult r = Run(start, start, out BacktestInput input);

        BacktestSignal signal = Assert.Single(r.Signals);
        Assert.Equal(start, signal.BarIndex);                      // SMA valid since bar 4, but no order before bar 6
        Assert.Equal(BarCount, r.EquityPoints.Length);             // equity is recorded for bars 0..5 as well
        AllInvariants(input, VerificationHarness.MakeConfig(), r);
    }
}
