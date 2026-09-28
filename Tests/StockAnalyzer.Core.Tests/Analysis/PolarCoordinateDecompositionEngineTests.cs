using System;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class PolarCoordinateDecompositionEngineTests
{
    private static decimal[] GenerateSinePrices(int count, double period, double amplitude = 10.0, double basePrice = 100.0)
    {
        var prices = new decimal[count];
        for (int i = 0; i < count; i++)
        {
            double angle = (2.0 * Math.PI * i) / period;
            prices[i] = (decimal)(basePrice + amplitude * Math.Sin(angle));
        }

        return prices;
    }

    [Fact]
    public void Decompose_EmptyPrices_ReturnsEmptyResult()
    {
        var result = PolarCoordinateDecompositionEngine.Decompose(ReadOnlySpan<decimal>.Empty);

        Assert.Equal(0, result.Count);
        Assert.Empty(result.Samples);
    }

    [Fact]
    public void Decompose_SinglePrice_ReturnsWarmupResult()
    {
        var result = PolarCoordinateDecompositionEngine.Decompose(new decimal[] { 100m });

        Assert.Equal(1, result.Count);
        Assert.True(result[0].IsWarmup);
        Assert.False(result[0].IsValid);
    }

    [Fact]
    public void Decompose_PureSineWave20Bar_DominantCyclePeriodNear20()
    {
        var prices = GenerateSinePrices(150, period: 20.0);

        var result = PolarCoordinateDecompositionEngine.Decompose(prices);

        Assert.Equal(150, result.Count);
        for (int i = 70; i < 140; i++)
        {
            Assert.False(result[i].IsWarmup);
            Assert.True(result[i].IsValid);
            Assert.InRange(result[i].DominantCyclePeriod, 17.0m, 23.0m);
        }
    }

    [Fact]
    public void Decompose_ScaleAndOffsetInvariance_PeriodAndPhaseMatch()
    {
        var basePrices = GenerateSinePrices(120, period: 20.0, amplitude: 10.0, basePrice: 100.0);
        var scaledPrices = GenerateSinePrices(120, period: 20.0, amplitude: 100.0, basePrice: 1000.0);
        var offsetPrices = GenerateSinePrices(120, period: 20.0, amplitude: 10.0, basePrice: 5000.0);

        var baseResult = PolarCoordinateDecompositionEngine.Decompose(basePrices);
        var scaledResult = PolarCoordinateDecompositionEngine.Decompose(scaledPrices);
        var offsetResult = PolarCoordinateDecompositionEngine.Decompose(offsetPrices);

        for (int i = 60; i < 120; i++)
        {
            Assert.Equal(baseResult[i].DominantCyclePeriod, scaledResult[i].DominantCyclePeriod, 1);
            Assert.Equal(baseResult[i].DominantCyclePeriod, offsetResult[i].DominantCyclePeriod, 1);
            Assert.Equal(baseResult[i].PhaseDegrees, scaledResult[i].PhaseDegrees, 6);
            Assert.Equal(baseResult[i].PhaseDegrees, offsetResult[i].PhaseDegrees, 6);
        }
    }

    [Fact]
    public void Decompose_PhaseDegrees_AlwaysInZeroTo360()
    {
        var prices = GenerateSinePrices(160, period: 24.0);

        var result = PolarCoordinateDecompositionEngine.Decompose(prices);

        for (int i = 0; i < result.Count; i++)
        {
            Assert.InRange(result[i].PhaseDegrees, 0.0, Math.BitDecrement(360.0));
        }
    }

    [Fact]
    public void Decompose_PhaseRadians_MatchHilbertEngineSignConvention()
    {
        var prices = GenerateSinePrices(140, period: 20.0);

        var polar = PolarCoordinateDecompositionEngine.Decompose(prices);
        var hilbert = HilbertDecompositionEngine.Decompose(prices);

        Assert.Equal(hilbert.Count, polar.Count);
        for (int i = 0; i < polar.Count; i++)
        {
            // atan2(-Q, I) is produced by the reused Hilbert engine and passed through verbatim.
            Assert.Equal(hilbert[i].PhaseRad, polar[i].PhaseRadians);
        }
    }

    [Fact]
    public void Decompose_CycleAngularFrequency_ConsistentWithInstantaneousPeriod()
    {
        var prices = GenerateSinePrices(140, period: 18.0);

        var result = PolarCoordinateDecompositionEngine.Decompose(prices);

        for (int i = 0; i < result.Count; i++)
        {
            double expected = (2.0 * Math.PI) / (double)result[i].InstantaneousPeriod;
            Assert.Equal(expected, result[i].CycleAngularFrequency, 12);
        }
    }

    [Fact]
    public void Decompose_PassThroughFields_MatchReusedHilbertEngineExactly()
    {
        var prices = GenerateSinePrices(150, period: 20.0);

        var polar = PolarCoordinateDecompositionEngine.Decompose(prices);
        var hilbert = HilbertDecompositionEngine.Decompose(prices);

        Assert.Equal(hilbert.Count, polar.Count);
        Assert.Equal(hilbert.WarmupBars, polar.WarmupBars);

        for (int i = 0; i < polar.Count; i++)
        {
            var p = polar[i];
            var h = hilbert[i];

            Assert.Equal(h.InPhase, p.InPhase);
            Assert.Equal(h.Quadrature, p.Quadrature);
            Assert.Equal(h.Amplitude, p.Amplitude);
            Assert.Equal(h.Power, p.Power);
            Assert.Equal(h.NormalizedInPhase, p.NormalizedInPhase);
            Assert.Equal(h.NormalizedQuadrature, p.NormalizedQuadrature);
            Assert.Equal(h.PhaseRad, p.PhaseRadians);
            Assert.Equal(h.UnwrappedPhaseDeg, p.UnwrappedPhaseDegrees);
            Assert.Equal(h.InstantaneousPeriod, p.InstantaneousPeriod);
            Assert.Equal(h.DominantCycle, p.DominantCyclePeriod);
            Assert.Equal(h.CycleStability, p.PhaseStability);
            Assert.Equal(h.IsWarmup, p.IsWarmup);
            Assert.Equal(h.IsValid, p.IsValid);
        }
    }

    [Fact]
    public void Decompose_PhaseAngularVelocity_IsPhaseDeltaDegConvertedToRadPerBar()
    {
        var prices = GenerateSinePrices(240, period: 20.0);

        var polar = PolarCoordinateDecompositionEngine.Decompose(prices);
        var hilbert = HilbertDecompositionEngine.Decompose(prices);

        Assert.Equal(hilbert.Count, polar.Count);
        for (int i = 0; i < polar.Count; i++)
        {
            // Verbatim deg -> rad/bar conversion of the reused engine's PhaseDeltaDeg; not recomputed.
            // Mirrors the engine's single scale factor (Math.PI / 180.0) exactly.
            double expected = hilbert[i].PhaseDeltaDeg * (Math.PI / 180.0);
            Assert.Equal(expected, polar[i].PhaseAngularVelocity);
        }
    }

    [Fact]
    public void Decompose_PhaseFrequencyError_EqualsAbsDifferenceOfCycleAndPhaseRates_OrNaN()
    {
        var prices = GenerateSinePrices(240, period: 20.0);

        var result = PolarCoordinateDecompositionEngine.Decompose(prices);

        for (int i = 0; i < result.Count; i++)
        {
            var s = result[i];
            if (double.IsNaN(s.CycleAngularFrequency))
            {
                Assert.True(double.IsNaN(s.PhaseFrequencyError));
            }
            else
            {
                Assert.Equal(Math.Abs(s.CycleAngularFrequency - s.PhaseAngularVelocity), s.PhaseFrequencyError);
                Assert.True(s.PhaseFrequencyError >= 0.0);
            }
        }
    }

    [Fact]
    public void Decompose_FlatLine_MicroAmplitudeGuardEngaged()
    {
        var prices = new decimal[100];
        Array.Fill(prices, 100m);

        var result = PolarCoordinateDecompositionEngine.Decompose(prices);

        Assert.Equal(100, result.Count);
        for (int i = 20; i < 100; i++)
        {
            Assert.True(result[i].Amplitude < 1e-6m);
            Assert.False(result[i].IsValid);
            Assert.Equal(0m, result[i].NormalizedInPhase);
            Assert.Equal(0m, result[i].NormalizedQuadrature);
        }
    }

    [Fact]
    public void Decompose_IsValid_EqualsIsPhaseValid_ForEveryBar()
    {
        var prices = GenerateSinePrices(200, period: 20.0);

        var result = PolarCoordinateDecompositionEngine.Decompose(prices);

        for (int i = 0; i < result.Count; i++)
        {
            Assert.Equal(result[i].IsValid, result[i].IsPhaseValid);
        }
    }

    [Fact]
    public void Decompose_ValidityFlags_MatchDocumentedPredicates()
    {
        var prices = GenerateSinePrices(240, period: 18.0);
        var parameters = new HilbertDecompositionParameters();

        var polar = PolarCoordinateDecompositionEngine.Decompose(prices);
        var hilbert = HilbertDecompositionEngine.Decompose(prices, parameters);

        Assert.Equal(hilbert.Count, polar.Count);
        for (int i = 0; i < polar.Count; i++)
        {
            HilbertSampleResult h = hilbert[i];
            PolarSampleResult p = polar[i];

            bool aboveThreshold = h.Amplitude >= parameters.MicroAmplitudeThreshold;
            bool expectedAmplitudeValid = !h.IsWarmup;
            bool expectedPhaseValid = !h.IsWarmup && aboveThreshold;
            bool expectedCycleFrequencyValid = expectedPhaseValid && h.InstantaneousPeriod > 0m;
            bool expectedFrequencyValid = expectedCycleFrequencyValid
                && !h.TrendMode
                && h.InstantaneousPeriod >= (decimal)parameters.MinimumCyclePeriod;

            Assert.Equal(expectedAmplitudeValid, p.IsAmplitudeValid);
            Assert.Equal(expectedPhaseValid, p.IsPhaseValid);
            Assert.Equal(expectedCycleFrequencyValid, p.IsCycleFrequencyValid);
            Assert.Equal(expectedFrequencyValid, p.IsFrequencyValid);
            Assert.Equal(p.IsPhaseValid, p.IsValid);
        }
    }

    [Fact]
    public void Decompose_FlatLine_AmplitudeValidButPhaseAndFrequencyInvalidPostWarmup()
    {
        var prices = new decimal[100];
        Array.Fill(prices, 100m);

        var result = PolarCoordinateDecompositionEngine.Decompose(prices);

        // WarmupBars defaults to 50; bars 55..99 are out of warm-up with a sub-threshold amplitude.
        for (int i = 55; i < 100; i++)
        {
            Assert.False(result[i].IsWarmup);
            Assert.True(result[i].IsAmplitudeValid);
            Assert.False(result[i].IsPhaseValid);
            Assert.False(result[i].IsFrequencyValid);
            Assert.Equal(result[i].IsValid, result[i].IsPhaseValid);
        }
    }

    [Fact]
    public void Decompose_FromHilbertResult_AllocatesOnlyTheResultArray_NoPerBarAllocation()
    {
        var prices = GenerateSinePrices(4000, period: 20.0);
        var hilbert = HilbertDecompositionEngine.Decompose(prices);

        // Warm up the JIT and any first-call static initialization on this thread.
        _ = PolarCoordinateDecompositionEngine.Decompose(hilbert);

        // Self-calibrate against the true size of one PolarSampleResult[n] (the struct is stored
        // inline in the array; PolarSampleResult is a readonly record struct).
        long beforeReference = GC.GetAllocatedBytesForCurrentThread();
        var reference = new PolarSampleResult[hilbert.Count];
        long arrayOnlyBytes = GC.GetAllocatedBytesForCurrentThread() - beforeReference;
        GC.KeepAlive(reference);

        long beforeDecompose = GC.GetAllocatedBytesForCurrentThread();
        var result = PolarCoordinateDecompositionEngine.Decompose(hilbert);
        long decomposeBytes = GC.GetAllocatedBytesForCurrentThread() - beforeDecompose;
        GC.KeepAlive(result);

        // The engine allocates the PolarSampleResult[n] plus the small PolarDecompositionResult
        // record; the per-bar loop does no boxing / List / LINQ. Allow a fixed 4 KB slack for the
        // record object and runtime noise, but nothing that scales with the 4000-bar length.
        Assert.True(
            decomposeBytes <= arrayOnlyBytes + 4096,
            $"per-bar allocation detected: decompose={decomposeBytes} bytes, array-only={arrayOnlyBytes} bytes");
    }

    [Fact]
    public void Decompose_PhaseCrossesThreeSixtySeam_UnwrappedDeltaStaysSmall()
    {
        var prices = GenerateSinePrices(400, period: 20.0);

        var result = PolarCoordinateDecompositionEngine.Decompose(prices);

        bool sawSeamCrossing = false;
        for (int i = 80; i < result.Count; i++)
        {
            double prev = result[i - 1].PhaseDegrees;
            double curr = result[i].PhaseDegrees;

            // A large jump in the [0, 360) principal value = the phase crossed the 360 -> 0 seam.
            if (Math.Abs(curr - prev) <= 300.0)
            {
                continue;
            }

            sawSeamCrossing = true;
            double unwrappedDelta = result[i].UnwrappedPhaseDegrees - result[i - 1].UnwrappedPhaseDegrees;

            // Shortest angular path: the unwrapped delta is one bar's worth of advance
            // (~360/20 = 18 deg), never ~-342 deg.
            Assert.True(
                Math.Abs(unwrappedDelta) < 30.0,
                $"unwrapped delta not shortest-path across the seam: {unwrappedDelta} deg");
        }

        Assert.True(sawSeamCrossing, "test data never crossed the 360 -> 0 phase seam");
    }

    [Fact]
    public void Decompose_PeriodBeyondMaxClampsToTrendMode_FrequencyInvalidWhilePhaseValid()
    {
        // True period 80 exceeds HilbertDecompositionParameters.MaxPeriod (50): the dominant-cycle
        // estimate saturates near MaxPeriod, forcing TrendMode while the amplitude stays healthy
        // (so the phase remains valid).
        var prices = GenerateSinePrices(360, period: 80.0, amplitude: 10.0);
        var parameters = new HilbertDecompositionParameters();

        var polar = PolarCoordinateDecompositionEngine.Decompose(prices);
        var hilbert = HilbertDecompositionEngine.Decompose(prices, parameters);

        Assert.Equal(hilbert.Count, polar.Count);

        bool sawPhaseValidTrendModeBar = false;
        for (int i = 0; i < polar.Count; i++)
        {
            if (hilbert[i].IsWarmup || !hilbert[i].TrendMode || !polar[i].IsPhaseValid)
            {
                continue;
            }

            sawPhaseValidTrendModeBar = true;
            Assert.False(polar[i].IsFrequencyValid);
            // U-3(a): the trend-mode-agnostic flag still reports the cycle rate as usable.
            Assert.True(polar[i].IsCycleFrequencyValid);
        }

        Assert.True(sawPhaseValidTrendModeBar, "test data never produced a phase-valid bar in trend mode");
    }

    [Fact]
    public void Decompose_CycleFrequencyValid_DivergesFromFrequencyValidOnlyByTrendModeOrNyquistFloor()
    {
        var prices = GenerateSinePrices(360, period: 80.0, amplitude: 10.0); // drives trend mode
        var parameters = new HilbertDecompositionParameters();

        var polar = PolarCoordinateDecompositionEngine.Decompose(prices);
        var hilbert = HilbertDecompositionEngine.Decompose(prices, parameters);

        Assert.Equal(hilbert.Count, polar.Count);

        bool sawDivergence = false;
        for (int i = 0; i < polar.Count; i++)
        {
            PolarSampleResult p = polar[i];

            // IsFrequencyValid is strictly stronger than IsCycleFrequencyValid.
            if (p.IsFrequencyValid)
            {
                Assert.True(p.IsCycleFrequencyValid);
            }

            if (p.IsCycleFrequencyValid && !p.IsFrequencyValid)
            {
                sawDivergence = true;
                Assert.True(
                    hilbert[i].TrendMode
                        || hilbert[i].InstantaneousPeriod < (decimal)parameters.MinimumCyclePeriod);
            }
        }

        Assert.True(sawDivergence, "test data never separated IsCycleFrequencyValid from IsFrequencyValid");
    }

}
