using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// Independent evidence for the 15 extended metrics (Y:\Temp\sa_implementation_plan_BacktestReportVerificationOracle.md, T4): the production
/// report is compared exactly with <see cref="ExtendedMetricsReference"/> on seeded random data, general laws are asserted on every case, and
/// hand-derived literals pin the reference itself so that a shared misunderstanding cannot hide in both implementations.
/// </summary>
public class ExtendedMetricsOracleTests
{
    private const int SeededCaseCount = 300;
    private const decimal InitialCapital = 100m;
    private const decimal SamplePrice = 100m;
    private const decimal MarginPerUnit = 30m;

    // ---- Scenario construction (real records, no mocks) ---------------------------------------------------------------------------

    /// <summary>Trades are laid out back to back on the bar axis (entry_i &gt;= exit_{i-1}) so the trade order contract holds.</summary>
    private static BacktestResult Build(
        IReadOnlyList<decimal> nets, IReadOnlyList<int> holdingBars, decimal[] pointEquity, int[] endOfBarPosition, bool margin = false,
        int[]? roundTripBars = null, decimal roundTripNet = 1m)
    {
        // Same-bar round trips: a position opened and closed on one bar (flat before and after), i.e. a Buy then a Sell fill on that bar and a
        // trade with EntryBar == ExitBar == bar. They are appended to the layout trades and ordered by ExitBar (stable) to keep the order contract.
        var tradeList = new List<BacktestTrade>();
        int cursor = 0;
        for (int i = 0; i < nets.Count; i++)
        {
            int entry = cursor;
            int exit = entry + holdingBars[i];
            cursor = exit;
            tradeList.Add(new BacktestTrade(
                TradeId: i + 1, Side: TradeSide.Long,
                EntryBar: entry, EntryTime: ReportTestHelpers.BaseUtc.AddDays(entry), EntryPrice: SamplePrice,
                ExitBar: exit, ExitTime: ReportTestHelpers.BaseUtc.AddDays(exit), ExitPrice: SamplePrice,
                Quantity: 1m, ClosedGross: nets[i], ClosedNet: nets[i], EntryFee: 0m, ExitFee: 0m,
                HoldingBars: holdingBars[i], IsForcedLiquidation: false));
        }

        foreach (int bar in roundTripBars ?? Array.Empty<int>())
        {
            tradeList.Add(new BacktestTrade(
                TradeId: tradeList.Count + 1, Side: TradeSide.Long,
                EntryBar: bar, EntryTime: ReportTestHelpers.BaseUtc.AddDays(bar), EntryPrice: SamplePrice,
                ExitBar: bar, ExitTime: ReportTestHelpers.BaseUtc.AddDays(bar), ExitPrice: SamplePrice,
                Quantity: 1m, ClosedGross: roundTripNet, ClosedNet: roundTripNet, EntryFee: 0m, ExitFee: 0m,
                HoldingBars: 0, IsForcedLiquidation: true));
        }
        ImmutableArray<BacktestTrade> trades = tradeList.OrderBy(trade => trade.ExitBar).ToImmutableArray();

        var fills = ImmutableArray.CreateBuilder<BacktestFill>();
        var points = ImmutableArray.CreateBuilder<EquityPoint>(pointEquity.Length);
        for (int bar = 0; bar < pointEquity.Length; bar++)
        {
            int previous = bar == 0 ? 0 : endOfBarPosition[bar - 1];
            int delta = endOfBarPosition[bar] - previous;
            if (delta != 0)
            {
                fills.Add(new BacktestFill(
                    FillId: fills.Count + 1, OrderId: fills.Count + 1, BarIndex: bar, FillTime: ReportTestHelpers.BaseUtc.AddDays(bar),
                    Side: delta > 0 ? OrderSide.Buy : OrderSide.Sell, Price: SamplePrice, Quantity: Math.Abs(delta), Commission: 0m, SlippageAmount: 0m));
            }

            if (roundTripBars is not null && Array.IndexOf(roundTripBars, bar) >= 0)
            {
                foreach (OrderSide side in new[] { OrderSide.Buy, OrderSide.Sell })
                {
                    fills.Add(new BacktestFill(
                        FillId: fills.Count + 1, OrderId: fills.Count + 1, BarIndex: bar, FillTime: ReportTestHelpers.BaseUtc.AddDays(bar),
                        Side: side, Price: SamplePrice, Quantity: 1m, Commission: 0m, SlippageAmount: 0m));
                }
            }

            int position = endOfBarPosition[bar];
            decimal marketValue = margin ? 0m : position * SamplePrice;
            decimal heldMargin = margin ? Math.Abs(position) * MarginPerUnit : 0m;
            points.Add(new EquityPoint(bar, ReportTestHelpers.BaseUtc.AddDays(bar), pointEquity[bar], pointEquity[bar], marketValue, heldMargin));
        }

        return new BacktestResult(
            ImmutableArray<BacktestOrder>.Empty, fills.ToImmutable(), trades, points.MoveToImmutable(),
            ImmutableArray<BacktestSignal>.Empty,
            new BacktestConfiguration { InitialCapital = InitialCapital, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m },
            RunStatus.Completed, strategyName: "oracle", reproducibilityHash: new byte[32], isInsufficientData: false);
    }

