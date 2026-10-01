#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Tests.Backtest.Reporting;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Trades order contract (Y:\Temp\sa_implementation_plan_BacktestReportIntegrityContracts.md, T3, Gate D6 = reject). The streak metrics read
/// <see cref="BacktestResult.Trades"/> in list order, so a result whose trades are not in close order is rejected at report generation instead of
/// being sorted or silently mis-measured. The real engine must satisfy the contract on every path, including same-bar reversal and liquidation.
/// </summary>
public class BacktestTradeOrderContractTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    // ---- Generator precondition -------------------------------------------------------------------------------------------------

    private static BacktestReport Generate(params BacktestTrade[] trades) => new BacktestReportGenerator().Generate(
        ReportTestHelpers.BuildResult(100m, new[] { 101m, 102m, 103m }, trades), ReportTestHelpers.Options());

    [Fact]
    public void Generate_ExitBarGoingBackwards_IsRejectedNamingTheIndex()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => Generate(
            ReportTestHelpers.Trade(1m, entryBar: 0, exitBar: 5),
            ReportTestHelpers.Trade(1m, entryBar: 0, exitBar: 4)));

        Assert.Contains("Trades[1]", error.Message);
        Assert.Contains("Trades[0]", error.Message);
    }

    [Fact]
    public void Generate_EntryBarAfterExitBar_IsRejectedNamingTheIndex()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => Generate(
            ReportTestHelpers.Trade(1m, entryBar: 0, exitBar: 1),
            ReportTestHelpers.Trade(1m, entryBar: 4, exitBar: 3)));

        Assert.Contains("Trades[1]", error.Message);
    }

    [Fact]
    public void Generate_ViolationDeepInTheList_IsRejectedNamingThatIndex()
    {
        var trades = new List<BacktestTrade>();
        for (int i = 0; i < 20; i++) trades.Add(ReportTestHelpers.Trade(1m, entryBar: i, exitBar: i + 1));
        trades.Add(ReportTestHelpers.Trade(1m, entryBar: 0, exitBar: 3));

        ArgumentException error = Assert.Throws<ArgumentException>(() => Generate(trades.ToArray()));

        Assert.Contains("Trades[20]", error.Message);
    }

    [Fact]
    public void Generate_EqualExitBars_AreAccepted() => Generate(
        ReportTestHelpers.Trade(1m, entryBar: 0, exitBar: 2),
        ReportTestHelpers.Trade(-1m, entryBar: 2, exitBar: 2),
        ReportTestHelpers.Trade(1m, entryBar: 2, exitBar: 2));

    [Fact]
    public void Generate_ZeroLengthTrade_IsAccepted() => Generate(ReportTestHelpers.Trade(1m, entryBar: 3, exitBar: 3));

    [Fact]
    public void Generate_EmptyAndSingleTradeLists_AreAccepted()
    {
        Generate();
        Generate(ReportTestHelpers.Trade(1m));
    }

    [Fact]
    public void Generate_TradesAreNeverReordered()
    {
        // Loss, win, win: the longest win streak is 2 only if the list order is respected. A sort by any key would not change it here, so the
        // real evidence is the two streak metrics staying exactly as listed for an order the engine could produce.
        BacktestReport report = Generate(
            ReportTestHelpers.Trade(-1m, entryBar: 0, exitBar: 1),
            ReportTestHelpers.Trade(1m, entryBar: 1, exitBar: 2),
            ReportTestHelpers.Trade(1m, entryBar: 2, exitBar: 3));

        Assert.Equal(2m, report.MaxConsecutiveWins!.Value.Value);
        Assert.Equal(1m, report.MaxConsecutiveLosses!.Value.Value);
    }

    // ---- Real-engine contract ------------------------------------------------------------------------------------------------------

    private static CandleData Bar(int dayOffset, decimal open, decimal high, decimal low, decimal close) => new(Bar0.AddDays(dayOffset), open, high, low, close, 1000);

    private static CandleData Flat(int dayOffset, decimal price) => Bar(dayOffset, price, price, price, price);

    private static StrategyOrderRequest Req(SignalType type, OrderType orderType = OrderType.Market, decimal? limitPrice = null)
        => new(type, orderType, limitPrice, null, Gtc, "r");

    private static BacktestConfiguration MarginConfig() => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 20m,
        InitialMarginRatio = 0.3m,
        MaintenanceMarginRatio = 0.2m,
    };

    private static BacktestConfiguration CashConfig() => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
    };

    private static BacktestResult Run(
        BacktestConfiguration config,
        ImmutableArray<CandleData> bars,
        Dictionary<int, StrategyOrderRequest> entryScript,
        Dictionary<int, StrategyOrderRequest>? exitScript = null)
    {
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, 0);
        return VerificationHarness.CreateEngine().Run(input, config, new ScriptedStrategy(entryScript, exitScript));
    }

    /// <summary>The engine's single-position property and the P1 field definitions that the report layer relies on.</summary>
    private static void AssertEngineTradeContract(BacktestResult result, int expectedTrades)
    {
        Assert.Equal(expectedTrades, result.Trades.Length);
        for (int i = 0; i < result.Trades.Length; i++)
        {
            BacktestTrade trade = result.Trades[i];
            Assert.True(trade.EntryBar <= trade.ExitBar, $"Trades[{i}] EntryBar {trade.EntryBar} > ExitBar {trade.ExitBar}");
            Assert.Equal(trade.ExitBar - trade.EntryBar, trade.HoldingBars);
            if (i > 0)
            {
                BacktestTrade previous = result.Trades[i - 1];
                Assert.True(trade.ExitBar >= previous.ExitBar, $"Trades[{i}] ExitBar {trade.ExitBar} < Trades[{i - 1}] ExitBar {previous.ExitBar}");
                Assert.True(trade.EntryBar >= previous.ExitBar, $"Trades[{i}] EntryBar {trade.EntryBar} < Trades[{i - 1}] ExitBar {previous.ExitBar} (single position)");
            }
        }

        // Whatever the engine produced must be accepted by the report generator (the contract is consistent with reality).
        new BacktestReportGenerator().Generate(result, ReportTestHelpers.Options());
    }

    [Fact]
    public void Engine_SeveralRoundTrips_SatisfyTheContract()
    {
        BacktestResult result = Run(CashConfig(),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 102m), Flat(3, 101m), Flat(4, 100m), Flat(5, 98m), Flat(6, 99m), Flat(7, 99m)),
            new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(SignalType.LongEntry),
                [3] = Req(SignalType.ShortEntry),
            },
            new Dictionary<int, StrategyOrderRequest>
            {
                [1] = Req(SignalType.LongExit),
                [5] = Req(SignalType.ShortExit),
            });

        AssertEngineTradeContract(result, expectedTrades: 2);
    }

    [Fact]
    public void Engine_ReversalOnTheSameBar_SatisfiesTheContract()
    {
        // The Exit and the reversing Entry both fill at bar2's Open; the new Short is liquidated on that bar: two trades whose ExitBar is equal.
        BacktestResult result = Run(MarginConfig(),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Bar(2, 100m, 130m, 100m, 120m)),
            new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(SignalType.LongEntry),
                [1] = Req(SignalType.ShortEntry),
            },
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit) });

        AssertEngineTradeContract(result, expectedTrades: 2);
        Assert.Equal(result.Trades[0].ExitBar, result.Trades[1].ExitBar);
    }

    [Fact]
    public void Engine_ForcedLiquidationOnTheEntryBar_SatisfiesTheContract()
    {
        BacktestResult result = Run(MarginConfig(),
            ImmutableArray.Create(Flat(0, 100m), Bar(1, 100m, 100m, 60m, 100m), Flat(2, 100m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry) });

        AssertEngineTradeContract(result, expectedTrades: 1);
        Assert.Equal(0, result.Trades[0].HoldingBars);
    }

    [Fact]
    public void Engine_SeededRandomRuns_SatisfyTheContract_IncludingLiquidationsAndReentries()
    {
        // Two account shapes over the shared fuzz seeds: cash-equivalent, and 10x leverage where forced liquidations and re-entries occur.
        BacktestConfiguration[] configs =
        {
            VerificationHarness.MakeConfig(10_000m, sizingParameter: 10m),
            VerificationHarness.MakeConfig(10_000m, slippageRatio: 0.001m, sizingParameter: 400m, initialMarginRatio: 0.1m, maintenanceMarginRatio: 0.05m, liquidationPenaltyRatio: 0.01m),
        };
        int totalTrades = 0;
        int forcedLiquidations = 0;

        for (int seed = 1; seed <= VerificationParameters.FuzzSeedCount; seed++)
        {
            ImmutableArray<CandleData> bars = SyntheticBars.SeededRandomWalk(seed, VerificationParameters.FuzzBarCount, VerificationParameters.FuzzStartPrice);
            BacktestInput input = VerificationHarness.MakeInput(bars);
            foreach (BacktestConfiguration config in configs)
            {
                BacktestResult result = VerificationHarness.Run(input, config, new SeededRandomStrategy(seed));
                AssertEngineTradeContract(result, result.Trades.Length);
                totalTrades += result.Trades.Length;
                forcedLiquidations += result.Trades.Count(trade => trade.IsForcedLiquidation);
            }
        }

        // Guards the test itself: a corpus without trades or liquidations would prove nothing.
        Assert.True(totalTrades > 50, $"corpus produced only {totalTrades} trades");
        Assert.True(forcedLiquidations > 0, "corpus produced no forced liquidation");
    }
}
