using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Tests;

public class CoreBSmaIndicatorTests
{
    [Fact]
    public void Factory_CanDiscoverAndCreateBSMA()
    {
        // 1. Factory registration
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.BSMA));

        // 2. Factory creation
        var indicator = IndicatorFactory.Default.Create(IndicatorType.BSMA);
        Assert.NotNull(indicator);
        Assert.IsType<CoreBSmaIndicator>(indicator);

        // 3. Configuration via factory
        var param = new CoreBSmaParameter
        {
            Period = 20,
            Degree = 4,
            Offset = 0.75,
            Sigma = 8.0
        };
        var configuredIndicator = IndicatorFactory.Default.Create(IndicatorType.BSMA, param) as CoreBSmaIndicator;
        Assert.NotNull(configuredIndicator);
        Assert.Equal(20, configuredIndicator.Period);
        Assert.Equal(4, configuredIndicator.Degree);
        Assert.Equal(0.75, configuredIndicator.Offset);
        Assert.Equal(8.0, configuredIndicator.Sigma);
        Assert.Equal("BSMA (20, 4, 0.75, 8.0)", configuredIndicator.Name);
        Assert.True(configuredIndicator.IsOverlay);
    }

    [Fact]
    public void DefaultSettings_AreCorrectAndMatchCategory()
    {
        var indicator = new CoreBSmaIndicator();
        var settings = indicator.GetDefaultSettings();

        Assert.Equal(IndicatorType.BSMA, settings.TypeEnum);
        Assert.True(settings.IsEnabled);
        Assert.Equal(CoreIndicatorCategory.Trend, settings.Category);
        Assert.True(settings.IsOverlay);
        Assert.Equal(IndicatorDefaultConstants.DodgerBlue, settings.Color);
        Assert.Equal(IndicatorDefaultConstants.DefaultOverlayThickness, settings.Thickness);
        Assert.Equal(CoreLineStyle.Solid, settings.Style);

        var param = Assert.IsType<CoreBSmaParameter>(settings.ParameterObject);
        Assert.Equal(14, param.Period);
        Assert.Equal(3, param.Degree);
        Assert.Equal(0.85, param.Offset);
        Assert.Equal(6.0, param.Sigma);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void BasisSplineKernel_MathematicalInvariants_HoldAcrossAllDegrees(int degree)
    {
        double halfSupport = (degree + 1) * 0.5;

        // Test non-negativity and symmetry across multiple points
        for (double z = 0.0; z <= halfSupport + 1.0; z += 0.1)
        {
            double pos = CoreBSmaIndicator.EvaluateBasisSplineKernel(degree, z);
            double neg = CoreBSmaIndicator.EvaluateBasisSplineKernel(degree, -z);

            // Non-negativity
            Assert.True(pos >= 0.0, $"Kernel value must be non-negative for degree {degree} at z={z}");
            Assert.True(neg >= 0.0, $"Kernel value must be non-negative for degree {degree} at z={-z}");

            // Symmetry Mp(z) == Mp(-z)
            Assert.Equal(pos, neg, 10);

            // Support boundary Mp(z) == 0 outside support
            if (z >= halfSupport)
            {
                Assert.Equal(0.0, pos);
                Assert.Equal(0.0, neg);
            }
        }
    }

    [Theory]
    [InlineData(14, 1, 0.5, 5.0)]
    [InlineData(14, 2, 0.85, 6.0)]
    [InlineData(14, 3, 0.85, 6.0)]
    [InlineData(20, 4, 0.7, 4.0)]
    [InlineData(50, 5, 0.9, 10.0)]
    public void ComputeNormalizedWeights_SumEqualsOne(int period, int degree, double offset, double sigma)
    {
        Span<double> weights = stackalloc double[period];
        CoreBSmaIndicator.ComputeNormalizedWeights(period, degree, offset, sigma, weights);

        double sum = 0.0;
        for (int i = 0; i < period; i++)
        {
            Assert.True(weights[i] >= 0.0, $"Weight at {i} must be non-negative");
            sum += weights[i];
        }

        Assert.Equal(1.0, sum, 10);
    }

    [Fact]
    public void Calculate_NullOrEmptyCandles_ReturnsSuccess()
    {
        var indicator = new CoreBSmaIndicator();

        // Null candles
        var nullResult = indicator.Calculate(null!);
        Assert.False(nullResult.IsSuccessful);
        Assert.Empty(indicator.Values);

        // Empty candles
        var emptyResult = indicator.Calculate(new List<CoreCandleData>());
        Assert.True(emptyResult.IsSuccessful);
        Assert.Empty(indicator.Values);
    }

    [Fact]
    public void Calculate_CountLessThanPeriod_ReturnsAllNulls()
    {
        var indicator = new CoreBSmaIndicator { Period = 5 };
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today.AddDays(0), 10m, 12m, 8m, 10m, 1000),
            new(DateTime.Today.AddDays(1), 10m, 13m, 9m, 11m, 1000),
            new(DateTime.Today.AddDays(2), 11m, 14m, 10m, 12m, 1000),
            new(DateTime.Today.AddDays(3), 12m, 15m, 11m, 13m, 1000)
        };

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(4, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_ConstantInput_ProducesExactConstantOutput()
    {
        var indicator = new CoreBSmaIndicator { Period = 14, Degree = 3, Offset = 0.85, Sigma = 6.0 };
        const decimal constantPrice = 123.45m;
        var candles = Enumerable.Range(0, 100)
            .Select(i => new CoreCandleData(DateTime.Today.AddDays(i), constantPrice, constantPrice, constantPrice, constantPrice, 1000))
            .ToList();

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(100, indicator.Values.Count);

        // Leading Period - 1 bars are null
        for (int i = 0; i < 13; i++)
        {
            Assert.Null(indicator.Values[i]);
        }

        // Remaining bars equal constant price within float rounding precision
        for (int i = 13; i < 100; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.Equal((double)constantPrice, (double)indicator.Values[i]!.Value, 6);
        }
    }

    [Fact]
    public void Calculate_StepResponse_ReachesThresholdFasterThanSmaAndRemainsSmooth()
    {
        int period = 14;
        var bsma = new CoreBSmaIndicator { Period = period, Degree = 3, Offset = 0.85, Sigma = 6.0 };
        var sma = new CoreSmaIndicator { Period = period };

        // 50 bars at 0.0 followed by 50 bars at 100.0
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 50; i++)
        {
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), 0m, 0m, 0m, 0m, 1000));
        }
        for (int i = 50; i < 100; i++)
        {
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), 100m, 100m, 100m, 100m, 1000));
        }

        bsma.Calculate(candles);
        sma.Calculate(candles);

        // 1. Find index where output exceeds 50.0 (Step response midpoint)
        int bsmaCrossIndex = -1;
        int smaCrossIndex = -1;

        for (int i = 50; i < 100; i++)
        {
            if (bsmaCrossIndex == -1 && bsma.Values[i].HasValue && bsma.Values[i]!.Value >= 50.0m)
            {
                bsmaCrossIndex = i;
            }
            if (smaCrossIndex == -1 && sma.Values[i].HasValue && sma.Values[i]!.Value >= 50.0m)
            {
                smaCrossIndex = i;
            }
        }

        Assert.True(bsmaCrossIndex > 0);
        Assert.True(smaCrossIndex > 0);
        // BSMA with Offset = 0.85 should cross 50.0 at least 2 bars earlier than standard SMA
        Assert.True(bsmaCrossIndex <= smaCrossIndex - 2, $"BSMA index ({bsmaCrossIndex}) should be at least 2 bars earlier than SMA index ({smaCrossIndex})");

        // 2. Smoothness test: second difference max absolute value in transition region
        decimal maxBsmaSecondDiff = 0m;
        for (int i = 51; i < 99; i++)
        {
            if (bsma.Values[i - 1].HasValue && bsma.Values[i].HasValue && bsma.Values[i + 1].HasValue)
            {
                decimal diff2 = Math.Abs(bsma.Values[i + 1]!.Value - 2 * bsma.Values[i]!.Value + bsma.Values[i - 1]!.Value);
                if (diff2 > maxBsmaSecondDiff) maxBsmaSecondDiff = diff2;
            }
        }

        // Verify that BSMA smooth curve has bounded finite second difference
        Assert.True(maxBsmaSecondDiff < 100m);
    }

    [Fact]
    public void Parameter_Validation_EnforcesStrictBoundaries()
    {
        var param = new CoreBSmaParameter();

        // Valid default
        param.Validate();

        // Period validation
        param.Period = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Period = 501;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Period = 14;

        // Degree validation
        param.Degree = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Degree = 6;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Degree = 3;

        // Offset validation
        param.Offset = -0.01;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Offset = 1.01;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Offset = double.NaN;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Offset = double.PositiveInfinity;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Offset = 0.85;

        // Sigma validation
        param.Sigma = 0.49;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Sigma = 20.01;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Sigma = double.NaN;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Sigma = double.NegativeInfinity;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Sigma = 6.0;

        param.Validate();
    }

    [Fact]
    public void PropertySetters_ClampSafelyAndFallback()
    {
        var indicator = new CoreBSmaIndicator();

        // Period clamp
        indicator.Period = 1;
        Assert.Equal(2, indicator.Period);
        indicator.Period = 1000;
        Assert.Equal(500, indicator.Period);

        // Degree clamp
        indicator.Degree = 0;
        Assert.Equal(1, indicator.Degree);
        indicator.Degree = 10;
        Assert.Equal(5, indicator.Degree);

        // Offset clamp & NaN fallback
        indicator.Offset = -0.5;
        Assert.Equal(0.0, indicator.Offset);
        indicator.Offset = 1.5;
        Assert.Equal(1.0, indicator.Offset);
        indicator.Offset = double.NaN;
        Assert.Equal(0.85, indicator.Offset);

        // Sigma clamp & NaN fallback
        indicator.Sigma = 0.1;
        Assert.Equal(0.5, indicator.Sigma);
        indicator.Sigma = 50.0;
        Assert.Equal(20.0, indicator.Sigma);
        indicator.Sigma = double.PositiveInfinity;
        Assert.Equal(6.0, indicator.Sigma);
    }

    [Fact]
    public void ComputeNormalizedWeights_ExtremeDegenerateParams_FallsBackSafely()
    {
        // 1. Extreme degenerate case with Offset >= 0.5:
        // N=2, Sigma=20.0, Offset=0.5 -> m=0.5, s=0.1, z0=-5, z1=5 -> all outside support [-2, 2]
        Span<double> weightsRecent = stackalloc double[2];
        CoreBSmaIndicator.ComputeNormalizedWeights(2, 3, 0.5, 20.0, weightsRecent);
        Assert.Equal(0.0, weightsRecent[0]);
        Assert.Equal(1.0, weightsRecent[1]);

        // 2. Extreme degenerate case with Offset < 0.5:
        // N=2, Sigma=20.0, Offset=0.2 -> m=0.2, s=0.1, z0=-2, z1=8 -> all outside support or zero sum
        Span<double> weightsOldest = stackalloc double[2];
        CoreBSmaIndicator.ComputeNormalizedWeights(2, 3, 0.2, 20.0, weightsOldest);
        Assert.Equal(1.0, weightsOldest[0]);
        Assert.Equal(0.0, weightsOldest[1]);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Calculate_OffsetBoundaries_ExecutesCorrectly(double offset)
    {
        var indicator = new CoreBSmaIndicator { Period = 10, Degree = 3, Offset = offset, Sigma = 6.0 };
        var candles = Enumerable.Range(0, 50)
            .Select(i => new CoreCandleData(DateTime.Today.AddDays(i), 100m + i, 105m + i, 95m + i, 100m + i, 1000))
            .ToList();

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(50, indicator.Values.Count);

        for (int i = 0; i < 9; i++)
        {
            Assert.Null(indicator.Values[i]);
        }
        for (int i = 9; i < 50; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.True(indicator.Values[i]!.Value >= 90m && indicator.Values[i]!.Value <= 160m);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EvaluateBasisSplineKernel_Analytic_MatchesMathematicalTruncatedPower(int degree)
    {
        double halfSupport = (degree + 1) * 0.5;

        // Truncated-power formula calculator
        double TruncatedPowerRef(int p, double z)
        {
            if (Math.Abs(z) >= (p + 1) * 0.5) return 0.0;
            double sum = 0.0;
            int n = p + 1;
            double factor = 1.0;
            for (int i = 1; i <= p; i++) factor *= i;

            for (int k = 0; k <= n; k++)
            {
                double baseVal = z + (p + 1) * 0.5 - k;
                if (baseVal > 0.0)
                {
                    double term = Math.Pow(baseVal, p);
                    double binom = 1.0;
                    for (int i = 1; i <= k; i++) binom = binom * (n - (k - i)) / i;
                    if ((k & 1) == 1) sum -= binom * term;
                    else sum += binom * term;
                }
            }
            return Math.Max(0.0, sum / factor);
        }

        for (double z = -halfSupport - 1.0; z <= halfSupport + 1.0; z += 0.05)
        {
            double analyticVal = CoreBSmaIndicator.EvaluateBasisSplineKernel(degree, z);
            double refVal = TruncatedPowerRef(degree, z);
            Assert.Equal(refVal, analyticVal, 9);
        }
    }
}