    private static BacktestReport Generate(BacktestResult result, int historyStart, int? tradingStart = null) =>
        new BacktestReportGenerator().Generate(result, ReportTestHelpers.Options(historyStartIndex: historyStart, tradingStartIndex: tradingStart));

    private static IEnumerable<(string Name, MetricValue Metric)> Actual(BacktestReport report) =>
        report.EnumerateExtendedMetrics().Select(entry => (entry.Name, entry.Metric));

    private static void AssertSameMetric(string label, string name, MetricValue expected, MetricValue actual) =>
        ExtendedMetricsReference.AssertSameMetric(label, name, expected, actual);

    private static void AssertReportEqualsReference(string label, BacktestResult result, int historyStart, int? tradingStart = null) =>
        ExtendedMetricsReference.AssertMatches(label, result, Generate(result, historyStart, tradingStart), historyStart, tradingStart ?? historyStart);

    // ---- Seeded random cases ----------------------------------------------------------------------------------------------------------

    private sealed record Case(
        decimal[] Nets, int[] HoldingBars, decimal[] PointEquity, int[] Position, int HistoryStart, int TradingStart, bool Margin, int[] RoundTripBars);

    private static BacktestResult Materialize(Case c, bool reverseLayout = false) => Build(
        reverseLayout ? c.Nets.Reverse().ToArray() : c.Nets,
        reverseLayout ? c.HoldingBars.Reverse().ToArray() : c.HoldingBars,
        c.PointEquity, c.Position, c.Margin, c.RoundTripBars);

