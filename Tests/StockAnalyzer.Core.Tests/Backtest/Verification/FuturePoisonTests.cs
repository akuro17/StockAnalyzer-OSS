using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// Future Poison: run on Day 1-100, then replace Day 51-100 (0-based index 50+) with absurd bars and re-run. Everything that is
/// decided or executed on Day 1-50 - signals, orders, fills, trades, per-bar equity - must be IDENTICAL, otherwise future data
/// leaked into the past. The literal poison value -999 is not usable: BacktestInput rejects non-positive prices, so the poison is
/// "valid but absurd" (x1000 / x0.001 alternating, OHLC ordering preserved).
/// </summary>
[Trait("Category", "BacktestVerification")]
public class FuturePoisonTests
{
    private static ImmutableArray<CandleData> Original() => SyntheticBars.SeededRandomWalk(
        VerificationParameters.PrimarySeed, VerificationParameters.PoisonBarCount, VerificationParameters.FuzzStartPrice);

    private static BacktestConfiguration Config() => VerificationHarness.MakeConfig(
        initialCapital: 100_000m, commissionFlat: 1m, commissionPerUnit: 0.01m, slippageRatio: 0.001m,
        sizingParameter: 50m, initialMarginRatio: 0.5m, maintenanceMarginRatio: 0.25m, liquidationPenaltyRatio: 0.005m);

    private static BacktestResult RunSma(ImmutableArray<CandleData> bars)
        => VerificationHarness.Run(VerificationHarness.MakeInput(bars, VerificationParameters.SmaPeriod), Config(),
            VerificationHarness.SmaTrendStrategy(VerificationParameters.SmaPeriod));

    private static BacktestResult RunRandom(ImmutableArray<CandleData> bars)
        => VerificationHarness.Run(VerificationHarness.MakeInput(bars), Config(), new SeededRandomStrategy(VerificationParameters.PrimarySeed));

    /// <summary>
    /// Lists every difference between the two runs that is fully determined by bars with index &lt; <paramref name="k"/>.
    /// Order terminal state is intentionally excluded because it may be decided in the poisoned suffix. Tests that compare
    /// terminal order state run both inputs truncated to <paramref name="k"/>.
    /// </summary>
    private static List<string> FindPrefixDifferences(BacktestResult a, BacktestResult b, int k)
    {
        var diffs = new List<string>();

        if (a.EquityPoints.Length < k || b.EquityPoints.Length < k)
        {
            diffs.Add($"a run stopped before bar {k} (a={a.EquityPoints.Length}, b={b.EquityPoints.Length} equity points)");
            return diffs;
        }
        for (int i = 0; i < k; i++)
        {
            if (a.EquityPoints[i] != b.EquityPoints[i]) diffs.Add($"EquityPoint[{i}] differs");
        }

        if (!a.Signals.Where(s => s.BarIndex < k).SequenceEqual(b.Signals.Where(s => s.BarIndex < k))) diffs.Add("Signals differ");
        if (!a.Fills.Where(f => f.BarIndex < k).SequenceEqual(b.Fills.Where(f => f.BarIndex < k))) diffs.Add("Fills differ");
        if (!a.Trades.Where(t => t.ExitBar < k).SequenceEqual(b.Trades.Where(t => t.ExitBar < k))) diffs.Add("Trades differ");

        BacktestOrder[] ordersA = a.Orders.Where(o => o.SubmittedBar < k).ToArray();
        BacktestOrder[] ordersB = b.Orders.Where(o => o.SubmittedBar < k).ToArray();
        if (ordersA.Length != ordersB.Length)
        {
            diffs.Add("Order count differs");
            return diffs;
        }
        for (int i = 0; i < ordersA.Length; i++)
        {
            BacktestOrder x = ordersA[i];
            BacktestOrder y = ordersB[i];
            bool submissionEqual = OrderSubmissionFieldsEqual(x, y);
            if (!submissionEqual) diffs.Add($"Order {x.OrderId} submission fields differ");
        }
        return diffs;
    }

    private static bool OrderSubmissionFieldsEqual(BacktestOrder x, BacktestOrder y) =>
        x.OrderId == y.OrderId && x.Side == y.Side && x.Type == y.Type && x.Quantity == y.Quantity
        && x.LimitPrice == y.LimitPrice && x.StopPrice == y.StopPrice && x.TimeInForce == y.TimeInForce
        && x.SubmittedBar == y.SubmittedBar && x.EarliestFillBar == y.EarliestFillBar;

    [Fact]
    public void RealSmaStrategy_PoisoningDay51To100_DoesNotChangeAnythingDecidedOnDay1To50()
    {
        ImmutableArray<CandleData> original = Original();
        BacktestResult a = RunSma(original);
        BacktestResult b = RunSma(SyntheticBars.Poison(original, VerificationParameters.PoisonStartIndex));

        Assert.True(a.Fills.Count(f => f.BarIndex < VerificationParameters.PoisonStartIndex) >= 2,
            "Test data must trade during Day 1-50, otherwise the comparison proves nothing.");
        Assert.Empty(FindPrefixDifferences(a, b, VerificationParameters.PoisonStartIndex));
    }

