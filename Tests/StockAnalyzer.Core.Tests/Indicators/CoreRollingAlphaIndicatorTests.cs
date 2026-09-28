using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreRollingAlphaIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(decimal[] closes, long[]? volumes = null)
    {
        var startDate = new DateTime(2020, 1, 1);
        return closes.Select((close, i) => new CoreCandleData(
            startDate.AddDays(i),
            i > 0 ? closes[i - 1] : close,
            close + 1,
            close - 1,
            close,
            volumes != null && i < volumes.Length ? volumes[i] : 1000
        )).ToList();
    }

    [Fact]
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.RollingAlpha));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.RollingAlpha);
        Assert.NotNull(indicator);
        Assert.IsType<CoreRollingAlphaIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.RollingAlpha);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.True(settings.IncludeZeroBaseline);
        // Dynamic auto-scaling: MinValue and MaxValue must be null
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);
        Assert.IsType<CoreRollingAlphaParameter>(settings.ParameterObject);
        var param = (CoreRollingAlphaParameter)settings.ParameterObject!;
        Assert.Equal(IndicatorDefaultConstants.RollingAlphaPeriod, param.Period);
        Assert.Equal(BetaCalculationMode.SimpleReturn, param.CalculationMode);
        Assert.True(param.Annualize);
        Assert.Equal(IndicatorDefaultConstants.RollingAlphaAnnualizationFactor, param.AnnualizationFactor);

        // Verify SeriesColors contain Alpha only
        Assert.NotNull(settings.SeriesColors);
        Assert.Contains(settings.SeriesColors, sc => sc.Name == "Values" && sc.DisplayName == "Alpha");
        Assert.DoesNotContain(settings.SeriesColors, sc => sc.Name == "RSquared");
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreRollingAlphaParameter
        {
            Period = 60,
            ComparisonSymbol = "SPY",
            AnnualizationFactor = 252
        };
        param.Validate();

        param.Period = 4;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        param.Period = 1001;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        param.Period = 60;
        param.AnnualizationFactor = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        param.AnnualizationFactor = 366;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void Calculate_WithBenchmarkEqualToAsset_ReturnsZeroAlpha()
    {
        // 7 prices with non-constant returns
        var closes = new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m, 107m };
        var seriesA = CreateTestCandles(closes);
        var seriesB = CreateTestCandles(closes);

        var indicator = new CoreRollingAlphaIndicator(5, seriesB)
        {
            Annualize = false
        };
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        // Since asset == benchmark, Beta = 1.0 and Alpha = y_bar - 1 * x_bar = 0.0
        Assert.Equal(0.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void Calculate_WithSyntheticConstantAlpha_MatchesHandCalculatedValue()
    {
        // Bench simple returns: r = (P_t - P_{t-1}) / P_{t-1}
        var benchCloses = new decimal[] { 100m, 110m, 105m, 115m, 110m, 120m };
        // Exact relation: r_asset = 2.0 * r_bench + 0.01 (1% daily excess return)
        // With beta = 2.0, intercept alpha_daily = mean(r_asset) - 2.0 * mean(r_bench) = 0.01
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

        // 1) Test Daily Alpha (Annualize = false)
        var indicatorDaily = new CoreRollingAlphaIndicator(5, seriesB)
        {
            Annualize = false,
            CalculationMode = BetaCalculationMode.SimpleReturn
        };
        var resultDaily = indicatorDaily.Calculate(seriesA);
        Assert.True(resultDaily.IsSuccessful);
        Assert.NotNull(indicatorDaily.Values.Last());
        Assert.Equal(0.01m, indicatorDaily.Values.Last()!.Value, 4);

        // 2) Test Annualized Alpha (Annualize = true, Factor = 252) -> 0.01 * 252 = 2.52
        var indicatorAnnual = new CoreRollingAlphaIndicator(5, seriesB)
        {
            Annualize = true,
            AnnualizationFactor = 252,
            CalculationMode = BetaCalculationMode.SimpleReturn
        };
        var resultAnnual = indicatorAnnual.Calculate(seriesA);
        Assert.True(resultAnnual.IsSuccessful);
        Assert.NotNull(indicatorAnnual.Values.Last());
        Assert.Equal(2.52m, indicatorAnnual.Values.Last()!.Value, 3);
    }

    [Fact]
    public void Calculate_LogReturnMode_CalculatesCorrectAlpha()
    {
        // Bench prices
        var benchCloses = new decimal[] { 100m, 110m, 99m, 108.9m, 98.01m, 107.811m };
        // Log return: r_asset = r_bench + 0.005
        // ln(P_asset_t / P_asset_{t-1}) = ln(P_bench_t / P_bench_{t-1}) + 0.005
        // => P_asset_t = P_asset_{t-1} * (P_bench_t / P_bench_{t-1}) * e^0.005
        decimal expDelta = (decimal)Math.Exp(0.005);
        var assetCloses = new decimal[benchCloses.Length];
        assetCloses[0] = 100m;
        for (int i = 1; i < benchCloses.Length; i++)
        {
            assetCloses[i] = assetCloses[i - 1] * (benchCloses[i] / benchCloses[i - 1]) * expDelta;
        }

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        var indicator = new CoreRollingAlphaIndicator(4, seriesB)
        {
            CalculationMode = BetaCalculationMode.LogReturn,
            Annualize = false
        };
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(0.005m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void Calculate_WithZeroVarianceBenchmark_ReturnsNull()
    {
        var benchCloses = new decimal[] { 100m, 100m, 100m, 100m, 100m, 100m };
        var assetCloses = new decimal[] { 100m, 102m, 101m, 103m, 102m, 104m };

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        var indicator = new CoreRollingAlphaIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        // Zero benchmark variance causes division by zero guard to trigger -> null
        Assert.Null(indicator.Values.Last());
    }

    [Fact]
    public void Calculate_DifferentLengthSeries_AlignsFromEnd()
    {
        var benchCloses = new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m };
        // Asset series has 2 extra historical bars
        var assetCloses = new decimal[] { 90m, 95m, 100m, 105m, 102m, 108m, 104m, 110m };

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        var indicator = new CoreRollingAlphaIndicator(5, seriesB)
        {
            Annualize = false
        };
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.Equal(seriesA.Count, indicator.Values.Count);
        // The last bar should align with the last 5 bars of both series
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(0.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void Calculate_WithoutBenchmark_ReturnsNulls()
    {
        var closes = new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m, 107m };
        var series = CreateTestCandles(closes);

        var indicator = new CoreRollingAlphaIndicator(5);
        var result = indicator.Calculate(series);

        Assert.True(result.IsSuccessful);
        Assert.Equal(closes.Length, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_SyntheticData_OutputsSingleMainSeriesWithoutRSquared()
    {
        // Bench simple returns: r = (P_t - P_{t-1}) / P_{t-1}
        var benchCloses = new decimal[] { 100m, 110m, 105m, 115m, 110m, 120m };
        // Exactly linear relation: r_asset = 2.0 * r_bench + 0.01
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

        var indicator = new CoreRollingAlphaIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());

        // Verify only Main series is exposed, and RSquared is decoupled
        Assert.False(result.HasSeries("RSquared"));
        Assert.Single(result.SeriesNames);
        Assert.Equal(IndicatorResult.MainSeriesName, result.SeriesNames.First());
    }
}
