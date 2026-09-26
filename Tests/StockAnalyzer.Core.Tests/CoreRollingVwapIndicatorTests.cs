using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volume;
using StockAnalyzer.Core.Models.Parameters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreRollingVwapIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(decimal[] highs, decimal[] lows, decimal[] closes, decimal[] volumes)
    {
        var startDate = DateTime.Today;
        return closes.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i),
            closes[i], // Open
            highs[i],
            lows[i],
            closes[i],
            (long)volumes[i]
        )).ToList();
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectRollingVwap()
    {
        // Arrange
        var indicator = new CoreRollingVwapIndicator { Period = 3 };
        var candles = CreateTestCandles(
            new decimal[] { 10, 11, 12, 14 },
            new decimal[] { 8,  9,  10, 12 },
            new decimal[] { 9,  10, 11, 13 },
            new decimal[] { 100, 150, 200, 100 }
        );

        // Typical prices:
        // Bar 0: (10 + 8 + 9) / 3 = 9, PV = 900, V = 100
        // Bar 1: (11 + 9 + 10) / 3 = 10, PV = 1500, V = 150
        // Bar 2: (12 + 10 + 11) / 3 = 11, PV = 2200, V = 200
        // Bar 3: (14 + 12 + 13) / 3 = 13, PV = 1300, V = 100
        //
        // Window 3:
        // Bar 0: null (warmup)
        // Bar 1: null (warmup)
        // Bar 2: (900 + 1500 + 2200) / (100 + 150 + 200) = 4600 / 450 = 10.222222222222222222222222222m
        // Bar 3: (1500 + 2200 + 1300) / (150 + 200 + 100) = 5000 / 450 = 11.111111111111111111111111111m

        // Act
        indicator.Calculate(candles);

        // Assert
        Assert.Equal(4, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.NotNull(indicator.Values[2]);
        Assert.NotNull(indicator.Values[3]);

        Assert.Equal(4600m / 450m, indicator.Values[2]!.Value);
        Assert.Equal(5000m / 450m, indicator.Values[3]!.Value);
    }

    [Fact]
    public void Calculate_WithPeriod1_ReturnsExactPrice()
    {
        var indicator = new CoreRollingVwapIndicator { Period = 1 };
        var candles = CreateTestCandles(
            new decimal[] { 11, 22, 33 },
            new decimal[] { 9,  18, 27 },
            new decimal[] { 10, 20, 30 },
            new decimal[] { 100, 200, 300 }
        );

        indicator.Calculate(candles);

        // Typical prices: 10, 20, 30
        Assert.Equal(3, indicator.Values.Count);
        Assert.Equal(10m, indicator.Values[0]);
        Assert.Equal(20m, indicator.Values[1]);
        Assert.Equal(30m, indicator.Values[2]);
    }

    [Fact]
    public void Calculate_WithZeroVolume_ReturnsNull()
    {
        var indicator = new CoreRollingVwapIndicator { Period = 2 };
        var candles = CreateTestCandles(
            new decimal[] { 10, 12, 14 },
            new decimal[] { 8,  10, 12 },
            new decimal[] { 9,  11, 13 },
            new decimal[] { 0,  0,  0 }
        );

        indicator.Calculate(candles);

        Assert.Equal(3, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNulls()
    {
        var indicator = new CoreRollingVwapIndicator { Period = 5 };
        var candles = CreateTestCandles(
            new decimal[] { 10, 12, 14 },
            new decimal[] { 8,  10, 12 },
            new decimal[] { 9,  11, 13 },
            new decimal[] { 100, 100, 100 }
        );

        indicator.Calculate(candles);

        Assert.Equal(3, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreRollingVwapIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }

    [Fact]
    public void Calculate_WithIntermittentNullPrices_ReturnsNullInWindow()
    {
        var prices = new decimal?[] { 10m, null, 12m, 14m, 16m };
        var volumes = new long[] { 100, 100, 100, 100, 100 };

        var result = IndicatorCalculationHelper.CalculateRollingVwap(prices, volumes, period: 3);

        Assert.Equal(5, result.Count);
        Assert.Null(result[0]); // warmup
        Assert.Null(result[1]); // warmup
        Assert.Null(result[2]); // window [0..2] contains null at index 1
        Assert.Null(result[3]); // window [1..3] contains null at index 1
        Assert.NotNull(result[4]); // window [2..4] has 12, 14, 16 -> (12+14+16)/3 = 14m
        Assert.Equal(14m, result[4]!.Value);
    }

    [Fact]
    public void DefaultPriceSource_IsTypical()
    {
        var indicator = new CoreRollingVwapIndicator();
        Assert.Equal(PriceType.Typical, indicator.PriceSource);
    }

    [Fact]
    public void DefaultSettings_PriceSource_IsTypical()
    {
        var indicator = new CoreRollingVwapIndicator();
        var settings = indicator.GetDefaultSettings();
        Assert.Equal(PriceType.Typical, settings.PriceSource);
        Assert.True(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Volume, settings.Category);
        Assert.Equal(IndicatorType.RollingVWAP, settings.TypeEnum);
    }

    [Fact]
    public void DefaultPeriod_Is20()
    {
        var indicator = new CoreRollingVwapIndicator();
        Assert.Equal(IndicatorDefaultConstants.RollingVwapPeriod, indicator.Period);
        Assert.Equal(20, indicator.Period);
    }

    [Fact]
    public void Configure_WithCoreRollingVwapParameter_SetsPeriod()
    {
        var indicator = new CoreRollingVwapIndicator();
        indicator.Configure(new CoreRollingVwapParameter { Period = 50 });
        Assert.Equal(50, indicator.Period);
        Assert.Equal("Rolling VWAP (50)", indicator.Name);
    }

    [Fact]
    public void Calculate_WithClosePriceSource_DiffersFromTypical()
    {
        var candles = CreateTestCandles(
            new decimal[] { 20, 22, 24 },
            new decimal[] { 8,  9,  10 },
            new decimal[] { 9,  10, 11 },
            new decimal[] { 100, 100, 100 }
        );

        var typicalIndicator = new CoreRollingVwapIndicator { Period = 3, PriceSource = PriceType.Typical };
        typicalIndicator.Calculate(candles);

        var closeIndicator = new CoreRollingVwapIndicator { Period = 3, PriceSource = PriceType.Close };
        closeIndicator.Calculate(candles);

        Assert.NotNull(typicalIndicator.Values[2]);
        Assert.NotNull(closeIndicator.Values[2]);
        Assert.NotEqual(typicalIndicator.Values[2]!.Value, closeIndicator.Values[2]!.Value);
    }

    [Fact]
    public void IndicatorFactory_Create_ReturnsConfiguredInstance()
    {
        var indicator = IndicatorFactory.Default.Create(IndicatorType.RollingVWAP);
        Assert.NotNull(indicator);
        Assert.IsType<CoreRollingVwapIndicator>(indicator);
        Assert.Equal(IndicatorType.RollingVWAP, indicator.GetDefaultSettings().TypeEnum);
    }

    [Fact]
    public void SlidingWindow_LargeSeries_ClosePrice_MatchesDirectCalculationExactly()
    {
        // For terminating decimal price sources like Close, decimal arithmetic has 0 drift across 1000 bars
        const int count = 1000;
        const int period = 20;
        var random = new Random(42);
        var startDate = new DateTime(2023, 1, 1);

        decimal currentPrice = 100m;
        var candles = new List<CoreCandleData>(count);

        for (int i = 0; i < count; i++)
        {
            currentPrice += (decimal)(random.NextDouble() * 4 - 2);
            decimal high = currentPrice + (decimal)(random.NextDouble() * 2);
            decimal low = currentPrice - (decimal)(random.NextDouble() * 2);
            decimal close = Math.Round(currentPrice, 2);
            long volume = random.Next(1000, 50000);

            candles.Add(new CoreCandleData(startDate.AddDays(i), currentPrice, high, low, close, volume));
        }

        var indicator = new CoreRollingVwapIndicator { Period = period, PriceSource = PriceType.Close };
        indicator.Calculate(candles);

        var prices = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Close);

        Assert.Equal(count, indicator.Values.Count);

        for (int i = 0; i < count; i++)
        {
            if (i < period - 1)
            {
                Assert.Null(indicator.Values[i]);
            }
            else
            {
                decimal directSumPV = 0m;
                long directSumV = 0;
                for (int j = 0; j < period; j++)
                {
                    directSumPV += prices[i - j]!.Value * candles[i - j].Volume;
                    directSumV += candles[i - j].Volume;
                }
                decimal expected = directSumPV / (decimal)directSumV;
                Assert.Equal(expected, indicator.Values[i]!.Value);
            }
        }
    }

    [Fact]
    public void SlidingWindow_LargeSeries_TypicalPrice_MaintainsSubUlpPrecision()
    {
        // For non-terminating recurring fractions like Typical (divided by 3), 
        // 128-bit decimal floating point limits finite precision to 28-29 digits.
        // The sliding window maintains precision within 1e-24m (sub-ULP precision).
        const int count = 1000;
        const int period = 20;
        var random = new Random(42);
        var startDate = new DateTime(2023, 1, 1);

        decimal currentPrice = 100m;
        var candles = new List<CoreCandleData>(count);

        for (int i = 0; i < count; i++)
        {
            currentPrice += (decimal)(random.NextDouble() * 4 - 2);
            decimal high = currentPrice + (decimal)(random.NextDouble() * 2);
            decimal low = currentPrice - (decimal)(random.NextDouble() * 2);
            decimal close = currentPrice;
            long volume = random.Next(1000, 50000);

            candles.Add(new CoreCandleData(startDate.AddDays(i), currentPrice, high, low, close, volume));
        }

        var indicator = new CoreRollingVwapIndicator { Period = period, PriceSource = PriceType.Typical };
        indicator.Calculate(candles);

        var prices = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Typical);

        Assert.Equal(count, indicator.Values.Count);

        for (int i = 0; i < count; i++)
        {
            if (i < period - 1)
            {
                Assert.Null(indicator.Values[i]);
            }
            else
            {
                decimal directSumPV = 0m;
                long directSumV = 0;
                for (int j = 0; j < period; j++)
                {
                    directSumPV += prices[i - j]!.Value * candles[i - j].Volume;
                    directSumV += candles[i - j].Volume;
                }
                decimal expected = directSumPV / (decimal)directSumV;
                decimal actual = indicator.Values[i]!.Value;
                Assert.True(Math.Abs(expected - actual) <= 1e-24m, $"Mismatch at bar {i}: expected {expected}, actual {actual}");
            }
        }
    }

    [Fact]
    public void Calculate_WithNegativeVolume_ClampsToZero()
    {
        var prices = new decimal?[] { 10m, 20m, 30m };
        var volumes = new long[] { 100, -50, 100 };

        var result = IndicatorCalculationHelper.CalculateRollingVwap(prices, volumes, period: 2);

        Assert.Equal(3, result.Count);
        Assert.Null(result[0]); // warmup
        // Bar 1: window [0..1], volumes are 100 and -50 (clamped to 0) -> PV = 10*100 + 20*0 = 1000, V = 100 -> 10m
        Assert.Equal(10m, result[1]!.Value);
        // Bar 2: window [1..2], volumes are -50 (clamped to 0) and 100 -> PV = 20*0 + 30*100 = 3000, V = 100 -> 30m
        Assert.Equal(30m, result[2]!.Value);
    }

    [Fact]
    public void Calculate_CandlesWithNegativeVolume_ClampsToZero()
    {
        var candles = CreateTestCandles(
            new decimal[] { 10, 20, 30 },
            new decimal[] { 10, 20, 30 },
            new decimal[] { 10, 20, 30 },
            new decimal[] { 100, -50, 100 }
        );

        var result = IndicatorCalculationHelper.CalculateRollingVwap(candles, period: 2, PriceType.Close);

        Assert.Equal(3, result.Count);
        Assert.Null(result[0]);
        Assert.Equal(10m, result[1]!.Value);
        Assert.Equal(30m, result[2]!.Value);
    }

    [Fact]
    public void Parameter_Validation_RejectsInvalidPeriods()
    {
        var validParam = new CoreRollingVwapParameter { Period = 20 };
        validParam.Validate(); // Does not throw

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRollingVwapParameter { Period = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRollingVwapParameter { Period = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRollingVwapParameter { Period = 10001 }.Validate());
    }

    [Fact]
    public void Helper_Validation_ThrowsOnNullOrInvalidArgs()
    {
        Assert.Throws<ArgumentNullException>(() => IndicatorCalculationHelper.CalculateRollingVwap((IReadOnlyList<CoreCandleData>)null!, 20));
        Assert.Throws<ArgumentNullException>(() => IndicatorCalculationHelper.CalculateRollingVwap(null!, new long[] { 100 }, 20));
        Assert.Throws<ArgumentNullException>(() => IndicatorCalculationHelper.CalculateRollingVwap(new decimal?[] { 10m }, null!, 20));
        Assert.Throws<ArgumentException>(() => IndicatorCalculationHelper.CalculateRollingVwap(new decimal?[] { 10m }, new long[] { 100, 200 }, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndicatorCalculationHelper.CalculateRollingVwap(new decimal?[] { 10m }, new long[] { 100 }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndicatorCalculationHelper.CalculateRollingVwap(new decimal?[] { 10m }, new long[] { 100 }, 10001));
    }
}
