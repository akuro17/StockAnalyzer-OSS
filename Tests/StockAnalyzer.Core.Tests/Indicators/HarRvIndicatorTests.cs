using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class HarRvIndicatorTests
{
    private static List<CoreCandleData> BuildVaryingCandles(int count, int seed = 77)
    {
        var rng = new Random(seed);
        var candles = new List<CoreCandleData>(count);
        double close = 100.0;
        for (int i = 0; i < count; i++)
        {
            double drift = 0.0005 + 0.012 * (rng.NextDouble() - 0.5);
            double open = close * (1.0 + 0.004 * (rng.NextDouble() - 0.5));
            double nextClose = open * (1.0 + drift);
            double high = Math.Max(open, nextClose) * (1.0 + 0.003 + 0.01 * rng.NextDouble());
            double low = Math.Min(open, nextClose) * (1.0 - (0.003 + 0.01 * rng.NextDouble()));
            candles.Add(new CoreCandleData(
                new DateTime(2023, 1, 1).AddDays(i),
                (decimal)Math.Round(open, 4),
                (decimal)Math.Round(high, 4),
                (decimal)Math.Round(low, 4),
                (decimal)Math.Round(nextClose, 4),
                1000L + i));
            close = nextClose;
        }

        return candles;
    }

    [Fact]
    public void RogersSatchellStep_MatchesManualLogFormulaAndHandlesInvalidPrices()
    {
        double step = RangeVolatilityMath.ComputeRogersSatchellStep(100m, 110m, 95m, 105m);
        double expected = Math.Log(110.0 / 100.0) * Math.Log(110.0 / 105.0)
                        + Math.Log(95.0 / 100.0) * Math.Log(95.0 / 105.0);

        Assert.Equal(expected, step, 12);
        Assert.True(double.IsNaN(RangeVolatilityMath.ComputeRogersSatchellStep(0m, 110m, 95m, 105m)));
        Assert.True(double.IsNaN(RangeVolatilityMath.ComputeRogersSatchellStep(100m, 110m, -1m, 105m)));
    }

    [Theory]
    [InlineData(HarRvBaseEstimator.RogersSatchell, 0)]
    [InlineData(HarRvBaseEstimator.CloseToClose, 1)]
    [InlineData(HarRvBaseEstimator.YangZhang, 5)]
    public void AllBaseEstimators_RespectWarmupAndProduceAllFourSeries(HarRvBaseEstimator estimator, int expectedDailyWarmup)
    {
        const int shortP = 1;
        const int medP = 3;
        const int longP = 6;
        const int estWindow = 20;
        int firstValidBar = expectedDailyWarmup + longP + estWindow - 1;

        var candles = BuildVaryingCandles(60);
        var indicator = new CoreHarRvIndicator
        {
            BaseEstimator = estimator,
            ShortPeriod = shortP,
            MediumPeriod = medP,
            LongPeriod = longP,
            EstimationWindow = estWindow
        };

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(candles.Count, result.MainValues.Count);
        Assert.True(result.HasSeries(CoreHarRvIndicator.RvShortSeriesName));
        Assert.True(result.HasSeries(CoreHarRvIndicator.RvMediumSeriesName));
        Assert.True(result.HasSeries(CoreHarRvIndicator.RvLongSeriesName));

        var rvLong = result.GetSeries(CoreHarRvIndicator.RvLongSeriesName);
        Assert.Null(rvLong[expectedDailyWarmup + longP - 2]);
        Assert.NotNull(rvLong[expectedDailyWarmup + longP - 1]);

        for (int i = 0; i < firstValidBar; i++)
        {
            Assert.Null(result.MainValues[i]);
        }

        for (int i = firstValidBar; i < candles.Count; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.True(result.MainValues[i]!.Value >= 0m);
        }
    }

    [Fact]
    public void Causality_AppendingFutureBarsDoesNotAlterPrefix()
    {
        var full = BuildVaryingCandles(110, seed: 99);
        var prefix = full.Take(95).ToList();

        var fullResult = new CoreHarRvIndicator().Calculate(full);
        var prefixResult = new CoreHarRvIndicator().Calculate(prefix);

        Assert.True(fullResult.IsSuccessful);
        Assert.True(prefixResult.IsSuccessful);
        for (int i = 0; i < prefix.Count; i++)
        {
            Assert.Equal(prefixResult.MainValues[i], fullResult.MainValues[i]);
            Assert.Equal(prefixResult.GetSeries(CoreHarRvIndicator.RvShortSeriesName)[i], fullResult.GetSeries(CoreHarRvIndicator.RvShortSeriesName)[i]);
        }
    }

    [Fact]
    public void Annualize_ScalesMainAndComponentSeriesBySqrtPeriodsPerYear()
    {
        var candles = BuildVaryingCandles(100, seed: 42);
        var perBar = new CoreHarRvIndicator { Annualize = false, PeriodsPerYear = 52 }.Calculate(candles);
        var annual = new CoreHarRvIndicator { Annualize = true, PeriodsPerYear = 52 }.Calculate(candles);

        double factor = Math.Sqrt(52.0);
        for (int i = 81; i < candles.Count; i++)
        {
            Assert.NotNull(perBar.MainValues[i]);
            Assert.NotNull(annual.MainValues[i]);
            Assert.Equal((double)perBar.MainValues[i]!.Value * factor, (double)annual.MainValues[i]!.Value, 1e-6);
            Assert.Equal(
                (double)perBar.GetSeries(CoreHarRvIndicator.RvLongSeriesName)[i]!.Value * factor,
                (double)annual.GetSeries(CoreHarRvIndicator.RvLongSeriesName)[i]!.Value,
                1e-6);
        }
    }

    [Fact]
    public void ZeroVarianceConstantCandles_ResolveWithoutFailureOrNonFiniteOutput()
    {
        var flat = Enumerable.Range(0, 100)
            .Select(i => new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), 100m, 100m, 100m, 100m, 100L))
            .ToList();

        var result = new CoreHarRvIndicator().Calculate(flat);

        Assert.True(result.IsSuccessful);
        Assert.Equal(100, result.MainValues.Count);
        Assert.Equal(0m, result.MainValues[81]);
    }

    [Fact]
    public void ValidationAndRegistration_EnforceHorizonOrderingAndSsoTEntries()
    {
        var p = new CoreHarRvParameter();
        p.Validate();
        Assert.Equal(81, p.GetRequiredWarmupBars());

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHarRvParameter { ShortPeriod = 5, MediumPeriod = 5 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHarRvParameter { MediumPeriod = 22, LongPeriod = 22 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHarRvParameter { EstimationWindow = 10 }.Validate());

        var created = IndicatorFactory.CreateStatic(IndicatorType.HarRv);
        Assert.IsType<CoreHarRvIndicator>(created);

        var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.HarRv);
        Assert.Equal(CoreIndicatorCategory.Volatility, settings.Category);
        Assert.IsType<CoreHarRvParameter>(settings.ParameterObject);
        Assert.Equal(4, settings.SeriesColors.Count);

        var volGroup = ScreenerIndicatorGroup.GetDefaultGroups().Single(g => g.Name == "Volatility");
        Assert.Contains(IndicatorType.HarRv, volGroup.IndicatorTypes);
    }

    [Fact]
    public void ScaleInvariance_LowVolatilityAndHighVolatilitySeries_ScaleLinearly()
    {
        // Build two candle streams with identical relative dynamics: 0.1% daily vol vs 10% daily vol (100x scale).
        const int count = 120;
        var rng = new Random(2026);
        var lowVolCandles = new List<CoreCandleData>(count);
        var highVolCandles = new List<CoreCandleData>(count);
        double closeLow = 100.0;
        double closeHigh = 100.0;

        for (int i = 0; i < count; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            double regimeMult = (i / 15) % 2 == 0 ? 1.5 : 0.6;
            double rBase = 0.001 * regimeMult * z;

            closeLow *= Math.Exp(rBase);
            closeHigh *= Math.Exp(100.0 * rBase);

            decimal pLow = (decimal)Math.Round(closeLow, 8);
            decimal pHigh = (decimal)Math.Round(closeHigh, 8);
            DateTime dt = new DateTime(2024, 1, 1).AddDays(i);
            lowVolCandles.Add(new CoreCandleData(dt, pLow, pLow, pLow, pLow, 1000L));
            highVolCandles.Add(new CoreCandleData(dt, pHigh, pHigh, pHigh, pHigh, 1000L));
        }

        var lowIndicator = new CoreHarRvIndicator
        {
            BaseEstimator = HarRvBaseEstimator.CloseToClose,
            ShortPeriod = 1,
            MediumPeriod = 5,
            LongPeriod = 10,
            EstimationWindow = 40
        };
        var highIndicator = new CoreHarRvIndicator
        {
            BaseEstimator = HarRvBaseEstimator.CloseToClose,
            ShortPeriod = 1,
            MediumPeriod = 5,
            LongPeriod = 10,
            EstimationWindow = 40
        };

        var resLow = lowIndicator.Calculate(lowVolCandles);
        var resHigh = highIndicator.Calculate(highVolCandles);

        Assert.True(resLow.IsSuccessful && resHigh.IsSuccessful);
        for (int i = 50; i < count; i++)
        {
            Assert.NotNull(resLow.MainValues[i]);
            Assert.NotNull(resHigh.MainValues[i]);
            double lowVal = (double)resLow.MainValues[i]!.Value;
            double highVal = (double)resHigh.MainValues[i]!.Value;
            if (lowVal > 1e-6)
            {
                double ratio = highVal / lowVal;
                Assert.InRange(ratio, 99.99, 100.01);
            }
        }
    }
}
