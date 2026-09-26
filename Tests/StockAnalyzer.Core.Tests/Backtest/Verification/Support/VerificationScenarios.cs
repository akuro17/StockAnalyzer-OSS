#nullable enable
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using static StockAnalyzer.Core.Tests.Backtest.Verification.VerificationHarness;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

internal readonly record struct ScenarioRun(BacktestInput Input, BacktestConfiguration Config, BacktestResult Result);

/// <summary>
/// The hand-calculated scenarios, defined ONCE and used by both the explicit-assertion tests (which carry the hand tables)
/// and the Golden Master snapshots. Every scenario runs through the real <see cref="BacktestEngine"/>.
/// All bar tables are (Open, High, Low, Close); "signal" = the bar whose Close the strategy decides on, the order fills on the
/// next bar (Market at Open, MarketOnClose at Close).
/// </summary>
internal static class VerificationScenarios
{
    // ---- configurations ----

    /// <summary>Capital 1,000,000; qty 100; IMR=1 / MMR=0.5 (cash-account equivalent); flat fee 50 + 0.5/unit => 100 per fill; no slippage.</summary>
    public static BacktestConfiguration CostedConfig(decimal slippageRatio = 0m, decimal initialMarginRatio = 1m, decimal maintenanceMarginRatio = 0.5m)
        => MakeConfig(commissionFlat: 50m, commissionPerUnit: 0.5m, slippageRatio: slippageRatio,
            initialMarginRatio: initialMarginRatio, maintenanceMarginRatio: maintenanceMarginRatio);

    /// <summary>Fee-free variant of <see cref="CostedConfig"/>.</summary>
    public static BacktestConfiguration FreeConfig() => MakeConfig();

    private static ScenarioRun Execute(ImmutableArray<CandleData> bars, BacktestConfiguration config, IBacktestStrategy strategy)
    {
        BacktestInput input = MakeInput(bars);
        return new ScenarioRun(input, config, Run(input, config, strategy));
    }

    private static ScenarioRun Execute(ImmutableArray<CandleData> bars, BacktestConfiguration config, Dictionary<int, StrategyOrderRequest> primary,
        Dictionary<int, StrategyOrderRequest>? accompanyingExit = null)
        => Execute(bars, config, new ScriptedStrategy(primary, accompanyingExit));

    // ---- S1 / S2 / S3: Long round trip ----

