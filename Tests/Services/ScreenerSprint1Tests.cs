using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Services;

public class ScreenerSprint1Tests
{
    private static List<CoreCandleData> CreateTestCandles(int count = 100)
    {
        var candles = new List<CoreCandleData>(count);
        var baseDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal price = 100m + i * 0.5m + (decimal)Math.Sin(i * 0.2) * 5m;
            candles.Add(new CoreCandleData(
                baseDate.AddDays(i),
                price,
                price + 2m,
                price - 2m,
                price + 1m,
                10000 + i * 100));
        }
        return candles;
    }

    [Fact]
    public void TryResolveSeries_InvalidOrNullResult_ReturnsFalseAndEmpty()
    {
        bool resolved = IndicatorOutputSeriesResolver.TryResolveSeries(
            null,
            IndicatorType.SMA,
            "Main",
            (PriceType?)null,
            out var series);

        Assert.False(resolved);
        Assert.Empty(series);

        var failResult = IndicatorResult.Failure("error");
        resolved = IndicatorOutputSeriesResolver.TryResolveSeries(
            failResult,
            IndicatorType.SMA,
            "Main",
            (PriceType?)null,
            out series);

        Assert.False(resolved);
        Assert.Empty(series);
    }

    [Fact]
    public void TryResolveSeries_UnknownOutputName_ReturnsFalse_FailClosed()
    {
        var candles = CreateTestCandles(50);
        var bb = IndicatorFactory.Default.Create(IndicatorType.BB);
        Assert.NotNull(bb);
        var result = bb.Calculate(candles);

        bool resolved = IndicatorOutputSeriesResolver.TryResolveSeries(
            result,
            IndicatorType.BB,
            "NonExistentBand",
            (PriceType?)null,
            out var series);

        Assert.False(resolved);
        Assert.Empty(series);
    }

    [Theory]
    [InlineData(IndicatorType.BB, "Main", "UpperBand", "LowerBand")]
    [InlineData(IndicatorType.PrimeNumberBands, "Main", "UpperBand", "LowerBand")]
    [InlineData(IndicatorType.ADX, "Main", "PlusDI", "MinusDI")]
    [InlineData(IndicatorType.Donchian, "Main", "UpperBand", "LowerBand")]
    [InlineData(IndicatorType.ElderRay, "Main", "BullPower", "BearPower")]
    [InlineData(IndicatorType.FisherTransform, "Main", "Signal", null)]
    [InlineData(IndicatorType.Keltner, "Main", "UpperBand", "LowerBand")]
    [InlineData(IndicatorType.MurreyMath, "Main", "Level0_8", "Level8_8")]
    [InlineData(IndicatorType.RVI, "Main", "SignalLine", null)]
    [InlineData(IndicatorType.SSAAnomaly, "Main", "AnomalyStateSeries", "EnterThresholdSeries")]
    [InlineData(IndicatorType.SSAResidualBand, "Main", "UpperBand", "BandWidth")]
    [InlineData(IndicatorType.SSASNR, "Main", "SignalPurity", "ThresholdHighSeries")]
    [InlineData(IndicatorType.ThreeLineBreakSignal, "Main", "Histogram", null)]
    [InlineData(IndicatorType.Vortex, "Main", "VIPlus", "VIMinus")]
    public void MultiSeriesIndicators_AllSubSeriesRegisteredAndResolvable(
        IndicatorType type,
        string mainSeries,
        string subSeries1,
        string? subSeries2)
    {
        var candles = CreateTestCandles(80);
        var indicator = IndicatorFactory.Default.Create(type);
        Assert.NotNull(indicator);

        var result = indicator.Calculate(candles);
        Assert.NotNull(result);
        Assert.True(result.IsSuccessful, $"Indicator {type} calculation failed: {result.ErrorMessage}");

        // Main series
        bool hasMain = IndicatorOutputSeriesResolver.TryResolveSeries(result, type, mainSeries, (PriceType?)null, out var mainOut);
        Assert.True(hasMain, $"Could not resolve {mainSeries} for {type}");
        Assert.NotEmpty(mainOut);

        // SubSeries 1
        bool hasSub1 = IndicatorOutputSeriesResolver.TryResolveSeries(result, type, subSeries1, (PriceType?)null, out var sub1Out);
        Assert.True(hasSub1, $"Could not resolve {subSeries1} for {type}");
        Assert.NotEmpty(sub1Out);

        // SubSeries 2 (if specified)
        if (subSeries2 != null)
        {
            bool hasSub2 = IndicatorOutputSeriesResolver.TryResolveSeries(result, type, subSeries2, (PriceType?)null, out var sub2Out);
            Assert.True(hasSub2, $"Could not resolve {subSeries2} for {type}");
            Assert.NotEmpty(sub2Out);
        }
    }

    [Fact]
    public void BollingerBands_BothLegacyAndBandSuffixes_AreResolvable()
    {
        var candles = CreateTestCandles(50);
        var bb = IndicatorFactory.Default.Create(IndicatorType.BB);
        Assert.NotNull(bb);
        var result = bb.Calculate(candles);

        Assert.True(IndicatorOutputSeriesResolver.TryResolveSeries(result, IndicatorType.BB, "Upper", (PriceType?)null, out var upper));
        Assert.True(IndicatorOutputSeriesResolver.TryResolveSeries(result, IndicatorType.BB, "UpperBand", (PriceType?)null, out var upperBand));
        Assert.True(IndicatorOutputSeriesResolver.TryResolveSeries(result, IndicatorType.BB, "Lower", (PriceType?)null, out var lower));
        Assert.True(IndicatorOutputSeriesResolver.TryResolveSeries(result, IndicatorType.BB, "LowerBand", (PriceType?)null, out var lowerBand));
        Assert.True(IndicatorOutputSeriesResolver.TryResolveSeries(result, IndicatorType.BB, "MiddleBand", (PriceType?)null, out var middleBand));

        Assert.Equal(upper, upperBand);
        Assert.Equal(lower, lowerBand);
        Assert.Equal(result.MainValues, middleBand);
    }

    [Fact]
    public void ScreenerCatalogProvider_ExcludesBlockedTypes()
    {
        var provider = new ScreenerCatalogProvider();
        var catalog = provider.GetCatalogItems();

        var blockedTypes = Enum.GetValues<IndicatorType>()
            .Where(t => BacktestIndicatorEligibility.IsTypeBlocked(t))
            .ToHashSet();

        Assert.NotEmpty(blockedTypes);

        foreach (var item in catalog.Where(i => i.CategoryType == ScreenerItemCategoryType.Indicator))
        {
            if (item.IndicatorType is { } indType)
            {
                Assert.False(blockedTypes.Contains(indType));
            }
        }
    }

    [Fact]
    public void ScreenerCatalogProvider_ExcludesChikouSpanFromIchimoku()
    {
        var provider = new ScreenerCatalogProvider();
        var seriesNames = provider.GetOutputSeriesNames(IndicatorType.Ichimoku);

        Assert.NotEmpty(seriesNames);
        Assert.DoesNotContain("ChikouSpan", seriesNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Chikou", seriesNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("TenkanSen", seriesNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("KijunSen", seriesNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("SenkouSpanA", seriesNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("SenkouSpanB", seriesNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScreenerValueExtractor_NonD1TimeFrame_ReturnsNullAndRecordsFault()
    {
        var extractor = new ScreenerValueExtractor();
        var candles = CreateTestCandles(50);
        var config = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.SMA,
            TimeFrame = TimeFrame.W1,
            Parameters = new Dictionary<string, object> { { "Period", 10 } }
        };

        decimal? val = extractor.ExtractValueNullable(config, candles);

        Assert.Null(val);
        var snapshot = extractor.Faults.TakeSnapshotAndReset();
        Assert.Contains(snapshot, s => s.Site == ScreenerFaultLog.SiteIndicatorCalculate && s.Count > 0);
    }

    [Fact]
    public void ScreenerValueExtractor_BlockedIndicator_ReturnsNullAndRecordsFault()
    {
        var extractor = new ScreenerValueExtractor();
        var candles = CreateTestCandles(50);
        var config = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.ZigZag,
            TimeFrame = TimeFrame.D1
        };

        decimal? val = extractor.ExtractValueNullable(config, candles);

        Assert.Null(val);
        var snapshot = extractor.Faults.TakeSnapshotAndReset();
        Assert.Contains(snapshot, s => s.Site == ScreenerFaultLog.SiteIndicatorCalculate && s.Count > 0);
    }

    [Fact]
    public void ScreenerValueExtractor_BlockedSeries_ChikouSpan_ReturnsNullAndRecordsFault()
    {
        var extractor = new ScreenerValueExtractor();
        var candles = CreateTestCandles(50);
        var config = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.Ichimoku,
            OutputName = "ChikouSpan",
            TimeFrame = TimeFrame.D1
        };

        decimal? val = extractor.ExtractValueNullable(config, candles);

        Assert.Null(val);
        var snapshot = extractor.Faults.TakeSnapshotAndReset();
        Assert.Contains(snapshot, s => s.Site == ScreenerFaultLog.SiteIndicatorCalculate && s.Count > 0);
    }

    [Fact]
    public void ScreenerValueExtractor_UnknownSeries_ReturnsNullAndRecordsFault_FailClosed()
    {
        var extractor = new ScreenerValueExtractor();
        var candles = CreateTestCandles(50);
        var config = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.BB,
            OutputName = "NonExistentBand",
            TimeFrame = TimeFrame.D1
        };

        decimal? val = extractor.ExtractValueNullable(config, candles);

        Assert.Null(val);
        var snapshot = extractor.Faults.TakeSnapshotAndReset();
        Assert.Contains(snapshot, s => s.Site == ScreenerFaultLog.SiteIndicatorCalculate && s.Count > 0);
    }

    [Fact]
    public void ScreenerValueExtractor_Ichimoku_SenkouSpanA_AnchoredAtLatestBarIndex()
    {
        var extractor = new ScreenerValueExtractor();
        var candles = CreateTestCandles(120);

        // Direct calculation of Ichimoku
        var ichimoku = new CoreIchimokuIndicator();
        var result = ichimoku.Calculate(candles);
        Assert.True(result.IsSuccessful);

        // SenkouSpanA has length candles.Count + 26
        Assert.True(result.HasSeries("SenkouSpanA"));
        var spanA = result.GetSeries("SenkouSpanA");
        Assert.Equal(candles.Count + ichimoku.KijunPeriod, spanA.Count);

        // The expected value at Offset = 0 is spanA[candles.Count - 1], NOT spanA[spanA.Count - 1]
        decimal? expectedLatestBarValue = spanA[candles.Count - 1];
        Assert.NotNull(expectedLatestBarValue);

        var config = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.Ichimoku,
            OutputName = "SenkouSpanA",
            Offset = 0,
            TimeFrame = TimeFrame.D1
        };

        decimal? extractedVal = extractor.ExtractValueNullable(config, candles);

        Assert.NotNull(extractedVal);
        Assert.Equal(expectedLatestBarValue.Value, extractedVal.Value);
    }

    [Fact]
    public void ScreenerValueExtractor_MultiSeries_ExtractsDifferentSubSeriesCorrectly()
    {
        var extractor = new ScreenerValueExtractor();
        var candles = CreateTestCandles(80);

        var configUpper = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.BB,
            OutputName = "UpperBand",
            TimeFrame = TimeFrame.D1
        };

        var configLower = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.BB,
            OutputName = "LowerBand",
            TimeFrame = TimeFrame.D1
        };

        decimal? upperVal = extractor.ExtractValueNullable(configUpper, candles);
        decimal? lowerVal = extractor.ExtractValueNullable(configLower, candles);

        Assert.NotNull(upperVal);
        Assert.NotNull(lowerVal);
        Assert.True(upperVal > lowerVal, "UpperBand should be strictly greater than LowerBand for volatile candles.");
    }
}
