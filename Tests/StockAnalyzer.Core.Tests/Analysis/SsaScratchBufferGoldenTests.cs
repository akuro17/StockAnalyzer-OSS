using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// The SSA engines take their detrended scratch series with <c>stackalloc</c> for short series and from
/// <see cref="ArrayPool{T}"/> for longer ones (<see cref="MathBufferLimits.StackAllocThreshold"/>).
/// The golden hashes below were captured from the code that used <c>stackalloc</c> for every length, so a matching
/// hash proves the pooled path (n above the threshold) reproduces the previous output bit for bit, also when the
/// rented array still holds garbage from an earlier renter.
/// </summary>
public class SsaScratchBufferGoldenTests
{
    private const int EmbeddingDimension = 15;
    private const int LargeSeriesSmokeCount = 20_000;
    private const int ExtremeSeriesSmokeCount = 400_000;
    private const int ExtremeSeriesEmbeddingDimension = 8;
    private const int ExtremeSeriesStackBytesLimit = 1024 * 1024;
    private const int WideSeriesCount = 400;
    private const int WideEmbeddingDimension = 40;
    private const int WideComponentCount = 39;
    private const int SmallStackBytes = 256 * 1024;
    private const int HugeComponentSeriesCount = 500;
    private const int HugeEmbeddingDimension = 201;
    private const int HugeComponentCount = 200;

    // FNV-1a 64-bit constants.
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    // Captured from the unconditional-stackalloc implementation (Run 6 baseline).
    private static readonly Dictionary<(string Engine, int N), ulong> Golden = new()
    {
        [("Anomaly", 100)] = 4474389141400052009UL,
        [("Anomaly", 512)] = 8358039342167122982UL,
        [("Anomaly", 513)] = 4750966699929712907UL,
        [("Anomaly", 600)] = 4759900806459138974UL,
        [("Anomaly", 2000)] = 13152503586374360119UL,
        [("CausalEndpoint", 100)] = 8702473894601093991UL,
        [("CausalEndpoint", 512)] = 16241091658026628517UL,
        [("CausalEndpoint", 513)] = 9521442760561991966UL,
        [("CausalEndpoint", 600)] = 1891667099106409757UL,
        [("CausalEndpoint", 2000)] = 7405215513333283162UL,
        [("Decompose", 100)] = 17554952529548004697UL,
        [("Decompose", 512)] = 14981006452958170383UL,
        [("Decompose", 513)] = 5900081731862925559UL,
        [("Decompose", 600)] = 10142071770992139109UL,
        [("Decompose", 2000)] = 2593786858284461849UL,
        [("Projection", 100)] = 14206316886838732063UL,
        [("Projection", 512)] = 9703809756056119420UL,
        [("Projection", 513)] = 11660090706624360711UL,
        [("Projection", 600)] = 1599742316742501056UL,
        [("Projection", 2000)] = 11816767159776426368UL,
        [("SupportResistance", 100)] = 1776538662167155100UL,
        [("SupportResistance", 512)] = 7211636957459607613UL,
        [("SupportResistance", 513)] = 9904284521734767853UL,
        [("SupportResistance", 600)] = 6970284239651525489UL,
        [("SupportResistance", 2000)] = 7739916987365183524UL,
        [("SupportResistanceWide", 400)] = 5992170147046082083UL,
        [("AnomalyWide", 400)] = 11038942471574229293UL,
    };

    public static IEnumerable<object[]> SeriesLengths()
    {
        yield return new object[] { 100 };   // stackalloc path
        yield return new object[] { MathBufferLimits.StackAllocThreshold };       // boundary: still stackalloc
        yield return new object[] { MathBufferLimits.StackAllocThreshold + 1 };   // boundary: first pooled length
        yield return new object[] { 600 };
        yield return new object[] { 2000 };
    }

    [Theory]
    [MemberData(nameof(SeriesLengths))]
    public void Decompose_MatchesBaseline(int n)
    {
        AssertGolden("Decompose", n, HashDecompose(n));
    }