    /// <summary>
    /// bar0 (100,100,100,100) signal LongEntry | bar1 (100,110,100,110) entry fills @100, signal LongExit | bar2 (110,110,110,110) exit fills @110.
    /// </summary>
    public static ScenarioRun LongRoundTrip(BacktestConfiguration config) => Execute(
        SyntheticBars.FromTable((100m, 100m, 100m, 100m), (100m, 110m, 100m, 110m), (110m, 110m, 110m, 110m)),
        config,
        new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry), [1] = Req(SignalType.LongExit) });

    /// <summary>Price never moves (3 flat bars at 100): the round trip can only lose its costs.</summary>
    public static ScenarioRun FlatPriceRoundTrip(BacktestConfiguration config) => Execute(
        SyntheticBars.Flat(3, 100m),
        config,
        new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry), [1] = Req(SignalType.LongExit) });

    /// <summary>
    /// bar0 flat 100 signal ShortEntry | bar1 (100,100,90,90) short fills @100, signal ShortExit | bar2 (90,90,90,90) cover fills @90.
    /// </summary>
    public static ScenarioRun ShortRoundTrip(BacktestConfiguration config) => Execute(
        SyntheticBars.FromTable((100m, 100m, 100m, 100m), (100m, 100m, 90m, 90m), (90m, 90m, 90m, 90m)),
        config,
        new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.ShortEntry), [1] = Req(SignalType.ShortExit) });

    // ---- S5: three round trips (+ optional open fourth) ----

    public static readonly ImmutableArray<CandleData> MultiTradeBars = SyntheticBars.FromTable(
        (100m, 100m, 100m, 100m),   // 0 signal entry #1
        (100m, 105m, 99m, 104m),    // 1 entry #1 fills @100, signal exit #1
        (105m, 106m, 104m, 105m),   // 2 exit #1 fills @105
        (105m, 105m, 103m, 104m),   // 3 signal entry #2
        (104m, 108m, 104m, 107m),   // 4 entry #2 fills @104, signal exit #2
        (107m, 107m, 106m, 106m),   // 5 exit #2 fills @107
        (106m, 106m, 100m, 101m),   // 6 signal entry #3
        (101m, 103m, 100m, 102m),   // 7 entry #3 fills @101, signal exit #3
        (102m, 102m, 102m, 102m),   // 8 exit #3 fills @102 (optional: signal entry #4)
        (102m, 102m, 102m, 102m));  // 9 (optional: entry #4 fills @102 and stays open)

    /// <summary>Three closed trades: gross 500 + 300 + 100 = 900. With <paramref name="leaveFourthOpen"/> a 4th Long opens on the last bar and stays open.</summary>
    public static ScenarioRun MultiTrade(BacktestConfiguration config, bool leaveFourthOpen, SignalType entrySignal = SignalType.LongEntry, SignalType exitSignal = SignalType.LongExit)
    {
        var script = new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(entrySignal), [1] = Req(exitSignal),
            [3] = Req(entrySignal), [4] = Req(exitSignal),
            [6] = Req(entrySignal), [7] = Req(exitSignal),
        };
        if (leaveFourthOpen) script[8] = Req(entrySignal);
        return Execute(MultiTradeBars, config, script);
    }

    // ---- Gap fills ----

    /// <summary>bar0 (95 flat) signal LongEntry as Buy Stop @100; bar1 as given.</summary>
    public static ScenarioRun BuyStopEntry(BacktestConfiguration config, (decimal O, decimal H, decimal L, decimal C) bar1) => Execute(
        SyntheticBars.FromTable((95m, 95m, 95m, 95m), bar1, (bar1.C, bar1.C, bar1.C, bar1.C)),
        config,
        new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Stop, stopPrice: 100m) });

    /// <summary>bar0 flat 100 signal LongEntry | bar1 flat 100 entry fills @100, signal LongExit as Sell Stop @90 | bar2 (80,85,78,82) gaps below the stop.</summary>
    public static ScenarioRun SellStopLossGap(BacktestConfiguration config) => Execute(
        SyntheticBars.FromTable((100m, 100m, 100m, 100m), (100m, 100m, 100m, 100m), (80m, 85m, 78m, 82m)),
        config,
        new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry),
            [1] = Req(SignalType.LongExit, OrderType.Stop, stopPrice: 90m),
        });

    /// <summary>bar0 flat 100 signal ShortEntry | bar1 flat 100 short fills @100, signal ShortExit as Buy Stop @110 | bar2 (120,125,118,122) gaps above the stop.</summary>
    public static ScenarioRun BuyStopCoverGap(BacktestConfiguration config) => Execute(
        SyntheticBars.FromTable((100m, 100m, 100m, 100m), (100m, 100m, 100m, 100m), (120m, 125m, 118m, 122m)),
        config,
        new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.ShortEntry),
            [1] = Req(SignalType.ShortExit, OrderType.Stop, stopPrice: 110m),
        });

    /// <summary>bar0 (105 flat) signal LongEntry as Buy Limit @100; bar1 as given.</summary>
    public static ScenarioRun BuyLimitEntry(BacktestConfiguration config, (decimal O, decimal H, decimal L, decimal C) bar1) => Execute(
        SyntheticBars.FromTable((105m, 105m, 105m, 105m), bar1, (bar1.C, bar1.C, bar1.C, bar1.C)),
        config,
        new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 100m) });

    // ---- Reversal ----

    /// <summary>
    /// Same-quantity reversal (Fixed 100, IMR=1): bar0 flat 100 LongEntry | bar1 flat 100 long fills @100, reversal pair signalled
    /// (ShortEntry + accompanying LongExit) | bar2 flat 110 LongExit then ShortEntry fill @110, signal ShortExit | bar3 flat 105 cover @105.
    /// </summary>
    public static ScenarioRun ReversalSameQuantity(BacktestConfiguration config) => Execute(
        SyntheticBars.FlatSeries(100m, 100m, 110m, 105m),
        config,
        new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry),
            [1] = Req(SignalType.ShortEntry),
            [2] = Req(SignalType.ShortExit),
        },
        new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit) });

    /// <summary>Capital 10,000; PercentOfEquity 0.5; IMR=1/MMR=0.5; no fees/slippage. See L1_PositionReversalTests for the hand table (Long 50 -> Short 42).</summary>
    public static BacktestConfiguration ReversalPercentConfig() => MakeConfig(
        initialCapital: 10_000m, sizingModel: PositionSizingModel.PercentOfEquity, sizingParameter: 0.5m);

    public static ScenarioRun ReversalLong50ToShort42() => Execute(
        SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),
            (100m, 140m, 100m, 140m),
            (140m, 140m, 140m, 140m),
            (130m, 130m, 130m, 130m)),
        ReversalPercentConfig(),
        new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry),
            [1] = Req(SignalType.ShortEntry),
            [2] = Req(SignalType.ShortExit),
        },
        new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit) });

    /// <summary>ShortEntry signalled while Long WITHOUT an accompanying LongExit: the engine never evaluates it while the old side is held.</summary>
    public static ScenarioRun EntryWithoutAccompanyingExit(BacktestConfiguration config) => Execute(
        SyntheticBars.Flat(4, 100m),
        config,
        new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry),
            [1] = Req(SignalType.ShortEntry),
        });

    // ---- MOC and forced liquidation ----

    /// <summary>bar0 flat 100 LongEntry(MOC) | bar1 (105,112,104,108) fills @Close 108, signal LongExit | bar2 (110,115,109,111) exit @Open 110.</summary>
    public static ScenarioRun MocRoundTrip(BacktestConfiguration config) => Execute(
        SyntheticBars.FromTable((100m, 100m, 100m, 100m), (105m, 112m, 104m, 108m), (110m, 115m, 109m, 111m)),
        config,
        new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.MarketOnClose),
            [1] = Req(SignalType.LongExit),
        });

    /// <summary>Capital 1000; qty 20; IMR 0.30 / MMR 0.20; no penalty.</summary>
    public static BacktestConfiguration LiquidationConfig() => MakeConfig(
        initialCapital: 1000m, sizingParameter: 20m, initialMarginRatio: 0.30m, maintenanceMarginRatio: 0.20m);

    /// <summary>
    /// Entry fills @100 (bar1): notional 2000, margin 600, Cash 400; P_liq = (2000-400-600)/(20*0.8) = 62.5; bar2 (90,91,60,65) Low 60 breaches it.
    /// </summary>
    public static ScenarioRun MarginLiquidationPartial() => Execute(
        SyntheticBars.FromTable((100m, 100m, 100m, 100m), (100m, 100m, 100m, 100m), (90m, 91m, 60m, 65m)),
        LiquidationConfig(),
        new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry) });

    // ---- Golden Master catalogue (name = golden file name) ----

    public static IReadOnlyDictionary<string, System.Func<ScenarioRun>> Golden { get; } = new Dictionary<string, System.Func<ScenarioRun>>
    {
        ["simple_long"] = () => LongRoundTrip(CostedConfig()),
        ["simple_short"] = () => ShortRoundTrip(CostedConfig()),
        ["cost_slippage_roundtrip"] = () => LongRoundTrip(CostedConfig(slippageRatio: 0.01m)),
        ["gap_stop_entry"] = () => BuyStopEntry(FreeConfig(), (110m, 112m, 109m, 111m)),
        ["gap_stop_loss"] = () => SellStopLossGap(FreeConfig()),
        ["reversal_two_leg_50_to_42"] = ReversalLong50ToShort42,
        ["moc_entry"] = () => MocRoundTrip(FreeConfig()),
        ["margin_liquidation_partial"] = MarginLiquidationPartial,
    };
}