    [Fact]
    public void SeededRandomOrders_PoisoningDay51To100_DoesNotChangeAnythingDecidedOnDay1To50()
    {
        ImmutableArray<CandleData> original = Original();
        BacktestResult a = RunRandom(original);
        BacktestResult b = RunRandom(SyntheticBars.Poison(original, VerificationParameters.PoisonStartIndex));

        Assert.True(a.Fills.Count(f => f.BarIndex < VerificationParameters.PoisonStartIndex) >= 2,
            "Test data must trade during Day 1-50, otherwise the comparison proves nothing.");
        Assert.Empty(FindPrefixDifferences(a, b, VerificationParameters.PoisonStartIndex));
    }

    [Fact]
    public void PositiveControl_PoisoningEarlier_IsDetectedByTheSameComparer()
    {
        // Poison from Day 26: bars 25..49 are inside the compared prefix, so the comparer MUST report a difference.
        ImmutableArray<CandleData> original = Original();
        BacktestResult a = RunRandom(original);
        BacktestResult b = RunRandom(SyntheticBars.Poison(original, VerificationParameters.PoisonControlStartIndex));

        Assert.NotEmpty(FindPrefixDifferences(a, b, VerificationParameters.PoisonStartIndex));
    }

    [Fact]
    public void PoisonIsEffective_TheSuffixResultsDoDiffer()
    {
        ImmutableArray<CandleData> original = Original();
        BacktestResult a = RunRandom(original);
        BacktestResult b = RunRandom(SyntheticBars.Poison(original, VerificationParameters.PoisonStartIndex));

        Assert.NotEqual(BacktestSnapshot.ToCanonicalJson(a), BacktestSnapshot.ToCanonicalJson(b));
    }

    [Fact]
    public void PoisonedBars_RemainValidInput()
    {
        ImmutableArray<CandleData> poisoned = SyntheticBars.Poison(Original(), VerificationParameters.PoisonStartIndex);
        Assert.All(poisoned, b => Assert.True(b.IsValid() && b.Open > 0m && b.Low > 0m));
        // Would throw if any bar were invalid.
        _ = VerificationHarness.MakeInput(poisoned);
    }

    [Fact]
    public void PrefixComparer_Boundaries_K0_K1_AndN_AreDefined()
    {
        ImmutableArray<CandleData> original = Original();
        BacktestResult baseline = RunRandom(original);
        BacktestResult poisoned = RunRandom(SyntheticBars.Poison(original, VerificationParameters.PoisonStartIndex));

        Assert.Empty(FindPrefixDifferences(baseline, poisoned, 0));
        Assert.Empty(FindPrefixDifferences(baseline, poisoned, 1));
        Assert.Empty(FindPrefixDifferences(baseline, baseline, original.Length));
    }

    [Theory]
    [InlineData(OrderType.Limit)]
    [InlineData(OrderType.StopLimit)]
    public void RealEngine_GtcOrderSuffixStateDiffers_ButPrefixProjectionAndTruncatedStateMatch(OrderType type)
    {
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ImmutableArray<CandleData> prefix = ImmutableArray.Create(
            new CandleData(start, 100m, 100m, 100m, 100m, 1000),
            new CandleData(start.AddDays(1), 100m, 105m, 90m, 100m, 1000));
        ImmutableArray<CandleData> fillingSuffix = prefix.Add(
            new CandleData(start.AddDays(2), 100m, 120m, 40m, 80m, 1000));
        ImmutableArray<CandleData> untouchedSuffix = prefix.Add(
            new CandleData(start.AddDays(2), 100m, 105m, 80m, 100m, 1000));
        var strategy = new OneShotGtcStrategy(type);

        BacktestResult filling = VerificationHarness.Run(VerificationHarness.MakeInput(fillingSuffix), Config(), strategy);
        BacktestResult untouched = VerificationHarness.Run(VerificationHarness.MakeInput(untouchedSuffix), Config(), new OneShotGtcStrategy(type));
        BacktestResult truncatedA = VerificationHarness.Run(VerificationHarness.MakeInput(prefix), Config(), new OneShotGtcStrategy(type));
        BacktestResult truncatedB = VerificationHarness.Run(VerificationHarness.MakeInput(prefix), Config(), new OneShotGtcStrategy(type));

        Assert.Empty(FindPrefixDifferences(filling, untouched, prefix.Length));
        Assert.NotEqual(Assert.Single(filling.Orders).Status, Assert.Single(untouched.Orders).Status);
        Assert.Equal(truncatedA.Orders, truncatedB.Orders);
    }

    private sealed class OneShotGtcStrategy : StockAnalyzer.Core.Services.Backtest.Engine.IBacktestStrategy
    {
        private readonly OrderType _type;

        public OneShotGtcStrategy(OrderType type) => _type = type;

        public string Name => "Suffix-sensitive GTC verification";

        public IReadOnlyList<StockAnalyzer.Core.Services.Backtest.Engine.StrategyIndicatorRequest> GetRequiredIndicators() =>
            Array.Empty<StockAnalyzer.Core.Services.Backtest.Engine.StrategyIndicatorRequest>();

        public StockAnalyzer.Core.Services.Backtest.Engine.StrategyOrderRequest? Evaluate(
            StockAnalyzer.Core.Services.Backtest.Engine.StrategyContext context) =>
            context.BarIndex == 0
                ? new StockAnalyzer.Core.Services.Backtest.Engine.StrategyOrderRequest(
                    SignalType.LongEntry,
                    _type,
                    LimitPrice: 50m,
                    StopPrice: _type == OrderType.StopLimit ? 110m : null,
                    new TimeInForce(true, -1),
                    "GTC prefix oracle")
                : null;
    }
}