    [Theory]
    [MemberData(nameof(SeriesLengths))]
    public void ComputeCausalEndpoint_MatchesBaseline(int n)
    {
        AssertGolden("CausalEndpoint", n, HashCausalEndpoint(n));
    }

    [Theory]
    [MemberData(nameof(SeriesLengths))]
    public void Projection_MatchesBaseline(int n)
    {
        AssertGolden("Projection", n, HashProjection(n));
    }

    [Theory]
    [MemberData(nameof(SeriesLengths))]
    public void SupportResistance_MatchesBaseline(int n)
    {
        AssertGolden("SupportResistance", n, HashSupportResistance(n));
    }

    [Theory]
    [MemberData(nameof(SeriesLengths))]
    public void Anomaly_MatchesBaseline(int n)
    {
        AssertGolden("Anomaly", n, HashAnomaly(n));
    }

    [Theory]
    [InlineData(600)]
    [InlineData(2000)]
    public void PooledPath_IgnoresGarbageLeftInTheRentedArray(int n)
    {
        PoisonThePool(n);

        AssertGolden("Decompose", n, HashDecompose(n));
        PoisonThePool(n);
        AssertGolden("CausalEndpoint", n, HashCausalEndpoint(n));
        PoisonThePool(n);
        AssertGolden("Projection", n, HashProjection(n));
        PoisonThePool(n);
        AssertGolden("SupportResistance", n, HashSupportResistance(n));
        PoisonThePool(n);
        AssertGolden("Anomaly", n, HashAnomaly(n));
    }

    [Fact]
    public void SeparabilityBranch_IsExercisedByTheGoldenSeries()
    {
        // Non-vacuity: the multi-component branch (the one that reads the scratch series a second time) must run.
        const int n = 2000;
        var (samples, timestamps) = BuildSeries(n);

        var sr = SsaSupportResistanceEngine.Calculate(samples, timestamps, embeddingDimension: EmbeddingDimension);
        var anomaly = SsaAnomalyDetectionEngine.CalculateAnomaly(samples, timestamps, embeddingDimension: EmbeddingDimension);

        Assert.True(sr.NumComponents > 1, "SR golden series must select more than one component.");
        Assert.True(sr.SeparabilityScore < 100.0, "SR separability branch was not exercised.");
        Assert.True(anomaly.Separability < 100.0, "Anomaly separability branch was not exercised.");
    }

    [Fact]
    public void SupportResistance_ManyComponents_MatchesBaseline()
    {
        // r * r = 1521 doubles: above the stackalloc threshold, so the w-correlation matrix comes from the pool after the fix.
        AssertGolden("SupportResistanceWide", WideSeriesCount, HashSupportResistanceWide());
    }

    [Fact]
    public void Anomaly_ManyComponents_MatchesBaseline()
    {
        AssertGolden("AnomalyWide", WideSeriesCount, HashAnomalyWide());
    }

