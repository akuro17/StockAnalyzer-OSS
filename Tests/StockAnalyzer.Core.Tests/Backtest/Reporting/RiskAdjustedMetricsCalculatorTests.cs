using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

public class RiskAdjustedMetricsCalculatorTests
{
    [Fact]
    public void Sharpe_ZeroVariance_Undefined()
    {
        // Constant equity -> every r[j] is identical -> zero variance.
        var result = ReportTestHelpers.BuildResult(100m, new[] { 100m, 100m, 100m });
        var options = ReportTestHelpers.Options();
        var sample = EquitySample.Build(result, options);

        var sharpe = RiskAdjustedMetricsCalculator.ComputeBarSharpe(sample, rf: 0m);

        Assert.Equal(MetricStatus.Undefined, sharpe.Status);
        Assert.Equal(MetricReason.ZeroDivisor, sharpe.Reason);
    }

    [Fact]
    public void Sortino_NoDownside_Undefined()
    {
        // Every return is a gain -> downside is always 0 -> downside variance is 0.
        var result = ReportTestHelpers.BuildResult(100m, new[] { 110m, 121m });
        var options = ReportTestHelpers.Options();
        var sample = EquitySample.Build(result, options);

        var sortino = RiskAdjustedMetricsCalculator.ComputeBarSortino(sample, mar: 0m);

        Assert.Equal(MetricStatus.Undefined, sortino.Status);
        Assert.Equal(MetricReason.DownsideZero, sortino.Reason);
    }

