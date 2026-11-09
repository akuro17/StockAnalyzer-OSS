using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CorePacfIndicatorTests
{
    [Fact]
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.PACF));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.PACF);
        Assert.NotNull(indicator);
        Assert.IsType<CorePacfIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.PACF);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.Equal(-1.0m, settings.MinValue);
        Assert.Equal(1.0m, settings.MaxValue);
        Assert.True(settings.IncludeZeroBaseline);
        Assert.IsType<CorePacfParameter>(settings.ParameterObject);

        var param = (CorePacfParameter)settings.ParameterObject!;
        Assert.Equal(50, param.Period);
        Assert.Equal(1, param.Lag);
        Assert.Equal(CorrelationCalculationMode.PriceLevel, param.CalculationMode);
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CorePacfParameter
        {
            Period = 50,
            Lag = 1,
            CalculationMode = CorrelationCalculationMode.PriceLevel
        };
        param.Validate();

        param.Period = 9;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Period = 501;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Period = 50;

        param.Lag = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Lag = 51;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Lag = 1;
        param.Validate();
    }

    [Fact]
    public void PacfMath_AR1_Process_TheoreticalMatch()
    {
        // For AR(1) process: X_t = 0.7 * X_{t-1} + e_t
        // PACF at lag 1 should be ~0.7, and at lag 2 should be ~0.0
        int n = 1000;
        double[] series = new double[n];
        var rand = new Random(42);
        series[0] = 100.0;
        for (int t = 1; t < n; t++)
        {
            double noise = (rand.NextDouble() - 0.5) * 2.0;
            series[t] = 0.7 * series[t - 1] + noise;
        }

        Span<double> pacfCoeffs = stackalloc double[3];
        bool ok = PacfMath.TryCalculatePacf(series, 2, pacfCoeffs, out double variance);

        Assert.True(ok);
        Assert.True(variance > 0.0);
        // PACF(1) should be close to 0.7
        Assert.InRange(pacfCoeffs[1], 0.60, 0.80);
        // PACF(2) should cut off close to 0.0
        Assert.InRange(pacfCoeffs[2], -0.10, 0.10);
    }

    [Fact]
    public void Calculate_AR1_IndicatorExecution()
    {
        int period = 60;
        int count = 100;
        var rand = new Random(123);
        decimal current = 50m;
        var candles = CreateSampleCandles(count, _ =>
        {
            current = 0.65m * current + (decimal)(rand.NextDouble() - 0.5) * 4m;
            return Math.Max(1m, current + 50m);
        });

        var indicator = new CorePacfIndicator(period, lag: 1);
        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        var pacf = result.MainValues;
        var upper = result.GetSeries(CorePacfIndicator.UpperConfidenceBandSeriesName);
        var lower = result.GetSeries(CorePacfIndicator.LowerConfidenceBandSeriesName);

        for (int i = 0; i < period - 1; i++)
        {
            Assert.Null(pacf[i]);
            Assert.Null(upper[i]);
            Assert.Null(lower[i]);
        }

        for (int i = period - 1; i < count; i++)
        {
            Assert.NotNull(pacf[i]);
            Assert.InRange(pacf[i]!.Value, -1.0m, 1.0m);

            Assert.NotNull(upper[i]);
            Assert.NotNull(lower[i]);
            Assert.Equal(-upper[i]!.Value, lower[i]!.Value);
            Assert.True(upper[i]!.Value > 0m);
        }
    }

    [Fact]
    public void Calculate_ZeroVariance_ReturnsNull()
    {
        int period = 20;
        int count = 30;
        var candles = CreateSampleCandles(count, _ => 100m); // Constant

        var indicator = new CorePacfIndicator(period, lag: 1);
        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        Assert.All(result.MainValues, Assert.Null);
    }

    [Fact]
    public void Calculate_LogReturnMode_HandlesNonPositiveSafely()
    {
        int period = 20;
        int count = 30;
        var candles = CreateSampleCandles(count, i => i == 15 ? 0.0m : 100m + i);

        var param = new CorePacfParameter
        {
            Period = period,
            Lag = 1,
            CalculationMode = CorrelationCalculationMode.LogReturn
        };

        var indicator = new CorePacfIndicator();
        indicator.Configure(param);

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        // Window containing bar 15 should evaluate to null
        Assert.Null(result.MainValues[15]);
        Assert.Null(result.MainValues[16]);
    }

    [Fact]
    public void Calculate_EffectiveLengthLessThanOrEqualToLag_ReturnsNullSafely()
    {
        int period = 10;
        int count = 20;
        var candles = CreateSampleCandles(count, i => 100m + i);

        // In LogReturn mode, effective length = 10 - 1 = 9.
        // If Lag = 9, effectiveLength <= Lag triggers runtime guard.
        var param = new CorePacfParameter
        {
            Period = period,
            Lag = 9,
            CalculationMode = CorrelationCalculationMode.LogReturn
        };

        var indicator = new CorePacfIndicator();
        indicator.Configure(param);

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);
        Assert.All(result.MainValues, Assert.Null);
    }

    private static List<CoreCandleData> CreateSampleCandles(int count, Func<int, decimal> priceSelector)
    {
        var list = new List<CoreCandleData>(count);
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
        {
            decimal p = priceSelector(i);
            list.Add(new CoreCandleData(
                baseDate.AddDays(i),
                p,
                p + 1m,
                p - 1m,
                p,
                1000L
            ));
        }
        return list;
    }
}