    [Fact]
    public void ManyComponents_DoNotNeedAStackProportionalToTheirSquare()
    {
        // The w-correlation matrix of r components has r * r doubles. A stackalloc of it needs 8 * r * r bytes: past the stack size that kills the
        // whole process (an uncatchable stack overflow). The engines run on a thread with a small explicit stack, so that a modest r - which
        // sa_find_support_resistance accepts through embeddingDimension and numComponents - is already beyond it, as a real 1 MiB stack is for r >= 363.
        Assert.True(HugeComponentCount * HugeComponentCount * sizeof(double) > SmallStackBytes,
            "The component count must make the matrix larger than the stack so that a stackalloc-based implementation cannot pass.");

        var (samples, timestamps) = BuildSeries(HugeComponentSeriesCount);
        int srComponents = 0;
        double srSeparability = double.NaN;
        bool anomalyEmpty = true;
        double anomalySeparability = double.NaN;
        Exception? failure = null;

        var worker = new Thread(
            () =>
            {
                try
                {
                    var sr = SsaSupportResistanceEngine.Calculate(samples, timestamps, embeddingDimension: HugeEmbeddingDimension, numComponents: HugeComponentCount, autoRank: false);
                    var anomaly = SsaAnomalyDetectionEngine.CalculateAnomaly(samples, timestamps, embeddingDimension: HugeEmbeddingDimension, numComponents: HugeComponentCount, autoRank: false);
                    srComponents = sr.NumComponents;
                    srSeparability = sr.SeparabilityScore;
                    anomalyEmpty = anomaly.IsEmpty;
                    anomalySeparability = anomaly.Separability;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            SmallStackBytes);
        worker.Start();
        worker.Join();

        Assert.Null(failure);
        Assert.Equal(HugeComponentCount, srComponents);
        Assert.False(anomalyEmpty);
        Assert.InRange(srSeparability, 0.0, 100.0);
        Assert.InRange(anomalySeparability, 0.0, 100.0);
    }

    [Fact]
    public void LargeSeries_Smoke_ProducesFiniteResults()
    {
        var (samples, timestamps) = BuildSeries(LargeSeriesSmokeCount);

        var decomposition = SsaDecompositionEngine.Decompose(samples, EmbeddingDimension);
        double endpoint = SsaDecompositionEngine.ComputeCausalEndpoint(samples, decomposition, EmbeddingDimension, numComponents: 2);
        var projection = SsaProjectionAnalysis.CalculateProjection(samples, timestamps, embeddingDimension: EmbeddingDimension);
        var sr = SsaSupportResistanceEngine.Calculate(samples, timestamps, embeddingDimension: EmbeddingDimension);
        var anomaly = SsaAnomalyDetectionEngine.CalculateAnomaly(samples, timestamps, embeddingDimension: EmbeddingDimension);

        Assert.True(double.IsFinite(endpoint));
        Assert.False(projection.ProjectedPoints.Count == 0);
        Assert.False(sr.IsEmpty);
        Assert.False(anomaly.IsEmpty);
        Assert.Equal(LargeSeriesSmokeCount, anomaly.ZScores.Count);
    }

    [Fact]
    public void ExtremelyLongSeries_DoesNotNeedAStackProportionalToItsLength()
    {
        // A stackalloc of n doubles needs 8 * n bytes. Past the stack size that kills the whole process, so the series
        // length is chosen above the default 1 MiB stack: only a heap/pool-backed scratch series can survive it.
        Assert.True(ExtremeSeriesSmokeCount * sizeof(double) > ExtremeSeriesStackBytesLimit,
            "The extreme series must be longer than the stack so that a stackalloc-based implementation cannot pass.");

        var (samples, timestamps) = BuildSeries(ExtremeSeriesSmokeCount);

        var decomposition = SsaDecompositionEngine.Decompose(samples, ExtremeSeriesEmbeddingDimension);
        double endpoint = SsaDecompositionEngine.ComputeCausalEndpoint(samples, decomposition, ExtremeSeriesEmbeddingDimension, numComponents: 2);
        var projection = SsaProjectionAnalysis.CalculateProjection(samples, timestamps, embeddingDimension: ExtremeSeriesEmbeddingDimension);
        var sr = SsaSupportResistanceEngine.Calculate(samples, timestamps, embeddingDimension: ExtremeSeriesEmbeddingDimension);
        var anomaly = SsaAnomalyDetectionEngine.CalculateAnomaly(samples, timestamps, embeddingDimension: ExtremeSeriesEmbeddingDimension);

        Assert.True(double.IsFinite(endpoint));
        Assert.False(decomposition.IsDegenerate);
        Assert.False(projection.ProjectedPoints.Count == 0);
        Assert.False(sr.IsEmpty);
        Assert.False(anomaly.IsEmpty);
    }

    private static void AssertGolden(string engine, int n, ulong actual)
    {
        Assert.True(
            Golden.TryGetValue((engine, n), out ulong expected) && expected == actual,
            $"GOLDEN ({engine}, {n}) actual = {actual}UL; stored = {(Golden.TryGetValue((engine, n), out ulong s) ? s.ToString() : "none")}");
    }

    private static void PoisonThePool(int n)
    {
        // Rent every power-of-two bucket the engines can ask for, fill it with NaN and give it back without clearing,
        // so the next renter of the same size gets a dirty array.
        for (int size = n; size <= n * 4; size *= 2)
        {
            double[] dirty = ArrayPool<double>.Shared.Rent(size);
            Array.Fill(dirty, double.NaN);
            ArrayPool<double>.Shared.Return(dirty, clearArray: false);
        }
    }

    private static (double[] Samples, List<DateTime> Timestamps) BuildSeries(int n)
    {
        var samples = new double[n];
        var timestamps = new List<DateTime>(n);
        var start = new DateTime(2000, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var generator = new DeterministicNoise(0x9E3779B97F4A7C15UL);
        for (int i = 0; i < n; i++)
        {
            double noise = generator.NextCentered();
            samples[i] = 100.0 + (0.01 * i) + (6.0 * Math.Sin(i / 9.0)) + (3.0 * Math.Sin(i / 31.0)) + (2.0 * noise);
            timestamps.Add(start.AddDays(i));
        }

        return (samples, timestamps);
    }

    private static ulong HashDecompose(int n)
    {
        var (samples, _) = BuildSeries(n);
        var d = SsaDecompositionEngine.Decompose(samples, EmbeddingDimension);
        var h = new Fnv();
        h.AddAll(d.Eigenvalues);
        h.AddAll(d.SingularValues);
        h.AddAll(d.ComponentEnergies);
        h.Add(d.Slope);
        h.Add(d.Intercept);
        h.Add(d.EffectiveRank);
        for (int r = 0; r < d.Eigenvectors.GetLength(0); r++)
        {
            for (int c = 0; c < d.Eigenvectors.GetLength(1); c++)
            {
                h.Add(d.Eigenvectors[r, c]);
            }
        }

        return h.Value;
    }

    private static ulong HashCausalEndpoint(int n)
    {
        var (samples, _) = BuildSeries(n);
        var d = SsaDecompositionEngine.Decompose(samples, EmbeddingDimension);
        var h = new Fnv();
        foreach (var mode in new[] { SsaDetrendMode.LeastSquaresLinear, SsaDetrendMode.EndpointLinear, SsaDetrendMode.None })
        {
            h.Add(SsaDecompositionEngine.ComputeCausalEndpoint(samples, d, EmbeddingDimension, numComponents: 3, mode));
        }

        return h.Value;
    }

    private static ulong HashProjection(int n)
    {
        var (samples, timestamps) = BuildSeries(n);
        var h = new Fnv();
        foreach (var forecast in new[] { SsaForecastMode.Recurrent, SsaForecastMode.Vector })
        {
            var p = SsaProjectionAnalysis.CalculateProjection(
                samples, timestamps, futureSteps: 30, embeddingDimension: EmbeddingDimension, numComponents: 3, forecastMode: forecast);
            h.AddPoints(p.ProjectedPoints);
            h.AddPoints(p.UpperBandPoints);
            h.AddPoints(p.LowerBandPoints);
            h.AddPoints(p.ReconstructedPoints);
            h.Add(p.ResidualStdDev);
            h.Add(p.Slope);
            h.Add(p.Intercept);
            h.Add(p.CumulativeVarianceRatio);
            h.Add(p.NuSquared);
            foreach (var c in p.Components)
            {
                h.Add(c.SingularValue);
                h.Add(c.VarianceRatio);
            }
        }

        return h.Value;
    }

    private static ulong HashSupportResistance(int n)
    {
        var (samples, timestamps) = BuildSeries(n);
        var h = new Fnv();
        foreach (var mode in new[] { SsaSupportResistanceMode.StructuralPivots, SsaSupportResistanceMode.DynamicEnvelopes, SsaSupportResistanceMode.ProjectedTargets })
        {
            var r = SsaSupportResistanceEngine.Calculate(samples, timestamps, mode, embeddingDimension: EmbeddingDimension);
            h.AddPoints(r.CenterBand);
            h.AddPoints(r.UpperBand);
            h.AddPoints(r.LowerBand);
            h.AddPoints(r.ProjectedPath);
            h.Add(r.ResidualStdDev);
            h.Add(r.SeparabilityScore);
            h.Add(r.Slope);
            h.Add(r.Intercept);
            h.Add(r.CumulativeVarianceRatio);
            h.Add(r.NuSquared);
            h.Add(r.NumComponents);
            foreach (var level in r.ResistanceLevels)
            {
                h.Add(level.Price);
                h.Add(level.Hits);
                h.Add(level.StrengthScore);
            }

            foreach (var level in r.SupportLevels)
            {
                h.Add(level.Price);
                h.Add(level.Hits);
                h.Add(level.StrengthScore);
            }
        }

        return h.Value;
    }

    private static ulong HashSupportResistanceWide()
    {
        var (samples, timestamps) = BuildSeries(WideSeriesCount);
        var r = SsaSupportResistanceEngine.Calculate(
            samples, timestamps, SsaSupportResistanceMode.StructuralPivots, embeddingDimension: WideEmbeddingDimension, numComponents: WideComponentCount, autoRank: false);
        var h = new Fnv();
        h.AddPoints(r.CenterBand);
        h.Add(r.SeparabilityScore);
        h.Add(r.ResidualStdDev);
        h.Add(r.NumComponents);
        foreach (var level in r.ResistanceLevels.Concat(r.SupportLevels))
        {
            h.Add(level.Price);
            h.Add(level.Hits);
            h.Add(level.StrengthScore);
        }

        return h.Value;
    }

    private static ulong HashAnomalyWide()
    {
        var (samples, timestamps) = BuildSeries(WideSeriesCount);
        var a = SsaAnomalyDetectionEngine.CalculateAnomaly(
            samples, timestamps, embeddingDimension: WideEmbeddingDimension, numComponents: WideComponentCount, autoRank: false);
        var h = new Fnv();
        foreach (double z in a.ZScores)
        {
            h.Add(z);
        }

        h.Add(a.ResidualStdDev);
        h.Add(a.Separability);
        return h.Value;
    }

    private static ulong HashAnomaly(int n)
    {
        var (samples, timestamps) = BuildSeries(n);
        var a = SsaAnomalyDetectionEngine.CalculateAnomaly(samples, timestamps, embeddingDimension: EmbeddingDimension);
        var h = new Fnv();
        h.AddPoints(a.ReconstructedPoints);
        h.AddPoints(a.UpperBandPoints);
        h.AddPoints(a.LowerBandPoints);
        foreach (double z in a.ZScores)
        {
            h.Add(z);
        }

        h.Add(a.ResidualStdDev);
        h.Add(a.Separability);
        foreach (var interval in a.Intervals)
        {
            h.Add(interval.StartIndex);
            h.Add(interval.EndIndex);
            h.Add(interval.PeakZ);
            h.Add(interval.MaxPriceDeviation);
        }

        return h.Value;
    }

    private struct Fnv
    {
        private ulong _value;
        private bool _started;

        public ulong Value => _started ? _value : FnvOffset;

        public void Add(double v) => AddBits((ulong)BitConverter.DoubleToInt64Bits(v));

        public void Add(int v) => AddBits((ulong)v);

        public void AddAll(IReadOnlyList<double> values)
        {
            foreach (double v in values)
            {
                Add(v);
            }
        }

        public void AddPoints(IReadOnlyList<Point> points)
        {
            foreach (var p in points)
            {
                Add(p.X);
                Add(p.Y);
            }
        }

        private void AddBits(ulong bits)
        {
            if (!_started)
            {
                _value = FnvOffset;
                _started = true;
            }

            for (int i = 0; i < sizeof(ulong); i++)
            {
                _value ^= (bits >> (i * 8)) & 0xFF;
                _value *= FnvPrime;
            }
        }
    }
}
