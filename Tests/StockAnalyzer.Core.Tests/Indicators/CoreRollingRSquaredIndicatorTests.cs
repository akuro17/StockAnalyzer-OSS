using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreRollingRSquaredIndicatorTests
{
    private static IReadOnlyList<CoreCandleData> CreateTestCandles(decimal[] closes)
    {
        var startDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return closes.Select((close, i) => new CoreCandleData(
            startDate.AddDays(i),
            i > 0 ? closes[i - 1] : close,
            close * 1.01m,
            close * 0.99m,
            close,
            1000L
        )).ToList();
    }

    [Fact]
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.RollingRSquared));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.RollingRSquared);
        Assert.NotNull(indicator);
        Assert.IsType<CoreRollingRSquaredIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.RollingRSquared);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.True(settings.IncludeZeroBaseline);
        // Fixed range: MinValue = 0m, MaxValue = 1m
        Assert.Equal(0m, settings.MinValue);
        Assert.Equal(1m, settings.MaxValue);
        Assert.IsType<CoreRollingRSquaredParameter>(settings.ParameterObject);
        var param = (CoreRollingRSquaredParameter)settings.ParameterObject!;
        Assert.Equal(IndicatorDefaultConstants.RollingRSquaredPeriod, param.Period);
        Assert.Equal(BetaCalculationMode.SimpleReturn, param.CalculationMode);

        // Verify SeriesColors contain R²
        Assert.NotNull(settings.SeriesColors);
        Assert.Contains(settings.SeriesColors, sc => sc.Name == "Values" && sc.DisplayName == "R²");
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreRollingRSquaredParameter
        {
            Period = 60,
            ComparisonSymbol = "SPY"
        };
        param.Validate();

        param.Period = 4;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        param.Period = 1001;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void Parameter_ImplementsICrossTickerParameter()
    {
        var param = new CoreRollingRSquaredParameter();
        Assert.IsAssignableFrom<ICrossTickerParameter>(param);

        var crossParam = (ICrossTickerParameter)param;
        crossParam.ComparisonSymbol = "  spy  ";
        Assert.Equal("SPY", crossParam.ComparisonSymbol);

        crossParam.ComparisonSymbol = "";
        Assert.Equal(string.Empty, crossParam.ComparisonSymbol);
    }

    [Fact]
    public void Parameter_GetDisplayName_FormatsCorrectly()
    {
        var param = new CoreRollingRSquaredParameter { Period = 30, ComparisonSymbol = "SPY" };
        Assert.Equal("Rolling R² (30, SPY)", param.GetDisplayName("Rolling R²"));

        param.CalculationMode = BetaCalculationMode.LogReturn;
        Assert.Equal("Rolling R² (30, SPY, Log)", param.GetDisplayName("Rolling R²"));

        param.ComparisonSymbol = "";
        Assert.Equal("Rolling R² (30, Log)", param.GetDisplayName("Rolling R²"));
    }

    [Fact]
    public void Calculate_WithoutBenchmark_ReturnsNulls()
    {
        var closes = new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m, 107m };
        var series = CreateTestCandles(closes);

        var indicator = new CoreRollingRSquaredIndicator(5);
        var result = indicator.Calculate(series);

        Assert.True(result.IsSuccessful);
        Assert.Equal(closes.Length, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_PerfectLinearCorrelation_ReturnsRSquaredOne()
    {
        // Bench simple returns
        var benchCloses = new decimal[] { 100m, 110m, 105m, 115m, 110m, 120m };
        // Exactly linear relation: r_asset = 2.0 * r_bench + 0.01 (r = 1.0 => R² = 1.0)
        var assetCloses = new decimal[benchCloses.Length];
        assetCloses[0] = 100m;
        for (int i = 1; i < benchCloses.Length; i++)
        {
            decimal benchReturn = (benchCloses[i] - benchCloses[i - 1]) / benchCloses[i - 1];
            decimal assetReturn = 2m * benchReturn + 0.01m;
            assetCloses[i] = assetCloses[i - 1] * (1m + assetReturn);
        }

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        var indicator = new CoreRollingRSquaredIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        // Since correlation is 1.0, R² = 1.0
        Assert.Equal(1.0m, indicator.Values.Last()!.Value, 4);

        // Value must be in range [0, 1]
        Assert.True(indicator.Values.Last()!.Value >= 0.0m && indicator.Values.Last()!.Value <= 1.0m);
    }

    [Fact]
    public void Calculate_PerfectNegativeCorrelation_ReturnsRSquaredOne()
    {
        // Bench simple returns
        var benchCloses = new decimal[] { 100m, 110m, 105m, 115m, 110m, 120m };
        // Perfect negative correlation: r_asset = -2.0 * r_bench (r = -1.0 => R² = (-1.0)² = 1.0)
        var assetCloses = new decimal[benchCloses.Length];
        assetCloses[0] = 100m;
        for (int i = 1; i < benchCloses.Length; i++)
        {
            decimal benchReturn = (benchCloses[i] - benchCloses[i - 1]) / benchCloses[i - 1];
            decimal assetReturn = -2m * benchReturn;
            assetCloses[i] = assetCloses[i - 1] * (1m + assetReturn);
        }

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        var indicator = new CoreRollingRSquaredIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        // Correlation is -1.0 => R² = 1.0
        Assert.Equal(1.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void Calculate_ZeroVarianceBenchmark_ReturnsNull()
    {
        // Flat benchmark price => variance S_xx = 0
        var benchCloses = new decimal[] { 100m, 100m, 100m, 100m, 100m, 100m };
        var assetCloses = new decimal[] { 100m, 102m, 101m, 104m, 103m, 105m };

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        var indicator = new CoreRollingRSquaredIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        // Variance of benchmark is 0 => R² must be null (no division by zero)
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_SecondaryCandles_WorksViaICrossTicker()
    {
        var benchCloses = new decimal[] { 100m, 110m, 105m, 115m, 110m, 120m };
        var assetCloses = new decimal[] { 100m, 102m, 101m, 104m, 103m, 105m };

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        var indicator = new CoreRollingRSquaredIndicator(5)
        {
            ComparisonSymbol = "SPY"
        };
        indicator.SetSecondaryCandles(seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        Assert.True(indicator.Values.Last()!.Value >= 0.0m && indicator.Values.Last()!.Value <= 1.0m);
    }

    [Fact]
    public void Calculate_ZeroVariancePrimaryAsset_ReturnsNull()
    {
        // Flat primary asset price => variance S_yy = 0
        var assetCloses = new decimal[] { 100m, 100m, 100m, 100m, 100m, 100m };
        var benchCloses = new decimal[] { 100m, 102m, 101m, 104m, 103m, 105m };

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        var indicator = new CoreRollingRSquaredIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        // Variance of asset is 0 => R² must be null (no division by zero)
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_WarmUpPeriod_ReturnsStrictlyNull()
    {
        var benchCloses = new decimal[] { 100m, 110m, 105m, 115m, 110m, 120m, 125m, 130m };
        var assetCloses = new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m, 115m, 120m };

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        const int period = 5;
        var indicator = new CoreRollingRSquaredIndicator(period, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        // For period 5 returns, first return is at index 1.
        // 5 valid returns require 6 price bars (indices 0..5).
        // Therefore, for index 0 to 4 (5 bars), R² must be strictly null.
        for (int i = 0; i < period; i++)
        {
            Assert.Null(indicator.Values[i]);
        }
        // At index 5 (6th bar), first window completes
        Assert.NotNull(indicator.Values[5]);
    }

    [Fact]
    public void Parameter_PriceSource_CanBeConfiguredAndSynced()
    {
        var param = new CoreRollingRSquaredParameter
        {
            Period = 20,
            PriceSource = PriceType.Open,
            ComparisonPriceSource = PriceType.High,
            ComparisonSymbol = "SPY"
        };

        var indicator = new CoreRollingRSquaredIndicator();
        indicator.Configure(param);

        Assert.Equal(20, indicator.Period);
        Assert.Equal(PriceType.Open, indicator.PriceSource);
        Assert.Equal(PriceType.High, indicator.ComparisonPriceSource);
        Assert.Equal("SPY", indicator.ComparisonSymbol);
    }

    [Fact]
    public void BacktestIndicatorEligibility_ContainsRollingRSquared()
    {
        Assert.Contains(IndicatorType.RollingRSquared, BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes);
    }
}
