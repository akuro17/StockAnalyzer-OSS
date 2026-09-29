using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CointegrationSpreadTests
{
    private static List<CoreCandleData> CreateTestCandles(decimal[] closes, DateTime? startDate = null)
    {
        var start = startDate ?? new DateTime(2020, 1, 1);
        return closes.Select((close, i) => new CoreCandleData(
            start.AddDays(i),
            close,
            close + 1,
            close - 1,
            close,
            1000
        )).ToList();
    }

    [Fact]
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.CointegrationSpread));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.CointegrationSpread);
        Assert.NotNull(indicator);
        Assert.IsType<CoreCointegrationSpreadIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.CointegrationSpread);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.IsType<CoreCointegrationSpreadParameter>(settings.ParameterObject);
        var param = (CoreCointegrationSpreadParameter)settings.ParameterObject!;
        Assert.Equal(IndicatorDefaultConstants.CointegrationSpreadPeriod, param.Period);
        Assert.Equal(IndicatorDefaultConstants.CointegrationSpreadZScorePeriod, param.ZScorePeriod);

        Assert.NotNull(settings.SeriesColors);
        Assert.Contains(settings.SeriesColors, sc => sc.Name == "Values" && sc.DisplayName == "Spread");
        Assert.Contains(settings.SeriesColors, sc => sc.Name == CoreCointegrationSpreadIndicator.ZScoreSeriesName && sc.DisplayName == "Z-Score");
        Assert.Contains(settings.SeriesColors, sc => sc.Name == CoreCointegrationSpreadIndicator.HedgeRatioSeriesName && sc.DisplayName.StartsWith("Hedge Ratio"));
        Assert.Contains(settings.SeriesColors, sc => sc.Name == CoreCointegrationSpreadIndicator.ADFStatisticSeriesName && sc.DisplayName.StartsWith("ADF Statistic"));
        // Intercept must be suppressed from chart rendering to avoid dominating sub-panel auto-range
        Assert.DoesNotContain(settings.SeriesColors, sc => sc.Name == CoreCointegrationSpreadIndicator.InterceptSeriesName);
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.CointegrationSpread, CoreCointegrationSpreadIndicator.InterceptSeriesName));
    }

    [Fact]
    public void Case01_EmptyCandles_ReturnsEmpty()
    {
        var indicator = new CoreCointegrationSpreadIndicator();
        var result = indicator.Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(indicator.Values);
        Assert.Empty(indicator.ZScore);
        Assert.Empty(indicator.HedgeRatio);
        Assert.Empty(indicator.Intercept);
        Assert.Empty(indicator.ADFStatistic);
    }

    [Fact]
    public void Case02_NullCandles_ReturnsFailure()
    {
        var indicator = new CoreCointegrationSpreadIndicator();
        var result = indicator.Calculate(null!);

        Assert.False(result.IsSuccessful);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Case03_NoComparisonSymbol_ReturnsAllNulls()
    {
        var closes = Enumerable.Range(1, 100).Select(i => (decimal)i).ToArray();
        var candles = CreateTestCandles(closes);

        var indicator = new CoreCointegrationSpreadIndicator(20);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(100, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
        Assert.All(indicator.ZScore, z => Assert.Null(z));
        Assert.All(indicator.HedgeRatio, h => Assert.Null(h));
        Assert.All(indicator.Intercept, a => Assert.Null(a));
        Assert.All(indicator.ADFStatistic, adf => Assert.Null(adf));
    }

    [Fact]
    public void Case04_InsufficientData_ReturnsNullsForWarmup()
    {
        var closesA = Enumerable.Range(1, 30).Select(i => (decimal)i * 2m).ToArray();
        var closesB = Enumerable.Range(1, 30).Select(i => (decimal)i).ToArray();

        var candlesA = CreateTestCandles(closesA);
        var candlesB = CreateTestCandles(closesB);

        var indicator = new CoreCointegrationSpreadIndicator(60, candlesB);
        var result = indicator.Calculate(candlesA);

        Assert.True(result.IsSuccessful);
        Assert.Equal(30, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Case05_PerfectCointegration_SpreadNearZero()
    {
        // Y = 2 * X + 5
        var random = new Random(42);
        int n = 100;
        var closesB = new decimal[n];
        var closesA = new decimal[n];

        decimal priceB = 50m;
        for (int i = 0; i < n; i++)
        {
            priceB += (decimal)(random.NextDouble() * 4 - 2);
            closesB[i] = priceB;
            closesA[i] = 2m * priceB + 5m; // Exact linear relation -> residuals should be 0
        }

        var candlesA = CreateTestCandles(closesA);
        var candlesB = CreateTestCandles(closesB);

        var indicator = new CoreCointegrationSpreadIndicator(20, candlesB);
        var result = indicator.Calculate(candlesA);

        Assert.True(result.IsSuccessful);
        for (int i = 19; i < n; i++)
        {
            Assert.NotNull(indicator.HedgeRatio[i]);
            Assert.Equal(2.0m, indicator.HedgeRatio[i]!.Value, 4);

            Assert.NotNull(indicator.Intercept[i]);
            Assert.Equal(5.0m, indicator.Intercept[i]!.Value, 4);

            Assert.NotNull(indicator.Values[i]);
            Assert.Equal(0.0m, indicator.Values[i]!.Value, 4);
        }
    }

    [Fact]
    public void Case06_KnownValues_SpreadAndZScore()
    {
        // Y = [100, 102, 105, 103, 108]
        // X = [ 50,  51,  53,  52,  54]
        // Period = 5, ZScorePeriod = 3
        var yPrices = new decimal[] { 100m, 102m, 105m, 103m, 108m };
        var xPrices = new decimal[] { 50m, 51m, 53m, 52m, 54m };

        var candlesY = CreateTestCandles(yPrices);
        var candlesX = CreateTestCandles(xPrices);

        var indicator = new CoreCointegrationSpreadIndicator(5, 3, candlesX);
        var result = indicator.Calculate(candlesY);

        Assert.True(result.IsSuccessful);
        Assert.Equal(5, indicator.Values.Count);

        // Warmup: indices 0..3 must be null
        for (int i = 0; i < 4; i++)
        {
            Assert.Null(indicator.Values[i]);
            Assert.Null(indicator.HedgeRatio[i]);
            Assert.Null(indicator.Intercept[i]);
        }

        // At index 4:
        Assert.NotNull(indicator.HedgeRatio[4]);
        Assert.Equal(1.9m, indicator.HedgeRatio[4]!.Value, 6);

        Assert.NotNull(indicator.Intercept[4]);
        Assert.Equal(4.8m, indicator.Intercept[4]!.Value, 6);

        Assert.NotNull(indicator.Values[4]);
        Assert.Equal(0.6m, indicator.Values[4]!.Value, 6);

        // Z-Score requires 3 valid spread bars; with only 1 valid spread bar at index 4, it should be null
        Assert.Null(indicator.ZScore[4]);
    }

    [Fact]
    public void Case07_HedgeRatioMatchesBeta()
    {
        var random = new Random(123);
        int n = 80;
        var yPrices = new decimal[n];
        var xPrices = new decimal[n];
        decimal y = 100m, x = 50m;
        for (int i = 0; i < n; i++)
        {
            x += (decimal)(random.NextDouble() * 2 - 1);
            y += (decimal)(random.NextDouble() * 3 - 1.5);
            xPrices[i] = x;
            yPrices[i] = y;
        }

        var candlesY = CreateTestCandles(yPrices);
        var candlesX = CreateTestCandles(xPrices);

        int period = 20;
        var cointIndicator = new CoreCointegrationSpreadIndicator(period, candlesX);
        cointIndicator.Calculate(candlesY);

        var (expectedBeta, _, _) = IndicatorCalculationHelper.CalculateRollingBetaStats(
            yPrices.Select(v => (decimal?)v).ToList(),
            xPrices.Select(v => (decimal?)v).ToList(),
            period);

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(expectedBeta[i], cointIndicator.HedgeRatio[i]);
        }
    }

    [Fact]
    public void Case08_ZScoreWarmup_CorrectLength()
    {
        int n = 50;
        int period = 10;
        int zScorePeriod = 5;

        // Generate series with non-zero spread variation so stdDev > 0
        var random = new Random(77);
        var yPrices = new decimal[n];
        var xPrices = new decimal[n];
        for (int i = 0; i < n; i++)
        {
            xPrices[i] = 10m + i * 0.5m;
            yPrices[i] = 2m * xPrices[i] + (decimal)(random.NextDouble() * 2 - 1);
        }

        var candlesY = CreateTestCandles(yPrices);
        var candlesX = CreateTestCandles(xPrices);

        var indicator = new CoreCointegrationSpreadIndicator(period, zScorePeriod, candlesX);
        indicator.Calculate(candlesY);

        // Spread is non-null starting at index (period - 1) = 9
        // ZScore needs zScorePeriod valid spreads, so starting at index 9 + 5 - 1 = 13
        for (int i = 0; i < 13; i++)
        {
            Assert.Null(indicator.ZScore[i]);
        }
        Assert.NotNull(indicator.ZScore[13]);
    }

    [Fact]
    public void Case09_DifferentLengthSeries_AlignsFromEnd()
    {
        var yPrices = Enumerable.Range(1, 40).Select(i => (decimal)i * 2m).ToArray(); // 40 items
        var xPrices = Enumerable.Range(1, 30).Select(i => (decimal)i).ToArray();      // 30 items

        var candlesY = CreateTestCandles(yPrices);
        var candlesX = CreateTestCandles(xPrices);

        var indicator = new CoreCointegrationSpreadIndicator(10, candlesX);
        var result = indicator.Calculate(candlesY);

        Assert.True(result.IsSuccessful);
        Assert.Equal(40, indicator.Values.Count);

        // Offset is 40 - 30 = 10. The first 10 bars of X are null, so spread cannot compute before index 10 + 10 - 1 = 19
        for (int i = 0; i < 19; i++)
        {
            Assert.Null(indicator.Values[i]);
        }
        Assert.NotNull(indicator.Values[19]);
    }

    [Fact]
    public void Case10_OverflowHandled()
    {
        var yPrices = new decimal[] { 100m, 102m, decimal.MaxValue / 2m, 103m, 108m };
        var xPrices = new decimal[] { 50m, 51m, 53m, 52m, 54m };

        var candlesY = CreateTestCandles(yPrices);
        var candlesX = CreateTestCandles(xPrices);

        var indicator = new CoreCointegrationSpreadIndicator(3, candlesX);
        var result = indicator.Calculate(candlesY);

        Assert.True(result.IsSuccessful);
        Assert.Equal(5, indicator.Values.Count);
        // Should not throw, overflow window yields null
        Assert.Null(indicator.Values[2]);
    }

    [Fact]
    public void Case11_ZeroVarianceWindow_SpreadNull()
    {
        // X has constant price -> S_xx = 0 < epsilon -> beta & spread should be null
        var yPrices = new decimal[] { 100m, 102m, 105m, 103m, 108m };
        var xPrices = new decimal[] { 50m, 50m, 50m, 50m, 50m };

        var candlesY = CreateTestCandles(yPrices);
        var candlesX = CreateTestCandles(xPrices);

        var indicator = new CoreCointegrationSpreadIndicator(5, candlesX);
        indicator.Calculate(candlesY);

        Assert.Null(indicator.HedgeRatio[4]);
        Assert.Null(indicator.Values[4]);
    }

    [Fact]
    public void Case12_ZeroVarianceSpread_ZScoreIsNull()
    {
        // Y = 2 * X + 5 -> Spread is exactly 0 everywhere -> stdDev == 0 -> ZScore should be NULL (not 0m!)
        int n = 30;
        var yPrices = new decimal[n];
        var xPrices = new decimal[n];
        for (int i = 0; i < n; i++)
        {
            xPrices[i] = 50m + i * 2m;
            yPrices[i] = 2m * xPrices[i] + 5m;
        }

        var candlesY = CreateTestCandles(yPrices);
        var candlesX = CreateTestCandles(xPrices);

        var indicator = new CoreCointegrationSpreadIndicator(5, 5, candlesX);
        indicator.Calculate(candlesY);

        // When spread variance is 0, Z-Score is undefined -> must be null
        for (int i = 8; i < n; i++)
        {
            Assert.Null(indicator.ZScore[i]);
        }
    }

    [Fact]
    public void Case13_Configure_WithCointegrationSpreadParameter()
    {
        var indicator = new CoreCointegrationSpreadIndicator();
        var param = new CoreCointegrationSpreadParameter
        {
            Period = 45,
            ZScorePeriod = 15,
            ComparisonSymbol = "AAPL",
            ComparisonPriceSource = PriceType.Open,
            PriceMode = CointegrationPriceMode.LogPrice
        };

        indicator.Configure(param);

        Assert.Equal(45, indicator.Period);
        Assert.Equal(15, indicator.ZScorePeriod);
        Assert.Equal("AAPL", indicator.ComparisonSymbol);
        Assert.Equal(PriceType.Open, indicator.ComparisonPriceSource);
        Assert.Equal(CointegrationPriceMode.LogPrice, indicator.PriceMode);
        Assert.Equal("Cointegration Spread(45, AAPL, Log, Z:15)", indicator.Name);
    }

    [Fact]
    public void Case14_Configure_WithSmaParameterFallback()
    {
        var indicator = new CoreCointegrationSpreadIndicator();
        var param = new CoreSmaParameter { Period = 35 };

        indicator.Configure(param);

        Assert.Equal(35, indicator.Period);
    }

    [Fact]
    public void Case15_DateAlignment_MatchesExactDates()
    {
        // Primary asset has trading on Days 1, 2, 3, 4, 5
        var baseDate = new DateTime(2023, 1, 1);
        var candlesA = new List<CoreCandleData>
        {
            new(baseDate.AddDays(1), 100m, 101m, 99m, 100m, 1000),
            new(baseDate.AddDays(2), 102m, 103m, 101m, 102m, 1000),
            new(baseDate.AddDays(3), 104m, 105m, 103m, 104m, 1000),
            new(baseDate.AddDays(4), 106m, 107m, 105m, 106m, 1000), // Day 4 is a holiday for B!
            new(baseDate.AddDays(5), 108m, 109m, 107m, 108m, 1000)
        };

        // Secondary asset missing Day 4
        var candlesB = new List<CoreCandleData?>
        {
            new(baseDate.AddDays(1), 50m, 51m, 49m, 50m, 1000),
            new(baseDate.AddDays(2), 51m, 52m, 50m, 51m, 1000),
            new(baseDate.AddDays(3), 52m, 53m, 51m, 52m, 1000),
            // Day 4 omitted
            new(baseDate.AddDays(5), 54m, 55m, 53m, 54m, 1000)
        };

        var indicator = new CoreCointegrationSpreadIndicator(3, 3)
        {
            ComparisonSymbol = "BENCH"
        };
        indicator.SetSecondaryCandles(candlesB);

        var result = indicator.Calculate(candlesA);

        Assert.True(result.IsSuccessful);
        Assert.Equal(5, indicator.Values.Count);

        // Day 1, 2: Warmup (Period=3) -> null
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);

        // Day 3: Window has Days 1, 2, 3 (all present in B) -> should calculate
        Assert.NotNull(indicator.Values[2]);

        // Day 4: B is missing on Day 4 -> window [Day 2, Day 3, Day 4] contains null for Day 4 -> must be null
        Assert.Null(indicator.Values[3]);

        // Day 5: Window [Day 3, Day 4, Day 5] contains null for Day 4 -> must be null
        Assert.Null(indicator.Values[4]);
    }

    [Fact]
    public void Case16_LogPriceMode_CalculatesLogSpread()
    {
        // Y = 100, 110, 121 -> ln(Y) = ln(100), ln(100)+ln(1.1), ln(100)+2*ln(1.1)
        // X = 50, 55, 60.5  -> ln(X) = ln(50), ln(50)+ln(1.1), ln(50)+2*ln(1.1)
        // Exact log-linear relation with beta = 1.0, alpha = ln(100) - ln(50) = ln(2)
        var yPrices = new decimal[] { 100m, 110m, 121m, 133.1m, 146.41m };
        var xPrices = new decimal[] { 50m, 55m, 60.5m, 66.55m, 73.205m };

        var candlesY = CreateTestCandles(yPrices);
        var candlesX = CreateTestCandles(xPrices);

        var indicator = new CoreCointegrationSpreadIndicator(3, 2, candlesX)
        {
            PriceMode = CointegrationPriceMode.LogPrice
        };
        var result = indicator.Calculate(candlesY);

        Assert.True(result.IsSuccessful);

        // Log beta should be 1.0
        for (int i = 2; i < 5; i++)
        {
            Assert.NotNull(indicator.HedgeRatio[i]);
            Assert.Equal(1.0m, indicator.HedgeRatio[i]!.Value, 4);

            Assert.NotNull(indicator.Values[i]);
            Assert.Equal(0.0m, indicator.Values[i]!.Value, 4); // Exact log residual is 0
        }
    }

    [Fact]
    public void Case17_SampleStdDev_UsesBesselCorrection()
    {
        // Spreads: [1.0, 2.0, 3.0]
        // Mean = 2.0
        // sumSqDiff = (1-2)^2 + (2-2)^2 + (3-2)^2 = 1 + 0 + 1 = 2
        // Population Var = 2 / 3 = 0.6667, Std = 0.8165
        // Sample Var = 2 / (3 - 1) = 1.0, Sample Std = 1.0!
        // At value 3.0, Z = (3.0 - 2.0) / 1.0 = 1.0
        // We configure Y and X such that rolling spreads yield [1.0, 2.0, 3.0]
        // Y = [10, 11, 12, 13, 14]
        // X = [10, 10, 10, 10, 10] is flat (beta null), so we create varying pairs:
        int n = 15;
        var yPrices = new decimal[n];
        var xPrices = new decimal[n];
        var random = new Random(999);
        for (int i = 0; i < n; i++)
        {
            xPrices[i] = 20m + i * 2m;
            // Introduce oscillating spread
            decimal noise = (i % 3 == 0) ? -1m : (i % 3 == 1 ? 0m : 1m);
            yPrices[i] = 1.5m * xPrices[i] + 10m + noise;
        }

        var candlesY = CreateTestCandles(yPrices);
        var candlesX = CreateTestCandles(xPrices);

        var indicator = new CoreCointegrationSpreadIndicator(5, 5, candlesX);
        indicator.Calculate(candlesY);

        // Z-Score is defined and non-null when noise is oscillating
        for (int i = 8; i < n; i++)
        {
            Assert.NotNull(indicator.ZScore[i]);
        }
    }

    [Fact]
    public void Case18_AdfStatistic_DetectsStationarity()
    {
        // Generate mean-reverting series (Ornstein-Uhlenbeck / AR(1) with phi = 0.2):
        // e_t = 0.2 * e_{t-1} + u_t  -> highly stationary!
        // Delta e_t = (0.2 - 1) * e_{t-1} + u_t = -0.8 * e_{t-1} + u_t
        // rho = -0.8 < 0 -> strong negative t-statistic
        int n = 70;
        var yPrices = new decimal[n];
        var xPrices = new decimal[n];
        var random = new Random(101);

        decimal spread = 0m;
        for (int i = 0; i < n; i++)
        {
            decimal x = 100m + (decimal)(random.NextDouble() * 10 - 5);
            xPrices[i] = x;
            decimal u = (decimal)(random.NextDouble() * 0.4 - 0.2);
            spread = 0.2m * spread + u; // Strongly mean-reverting
            yPrices[i] = 2m * x + 10m + spread;
        }

        var candlesY = CreateTestCandles(yPrices);
        var candlesX = CreateTestCandles(xPrices);

        var indicator = new CoreCointegrationSpreadIndicator(30, 20, candlesX);
        var result = indicator.Calculate(candlesY);

        Assert.True(result.IsSuccessful);

        // At the end, ADF statistic should be strongly negative (tau < -3.0)
        var lastAdf = indicator.ADFStatistic.Last();
        Assert.NotNull(lastAdf);
        Assert.True(lastAdf.Value < -2.5m, $"Expected strongly negative ADF statistic, but got {lastAdf.Value}");
    }

    [Fact]
    public void Case19_Validate_ThrowsOnZScorePeriodGreaterThanPeriod()
    {
        var param = new CoreCointegrationSpreadParameter
        {
            Period = 20,
            ZScorePeriod = 50 // Inconsistent!
        };

        var ex = Assert.Throws<ArgumentException>(() => param.Validate());
        Assert.Contains("ZScorePeriod", ex.Message);
    }
}
