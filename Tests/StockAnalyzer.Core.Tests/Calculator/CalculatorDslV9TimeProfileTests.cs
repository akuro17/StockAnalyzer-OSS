#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using Xunit;
using static StockAnalyzer.Core.Tests.Calculator.CalculatorDslTestHelpers;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v9 Batch 2 time-axis alignment and session opening-range tests.
/// </summary>
public sealed class CalculatorDslV9TimeProfileTests
{
    [Fact]
    public void AlignAsof_PerformsCausalAsOfJoinWithDuplicateSourceTimestamps()
    {
        // targetTimes before first srcTime receive fill (-99); duplicate srcTimes=10 select latest value (15).
        decimal[] aligned = Arr("align_asof([5, 10, 15, 20, 25], [10, 10, 20], [10, 15, 30], -99)");
        Assert.Equal(new[] { -99m, 15m, 15m, 30m, 30m }, aligned);

        Assert.Equal(new[] { 0m, 0m }, Arr("align_asof([1, 2], [], [], -0.0)"));
        AssertNoNegativeZero(Arr("align_asof([1, 2], [], [], -0.0)"), "align_asof");
        Assert.Empty(Arr("align_asof([], [1, 2], [10, 20])"));

        // MP-3: allowExactMatch = 0 prevents same-timestamp lookahead; maxTolerance caps staleness
        Assert.Equal(new[] { -99m, -99m, 10m, 10m, 30m }, Arr("align_asof([5, 10, 15, 20, 25], [10, 20], [10, 30], -99, 0)"));
        Assert.Equal(new[] { -99m, 10m, -99m, 30m, -99m }, Arr("align_asof([5, 10, 16, 20, 30], [10, 20], [10, 30], -99, 1, 5)"));
        Fails("align_asof([10], [10], [1], 0, 2)", CalculatorErrorCode.DomainError);
        Fails("align_asof([10], [10], [1], 0, 1, -2)", CalculatorErrorCode.DomainError);

        // Non-monotonic timestamps must fail with DomainError
        Fails("align_asof([10, 9], [1, 2], [10, 20])", CalculatorErrorCode.DomainError);
        Fails("align_asof([10, 11], [5, 4], [10, 20])", CalculatorErrorCode.DomainError);
        Fails("align_asof([10], [1, 2], [10])", CalculatorErrorCode.DimensionMismatch);
        Fails("align_asof(1, [1], [1])", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void ArrSessionRange_TracksOpeningRangeAcrossSessionsAndWarmup()
    {
        // Two sessions (1 and 2); openingBars = 2
        decimal[] openHigh = Arr("arr_session_range([10, 12, 15, 20, 18, 25], [8, 9, 10, 14, 15, 16], [1, 1, 1, 2, 2, 2], 2, 0, -1)");
        decimal[] openLow = Arr("arr_session_range([10, 12, 15, 20, 18, 25], [8, 9, 10, 14, 15, 16], [1, 1, 1, 2, 2, 2], 2, 1, -1)");

        Assert.Equal(new[] { -1m, 12m, 12m, -1m, 20m, 20m }, openHigh);
        Assert.Equal(new[] { -1m, 8m, 8m, -1m, 14m, 14m }, openLow);

        // Unified D-09 warmup: N < openingBars returns fill-filled length-N array without error
        Assert.Equal(new[] { -1m, -1m }, Arr("arr_session_range([10, 11], [9, 10], [1, 1], 5, 0, -1)"));
        Assert.Empty(Arr("arr_session_range([], [], [], 3, 0, -1)"));
        AssertNoNegativeZero(Arr("arr_session_range([1], [-0.0], [1], 1, 1, -0.0)"), "arr_session_range");

        // MP-3: skipFirstSession = 1 suppresses initial potentially truncated session
        Assert.Equal(
            new[] { -1m, -1m, -1m, -1m, 20m, 20m },
            Arr("arr_session_range([10, 12, 15, 20, 18, 25], [8, 9, 10, 14, 15, 16], [1, 1, 1, 2, 2, 2], 2, 0, -1, 1)"));
        Fails("arr_session_range([10], [8], [1], 1, 0, -1, 2)", CalculatorErrorCode.DomainError);

        // Error checks: high < low, invalid kind, invalid openingBars, dimension mismatch
        Fails("arr_session_range([10, 5], [8, 6], [1, 1], 1, 0)", CalculatorErrorCode.DomainError);
        Fails("arr_session_range([10], [8], [1], 1, 2)", CalculatorErrorCode.DomainError);
        Fails("arr_session_range([10], [8], [1], 0, 0)", CalculatorErrorCode.DomainError);
        Fails("arr_session_range([10], [8], [1], 513, 0)", CalculatorErrorCode.DomainError);
        Fails("arr_session_range([10, 11], [8], [1, 1], 1, 0)", CalculatorErrorCode.DimensionMismatch);
    }

    [Fact]
    public void VpBinsAndVpPoc_BinVolumesAndHandleZeroSpanAndPoolPaths()
    {
        // Prices [100, 102, 105, 110], volumes [10, 20, 50, 20], nBins = 2 -> delta = 10, binWidth = 5
        // [100, 105) -> bin 0 (100, 102 -> vol 30); [105, 110] -> bin 1 (105, 110 -> vol 70)
        Assert.Equal(new[] { 30m, 70m }, Arr("vp_bins([100, 102, 105, 110], [10, 20, 50, 20], 2)"));
        Assert.Equal(107.5m, Scalar("vp_poc([100, 102, 105, 110], [10, 20, 50, 20], 2)"));

        // Zero price span accumulates into bin 0 and POC returns the constant price
        Assert.Equal(new[] { 30m, 0m, 0m }, Arr("vp_bins([50, 50, 50], [10, 20, 0], 3)"));
        Assert.Equal(50m, Scalar("vp_poc([50, 50, 50], [10, 20, 0], 3)"));

        // Tie-break selects lowest bin index; nBins=128 exercises ArrayPool<decimal> (> 64)
        Assert.Equal(102.5m, Scalar("vp_poc([100, 110], [10, 10], 2)"));
        decimal[] bins128 = Arr("vp_bins([100, 228], [5, 15], 128)");
        Assert.Equal(128, bins128.Length);
        Assert.Equal(5m, bins128[0]);
        Assert.Equal(15m, bins128[127]);
        Assert.True(Scalar("vp_poc([100, 228], [5, 15], 128)") > 227m);

        // Zero total volume is valid for vp_bins but DomainError for vp_poc
        Assert.Equal(new[] { 0m, 0m }, Arr("vp_bins([100, 110], [0, 0], 2)"));
        Fails("vp_poc([100, 110], [0, 0], 2)", CalculatorErrorCode.DomainError);

        Fails("vp_bins([100, 110], [-1, 5], 2)", CalculatorErrorCode.DomainError);
        Fails("vp_bins([100, 110], [5], 2)", CalculatorErrorCode.DimensionMismatch);
        Fails("vp_bins([100], [5], 129)", CalculatorErrorCode.DomainError);
        Fails("vp_bins([], [], 2)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void VpBinsAndVpPoc_PreventDecimalBinWidthDriftAndPoolContamination()
    {
        // MP-1 (P-01): Non-terminating decimal priceSpan / nBins (nBins = 6) previously misclassified midpoint prices downward.
        Assert.Equal(new[] { 1m, 0m, 0m, 10m, 0m, 1m }, Arr("vp_bins([100, 102, 104], [1, 10, 1], 6)"));
        Assert.Equal(100m + ((7m * 4m) / 12m), Scalar("vp_poc([100, 102, 104], [1, 10, 1], 6)"));

        Assert.Equal(new[] { 100m, 0m, 0m, 500m, 0m, 100m }, Arr("vp_bins([1000, 1005, 1010], [100, 500, 100], 6)"));
        Assert.Equal(1000m + ((7m * 10m) / 12m), Scalar("vp_poc([1000, 1005, 1010], [100, 500, 100], 6)"));

        // ArrayPool zero-clear parity: consecutive nBins = 100 calls (> DecimalStackAllocThreshold = 64)
        decimal[] p1 = { 100m, 200m };
        decimal[] v1 = { 1_000_000m, 1m };
        decimal[] p2 = { 100m, 200m };
        decimal[] v2 = { 1m, 5m };
        Assert.True(StockAnalyzer.Core.MathUtils.DslTimeAndProfileMath.VolumeProfilePoc(p1, v1, 100, out _) == StockAnalyzer.Core.MathUtils.MathErrorCode.None);
        Assert.True(StockAnalyzer.Core.MathUtils.DslTimeAndProfileMath.VolumeProfilePoc(p2, v2, 100, out decimal secondPoc) == StockAnalyzer.Core.MathUtils.MathErrorCode.None);
        Assert.True(StockAnalyzer.Core.MathUtils.DslTimeAndProfileMath.VolumeProfilePoc(p2, v2, 100, out decimal isolatedPoc) == StockAnalyzer.Core.MathUtils.MathErrorCode.None);
        Assert.Equal(isolatedPoc, secondPoc);
    }

    [Fact]
    public void VpValueArea_ComputesValPocVahConsistentlyInExactDecimal()
    {
        // bins=[30, 50, 20], total=100, target=70 -> pocBin=1 (50), expands to lowerBin=0 (30 > 20) => [VAL=100, POC=105, VAH=106.666...]
        decimal[] va = Arr("vp_value_area([100, 105, 110], [30, 50, 20], 3, 0.70)");
        Assert.Equal(3, va.Length);
        Assert.Equal(100m, va[0]);
        Assert.Equal(105m, va[1]);
        Assert.Equal(100m + ((2m * 10m) / 3m), va[2]);

        // Equal neighbors prefer upper bin (matching VolumeAnalysis.ComputeValueArea)
        decimal[] tieVa = Arr("vp_value_area([100, 105, 110], [25, 50, 25], 3)");
        Assert.Equal(100m + (10m / 3m), tieVa[0]);
        Assert.Equal(105m, tieVa[1]);
        Assert.Equal(110m, tieVa[2]);

        // Zero price span collapses to constant price; sparse empty gaps expand to target ratio
        Assert.Equal(new[] { 50m, 50m, 50m }, Arr("vp_value_area([50, 50], [10, 20], 3)"));
        decimal[] sparseVa = Arr("vp_value_area([100, 150, 200], [40, 50, 10], 5, 0.70)");
        Assert.Equal(100m, sparseVa[0]);

        Fails("vp_value_area([100, 110], [0, 0], 2)", CalculatorErrorCode.DomainError);
        Fails("vp_value_area([100, 110], [5, 5], 2, 0)", CalculatorErrorCode.DomainError);
        Fails("vp_value_area([100, 110], [5, 5], 2, 1.01)", CalculatorErrorCode.DomainError);
        Fails("vp_value_area([100, 110], [5], 2)", CalculatorErrorCode.DimensionMismatch);
    }
}
