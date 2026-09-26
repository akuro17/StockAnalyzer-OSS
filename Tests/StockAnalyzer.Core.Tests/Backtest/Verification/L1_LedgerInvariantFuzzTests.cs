using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.Verification.LedgerAssertions;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// L1 property-style test without an extra package: seeded random OHLCV + seeded random strategy (Market/MOC/Limit/Stop/StopLimit,
/// Long/Short, GTC/GTD) across several account configurations. Instead of expected values it asserts the laws that must NEVER
/// break: per-bar Cash/Equity ledger match, trade-based P&amp;L identity, fill sanity, chronology.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class L1_LedgerInvariantFuzzTests
{
    private const decimal FuzzCapital = 10_000m;

    /// <summary>Six account shapes: cash-equivalent, costed, half margin, thirty-percent margin with percent sizing, high leverage (10x, so forced liquidations occur), full-equity sizing.</summary>
    private static BacktestConfiguration MakeFuzzConfig(int index) => index switch
    {
        0 => VerificationHarness.MakeConfig(FuzzCapital, sizingParameter: 10m),
        1 => VerificationHarness.MakeConfig(FuzzCapital, commissionFlat: 1m, commissionPerUnit: 0.01m, slippageRatio: 0.001m, sizingParameter: 10m),
        2 => VerificationHarness.MakeConfig(FuzzCapital, commissionFlat: 1m, slippageRatio: 0.002m, sizingParameter: 20m, initialMarginRatio: 0.5m, maintenanceMarginRatio: 0.25m),
        3 => VerificationHarness.MakeConfig(FuzzCapital, commissionFlat: 1m, commissionPerUnit: 0.01m, sizingModel: PositionSizingModel.PercentOfEquity, sizingParameter: 0.5m,
            initialMarginRatio: 0.3m, maintenanceMarginRatio: 0.2m, liquidationPenaltyRatio: 0.005m),
        4 => VerificationHarness.MakeConfig(FuzzCapital, slippageRatio: 0.001m, sizingParameter: 400m, initialMarginRatio: 0.1m, maintenanceMarginRatio: 0.05m, liquidationPenaltyRatio: 0.01m),
        5 => VerificationHarness.MakeConfig(FuzzCapital, commissionFlat: 1m, sizingModel: PositionSizingModel.PercentOfEquity, sizingParameter: 1m),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    [Fact]
    public void FuzzConfigCount_MatchesTheParameterClass()
    {
        for (int i = 0; i < VerificationParameters.FuzzConfigCount; i++) Assert.NotNull(MakeFuzzConfig(i));
        Assert.Throws<ArgumentOutOfRangeException>(() => MakeFuzzConfig(VerificationParameters.FuzzConfigCount));
    }

    [Theory]
    [MemberData(nameof(FuzzCases))]
    public void RandomOhlcvAndRandomOrders_NeverBreakTheLedgerInvariants(int seed, int configIndex)
    {
        ImmutableArray<CandleData> bars = SyntheticBars.SeededRandomWalk(seed, VerificationParameters.FuzzBarCount, VerificationParameters.FuzzStartPrice);
        BacktestInput input = VerificationHarness.MakeInput(bars);
        BacktestConfiguration config = MakeFuzzConfig(configIndex);

        BacktestResult result = VerificationHarness.Run(input, config, new SeededRandomStrategy(seed));

        Assert.True(result.Status is RunStatus.Completed or RunStatus.Insolvent, $"Unexpected status {result.Status}");
        AllInvariants(input, config, result);
        TradePnLConservation(input, config, result);

        if (result.Status == RunStatus.Insolvent)
        {
            Assert.True(result.EquityPoints[^1].Equity <= 0m || result.Trades.Any(t => t.IsForcedLiquidation),
                "Insolvent run must end with Equity <= 0 or a forced liquidation.");
        }
    }

    [Fact]
    public void FuzzCorpus_ActuallyExercisesEveryOrderTypeBothSidesLiquidationsAndRejections()
    {
        // Guards the fuzz test itself: without this a generator regression could silently turn every case into "no trades".
        var orderTypes = new System.Collections.Generic.HashSet<OrderType>();
        var tradeSides = new System.Collections.Generic.HashSet<TradeSide>();
        var filledOrderTypes = new System.Collections.Generic.HashSet<OrderType>();
        int forcedLiquidations = 0;
        int rejections = 0;
        int expirations = 0;

        foreach (object[] c in VerificationParameters.FuzzCases())
        {
            int seed = (int)c[0];
            BacktestConfiguration config = MakeFuzzConfig((int)c[1]);
            BacktestInput input = VerificationHarness.MakeInput(SyntheticBars.SeededRandomWalk(seed, VerificationParameters.FuzzBarCount, VerificationParameters.FuzzStartPrice));
            BacktestResult r = VerificationHarness.Run(input, config, new SeededRandomStrategy(seed));

            foreach (BacktestOrder o in r.Orders)
            {
                orderTypes.Add(o.Type);
                if (o.Status == OrderStatus.Filled) filledOrderTypes.Add(o.Type);
                if (o.Status == OrderStatus.Rejected) rejections++;
                if (o.Status == OrderStatus.Expired) expirations++;
            }
            foreach (BacktestTrade t in r.Trades)
            {
                tradeSides.Add(t.Side);
                if (t.IsForcedLiquidation) forcedLiquidations++;
            }
        }

        Assert.Equal(5, orderTypes.Count);
        Assert.Equal(5, filledOrderTypes.Count);
        Assert.Equal(2, tradeSides.Count);
        Assert.True(forcedLiquidations > 0, "no forced liquidation was exercised");
        Assert.True(rejections > 0, "no rejected order was exercised");
        Assert.True(expirations > 0, "no expired order was exercised");
    }

    public static System.Collections.Generic.IEnumerable<object[]> FuzzCases() => VerificationParameters.FuzzCases();
}
