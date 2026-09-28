using System;
using Xunit;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Tests.MathUtils;

public class ArimaMathTests
{
    [Fact]
    public void Difference_D0_CopiesExactValues()
    {
        double[] input = [1.0, 2.0, 4.0, 7.0];
        double[] output = new double[4];
        ArimaMath.Difference(input, output, 0);

        Assert.Equal(input, output);
    }

    [Fact]
    public void Difference_D1_ComputesFirstDifferences()
    {
        double[] input = [1.0, 3.0, 6.0, 10.0];
        double[] output = new double[3];
        ArimaMath.Difference(input, output, 1);

        double[] expected = [2.0, 3.0, 4.0];
        Assert.Equal(expected, output);
    }

    [Fact]
    public void Difference_D2_ComputesSecondDifferences()
    {
        double[] input = [1.0, 3.0, 6.0, 10.0];
        double[] output = new double[2];
        ArimaMath.Difference(input, output, 2);

        // 1st diff: [2, 3, 4], 2nd diff: [1, 1]
        double[] expected = [1.0, 1.0];
        Assert.Equal(expected, output);
    }

    [Fact]
    public void Difference_InvalidD_ThrowsArgumentOutOfRangeException()
    {
        double[] input = [1.0, 2.0, 3.0];
        double[] output = new double[3];
        Assert.Throws<ArgumentOutOfRangeException>(() => ArimaMath.Difference(input, output, 3));
    }

    [Fact]
    public void Undifference_D0_ReturnsForecastDirectly()
    {
        double[] rawWindow = [10.0, 20.0, 30.0];
        double forecast = ArimaMath.Undifference(5.5, rawWindow, 0);
        Assert.Equal(5.5, forecast);
    }

    [Fact]
    public void Undifference_D1_AddsDiffToLastPrice()
    {
        double[] rawWindow = [10.0, 20.0, 30.0];
        double forecast = ArimaMath.Undifference(5.0, rawWindow, 1);
        Assert.Equal(35.0, forecast); // 30 + 5
    }

    [Fact]
    public void Undifference_D2_ReconstructsSecondDifferencePrice()
    {
        double[] rawWindow = [1.0, 3.0, 6.0, 10.0];
        // 2 * 10 - 6 + 1 = 20 - 6 + 1 = 15
        double forecast = ArimaMath.Undifference(1.0, rawWindow, 2);
        Assert.Equal(15.0, forecast);
    }

    [Fact]
    public void SolveLevinsonDurbin_AR1_MatchesTheoreticalSolution()
    {
        // rho1 = 0.5 -> phi1 = 0.5, sigmaSq = 1 - 0.5^2 = 0.75
        double[] autocov = [1.0, 0.5];
        double[] phi = new double[1];

        bool success = ArimaMath.SolveLevinsonDurbin(autocov, phi, out double sigmaSq);

        Assert.True(success);
        Assert.Equal(0.5, phi[0], precision: 5);
        Assert.Equal(0.75, sigmaSq, precision: 5);
    }

    [Fact]
    public void SolveLevinsonDurbin_ZeroVariance_ReturnsFalse()
    {
        double[] autocov = [0.0, 0.0];
        double[] phi = new double[1];

        bool success = ArimaMath.SolveLevinsonDurbin(autocov, phi, out double sigmaSq);

        Assert.False(success);
        Assert.Equal(0.0, phi[0]);
        Assert.Equal(0.0, sigmaSq);
    }

    [Fact]
    public void SolveLinearSystem_2x2_SolvesCorrectly()
    {
        // 2x + y = 5
        // x + 3y = 5
        // Solution: x = 2, y = 1
        double[] a = [2.0, 1.0, 1.0, 3.0];
        double[] b = [5.0, 5.0];
        double[] x = new double[2];

        bool success = ArimaMath.SolveLinearSystem(a, b, x, 2);

        Assert.True(success);
        Assert.Equal(2.0, x[0], precision: 5);
        Assert.Equal(1.0, x[1], precision: 5);
    }

