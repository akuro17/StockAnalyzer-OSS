using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreVidyaIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, decimal startPrice, Func<int, decimal> priceProgression)
    {
        var candles = new List<CoreCandleData>();
        var date = DateTime.Today;
        for (int i = 0; i < count; i++)
        {
            var price = priceProgression(i);
            candles.Add(new CoreCandleData(date.AddDays(i), price, price, price, price, 100));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsExpectedValues()
    {
        var candles = GenerateCandleData(30, 100, i => 100 + (decimal)Math.Sin(i / 5.0) * 5);
        int smoothPeriod = 9;
        int cmoPeriod = 9;
        var indicator = new CoreVidyaIndicator { SmoothPeriod = smoothPeriod, CmoPeriod = cmoPeriod };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(30, result.MainValues.Count);
        // First cmoPeriod elements must be null
        Assert.True(result.MainValues.Take(cmoPeriod).All(v => v == null));

        // Subsequent elements must be calculated
        Assert.NotNull(result.MainValues[cmoPeriod]);
        Assert.True(result.MainValues[cmoPeriod] > 0);

        var priceRange = candles.Skip(cmoPeriod).Max(c => c.Close) - candles.Skip(cmoPeriod).Min(c => c.Close);
        var vidyaRange = result.MainValues.Skip(cmoPeriod).Max(v => v!.Value) - result.MainValues.Skip(cmoPeriod).Min(v => v!.Value);
        Assert.True(vidyaRange <= priceRange);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreVidyaIndicator { SmoothPeriod = 9, CmoPeriod = 9 };
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreVidyaIndicator { SmoothPeriod = 9, CmoPeriod = 9 };
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(8, 100, i => 100 + i);
        var indicator = new CoreVidyaIndicator { SmoothPeriod = 9, CmoPeriod = 9 };
        var result = indicator.Calculate(candles);

        Assert.Equal(8, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }

    [Fact]
    public void Calculate_WithConstantPrices_ZeroVolatilityHandledGracefully()
    {
        var candles = GenerateCandleData(15, 100m, _ => 100m);
        var indicator = new CoreVidyaIndicator { SmoothPeriod = 9, CmoPeriod = 9 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(15, result.MainValues.Count);
        Assert.True(result.MainValues.Take(9).All(v => v == null));
        for (int i = 9; i < 15; i++)
        {
            Assert.Equal(100m, result.MainValues[i]);
        }
    }

    [Fact]
    public void CalculateVidya_Helper_LinearTrend_MatchesExactFormula()
    {
        // 11 bars: 10, 11, 12, ..., 20 (monotonically increasing)
        // cmoPeriod = 5, smoothPeriod = 9
        // Initial SMA at index 4 (values 10..14) = 12.0m
        // Index 5 (value 15): UpSum = 5, DnSum = 0 -> CMO = 1.0 -> alpha = (2/10) * 1.0 = 0.2
        // VIDYA at index 5 = 0.2 * 15 + 0.8 * 12.0 = 3.0 + 9.6 = 12.6m
        var prices = Enumerable.Range(10, 11).Select(x => (decimal?)x).ToList();
        var result = IndicatorCalculationHelper.CalculateVidya(prices, smoothPeriod: 9, cmoPeriod: 5);

        Assert.Equal(11, result.Count);
        Assert.True(result.Take(5).All(v => v == null));

        Assert.NotNull(result[5]);
        Assert.Equal(12.6m, result[5]!.Value);
    }

    [Fact]
    public void CalculateVidya_Helper_IntermittentNulls_HandledDeterministically()
    {
        var prices = new List<decimal?> { 10m, null, 11m, 12m, 13m, 14m, null, 15m, 16m };
        var result = IndicatorCalculationHelper.CalculateVidya(prices, smoothPeriod: 9, cmoPeriod: 5);

        Assert.Equal(prices.Count, result.Count);
        Assert.Null(result[1]);
        Assert.Null(result[6]);

        // Valid values: 0(1), 2(2), 3(3), 4(4), 5(5 -> warmup SMA complete at validCount=5), 7(6 -> first recurrence)
        Assert.Null(result[5]);
        Assert.NotNull(result[7]);
        Assert.True(result[7] > 0m);
    }

    [Fact]
    public void Parameter_Validate_EnforcesLowerAndUpperBounds()
    {
        var validParam = new CoreVidyaParameter { SmoothPeriod = 9, CmoPeriod = 9 };
        validParam.Validate(); // Should not throw

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVidyaParameter { SmoothPeriod = 0, CmoPeriod = 9 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVidyaParameter { SmoothPeriod = 10001, CmoPeriod = 9 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVidyaParameter { SmoothPeriod = 9, CmoPeriod = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVidyaParameter { SmoothPeriod = 9, CmoPeriod = 10001 }.Validate());
    }

    [Fact]
    public void IndicatorFactory_CanCreate_Vidya_AndDiscoversParameters()
    {
        var indicator = IndicatorFactory.Default.Create(IndicatorType.VIDYA);
        Assert.NotNull(indicator);
        Assert.IsType<CoreVidyaIndicator>(indicator);

        var settings = indicator!.GetDefaultSettings();
        Assert.Equal(IndicatorType.VIDYA, settings.TypeEnum);
        Assert.True(settings.IsOverlay);
        Assert.NotNull(settings.ParameterObject);
        var param = Assert.IsType<CoreVidyaParameter>(settings.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.VidyaSmoothPeriod, param.SmoothPeriod);
        Assert.Equal(IndicatorDefaultConstants.VidyaCmoPeriod, param.CmoPeriod);
    }

    [Fact]
    public void CalculateSeries_DirectExecution_Succeeds()
    {
        var indicator = new CoreVidyaIndicator { SmoothPeriod = 9, CmoPeriod = 5 };
        var prices = new List<decimal?> { 10m, 11m, 12m, 13m, 14m, 15m, 16m };

        var result = indicator.CalculateSeries(prices);

        Assert.True(result.IsSuccessful);
        Assert.Equal(prices.Count, result.MainValues.Count);
        Assert.Equal(prices.Count, indicator.Values.Count);

        for (int i = 0; i < 5; i++)
        {
            Assert.Null(result.MainValues[i]);
        }
        Assert.NotNull(result.MainValues[5]);
        Assert.NotNull(result.MainValues[6]);
    }

    [Fact]
    public void CalculateVidya_Helper_LinearDowntrend_MatchesExactFormula()
    {
        // 11 bars monotonically decreasing: 20, 19, 18, ..., 10
        // cmoPeriod = 5, smoothPeriod = 9
        // Initial SMA at index 4 (values 20..16) = (20+19+18+17+16)/5 = 18.0m
        // Index 5 (value 15): UpSum = 0, DnSum = 5 -> CMO = -1.0 -> |CMO| = 1.0 -> alpha = (2/10) * 1.0 = 0.2
        // VIDYA at index 5 = 0.2 * 15 + 0.8 * 18.0 = 3.0 + 14.4 = 17.4m
        var prices = Enumerable.Range(0, 11).Select(x => (decimal?)(20 - x)).ToList();
        var result = IndicatorCalculationHelper.CalculateVidya(prices, smoothPeriod: 9, cmoPeriod: 5);

        Assert.Equal(11, result.Count);
        Assert.True(result.Take(5).All(v => v == null));

        Assert.NotNull(result[5]);
        Assert.Equal(17.4m, result[5]!.Value);
    }

    [Fact]
    public void CalculateVidya_Helper_N1_AppliesDirectAbsoluteCmo()
    {
        // N=1 implies K = 2/(1+1) = 1.0, so alpha = |CMO|
        // When |CMO| = 1.0 (monotonic trend), alpha = 1.0 -> VIDYA_t = Price_t
        var prices = Enumerable.Range(10, 11).Select(x => (decimal?)x).ToList();
        var result = IndicatorCalculationHelper.CalculateVidya(prices, smoothPeriod: 1, cmoPeriod: 5);

        Assert.Equal(11, result.Count);
        // At index 5: alpha = 1.0 * 1.0 = 1.0 -> VIDYA_5 = 1.0 * 15 + 0 * SMA = 15m
        Assert.NotNull(result[5]);
        Assert.Equal(15m, result[5]!.Value);
        Assert.Equal(16m, result[6]!.Value);
    }

    [Fact]
    public void CalculateVidya_Helper_SingleSpike_ImpulseAndDecay()
    {
        // Flat prices (100) with a single spike to 150 at index 6, then returning to 100
        var prices = new List<decimal?> { 100m, 100m, 100m, 100m, 100m, 100m, 150m, 100m, 100m, 100m, 100m, 100m };
        var result = IndicatorCalculationHelper.CalculateVidya(prices, smoothPeriod: 9, cmoPeriod: 5);

        // Index 5: flat warmup complete, VIDYA_5 = 100m
        Assert.Equal(100m, result[5]!.Value);

        // Index 6: spike to 150 -> UpSum=50, DnSum=0 -> CMO=1.0, alpha=0.2 -> VIDYA_6 = 0.2*150 + 0.8*100 = 110m
        Assert.NotNull(result[6]);
        Assert.Equal(110m, result[6]!.Value);
        decimal peakVidya = result[6]!.Value;

        // Index 7: drop back to 100 -> window contains both +50 and -50 -> UpSum=50, DnSum=50 -> CMO=0 -> alpha=0
        // Because net momentum cancelled out, VIDYA freezes at previous bar (110m)
        Assert.NotNull(result[7]);
        Assert.Equal(peakVidya, result[7]!.Value);

        // Index 11: the +50 spike leaves the 5-bar rolling window, leaving only -50 (DnSum=50, UpSum=0)
        // CMO = -1.0 -> alpha = 0.2 -> VIDYA decays towards 100m: 0.2*100 + 0.8*110 = 108m < peakVidya
        Assert.NotNull(result[11]);
        Assert.True(result[11]!.Value < peakVidya);
        Assert.Equal(108m, result[11]!.Value);
    }

    [Fact]
    public void CalculateVidya_Helper_Independent_N_and_M()
    {
        // Long smoothing (N=20) with short momentum window (M=3)
        var prices = Enumerable.Range(100, 20).Select(x => (decimal?)x).ToList();
        var result = IndicatorCalculationHelper.CalculateVidya(prices, smoothPeriod: 20, cmoPeriod: 3);

        Assert.Equal(20, result.Count);
        // Warmup: indices 0..2 are null
        Assert.True(result.Take(3).All(v => v == null));

        // Index 3: SMA of 100, 101, 102 = 101m. CMO = 1.0. K = 2/21.
        // Expected = (2/21) * 103 + (19/21) * 101 = (206 + 1919) / 21 = 2125 / 21 ≈ 101.190476m
        Assert.NotNull(result[3]);
        decimal expected = (2.0m / 21.0m) * 103m + (19.0m / 21.0m) * 101m;
        Assert.Equal(Math.Round(expected, 6), Math.Round(result[3]!.Value, 6));
    }

    [Fact]
    public void CalculateVidya_Helper_ContinuousFlatMarket_MaintainsZeroStability()
    {
        // 25 identical bars: perfectly flat market
        var prices = Enumerable.Repeat<decimal?>(50m, 25).ToList();
        var result = IndicatorCalculationHelper.CalculateVidya(prices, smoothPeriod: 9, cmoPeriod: 5);

        Assert.Equal(25, result.Count);
        // Indices 0..4 must be null
        Assert.True(result.Take(5).All(v => v == null));

        // Indices 5..24 must be exactly 50m with zero numerical drift
        for (int i = 5; i < 25; i++)
        {
            Assert.Equal(50m, result[i]!.Value);
        }
    }

    [Fact]
    public void CalculateVidya_Helper_CmoPeriod1_ExecutesWithoutError()
    {
        // cmoPeriod = 1: warmup is 1 bar (index 0 is null).
        // At index 1, previousVidya was initialized to 100m at bar 0.
        // change = 105 - 100 = 5. Up=5, Dn=0. CMO = 1.0. K = 2/3.
        // Expected VIDYA at index 1 = (2/3)*105 + (1/3)*100 = 70 + 33.333... = 103.333...
        var prices = new List<decimal?> { 100m, 105m, 110m };
        var result = IndicatorCalculationHelper.CalculateVidya(prices, smoothPeriod: 2, cmoPeriod: 1);

        Assert.Equal(3, result.Count);
        Assert.Null(result[0]);
        Assert.NotNull(result[1]);
        decimal expected1 = (2.0m / 3.0m) * 105m + (1.0m / 3.0m) * 100m;
        Assert.Equal(Math.Round(expected1, 6), Math.Round(result[1]!.Value, 6));

        Assert.NotNull(result[2]);
        decimal expected2 = (2.0m / 3.0m) * 110m + (1.0m / 3.0m) * expected1;
        Assert.Equal(Math.Round(expected2, 6), Math.Round(result[2]!.Value, 6));
    }

    [Fact]
    public void CalculateVidya_Helper_LargePeriod_PoolRentingSucceeds()
    {
        // cmoPeriod = 200: verifies ArrayPool rental for large periods
        var prices = Enumerable.Range(1, 250).Select(i => (decimal?)i).ToList();
        var result = IndicatorCalculationHelper.CalculateVidya(prices, smoothPeriod: 50, cmoPeriod: 200);

        Assert.Equal(250, result.Count);
        Assert.True(result.Take(200).All(v => v == null));
        Assert.NotNull(result[200]);
        Assert.True(result[200] > 0m);
    }
}
