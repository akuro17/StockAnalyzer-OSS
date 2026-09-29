using System.Collections.Generic;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// Test-scale parameters shared by the L1 verification suite (Y:\Temp\sa_implementation_plan_BacktestL1Verification.md).
/// These are properties of the TEST DESIGN, not domain values - each one names the requirement it comes from so no
/// scenario embeds an unexplained number.
/// </summary>
internal static class VerificationParameters
{
    /// <summary>User requirement: "same input / seed / settings executed 100 times must match completely".</summary>
    public const int DeterminismRunCount = 100;

    /// <summary>User requirement (Future Poison): the run covers Day 1-100.</summary>
    public const int PoisonBarCount = 100;

    /// <summary>User requirement (Future Poison): Day 1-50 are preserved, Day 51-100 are poisoned. Day 51 == 0-based index 50.</summary>
    public const int PoisonStartIndex = 50;

    /// <summary>Earlier poison start used only by the positive control (proves the prefix comparer detects a real difference).</summary>
    public const int PoisonControlStartIndex = 25;

    /// <summary>Indicator causality test: candles per indicator (long enough that slow-warm-up indicators have values before the poison starts).</summary>
    public const int IndicatorBarCount = 400;

    /// <summary>Indicator causality test: first poisoned bar; values of bars below this index must not change.</summary>
    public const int IndicatorPoisonStartIndex = 300;

    /// <summary>Mild poison factors for indicators (exactly representable; a x1000 spike can overflow decimal inside squared/cubed indicator math).</summary>
    public const decimal IndicatorPoisonUpFactor = 8m;
    public const decimal IndicatorPoisonDownFactor = 0.125m;

    /// <summary>Bars per fuzz run (long enough for several entries/exits/liquidations, short enough to stay fast).</summary>
    public const int FuzzBarCount = 200;

    /// <summary>Starting price of every synthetic random walk.</summary>
    public const decimal FuzzStartPrice = 100m;

    /// <summary>Number of distinct fuzz seeds; each seed is combined with every fuzz configuration index.</summary>
    public const int FuzzSeedCount = 20;

    /// <summary>Number of fuzz configurations defined by <c>L1_LedgerInvariantFuzzTests.MakeFuzzConfig</c>.</summary>
    public const int FuzzConfigCount = 6;

    /// <summary>Seed used by single-seed scenarios (determinism, poison).</summary>
    public const int PrimarySeed = 20260919;

    /// <summary>SMA period of the real-indicator strategy used by determinism and poison tests.</summary>
    public const int SmaPeriod = 5;

    /// <summary>Price multiplier used by the scale-invariance metamorphic test (exactly representable in decimal).</summary>
    public const decimal ScaleFactor = 10m;

    /// <summary>Pivot of the additive price mirror used by the Long/Short symmetry test (must keep every mirrored price &gt; 0).</summary>
    public const decimal MirrorPivot = 200m;

    public static IEnumerable<object[]> FuzzCases()
    {
        for (int seed = 1; seed <= FuzzSeedCount; seed++)
        {
            for (int configIndex = 0; configIndex < FuzzConfigCount; configIndex++)
            {
                yield return new object[] { seed, configIndex };
            }
        }
    }
}