    [Fact]
    public void EstimateArimaForecast_ZeroVariance_FallsBackToLastPrice()
    {
        double[] constantSeries = new double[30];
        Array.Fill(constantSeries, 100.0);

        bool success = ArimaMath.EstimateArimaForecast(constantSeries, 1, 1, 1, out double forecast);

        // Deterministic fallback: returns false and forecast == 100.0
        Assert.False(success);
        Assert.Equal(100.0, forecast);
    }

    [Fact]
    public void EstimateArimaForecast_ZeroVariance_HighPricedAndPennyStock_FallsBackCorrectly()
    {
        // High priced asset (e.g. Berkshire Hathaway / 50,000)
        double[] highPrice = new double[30];
        Array.Fill(highPrice, 50000.0);
        bool successHigh = ArimaMath.EstimateArimaForecast(highPrice, 1, 1, 1, out double forecastHigh);
        Assert.False(successHigh);
        Assert.Equal(50000.0, forecastHigh);

        // Penny stock / crypto (e.g. 0.0001)
        double[] pennyStock = new double[30];
        Array.Fill(pennyStock, 0.0001);
        bool successPenny = ArimaMath.EstimateArimaForecast(pennyStock, 1, 1, 1, out double forecastPenny);
        Assert.False(successPenny);
        Assert.Equal(0.0001, forecastPenny);
    }

    [Fact]
    public void EstimateArimaForecast_LinearRamp_WithD1_ForecastsNextStep()
    {
        // [1, 2, 3, ..., 30] with d=1, p=0, q=0 -> constant delta 1.0, forecast = 31.0
        double[] ramp = new double[30];
        for (int i = 0; i < 30; i++) ramp[i] = i + 1;

        bool success = ArimaMath.EstimateArimaForecast(ramp, 0, 1, 0, out double forecast);

        Assert.True(success);
        Assert.Equal(31.0, forecast, precision: 5);
    }

    [Fact]
    public void EstimateArimaForecast_InsufficientSamples_ReturnsFalse()
    {
        double[] shortSeries = [10.0, 12.0, 14.0];

        bool success = ArimaMath.EstimateArimaForecast(shortSeries, 2, 1, 2, out double forecast);

        Assert.False(success);
        Assert.Equal(14.0, forecast); // last price fallback
    }

    [Fact]
    public void EstimateArimaForecast_NaNOrInfinity_ReturnsFalse()
    {
        double[] corrupted = new double[30];
        Array.Fill(corrupted, 50.0);
        corrupted[10] = double.NaN;

        bool success = ArimaMath.EstimateArimaForecast(corrupted, 1, 1, 1, out double forecast);

        Assert.False(success);
    }

    [Fact]
    public void EstimateArimaForecast_ARIMA111_ProducesFiniteForecast()
    {
        // Synthetic oscillating price series
        double[] series = new double[50];
        for (int i = 0; i < 50; i++)
        {
            series[i] = 100.0 + i * 0.5 + Math.Sin(i * 0.3) * 5.0;
        }

        bool success = ArimaMath.EstimateArimaForecast(series, 1, 1, 1, out double forecast);

        Assert.True(success);
        Assert.False(double.IsNaN(forecast));
        Assert.False(double.IsInfinity(forecast));
        Assert.InRange(forecast, 90.0, 160.0);
    }

    [Fact]
    public void EstimateArimaMultiStepForecast_InsufficientData_ReturnsFalse()
    {
        double[] shortSeries = [100.0, 101.0];
        double[] forecasted = new double[10];
        double[] variances = new double[10];

        bool success = ArimaMath.EstimateArimaMultiStepForecast(
            shortSeries,
            p: 1, d: 2, q: 1,
            futureSteps: 10,
            forecasted,
            variances,
            out double innovationVariance,
            out double residualStdDev);

        Assert.False(success);
        Assert.Equal(shortSeries[^1], forecasted[0]);
    }

    [Fact]
    public void EstimateArimaMultiStepForecast_NaNOrInfinity_ReturnsFalse()
    {
        double[] series = new double[30];
        Array.Fill(series, 100.0);
        series[15] = double.NaN;
        double[] forecasted = new double[10];
        double[] variances = new double[10];

        bool success = ArimaMath.EstimateArimaMultiStepForecast(
            series,
            p: 1, d: 1, q: 1,
            futureSteps: 10,
            forecasted,
            variances,
            out _, out _);

        Assert.False(success);
    }

