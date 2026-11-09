using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreRankCorrelationIndicatorTests
{
    [Fact]
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.RankCorrelation));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.RankCorrelation);
        Assert.NotNull(indicator);
        Assert.IsType<CoreRankCorrelationIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.RankCorrelation);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.Equal(-1.0m, settings.MinValue);
        Assert.Equal(1.0m, settings.MaxValue);
        Assert.True(settings.IncludeZeroBaseline);
        Assert.IsType<CoreRankCorrelationParameter>(settings.ParameterObject);

        var param = (CoreRankCorrelationParameter)settings.ParameterObject!;
        Assert.Equal(IndicatorDefaultConstants.RankCorrelationDefaultPeriod, param.Period);
        Assert.Equal(RankCorrelationMethod.Spearman, param.Method);
        Assert.Equal(PriceType.Close, param.PriceSource);
        Assert.Equal(PriceType.Close, param.ComparisonPriceSource);
        Assert.Equal(string.Empty, param.ComparisonSymbol);
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreRankCorrelationParameter
        {
            Period = 14,
            Method = RankCorrelationMethod.Spearman,
            PriceSource = PriceType.Close,
            ComparisonPriceSource = PriceType.Close
        };
        param.Validate();

        param.Period = 4;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Period = 201;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Period = 14;

        param.Method = (RankCorrelationMethod)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Method = RankCorrelationMethod.Kendall;

        param.PriceSource = (PriceType)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.PriceSource = PriceType.Close;

        param.ComparisonPriceSource = (PriceType)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void Engine_Spearman_BasicCases()
    {
        double[] x = [1.0, 2.0, 3.0, 4.0, 5.0];
        double[] yAsc = [10.0, 20.0, 30.0, 40.0, 50.0];
        double[] yDesc = [50.0, 40.0, 30.0, 20.0, 10.0];
        double[] yFlat = [5.0, 5.0, 5.0, 5.0, 5.0];

        Span<double> rx = stackalloc double[5];
        Span<double> ry = stackalloc double[5];

        double rAsc = RankCorrelationEngine.CalculateSpearman(x, yAsc, rx, ry);
        Assert.Equal(1.0, rAsc, 6);

        double rDesc = RankCorrelationEngine.CalculateSpearman(x, yDesc, rx, ry);
        Assert.Equal(-1.0, rDesc, 6);

        double rFlat = RankCorrelationEngine.CalculateSpearman(x, yFlat, rx, ry);
        Assert.Equal(0.0, rFlat, 6);
    }

    [Fact]
    public void Engine_KendallTauB_BasicCases()
    {
        double[] x = [1.0, 2.0, 3.0, 4.0, 5.0];
        double[] yAsc = [10.0, 20.0, 30.0, 40.0, 50.0];
        double[] yDesc = [50.0, 40.0, 30.0, 20.0, 10.0];
        double[] yFlat = [5.0, 5.0, 5.0, 5.0, 5.0];

        double tauAsc = RankCorrelationEngine.CalculateKendallTauB(x, yAsc);
        Assert.Equal(1.0, tauAsc, 6);

        double tauDesc = RankCorrelationEngine.CalculateKendallTauB(x, yDesc);
        Assert.Equal(-1.0, tauDesc, 6);

        double tauFlat = RankCorrelationEngine.CalculateKendallTauB(x, yFlat);
        Assert.Equal(0.0, tauFlat, 6);
    }

    [Fact]
    public void Engine_WithTies_RemainsBounded()
    {
        double[] x = [1.0, 1.0, 2.0, 3.0, 3.0, 4.0];
        double[] y = [2.0, 2.0, 2.0, 5.0, 5.0, 6.0];

        Span<double> rx = stackalloc double[6];
        Span<double> ry = stackalloc double[6];

        double spearman = RankCorrelationEngine.CalculateSpearman(x, y, rx, ry);
        Assert.InRange(spearman, -1.0, 1.0);
        Assert.True(spearman > 0.8); // strongly positive monotonic

        double kendall = RankCorrelationEngine.CalculateKendallTauB(x, y);
        Assert.InRange(kendall, -1.0, 1.0);
        Assert.True(kendall > 0.8);
    }

    [Fact]
    public void Calculate_TwoTickers_Spearman_MonotonicPrices()
    {
        int period = 10;
        int count = 25;
        var primaryCandles = CreateSampleCandles(count, i => 100m + i * 2m, _ => 1000L);
        var secondaryCandles = CreateSampleCandles(count, i => 50m + i * 3m, _ => 2000L);

        var indicator = new CoreRankCorrelationIndicator(period, "BENCH", RankCorrelationMethod.Spearman);
        indicator.SetSecondaryCandles(secondaryCandles.Cast<CoreCandleData?>().ToList());

        var result = indicator.Calculate(primaryCandles);
        Assert.True(result.IsSuccessful);

        var values = result.MainValues;
        for (int i = 0; i < period - 1; i++)
        {
            Assert.Null(values[i]);
        }

        for (int i = period - 1; i < count; i++)
        {
            Assert.NotNull(values[i]);
            Assert.Equal(1.0m, Math.Round(values[i]!.Value, 4));
        }
    }

    [Fact]
    public void Calculate_TwoTickers_Kendall_InversePrices()
    {
        int period = 10;
        int count = 25;
        var primaryCandles = CreateSampleCandles(count, i => 100m + i * 2m, _ => 1000L);
        var secondaryCandles = CreateSampleCandles(count, i => 200m - i * 2m, _ => 2000L);

        var indicator = new CoreRankCorrelationIndicator(period, "BENCH", RankCorrelationMethod.Kendall);
        indicator.SetSecondaryCandles(secondaryCandles.Cast<CoreCandleData?>().ToList());

        var result = indicator.Calculate(primaryCandles);
        Assert.True(result.IsSuccessful);

        var values = result.MainValues;
        for (int i = period - 1; i < count; i++)
        {
            Assert.NotNull(values[i]);
            Assert.Equal(-1.0m, Math.Round(values[i]!.Value, 4));
        }
    }

    [Fact]
    public void Calculate_VolumeFallback_WhenComparisonSymbolEmpty()
    {
        int period = 8;
        int count = 20;
        // Price increases monotonically, Volume increases monotonically
        var candles = CreateSampleCandles(count, i => 100m + i * 5m, i => 1000L + i * 100L);

        var indicator = new CoreRankCorrelationIndicator(period, "", RankCorrelationMethod.Spearman);
        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        var values = result.MainValues;
        for (int i = 0; i < period - 1; i++)
        {
            Assert.Null(values[i]);
        }

        for (int i = period - 1; i < count; i++)
        {
            Assert.NotNull(values[i]);
            Assert.Equal(1.0m, Math.Round(values[i]!.Value, 4));
        }
    }

    [Fact]
    public void Calculate_MissingSecondaryCandles_ReturnsNull()
    {
        int period = 10;
        int count = 20;
        var primaryCandles = CreateSampleCandles(count, i => 100m + i, _ => 1000L);

        var indicator = new CoreRankCorrelationIndicator(period, "BENCH", RankCorrelationMethod.Spearman);
        // Secondary candles not set
        var result = indicator.Calculate(primaryCandles);
        Assert.True(result.IsSuccessful);

        var values = result.MainValues;
        for (int i = 0; i < count; i++)
        {
            Assert.Null(values[i]);
        }
    }

    private static List<CoreCandleData> CreateSampleCandles(int count, Func<int, decimal> priceSelector, Func<int, long> volumeSelector)
    {
        var list = new List<CoreCandleData>(count);
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
        {
            decimal p = priceSelector(i);
            long vol = volumeSelector(i);
            list.Add(new CoreCandleData(
                baseDate.AddDays(i),
                p,
                p + 1m,
                p - 1m,
                p,
                vol
            ));
        }
        return list;
    }
}