    [Fact]
    public void SQN_RiskMissing_NotApplicable()
    {
        var trades = ImmutableArray.Create(ReportTestHelpers.Trade(10m));

        var sqn = RiskAdjustedMetricsCalculator.ComputeSqn(trades);

        Assert.Equal(MetricStatus.NotApplicable, sqn.Status);
        Assert.Equal(MetricReason.RiskDataMissing, sqn.Reason);
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    public void SQN_LessThan30_Warning(int totalTrades, bool expectedWarning)
    {
        Assert.Equal(expectedWarning, RiskAdjustedMetricsCalculator.ComputeSqnWarning(totalTrades));
    }

    [Fact]
    public void Returns_TwoElements_CorrectSampleVariance()
    {
        var returns = ImmutableArray.Create(0.1, -0.1);

        (double sharpeMean, double sampleVariance) = RiskAdjustedMetricsCalculator.MeanAndSampleVariance(returns);
        (double sortinoMean, double downsideVariance) = RiskAdjustedMetricsCalculator.MeanAndDownsideVariance(returns);

        Assert.Equal(0d, sharpeMean, precision: 10);
        Assert.Equal(0.02d, sampleVariance, precision: 10);
        Assert.Equal(0d, sortinoMean, precision: 10);
        Assert.Equal(0.005d, downsideVariance, precision: 10);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(10, 2)]
    [InlineData(100, 4)]
    [InlineData(252, 4)]
    public void NeweyWestBandwidth_MatchesAutomaticRule(int m, int expectedBandwidth)
    {
        Assert.Equal(expectedBandwidth, RiskAdjustedMetricsCalculator.NeweyWestBandwidth(m));
    }

    [Fact]
    public void NeweyWestLongRunVariance_BandwidthZero_EqualsSampleVariance()
    {
        // bandwidth=0 must be an exact algebraic identity with gamma0 (no lag terms added at all),
        // regardless of the data's actual autocorrelation -- this is what makes
        // ComputeAnnualizedSharpeAutocorrelationAdjusted a strict generalization of ComputeAnnualizedSharpe.
        var x = ImmutableArray.Create(0.1, -0.1);
        (double mean, double sampleVariance) = RiskAdjustedMetricsCalculator.MeanAndSampleVariance(x);

        double longRunVariance = RiskAdjustedMetricsCalculator.NeweyWestLongRunVariance(x, mean, bandwidth: 0);

        Assert.Equal(sampleVariance, longRunVariance, precision: 12);
    }

    [Fact]
    public void AnnualizedSharpeAutocorrelationAdjusted_HandComputedSeries_MatchesIndependentCalculation()
    {
        // E=[100,110,100,115,105] -> r=[.10,-.0909...,.15,-.0870...], m=4, rf=0, AnnualPeriods=252.
        // Independently computed (Python): bandwidth=1, gamma0=0.015675019007743707,
        // gamma_1=-0.012387194411462974, long-run variance=0.003287824596280733,
        // annualized=4.992617637414459.
        var result = ReportTestHelpers.BuildResult(100m, new[] { 110m, 100m, 115m, 105m });
        var options = ReportTestHelpers.Options(annualPeriods: 252);
        var sample = EquitySample.Build(result, options);

        var adjusted = RiskAdjustedMetricsCalculator.ComputeAnnualizedSharpeAutocorrelationAdjusted(sample, rf: 0m, annualPeriods: 252);

        Assert.Equal(MetricStatus.Valid, adjusted.Status);
        Assert.Equal(4.992617637414459, (double)adjusted.Value!.Value, precision: 6);
    }

    [Fact]
    public void AnnualizedSharpeAutocorrelationAdjusted_SampleTooSmall_InsufficientData()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 110m });
        var options = ReportTestHelpers.Options();
        var sample = EquitySample.Build(result, options);

        var adjusted = RiskAdjustedMetricsCalculator.ComputeAnnualizedSharpeAutocorrelationAdjusted(sample, rf: 0m, annualPeriods: 252);

        Assert.Equal(MetricStatus.InsufficientData, adjusted.Status);
        Assert.Equal(MetricReason.SampleTooSmall, adjusted.Reason);
    }

    [Fact]
    public void AnnualizedSharpeAutocorrelationAdjusted_NegativeEquityPeriod_Undefined()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { -10m, 5m });
        var options = ReportTestHelpers.Options();
        var sample = EquitySample.Build(result, options);

        var adjusted = RiskAdjustedMetricsCalculator.ComputeAnnualizedSharpeAutocorrelationAdjusted(sample, rf: 0m, annualPeriods: 252);

        Assert.Equal(MetricStatus.Undefined, adjusted.Status);
        Assert.Equal(MetricReason.NegativeEquityInPeriod, adjusted.Reason);
    }

    [Fact]
    public void AnnualizedSharpeAutocorrelationAdjusted_ZeroVariance_Undefined()
    {
        // Constant equity -> every r[j] is identical -> gamma0 = 0, and every gamma_k = 0 too, so the
        // long-run variance is exactly 0 regardless of bandwidth.
        var result = ReportTestHelpers.BuildResult(100m, new[] { 100m, 100m, 100m, 100m });
        var options = ReportTestHelpers.Options();
        var sample = EquitySample.Build(result, options);

        var adjusted = RiskAdjustedMetricsCalculator.ComputeAnnualizedSharpeAutocorrelationAdjusted(sample, rf: 0m, annualPeriods: 252);

        Assert.Equal(MetricStatus.Undefined, adjusted.Status);
        Assert.Equal(MetricReason.ZeroDivisor, adjusted.Reason);
    }

    // m=30 deterministic synthetic series (E[i+1] = E[i]*(1 + 0.02*sin(i*0.9) + 0.001*i)), postWarmupEquity
    // = E[1..30] with initialCapital=100. Independently computed (Python, porting the exact `arch`
    // (bashtage/arch) `_single_optimal_block` algorithm verified during planning): b_cb = 7.699761347746385,
    // rounds to 8.
    private static readonly decimal[] BlockBootstrapFixtureEquity =
    {
        100.0000000000m, 101.6666538193m, 103.8501437261m, 105.0493633971m, 104.5398310335m,
        103.0187135220m, 102.0446417359m, 102.7932695971m, 105.2472900478m, 108.2360811430m,
        110.2105637506m, 110.4143741761m, 109.5731574672m, 109.3277495697m, 110.9318567054m,
        114.3791405326m, 118.4182289117m, 121.3709651233m, 122.4088762469m, 122.3254790670m,
        122.9346911537m, 125.6402936190m, 130.4489842244m, 135.9569387935m, 140.2565783197m,
        142.3964041744m, 143.2880461490m, 145.0367948342m, 149.2927779581m, 156.0806219362m,
    };

    private static readonly double[] BlockBootstrapFixtureReturns =
    {
        0.0, 0.016666538192549485, 0.021476952617563994, 0.011547597604676385, -0.004850408865897049,
        -0.014550602353301967, -0.009455289751119755, 0.007336278009686836, 0.023873357276983143,
        0.028397796216901527, 0.01824236970483506, 0.0018492821244935342, -0.00761872460132984,
        -0.0022396716783805726, 0.01467246094442265, 0.031075688531032375, 0.035313155530985574,
        0.024934811462612227, 0.008551560272030745, -0.0006813001016328091, 0.004980255064566341,
        0.022008453756136248, 0.03827347475014209, 0.042223054490042156, 0.031625009833098794,
        0.015256509750789737, 0.006261688837587043, 0.01220442829844215, 0.029344161450509443,
        0.04546666001476152,
    };

    [Fact]
    public void OptimalCircularBlockLength_HandComputedSeries_MatchesIndependentCalculation()
    {
        int blockLength = PolitisWhiteBlockBootstrap.OptimalCircularBlockLength(
            ImmutableArray.Create(BlockBootstrapFixtureReturns));

        Assert.Equal(8, blockLength);
    }

    [Fact]
    public void CircularBlockResample_SingleBlock_IsCircularRotationOfInput()
    {
        ImmutableArray<double> x = ImmutableArray.Create(BlockBootstrapFixtureReturns);
        var rng = new Random(42);

        ImmutableArray<double> resampled = PolitisWhiteBlockBootstrap.CircularBlockResample(x, x.Length, rng);

        Assert.Equal(x.Length, resampled.Length);
        // With blockLength == m, exactly one block is drawn: the result must be some circular
        // rotation of the input (doubling the input and scanning for the resampled sequence as a
        // contiguous substring proves this without needing to know the RNG's exact draw).
        double[] doubled = x.Concat(x).ToArray();
        bool isRotation = false;
        for (int offset = 0; offset < x.Length; offset++)
        {
            if (doubled.Skip(offset).Take(x.Length).SequenceEqual(resampled))
            {
                isRotation = true;
                break;
            }
        }
        Assert.True(isRotation, "Single-block circular resample must be a rotation of the input.");
    }

    [Fact]
    public void CircularBlockResample_GeneralCase_LengthMatchesAndEveryElementIsFromInput()
    {
        ImmutableArray<double> x = ImmutableArray.Create(BlockBootstrapFixtureReturns);
        var rng = new Random(7);

        ImmutableArray<double> resampled = PolitisWhiteBlockBootstrap.CircularBlockResample(x, blockLength: 5, rng);

        Assert.Equal(x.Length, resampled.Length);
        Assert.All(resampled, v => Assert.Contains(v, x));
    }

    [Fact]
    public void CircularBlockResample_SameSeedSameInput_Deterministic()
    {
        ImmutableArray<double> x = ImmutableArray.Create(BlockBootstrapFixtureReturns);

        ImmutableArray<double> first = PolitisWhiteBlockBootstrap.CircularBlockResample(x, blockLength: 5, new Random(123));
        ImmutableArray<double> second = PolitisWhiteBlockBootstrap.CircularBlockResample(x, blockLength: 5, new Random(123));

        Assert.Equal(first, second);
    }

    [Fact]
    public void AnnualizedSortinoAutocorrelationAdjusted_ValidSeries_ReturnsValidWithConfidenceInterval()
    {
        var result = ReportTestHelpers.BuildResult(100m, BlockBootstrapFixtureEquity);
        var options = ReportTestHelpers.Options(annualPeriods: 252);
        var sample = EquitySample.Build(result, options);

        (MetricValue point, ConfidenceInterval? interval) = RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
            sample, mar: 0m, annualPeriods: 252, bootstrapSeed: options.BootstrapSeed, bootstrapIterations: options.BootstrapIterations);

        Assert.Equal(MetricStatus.Valid, point.Status);
        Assert.Equal(MetricUnit.Dimensionless, point.Unit);
        Assert.True(interval.HasValue);
        Assert.True(interval!.Value.Lower <= interval.Value.Upper);
    }

    [Fact]
    public void AnnualizedSortinoAutocorrelationAdjusted_SameSeed_Deterministic()
    {
        var result = ReportTestHelpers.BuildResult(100m, BlockBootstrapFixtureEquity);
        var options = ReportTestHelpers.Options(annualPeriods: 252);
        var sample = EquitySample.Build(result, options);

        (MetricValue point1, ConfidenceInterval? interval1) = RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
            sample, mar: 0m, annualPeriods: 252, bootstrapSeed: options.BootstrapSeed, bootstrapIterations: options.BootstrapIterations);
        (MetricValue point2, ConfidenceInterval? interval2) = RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
            sample, mar: 0m, annualPeriods: 252, bootstrapSeed: options.BootstrapSeed, bootstrapIterations: options.BootstrapIterations);

        Assert.Equal(point1.Value, point2.Value);
        Assert.Equal(interval1!.Value.Lower, interval2!.Value.Lower);
        Assert.Equal(interval1.Value.Upper, interval2.Value.Upper);
    }

    [Fact]
    public void AnnualizedSortinoAutocorrelationAdjusted_NonconstantFixture_MatchesPreOptimizationOracleExactly()
    {
        const int annualPeriods = 252;
        const int bootstrapSeed = 42;
        const int bootstrapIterations = 1000;
        // Version-fixed oracle: the allocation-heavy calculation from commit 8bfd40d04fd5,
        // before the reusable-buffer/cancellation change. The block length 8 is independently
        // established by OptimalCircularBlockLength_HandComputedSeries_MatchesIndependentCalculation.
        const int independentlyEstablishedBlockLength = 8;
        var result = ReportTestHelpers.BuildResult(100m, BlockBootstrapFixtureEquity);
        EquitySample sample = EquitySample.Build(result, ReportTestHelpers.Options(annualPeriods: annualPeriods));

        double[] referenceReturns = new double[BlockBootstrapFixtureEquity.Length];
        decimal prior = 100m;
        for (int i = 0; i < referenceReturns.Length; i++)
        {
            decimal current = BlockBootstrapFixtureEquity[i];
            referenceReturns[i] = (double)(current / prior - 1m);
            prior = current;
        }
        Assert.Equal(referenceReturns, sample.Returns);

        (decimal expectedPoint, decimal expectedLower, decimal expectedUpper) =
            PreOptimizationSortinoOracle(referenceReturns, annualPeriods, bootstrapSeed,
                bootstrapIterations, independentlyEstablishedBlockLength);
        (MetricValue point, ConfidenceInterval? interval) =
            RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
                sample, 0m, annualPeriods, bootstrapSeed, bootstrapIterations);

        Assert.Equal(MetricStatus.Valid, point.Status);
        Assert.Equal(expectedPoint, point.Value);
        Assert.Equal(expectedLower, interval!.Value.Lower);
        Assert.Equal(expectedUpper, interval.Value.Upper);
    }

    [Fact]
    public void CircularBlockResample_CancellationAt1024ElementBoundary_LeavesNextSlotUntouched()
    {
        int boundary = MetricCalculation.CancellationCheckInterval;
        double[] source = Enumerable.Repeat(1d, boundary + 1).ToArray();
        double[] buffer = Enumerable.Repeat(-1d, source.Length).ToArray();
        using var cancellation = new CancellationTokenSource();
        var rng = new CancelOnSecondBlockRandom(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            PolitisWhiteBlockBootstrap.CircularBlockResample(
                source, boundary, rng, buffer, cancellation.Token));

        Assert.Equal(2, rng.DrawCount);
        Assert.Equal(1d, buffer[boundary - 1]);
        Assert.Equal(-1d, buffer[boundary]);
    }

    [Fact]
    public void AnnualizedSortinoAutocorrelationAdjusted_ReusesWorkBufferAcrossReplicates()
    {
        const int sampleLength = 2048;
        const int bootstrapIterations = 1000;
        const long maximumExpectedAllocationBytes = 1_000_000;
        double[] repeatingReturns = { -0.02d, 0.03d, 0.01d, -0.01d };
        var sample = new EquitySample
        {
            Equity = Enumerable.Repeat(100m, sampleLength + 1).ToImmutableArray(),
            Returns = Enumerable.Range(0, sampleLength)
                .Select(index => repeatingReturns[index % repeatingReturns.Length]).ToImmutableArray(),
            HasNegativeEquityPeriod = false,
            ReturnFailureReason = null,
        };

        // Warm the JIT before measuring only the synchronous bootstrap invocation. The old
        // per-replicate m-element allocation would exceed this bound by more than 10x.
        _ = RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
            sample, 0m, 252, 42, bootstrapIterations);
        long before = GC.GetAllocatedBytesForCurrentThread();
        (MetricValue point, _) = RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
            sample, 0m, 252, 42, bootstrapIterations);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(MetricStatus.Valid, point.Status);
        Assert.True(allocated < maximumExpectedAllocationBytes,
            $"A reusable sample buffer should keep allocation below {maximumExpectedAllocationBytes} bytes, but allocated {allocated} bytes.");
    }

    [Fact]
    public void AnnualizedSortinoAutocorrelationAdjusted_MLessThan2_InsufficientData()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 110m });
        var options = ReportTestHelpers.Options();
        var sample = EquitySample.Build(result, options);

        (MetricValue point, ConfidenceInterval? interval) = RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
            sample, mar: 0m, annualPeriods: 252, bootstrapSeed: 42, bootstrapIterations: 1000);

        Assert.Equal(MetricStatus.InsufficientData, point.Status);
        Assert.Equal(MetricReason.SampleTooSmall, point.Reason);
        Assert.Null(interval);
    }

    [Fact]
    public void AnnualizedSortinoAutocorrelationAdjusted_MBelowBootstrapThreshold_InsufficientData()
    {
        // m=5 (2 <= m < 20), with both up and down bars so downside variance != 0 -- isolates the
        // bootstrap-specific m<20 threshold from the earlier DownsideZero check.
        var result = ReportTestHelpers.BuildResult(100m, new[] { 110m, 100m, 115m, 105m, 102m });
        var options = ReportTestHelpers.Options();
        var sample = EquitySample.Build(result, options);

        (MetricValue point, ConfidenceInterval? interval) = RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
            sample, mar: 0m, annualPeriods: 252, bootstrapSeed: 42, bootstrapIterations: 1000);

        Assert.Equal(MetricStatus.InsufficientData, point.Status);
        Assert.Equal(MetricReason.SampleTooSmall, point.Reason);
        Assert.Null(interval);
    }

    [Fact]
    public void AnnualizedSortinoAutocorrelationAdjusted_NegativeEquityPeriod_Undefined()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { -10m, 5m });
        var options = ReportTestHelpers.Options();
        var sample = EquitySample.Build(result, options);

        (MetricValue point, ConfidenceInterval? interval) = RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
            sample, mar: 0m, annualPeriods: 252, bootstrapSeed: 42, bootstrapIterations: 1000);

        Assert.Equal(MetricStatus.Undefined, point.Status);
        Assert.Equal(MetricReason.NegativeEquityInPeriod, point.Reason);
        Assert.Null(interval);
    }

    [Theory]
    [InlineData(500, 1000, false)]  // exactly half of an even total -> sufficient (matches ">= half").
    [InlineData(499, 1000, true)]   // one below half of an even total -> insufficient.
    [InlineData(500, 1001, true)]   // 500/1001 (49.95%) is fractionally under half of an odd total; the
                                     // pre-fix `count < bootstrapIterations / 2` (integer division: 1001/2=500)
                                     // evaluated `500 < 500` as false and incorrectly treated this as sufficient.
    [InlineData(501, 1001, false)]  // 501/1001 (50.05%) clears half of the same odd total -> sufficient.
    public void HasInsufficientValidReplicates_HalfBoundary_ExactForOddAndEvenTotals(
        int validReplicateCount, int bootstrapIterations, bool expectedInsufficient)
    {
        bool actual = RiskAdjustedMetricsCalculator.HasInsufficientValidReplicates(validReplicateCount, bootstrapIterations);

        Assert.Equal(expectedInsufficient, actual);
    }

    [Fact]
    public void HasInsufficientValidReplicates_LargeValues_DoesNotOverflow()
    {
        Assert.False(RiskAdjustedMetricsCalculator.HasInsufficientValidReplicates(1_073_741_824, int.MaxValue));
    }

    [Fact]
    public void AnnualizedSortinoAutocorrelationAdjusted_ConstantNegativeSeries_UsesClosedForm()
    {
        var sample = new EquitySample
        {
            Equity = Enumerable.Repeat(100m, 21).ToImmutableArray(),
            Returns = Enumerable.Repeat(-0.1d, 20).ToImmutableArray(),
            HasNegativeEquityPeriod = false,
            ReturnFailureReason = null,
        };

        (MetricValue point, ConfidenceInterval? interval) =
            RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
                sample,
                mar: 0m,
                annualPeriods: 252,
                bootstrapSeed: 42,
                bootstrapIterations: 1000);

        decimal expected = (decimal)-Math.Sqrt(252d);
        Assert.Equal(expected, point.Value);
        Assert.Equal(expected, interval!.Value.Lower);
        Assert.Equal(expected, interval.Value.Upper);
        Assert.Equal(1, PolitisWhiteBlockBootstrap.OptimalCircularBlockLength(sample.Returns));
    }

    [Fact]
    public void AnnualizedSortinoAutocorrelationAdjusted_NoDownside_Undefined()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 110m, 121m });
        var options = ReportTestHelpers.Options();
        var sample = EquitySample.Build(result, options);

        (MetricValue point, ConfidenceInterval? interval) = RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
            sample, mar: 0m, annualPeriods: 252, bootstrapSeed: 42, bootstrapIterations: 1000);

        Assert.Equal(MetricStatus.Undefined, point.Status);
        Assert.Equal(MetricReason.DownsideZero, point.Reason);
        Assert.Null(interval);
    }

    private static (decimal Point, decimal Lower, decimal Upper) PreOptimizationSortinoOracle(
        double[] values, int annualPeriods, int bootstrapSeed, int bootstrapIterations, int blockLength)
    {
        static (double Mean, double DownsideVariance) Statistics(IReadOnlyList<double> series)
        {
            double mean = 0d;
            double downsideSumSq = 0d;
            foreach (double value in series)
            {
                mean += value;
                double downside = Math.Min(value, 0d);
                downsideSumSq += downside * downside;
            }
            return (mean / series.Count, downsideSumSq / series.Count);
        }

        static double Percentile(IReadOnlyList<double> sorted, double p)
        {
            double index = p * (sorted.Count - 1);
            int lower = (int)Math.Floor(index);
            int upper = (int)Math.Ceiling(index);
            if (lower == upper) return sorted[lower];
            double fraction = index - lower;
            return sorted[lower] + fraction * (sorted[upper] - sorted[lower]);
        }

        (double mean, double downsideVariance) = Statistics(values);
        double thetaHat = mean / Math.Sqrt(downsideVariance) * Math.Sqrt(annualPeriods);
        var rng = new Random(bootstrapSeed);
        var replicates = new List<double>(bootstrapIterations);
        for (int iteration = 0; iteration < bootstrapIterations; iteration++)
        {
            // This per-replicate allocation is intentional: it is the old reference behavior.
            var resampled = new List<double>(values.Length);
            while (resampled.Count < values.Length)
            {
                int start = rng.Next(0, values.Length);
                for (int j = 0; j < blockLength && resampled.Count < values.Length; j++)
                {
                    resampled.Add(values[(start + j) % values.Length]);
                }
            }
            (double replicateMean, double replicateDownsideVariance) = Statistics(resampled);
            if (replicateDownsideVariance == 0d) continue;
            replicates.Add(replicateMean / Math.Sqrt(replicateDownsideVariance) * Math.Sqrt(annualPeriods));
        }

        double bootstrapMean = 0d;
        foreach (double replicate in replicates) bootstrapMean += replicate;
        bootstrapMean /= replicates.Count;
        double adjusted = 2.0 * thetaHat - bootstrapMean;
        replicates.Sort();
        return ((decimal)adjusted, (decimal)Percentile(replicates, 0.025),
            (decimal)Percentile(replicates, 0.975));
    }

    private sealed class CancelOnSecondBlockRandom : Random
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelOnSecondBlockRandom(CancellationTokenSource cancellation) => _cancellation = cancellation;

        public int DrawCount { get; private set; }

        public override int Next(int minValue, int maxValue)
        {
            DrawCount++;
            if (DrawCount == 2) _cancellation.Cancel();
            return minValue;
        }
    }
}
