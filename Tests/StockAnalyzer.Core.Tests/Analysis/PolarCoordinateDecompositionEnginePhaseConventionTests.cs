using System;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Regression guards for the Ehlers <c>atan2(-Q, I)</c> phase sign convention (analytic signal
/// z = I - jQ, phase advancing). These lock the rotation direction and quadrant mapping so a future
/// "bug fix" that flips the sign back to the textbook <c>atan2(Q, I)</c> is caught here as well as
/// in the reused <see cref="HilbertDecompositionEngine"/>. The verbatim pass-through and the
/// <c>omega = 2*pi / P</c> identity are already covered by
/// <see cref="PolarCoordinateDecompositionEngineTests"/>; this file adds the direction-sensitive checks.
/// </summary>
public class PolarCoordinateDecompositionEnginePhaseConventionTests
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
    public void Decompose_PhaseDegrees_QuadrantMatchesSignsOfInPhaseAndMinusQuadrature()
    {
        var prices = GenerateSinePrices(240, period: 20.0);

        var result = PolarCoordinateDecompositionEngine.Decompose(prices);

        int checkedBars = 0;
        for (int i = 0; i < result.Count; i++)
        {
            var s = result[i];
            if (s.IsWarmup || !s.IsValid)
            {
                continue;
            }

            double x = (double)s.InPhase;      // real part
            double y = -(double)s.Quadrature;  // imaginary part under Ehlers z = I - jQ

            // Stay off the axes so quadrant classification is unambiguous.
            if (Math.Abs(x) < 1e-6 || Math.Abs(y) < 1e-6)
            {
                continue;
            }

            double expectedDeg = Math.Atan2(y, x) * 180.0 / Math.PI;
            if (expectedDeg < 0.0)
            {
                expectedDeg += 360.0;
            }

            Assert.Equal(expectedDeg, s.PhaseDegrees, 9);

            if (x > 0 && y > 0)
            {
                Assert.InRange(s.PhaseDegrees, 0.0, 90.0);
            }
            else if (x < 0 && y > 0)
            {
                Assert.InRange(s.PhaseDegrees, 90.0, 180.0);
            }
            else if (x < 0 && y < 0)
            {
                Assert.InRange(s.PhaseDegrees, 180.0, 270.0);
            }
            else
            {
                Assert.InRange(s.PhaseDegrees, 270.0, 360.0);
            }

            checkedBars++;
        }

        Assert.True(checkedBars > 20, "fixture must exercise multiple quadrants");
    }

    [Fact]
    public void Decompose_UnwrappedPhaseDegrees_RotatesOneConsistentDirectionForPureSine()
    {
        var prices = GenerateSinePrices(320, period: 20.0);

        var result = PolarCoordinateDecompositionEngine.Decompose(prices);

        int start = result.WarmupBars + 20;
        int positive = 0;
        int negative = 0;
        int total = 0;
        for (int i = start + 1; i < result.Count; i++)
        {
            if (!result[i].IsValid || !result[i - 1].IsValid)
            {
                continue;
            }

            double d = result[i].UnwrappedPhaseDegrees - result[i - 1].UnwrappedPhaseDegrees;
            if (d > 0.0)
            {
                positive++;
            }
            else if (d < 0.0)
            {
                negative++;
            }

            total++;
        }

        Assert.True(total > 100, "fixture must produce a long valid stretch");
        int dominant = Math.Max(positive, negative);
        Assert.True(
            dominant >= 0.95 * total,
            $"unwrapped phase should rotate one consistent direction for a pure sine (pos={positive}, neg={negative}, total={total})");
    }

    [Fact]
    public void Decompose_CycleAngularFrequency_IncreasesWhenCyclePeriodShortens()
    {
        // 30-bar cycle for the first half, then a phase-continuous 10-bar cycle: the shorter period
        // must raise omega = 2*pi / P once the Homodyne estimate has converged.
        var prices = new decimal[360];
        for (int i = 0; i < 180; i++)
        {
            prices[i] = (decimal)(100.0 + 10.0 * Math.Sin(2.0 * Math.PI * i / 30.0));
        }

        double phaseAtSwitch = 2.0 * Math.PI * 180 / 30.0;
        for (int i = 180; i < 360; i++)
        {
            prices[i] = (decimal)(100.0 + 10.0 * Math.Sin(phaseAtSwitch + (2.0 * Math.PI * (i - 180) / 10.0)));
        }

        var result = PolarCoordinateDecompositionEngine.Decompose(prices);

        double slowMean = MeanCycleAngularFrequency(result, 120, 175);
        double fastMean = MeanCycleAngularFrequency(result, 300, 355);

        Assert.True(
            fastMean > slowMean,
            $"omega must grow as the cycle shortens (slow={slowMean:F4}, fast={fastMean:F4})");
    }

    private static double MeanCycleAngularFrequency(PolarDecompositionResult result, int from, int to)
    {
        double sum = 0.0;
        int n = 0;
        for (int i = from; i <= to && i < result.Count; i++)
        {
            if (!result[i].IsValid || double.IsNaN(result[i].CycleAngularFrequency))
            {
                continue;
            }

            sum += result[i].CycleAngularFrequency;
            n++;
        }

        Assert.True(n > 0, "segment must contain valid samples");
        return sum / n;
    }
}
