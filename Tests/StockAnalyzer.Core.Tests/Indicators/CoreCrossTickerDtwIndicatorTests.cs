using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreCrossTickerDtwIndicatorTests
{
    [Fact]
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.CrossTickerDtw));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.CrossTickerDtw);
        Assert.NotNull(indicator);
        Assert.IsType<CoreCrossTickerDtwIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.CrossTickerDtw);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);
        Assert.True(settings.IncludeZeroBaseline);
        Assert.IsType<CoreCrossTickerDtwParameter>(settings.ParameterObject);

        var param = (CoreCrossTickerDtwParameter)settings.ParameterObject!;
        Assert.Equal(20, param.Period);
        Assert.Equal(3, param.WarpingRadius);
        Assert.Equal(DtwOutputMode.Distance, param.OutputMode);
    }

    [Fact]
    public void IndicatorChartSuppression_SuppressesSecondarySeriesFromChart()
    {
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.CrossTickerDtw, CoreCrossTickerDtwIndicator.NormalizedDistanceSeriesName));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.CrossTickerDtw, CoreCrossTickerDtwIndicator.SimilarityScoreSeriesName));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.CrossTickerDtw, IndicatorResult.MainSeriesName));

        Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(IndicatorType.CrossTickerDtw, CoreCrossTickerDtwIndicator.NormalizedDistanceSeriesName));
        Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(IndicatorType.CrossTickerDtw, CoreCrossTickerDtwIndicator.SimilarityScoreSeriesName));
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreCrossTickerDtwParameter
        {
            Period = 20,
            WarpingRadius = 3,
            OutputMode = DtwOutputMode.Distance
        };
        param.Validate();

        param.Period = 4;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Period = 201;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Period = 20;

        param.WarpingRadius = -2;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.WarpingRadius = 51;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.WarpingRadius = -1; // Unconstrained is valid
        param.Validate();
    }

    [Fact]
    public void Calculate_IdenticalSeries_ReturnsZeroDistanceAndOneSimilarity()
    {
        int period = 10;
        int count = 30;
        var primaryCandles = CreateSampleCandles(count, i => 100m + i * 2m);
        var secondaryCandles = CreateSampleCandles(count, i => 100m + i * 2m);

        var indicator = new CoreCrossTickerDtwIndicator(period, "BENCH");
        indicator.SetSecondaryCandles(secondaryCandles.Cast<CoreCandleData?>().ToList());

        var result = indicator.Calculate(primaryCandles);
        Assert.True(result.IsSuccessful);

        var main = result.MainValues;
        var normDist = result.GetSeries(CoreCrossTickerDtwIndicator.NormalizedDistanceSeriesName);
        var simScore = result.GetSeries(CoreCrossTickerDtwIndicator.SimilarityScoreSeriesName);

        for (int i = 0; i < period - 1; i++)
        {
            Assert.Null(main[i]);
            Assert.Null(normDist[i]);
            Assert.Null(simScore[i]);
        }

        for (int i = period - 1; i < count; i++)
        {
            Assert.NotNull(normDist[i]);
            Assert.Equal(0.0m, normDist[i]!.Value);
            Assert.NotNull(simScore[i]);
            Assert.Equal(1.0m, simScore[i]!.Value);
            Assert.Equal(0.0m, main[i]!.Value);
        }
    }

    [Fact]
    public void Calculate_BothWindowsFlat_ReturnsZeroDistanceAndOneSimilarity()
    {
        int period = 10;
        int count = 20;
        var primaryCandles = CreateSampleCandles(count, _ => 50m);
        var secondaryCandles = CreateSampleCandles(count, _ => 100m);

        var indicator = new CoreCrossTickerDtwIndicator(period, "BENCH");
        indicator.SetSecondaryCandles(secondaryCandles.Cast<CoreCandleData?>().ToList());

        var result = indicator.Calculate(primaryCandles);
        Assert.True(result.IsSuccessful);

        var normDist = result.GetSeries(CoreCrossTickerDtwIndicator.NormalizedDistanceSeriesName);
        var simScore = result.GetSeries(CoreCrossTickerDtwIndicator.SimilarityScoreSeriesName);

        for (int i = period - 1; i < count; i++)
        {
            Assert.Equal(0.0m, normDist[i]!.Value);
            Assert.Equal(1.0m, simScore[i]!.Value);
        }
    }

    [Fact]
    public void Calculate_OneWindowFlat_ReturnsNull()
    {
        int period = 10;
        int count = 20;
        var primaryCandles = CreateSampleCandles(count, _ => 50m); // Flat
        var secondaryCandles = CreateSampleCandles(count, i => 100m + i * 2m); // Non-flat

        var indicator = new CoreCrossTickerDtwIndicator(period, "BENCH");
        indicator.SetSecondaryCandles(secondaryCandles.Cast<CoreCandleData?>().ToList());

        var result = indicator.Calculate(primaryCandles);
        Assert.True(result.IsSuccessful);

        var normDist = result.GetSeries(CoreCrossTickerDtwIndicator.NormalizedDistanceSeriesName);
        for (int i = 0; i < count; i++)
        {
            Assert.Null(normDist[i]);
        }
    }

    [Fact]
    public void Calculate_MissingComparisonSymbol_ReturnsAllNulls()
    {
        int count = 20;
        var primaryCandles = CreateSampleCandles(count, i => 100m + i);

        // Empty comparison symbol
        var indicator = new CoreCrossTickerDtwIndicator(10, string.Empty);
        var result = indicator.Calculate(primaryCandles);
        Assert.True(result.IsSuccessful);
        Assert.All(result.MainValues, Assert.Null);

        // Specified symbol but candles null
        indicator = new CoreCrossTickerDtwIndicator(10, "SPY");
        indicator.SetSecondaryCandles(null);
        result = indicator.Calculate(primaryCandles);
        Assert.True(result.IsSuccessful);
        Assert.All(result.MainValues, Assert.Null);
    }

    [Fact]
    public void Calculate_OutputModeSimilarity_EmitsSimilarityAsMain()
    {
        int period = 10;
        int count = 25;
        var primaryCandles = CreateSampleCandles(count, i => 100m + (decimal)Math.Sin(i * 0.5) * 5m);
        var secondaryCandles = CreateSampleCandles(count, i => 100m + (decimal)Math.Sin(i * 0.5 + 0.2) * 5m);

        var param = new CoreCrossTickerDtwParameter
        {
            Period = period,
            ComparisonSymbol = "SPY",
            OutputMode = DtwOutputMode.Similarity
        };

        var indicator = new CoreCrossTickerDtwIndicator();
        indicator.Configure(param);
        indicator.SetSecondaryCandles(secondaryCandles.Cast<CoreCandleData?>().ToList());

        var result = indicator.Calculate(primaryCandles);
        Assert.True(result.IsSuccessful);

        var main = result.MainValues;
        var simScore = result.GetSeries(CoreCrossTickerDtwIndicator.SimilarityScoreSeriesName);

        for (int i = period - 1; i < count; i++)
        {
            Assert.NotNull(main[i]);
            Assert.Equal(simScore[i]!.Value, main[i]!.Value);
            Assert.InRange(main[i]!.Value, 0.0m, 1.0m);
        }
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