    [Fact]
    public void EstimateArimaMultiStepForecast_ConstantSeries_ReturnsFlat()
    {
        double[] constantSeries = new double[30];
        Array.Fill(constantSeries, 50.0);
        double[] forecasted = new double[10];
        double[] variances = new double[10];

        bool success = ArimaMath.EstimateArimaMultiStepForecast(
            constantSeries,
            p: 1, d: 1, q: 1,
            futureSteps: 10,
            forecasted,
            variances,
            out double innovationVariance,
            out _);

        Assert.True(success);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(50.0, forecasted[i], 4);
        }
    }

    [Fact]
    public void EstimateArimaMultiStepForecast_RandomWalk_ProducesLinearForecast()
    {
        // Linear ramp: x_t = 10 + 2*t. Difference d=1 is constant 2.0. p=0, q=0.
        double[] ramp = new double[20];
        for (int i = 0; i < 20; i++) ramp[i] = 10.0 + 2.0 * i;

        double[] forecasted = new double[5];
        double[] variances = new double[5];

        bool success = ArimaMath.EstimateArimaMultiStepForecast(
            ramp,
            p: 0, d: 1, q: 0,
            futureSteps: 5,
            forecasted,
            variances,
            out _, out _);

        Assert.True(success);
        double lastObserved = ramp[^1]; // 48.0
        for (int h = 1; h <= 5; h++)
        {
            Assert.Equal(lastObserved + 2.0 * h, forecasted[h - 1], 4);
        }
    }

    [Fact]
    public void EstimateArimaMultiStepForecast_D2_UndifferencesAccurately()
    {
        // Quadratic series: x_t = t^2. 1st diff: 2t+1. 2nd diff: 2.
        double[] quad = new double[20];
        for (int i = 0; i < 20; i++) quad[i] = i * i;

        double[] forecasted = new double[5];
        double[] variances = new double[5];

        bool success = ArimaMath.EstimateArimaMultiStepForecast(
            quad,
            p: 0, d: 2, q: 0,
            futureSteps: 5,
            forecasted,
            variances,
            out _, out _);

        Assert.True(success);
        for (int h = 1; h <= 5; h++)
        {
            int t = 19 + h;
            Assert.Equal((double)(t * t), forecasted[h - 1], 1);
        }
    }

    [Fact]
    public void EstimateArimaMultiStepForecast_ARIMA111_ProducesFiniteForecastsAndMonotonicVariances()
    {
        double[] series = new double[60];
        for (int i = 0; i < 60; i++)
        {
            series[i] = 100.0 + i * 0.3 + Math.Sin(i * 0.25) * 4.0;
        }

        double[] forecasted = new double[20];
        double[] variances = new double[20];

        bool success = ArimaMath.EstimateArimaMultiStepForecast(
            series,
            p: 1, d: 1, q: 1,
            futureSteps: 20,
            forecasted,
            variances,
            out double innovationVar,
            out double residStdDev);

        Assert.True(success);
        Assert.True(innovationVar > 0.0);
        Assert.True(residStdDev > 0.0);

        for (int h = 0; h < 20; h++)
        {
            Assert.False(double.IsNaN(forecasted[h]));
            Assert.False(double.IsInfinity(forecasted[h]));
            Assert.False(double.IsNaN(variances[h]));
            Assert.True(variances[h] >= 0.0);
            if (h > 0)
            {
                // Error variance is cumulative and non-decreasing
                Assert.True(variances[h] >= variances[h - 1] - 1e-9);
            }
        }
    }

    [Fact]
    public void EstimateArimaMultiStepForecast_CaseC_InsufficientSample_ReturnsFalseWithoutSilentFallback()
    {
        // 15 samples: N < 20 for Case C with q > 0
        double[] shortSeries = new double[15];
        for (int i = 0; i < 15; i++) shortSeries[i] = 100.0 + i;

        double[] forecasted = new double[5];
        double[] variances = new double[5];

        // Should return false and NOT silently change to AR(1) or mean
        bool success = ArimaMath.EstimateArimaMultiStepForecast(
            shortSeries,
            p: 1, d: 0, q: 1,
            futureSteps: 5,
            forecasted,
            variances,
            out _, out _);

        Assert.False(success);
    }
}