    private static Case RandomCase(int seed)
    {
        var rng = new Random(seed);
        int tradeCount = rng.Next(0, 81);
        var nets = new decimal[tradeCount];
        var holding = new int[tradeCount];
        for (int i = 0; i < tradeCount; i++)
        {
            nets[i] = rng.Next(-30, 31) / 10m;      // one decimal: zeros, ties and inexact averages all occur
            holding[i] = rng.Next(1, 11);           // same-bar (0-bar) trades come only from the explicit round trips below
        }

        int historyStart = rng.Next(0, 4);
        int tradingStart = historyStart + rng.Next(0, 3);
        int sampleBars = seed % 25 == 0 ? 1 : rng.Next(1, 61);
        int pointCount = historyStart + sampleBars;
        var equity = new decimal[pointCount];
        var position = new int[pointCount];
        decimal level = InitialCapital;
        int current = 0;
        for (int bar = 0; bar < pointCount; bar++)
        {
            if (rng.Next(10) >= 4) level = Math.Max(1m, level + rng.Next(-3, 4)); // ~40% plateaus
            equity[bar] = level;
            if (rng.Next(5) == 0) current = rng.Next(-2, 3);                       // includes direct long/short reversals
            position[bar] = current;
        }

        // Same-bar round trips only on bars that are flat before and after (so they are the only position that bar ever had).
        var roundTrips = new List<int>();
        for (int bar = 0; bar < pointCount; bar++)
        {
            bool flatBefore = bar == 0 || position[bar - 1] == 0;
            if (flatBefore && position[bar] == 0 && rng.Next(6) == 0) roundTrips.Add(bar);
        }
        return new Case(nets, holding, equity, position, historyStart, tradingStart, Margin: rng.Next(2) == 0, roundTrips.ToArray());
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, SeededCaseCount).Select(seed => new object[] { seed });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ProductionEqualsIndependentReference_OnSeededData(int seed)
    {
        Case c = RandomCase(seed);
        AssertReportEqualsReference($"seed {seed}", Materialize(c), c.HistoryStart, c.TradingStart);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GeneralLaws_HoldForEverySeededCase(int seed)
    {
        Case c = RandomCase(seed);
        BacktestResult result = Materialize(c);
        BacktestReport report = Generate(result, c.HistoryStart, c.TradingStart);
        string label = $"seed {seed}";

        Assert.Equal(report.TotalTrades, report.WinTrades + report.LossTrades + report.BreakevenTrades);

        if (report.GrossProfit is { Status: MetricStatus.Valid } grossProfit && report.GrossLoss is { Status: MetricStatus.Valid } grossLoss)
        {
            Assert.True(grossProfit.Value >= 0m && grossLoss.Value >= 0m, $"{label}: GP/GL negative");
            if (grossLoss.Value > 0m)
            {
                Assert.True(report.ProfitFactor.Status == MetricStatus.Valid, $"{label}: ProfitFactor not Valid with GL > 0");
                Assert.True(grossProfit.Value / grossLoss.Value == report.ProfitFactor.Value, $"{label}: GP/GL != ProfitFactor");
            }
            if (report.AverageWin is { Status: MetricStatus.Valid } averageWin)
            {
                // Division rounds at 28 digits, so the product only matches within that rounding.
                Assert.True(Math.Abs((averageWin.Value!.Value * report.WinTrades) - grossProfit.Value!.Value) < 1e-20m, $"{label}: AverageWin * W != GP");
            }
            if (report.AverageLoss is { Status: MetricStatus.Valid } averageLoss)
            {
                Assert.True(Math.Abs((averageLoss.Value!.Value * report.LossTrades) - grossLoss.Value!.Value) < 1e-20m, $"{label}: AverageLoss * L != GL");
            }
        }

        if (report.MaxConsecutiveWins is { Status: MetricStatus.Valid } streakWins) Assert.True(streakWins.Value <= report.WinTrades, $"{label}: streak > W");
        if (report.MaxConsecutiveLosses is { Status: MetricStatus.Valid } streakLosses) Assert.True(streakLosses.Value <= report.LossTrades, $"{label}: streak > L");

        if (report.MaxDepthDrawdownDuration is { Status: MetricStatus.Valid } depthDuration && report.LongestDrawdownDuration is { Status: MetricStatus.Valid } longest)
        {
            Assert.True(longest.Value >= depthDuration.Value, $"{label}: Max < DD duration");
        }

        int sampleBars = Math.Max(0, result.EquityPoints.Length - Math.Max(c.HistoryStart, c.TradingStart));
        if (report.Exposure is { Status: MetricStatus.Valid } exposure && report.TimeInMarket is { Status: MetricStatus.Valid } timeInMarket)
        {
            Assert.InRange(exposure.Value!.Value, 0m, 1m);
            Assert.True(timeInMarket.Value <= sampleBars, $"{label}: TimeInMarket > sample bars");
            Assert.True(exposure.Value == timeInMarket.Value / sampleBars, $"{label}: Exposure != TimeInMarket / m");
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ReversingTheTradeSequence_ChangesOnlyTheStreakMetrics(int seed)
    {
        Case c = RandomCase(seed);
        BacktestReport forward = Generate(Materialize(c), c.HistoryStart, c.TradingStart);
        BacktestReport reversed = Generate(Materialize(c, reverseLayout: true), c.HistoryStart, c.TradingStart);

        foreach ((string name, MetricValue metric) in Actual(forward))
        {
            if (name is nameof(BacktestReport.MaxConsecutiveWins) or nameof(BacktestReport.MaxConsecutiveLosses)) continue;
            AssertSameMetric($"seed {seed} reversed", name, metric, Actual(reversed).Single(entry => entry.Name == name).Metric);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GeneratingTwice_GivesIdenticalMetrics(int seed)
    {
        Case c = RandomCase(seed);
        BacktestResult result = Materialize(c);
        List<(string Name, MetricValue Metric)> first = Actual(Generate(result, c.HistoryStart, c.TradingStart)).ToList();
        List<(string Name, MetricValue Metric)> second = Actual(Generate(result, c.HistoryStart, c.TradingStart)).ToList();

        Assert.Equal(first, second);
    }

    // ---- Hand-derived literals: they pin BOTH implementations against the spec formulas ------------------------------------------------

    private static MetricValue Ok(decimal value, MetricUnit unit) => MetricValue.Valid(value, unit);

    private static MetricValue Non(MetricStatus status, MetricUnit unit, MetricReason reason) => MetricValue.NonValid(status, unit, reason);

    private static readonly MetricValue NoTradesCurrency = Non(MetricStatus.InsufficientData, MetricUnit.Currency, MetricReason.NoClosedTrades);
    private static readonly MetricValue NoWinsCurrency = Non(MetricStatus.Undefined, MetricUnit.Currency, MetricReason.NoWinningTrades);
    private static readonly MetricValue NoLossesCurrency = Non(MetricStatus.Undefined, MetricUnit.Currency, MetricReason.NoLosingTrades);

    public sealed record Literal(
        string Name, decimal[] Nets, int[] HoldingBars, decimal[] PointEquity, int[] Position, int HistoryStart,
        Dictionary<string, MetricValue> Expected, int? TradingStart = null, int[]? RoundTripBars = null)
    {
        public override string ToString() => Name;
    }

    private static Literal L(string name, decimal[] nets, int[] holding, decimal[] equity, int[] position, int historyStart, params (string Metric, MetricValue Value)[] expected) =>
        new(name, nets, holding, equity, position, historyStart, expected.ToDictionary(entry => entry.Metric, entry => entry.Value));

    /// <summary>Hand-derived right-censoring flags (MaxDepth, Longest) per literal case; a case not listed here does not pin flags.</summary>
    private static readonly Dictionary<string, (bool? MaxDepth, bool? Longest)> LiteralFlags = new()
    {
        ["mixed trades, two drawdown episodes"] = (false, false),        // both episodes recovered
        ["all wins"] = (false, false),                                   // no drawdown at all
        ["all losses, unrecovered drawdown"] = (true, true),             // the only episode never regains its peak
        ["no trades, recovered drawdown"] = (false, false),
        ["equal highs and equal depths"] = (false, false),               // the last bar regains the peak
        ["deepest episode is not the longest"] = (false, false),
        ["history offset with a warm-up position"] = (false, false),     // an unrecovered episode exists (1 bar) but a completed 2-bar one is longer and deeper
        ["unrecovered episode ties a completed episode"] = (false, true),// deepest completed (2 bars); the open one also has 2 bars -> lower bound
        ["empty sample"] = (null, null),                                 // metrics are not Valid -> no flag
    };

    public static IEnumerable<object[]> HandDerived()
    {
        yield return new object[]
        {
            // E = 100,110,105,100,110,120,118,119,117,121: episode 1 (peak idx1, recovers idx4) 3 bars depth 10/110; episode 2 (idx5 -> idx9) 4 bars depth 3/120.
            L("mixed trades, two drawdown episodes",
                new[] { 10m, -5m, 10m, 10m, -5m, 0m, -2m }, new[] { 1, 2, 3, 4, 5, 6, 7 },
                new[] { 110m, 105m, 100m, 110m, 120m, 118m, 119m, 117m, 121m }, new int[9], 0,
                ("GrossProfit", Ok(30m, MetricUnit.Currency)), ("GrossLoss", Ok(12m, MetricUnit.Currency)),
                ("AverageWin", Ok(10m, MetricUnit.Currency)), ("AverageLoss", Ok(4m, MetricUnit.Currency)),
                ("PayoffRatio", Ok(2.5m, MetricUnit.Dimensionless)), ("LargestWin", Ok(10m, MetricUnit.Currency)),
                ("LargestLoss", Ok(5m, MetricUnit.Currency)), ("AverageHoldingPeriod", Ok(4m, MetricUnit.Bars)),
                ("MaxConsecutiveWins", Ok(2m, MetricUnit.Count)), ("MaxConsecutiveLosses", Ok(1m, MetricUnit.Count)),
                ("MaxDepthDrawdownDuration", Ok(3m, MetricUnit.Bars)), ("LongestDrawdownDuration", Ok(4m, MetricUnit.Bars)),
                ("TimeInMarket", Ok(0m, MetricUnit.Bars)), ("Exposure", Ok(0m, MetricUnit.ExposureRatio))),
        };
        yield return new object[]
        {
            L("all wins",
                new[] { 3m, 3m, 3m }, new[] { 0, 0, 2 }, new[] { 101m, 102m, 103m }, new[] { 1, 1, 0 }, 0,
                ("GrossProfit", Ok(9m, MetricUnit.Currency)), ("GrossLoss", Ok(0m, MetricUnit.Currency)),
                ("AverageWin", Ok(3m, MetricUnit.Currency)), ("AverageLoss", NoLossesCurrency),
                ("PayoffRatio", Non(MetricStatus.PositiveInfinity, MetricUnit.Dimensionless, MetricReason.ZeroDivisor)),
                ("LargestWin", Ok(3m, MetricUnit.Currency)), ("LargestLoss", NoLossesCurrency),
                ("AverageHoldingPeriod", Ok(2m / 3m, MetricUnit.Bars)),
                ("MaxConsecutiveWins", Ok(3m, MetricUnit.Count)), ("MaxConsecutiveLosses", Ok(0m, MetricUnit.Count)),
                ("MaxDepthDrawdownDuration", Ok(0m, MetricUnit.Bars)), ("LongestDrawdownDuration", Ok(0m, MetricUnit.Bars)),
                ("TimeInMarket", Ok(2m, MetricUnit.Bars)), ("Exposure", Ok(2m / 3m, MetricUnit.ExposureRatio))),
        };
        yield return new object[]
        {
            // E = 100,99,98,90,95: the peak at idx0 is never regained, so the unrecovered episode is counted to the last bar (4).
            L("all losses, unrecovered drawdown",
                new[] { -2m, -4m }, new[] { 1, 1 }, new[] { 99m, 98m, 90m, 95m }, new[] { 0, -1, -1, -1 }, 0,
                ("GrossProfit", Ok(0m, MetricUnit.Currency)), ("GrossLoss", Ok(6m, MetricUnit.Currency)),
                ("AverageWin", NoWinsCurrency), ("AverageLoss", Ok(3m, MetricUnit.Currency)),
                ("PayoffRatio", Non(MetricStatus.Undefined, MetricUnit.Dimensionless, MetricReason.NoWinningTrades)),
                ("LargestWin", NoWinsCurrency), ("LargestLoss", Ok(4m, MetricUnit.Currency)),
                ("AverageHoldingPeriod", Ok(1m, MetricUnit.Bars)),
                ("MaxConsecutiveWins", Ok(0m, MetricUnit.Count)), ("MaxConsecutiveLosses", Ok(2m, MetricUnit.Count)),
                ("MaxDepthDrawdownDuration", Ok(4m, MetricUnit.Bars)), ("LongestDrawdownDuration", Ok(4m, MetricUnit.Bars)),
                ("TimeInMarket", Ok(3m, MetricUnit.Bars)), ("Exposure", Ok(0.75m, MetricUnit.ExposureRatio))),
        };
        yield return new object[]
        {
            L("all breakeven",
                new[] { 0m, 0m }, new[] { 3, 5 }, new[] { 100m, 100m }, new[] { 0, 0 }, 0,
                ("GrossProfit", Ok(0m, MetricUnit.Currency)), ("GrossLoss", Ok(0m, MetricUnit.Currency)),
                ("AverageWin", NoWinsCurrency), ("AverageLoss", NoLossesCurrency),
                ("PayoffRatio", Non(MetricStatus.Undefined, MetricUnit.Dimensionless, MetricReason.AllBreakeven)),
                ("LargestWin", NoWinsCurrency), ("LargestLoss", NoLossesCurrency),
                ("AverageHoldingPeriod", Ok(4m, MetricUnit.Bars)),
                ("MaxConsecutiveWins", Ok(0m, MetricUnit.Count)), ("MaxConsecutiveLosses", Ok(0m, MetricUnit.Count)),
                ("MaxDepthDrawdownDuration", Ok(0m, MetricUnit.Bars)), ("LongestDrawdownDuration", Ok(0m, MetricUnit.Bars)),
                ("TimeInMarket", Ok(0m, MetricUnit.Bars)), ("Exposure", Ok(0m, MetricUnit.ExposureRatio))),
        };
        yield return new object[]
        {
            L("single winning trade, always in the market",
                new[] { 7m }, new[] { 4 }, new[] { 100m }, new[] { 1 }, 0,
                ("GrossProfit", Ok(7m, MetricUnit.Currency)), ("GrossLoss", Ok(0m, MetricUnit.Currency)),
                ("AverageWin", Ok(7m, MetricUnit.Currency)), ("AverageLoss", NoLossesCurrency),
                ("PayoffRatio", Non(MetricStatus.PositiveInfinity, MetricUnit.Dimensionless, MetricReason.ZeroDivisor)),
                ("LargestWin", Ok(7m, MetricUnit.Currency)), ("LargestLoss", NoLossesCurrency),
                ("AverageHoldingPeriod", Ok(4m, MetricUnit.Bars)),
                ("MaxConsecutiveWins", Ok(1m, MetricUnit.Count)), ("MaxConsecutiveLosses", Ok(0m, MetricUnit.Count)),
                ("MaxDepthDrawdownDuration", Ok(0m, MetricUnit.Bars)), ("LongestDrawdownDuration", Ok(0m, MetricUnit.Bars)),
                ("TimeInMarket", Ok(1m, MetricUnit.Bars)), ("Exposure", Ok(1m, MetricUnit.ExposureRatio))),
        };
        yield return new object[]
        {
            // E = 100,90,100: a recovered episode of 2 bars (E[2] >= peak 100 counts as recovery).
            L("no trades, recovered drawdown",
                Array.Empty<decimal>(), Array.Empty<int>(), new[] { 90m, 100m }, new[] { 0, 0 }, 0,
                ("GrossProfit", NoTradesCurrency), ("GrossLoss", NoTradesCurrency), ("AverageWin", NoTradesCurrency), ("AverageLoss", NoTradesCurrency),
                ("PayoffRatio", Non(MetricStatus.InsufficientData, MetricUnit.Dimensionless, MetricReason.NoClosedTrades)),
                ("LargestWin", NoTradesCurrency), ("LargestLoss", NoTradesCurrency),
                ("AverageHoldingPeriod", Non(MetricStatus.InsufficientData, MetricUnit.Bars, MetricReason.NoClosedTrades)),
                ("MaxConsecutiveWins", Non(MetricStatus.InsufficientData, MetricUnit.Count, MetricReason.NoClosedTrades)),
                ("MaxConsecutiveLosses", Non(MetricStatus.InsufficientData, MetricUnit.Count, MetricReason.NoClosedTrades)),
                ("MaxDepthDrawdownDuration", Ok(2m, MetricUnit.Bars)), ("LongestDrawdownDuration", Ok(2m, MetricUnit.Bars)),
                ("TimeInMarket", Ok(0m, MetricUnit.Bars)), ("Exposure", Ok(0m, MetricUnit.ExposureRatio))),
        };
        yield return new object[]
        {
            // E = 100,90,100,90,100: two equal-depth episodes of 2 bars each; the first one wins the tie, the longest is also 2.
            L("equal highs and equal depths",
                Array.Empty<decimal>(), Array.Empty<int>(), new[] { 90m, 100m, 90m, 100m }, new int[4], 0,
                ("MaxDepthDrawdownDuration", Ok(2m, MetricUnit.Bars)), ("LongestDrawdownDuration", Ok(2m, MetricUnit.Bars))),
        };
        yield return new object[]
        {
            // E = 100,95,90,100,80,120.
            // episode A: peak idx0, below idx1..2, recovers idx3 (E=100 >= 100) -> 3 bars, depth 10/100.
            // episode B: peak idx3, below idx4, recovers idx5 -> 2 bars, depth 20/100 (deepest) -> MaxDepthDrawdownDuration 2, Max 3.
            L("deepest episode is not the longest",
                Array.Empty<decimal>(), Array.Empty<int>(), new[] { 95m, 90m, 100m, 80m, 120m }, new int[5], 0,
                ("MaxDepthDrawdownDuration", Ok(2m, MetricUnit.Bars)), ("LongestDrawdownDuration", Ok(3m, MetricUnit.Bars))),
        };
        yield return new object[]
        {
            // history offset 2: the sample is points[2..] = 90,100,95, E = 100,90,100,95. Positions per bar 1,1,1,0,0: only the sample bars
            // 2..4 (1,0,0) count, so a position opened in the warm-up must be carried by the cumulative fills: TimeInMarket 1 of 3.
            L("history offset with a warm-up position",
                Array.Empty<decimal>(), Array.Empty<int>(), new[] { 100m, 100m, 90m, 100m, 95m }, new[] { 1, 1, 1, 0, 0 }, 2,
                ("MaxDepthDrawdownDuration", Ok(2m, MetricUnit.Bars)), ("LongestDrawdownDuration", Ok(2m, MetricUnit.Bars)),
                ("TimeInMarket", Ok(1m, MetricUnit.Bars)), ("Exposure", Ok(1m / 3m, MetricUnit.ExposureRatio))),
        };
        yield return new object[]
        {
            // A same-bar long -> short reversal is ONE in-market bar (never two): positions 1,-1,-1,0 -> 3 of 4 bars.
            L("reversal is not double counted",
                Array.Empty<decimal>(), Array.Empty<int>(), new[] { 100m, 100m, 100m, 100m }, new[] { 1, -1, -1, 0 }, 0,
                ("TimeInMarket", Ok(3m, MetricUnit.Bars)), ("Exposure", Ok(0.75m, MetricUnit.ExposureRatio))),
        };
        yield return new object[]
        {
            // A breakeven trade resets BOTH streaks: W W B W W W L L B L -> longest win run 3, longest loss run 2.
            L("breakeven resets both streaks",
                new[] { 5m, 5m, 0m, 5m, 5m, 5m, -1m, -1m, 0m, -1m }, new int[10], new[] { 100m }, new[] { 0 }, 0,
                ("GrossProfit", Ok(25m, MetricUnit.Currency)), ("GrossLoss", Ok(3m, MetricUnit.Currency)),
                ("AverageWin", Ok(5m, MetricUnit.Currency)), ("AverageLoss", Ok(1m, MetricUnit.Currency)),
                ("PayoffRatio", Ok(5m, MetricUnit.Dimensionless)), ("LargestWin", Ok(5m, MetricUnit.Currency)),
                ("LargestLoss", Ok(1m, MetricUnit.Currency)), ("AverageHoldingPeriod", Ok(0m, MetricUnit.Bars)),
                ("MaxConsecutiveWins", Ok(3m, MetricUnit.Count)), ("MaxConsecutiveLosses", Ok(2m, MetricUnit.Count))),
        };
        yield return new object[]
        {
            L("fractional amounts",
                new[] { 0.5m, -0.25m, 1.25m }, new[] { 1, 1, 1 }, new[] { 100m }, new[] { 0 }, 0,
                ("GrossProfit", Ok(1.75m, MetricUnit.Currency)), ("GrossLoss", Ok(0.25m, MetricUnit.Currency)),
                ("AverageWin", Ok(0.875m, MetricUnit.Currency)), ("AverageLoss", Ok(0.25m, MetricUnit.Currency)),
                ("PayoffRatio", Ok(3.5m, MetricUnit.Dimensionless)), ("LargestWin", Ok(1.25m, MetricUnit.Currency)),
                ("LargestLoss", Ok(0.25m, MetricUnit.Currency))),
        };
        yield return new object[]
        {
            // A trade opened and closed on bar 1 (Buy then Sell fill, flat Close snapshot) counts as one in-market bar; it is a 0-bar trade for
            // AverageHoldingPeriod and, being the only trade, a 1-win streak of net +1.
            L("same-bar round trip on a flat bar",
                Array.Empty<decimal>(), Array.Empty<int>(), new[] { 100m, 100m, 100m }, new[] { 0, 0, 0 }, 0,
                ("GrossProfit", Ok(1m, MetricUnit.Currency)), ("AverageHoldingPeriod", Ok(0m, MetricUnit.Bars)),
                ("MaxConsecutiveWins", Ok(1m, MetricUnit.Count)),
                ("TimeInMarket", Ok(1m, MetricUnit.Bars)), ("Exposure", Ok(1m / 3m, MetricUnit.ExposureRatio)))
                with { RoundTripBars = new[] { 1 } },
        };
        yield return new object[]
        {
            // Warm-up bars 0..1 lie between HistoryStart (0) and TradingStart (2): the denominator is the 4 bars from bar 2, all held -> Exposure 1.
            L("trading start excludes warm-up bars from the exposure denominator",
                Array.Empty<decimal>(), Array.Empty<int>(), new[] { 100m, 100m, 100m, 100m, 100m, 100m }, new[] { 0, 0, 1, 1, 1, 1 }, 0,
                ("TimeInMarket", Ok(4m, MetricUnit.Bars)), ("Exposure", Ok(1m, MetricUnit.ExposureRatio)))
                with { TradingStart = 2 },
        };
        yield return new object[]
        {
            // E = 100,90,100,95,95: episode A (peak idx0, recovers idx2) 2 bars, depth 0.1 (deepest); episode B (peak idx2, never regained)
            // 2 bars to the last bar, depth 0.05. MaxDepth = A -> recovered, not censored. Longest = 2 is reached by BOTH a completed and an
            // unrecovered episode: the value is conservatively flagged as a lower bound.
            L("unrecovered episode ties a completed episode",
                Array.Empty<decimal>(), Array.Empty<int>(), new[] { 90m, 100m, 95m, 95m }, new int[4], 0,
                ("MaxDepthDrawdownDuration", Ok(2m, MetricUnit.Bars)), ("LongestDrawdownDuration", Ok(2m, MetricUnit.Bars))),
        };
        yield return new object[]
        {
            // Empty sample (history offset consumes every point): both bar-based metrics are InsufficientData(EmptyInput).
            L("empty sample",
                Array.Empty<decimal>(), Array.Empty<int>(), new[] { 100m, 100m }, new[] { 1, 1 }, 2,
                ("MaxDepthDrawdownDuration", Non(MetricStatus.InsufficientData, MetricUnit.Bars, MetricReason.EmptyInput)),
                ("LongestDrawdownDuration", Non(MetricStatus.InsufficientData, MetricUnit.Bars, MetricReason.EmptyInput)),
                ("TimeInMarket", Non(MetricStatus.InsufficientData, MetricUnit.Bars, MetricReason.EmptyInput)),
                ("Exposure", Non(MetricStatus.InsufficientData, MetricUnit.ExposureRatio, MetricReason.EmptyInput))),
        };
    }

    [Theory]
    [MemberData(nameof(HandDerived))]
    public void HandDerivedLiterals_MatchBothProductionAndTheReference(Literal literal)
    {
        BacktestResult result = Build(literal.Nets, literal.HoldingBars, literal.PointEquity, literal.Position, roundTripBars: literal.RoundTripBars);
        BacktestReport report = Generate(result, literal.HistoryStart, literal.TradingStart);
        Dictionary<string, MetricValue> production = Actual(report).ToDictionary(entry => entry.Name, entry => entry.Metric);
        Dictionary<string, MetricValue> reference = ExtendedMetricsReference.Compute(result, literal.HistoryStart, literal.TradingStart ?? literal.HistoryStart, report.CAGR)
            .Named().ToDictionary(entry => entry.Name, entry => entry.Metric);

        foreach ((string name, MetricValue expected) in literal.Expected)
        {
            AssertSameMetric($"{literal.Name} (production)", name, expected, production[name]);
            AssertSameMetric($"{literal.Name} (reference)", name, expected, reference[name]);
        }

        if (LiteralFlags.TryGetValue(literal.Name, out (bool? MaxDepth, bool? Longest) flags))
        {
            ExtendedMetricsReference.Expected referenceFlags = ExtendedMetricsReference.Compute(result, literal.HistoryStart, literal.TradingStart ?? literal.HistoryStart, report.CAGR);
            Assert.True(flags.MaxDepth == report.MaxDepthDrawdownDurationRightCensored, $"{literal.Name}: production max-depth flag");
            Assert.True(flags.Longest == report.LongestDrawdownDurationRightCensored, $"{literal.Name}: production longest flag");
            Assert.True(flags.MaxDepth == referenceFlags.MaxDepthDrawdownDurationRightCensored, $"{literal.Name}: reference max-depth flag");
            Assert.True(flags.Longest == referenceFlags.LongestDrawdownDurationRightCensored, $"{literal.Name}: reference longest flag");
        }
    }

    // ---- ExposureAdjustedCAGR precedence ------------------------------------------------------------------------------------------------

    [Fact]
    public void ExposureAdjustedCagr_FollowsThePrecedenceTable()
    {
        MetricValue validCagr = Ok(0.1m, MetricUnit.ReturnRatio);
        MetricValue undefinedCagr = Non(MetricStatus.Undefined, MetricUnit.ReturnRatio, MetricReason.NegativeFinalEquity);
        MetricValue validExposure = Ok(0.5m, MetricUnit.ExposureRatio);
        MetricValue zeroExposure = Ok(0m, MetricUnit.ExposureRatio);
        MetricValue emptyExposure = Non(MetricStatus.InsufficientData, MetricUnit.ExposureRatio, MetricReason.EmptyInput);

        // 1. a non-Valid CAGR wins over everything (even a zero or non-Valid Exposure)
        Assert.Equal(Non(MetricStatus.Undefined, MetricUnit.ReturnRatio, MetricReason.NegativeFinalEquity), ExposureMetricsCalculator.ComputeExposureAdjustedCagr(undefinedCagr, zeroExposure));
        Assert.Equal(Non(MetricStatus.Undefined, MetricUnit.ReturnRatio, MetricReason.NegativeFinalEquity), ExposureMetricsCalculator.ComputeExposureAdjustedCagr(undefinedCagr, emptyExposure));
        // 2. then a non-Valid Exposure
        Assert.Equal(Non(MetricStatus.InsufficientData, MetricUnit.ReturnRatio, MetricReason.EmptyInput), ExposureMetricsCalculator.ComputeExposureAdjustedCagr(validCagr, emptyExposure));
        // 3. then Exposure == 0
        Assert.Equal(Non(MetricStatus.Undefined, MetricUnit.ReturnRatio, MetricReason.ZeroDivisor), ExposureMetricsCalculator.ComputeExposureAdjustedCagr(validCagr, zeroExposure));
        // 4. otherwise the plain quotient
        Assert.Equal(Ok(0.2m, MetricUnit.ReturnRatio), ExposureMetricsCalculator.ComputeExposureAdjustedCagr(validCagr, validExposure));
    }
}
