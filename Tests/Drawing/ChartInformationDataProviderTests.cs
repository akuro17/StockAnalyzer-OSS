using System;
using System.Collections.Generic;
using System.Reflection;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class ChartInformationDataProviderTests
{
    [Fact]
    public void CandleInformation_SymbolPropertyIsExplicitlyExcluded()
    {
        // Specification requirement: Ticker symbol must be excluded from display
        var candleProps = typeof(CandleInformation).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.DoesNotContain(candleProps, p => p.Name.Equals("Symbol", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Ticker", StringComparison.OrdinalIgnoreCase));

        var snapshotProps = typeof(ChartInformationSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.DoesNotContain(snapshotProps, p => p.Name.Equals("Symbol", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Ticker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_WithValidCandles_FormatsOhlcvAndChangeAccurately()
    {
        var d1 = new DateTime(2026, 1, 1);
        var d2 = new DateTime(2026, 1, 2);

        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(d1, 100m, 105m, 99m, 102m, 1000000),
            new CoreCandleData(d2, 102m, 110m, 101m, 108m, 2500000)
        };

        var snapshot = ChartInformationDataProvider.Extract(
            candles: candles,
            indicators: null,
            indicatorResults: null,
            objectManager: null,
            targetTime: d2,
            targetPrice: 108m,
            theme: ThemeColors.Dark
        );

        Assert.NotNull(snapshot.Candle);
        Assert.Equal("2026/01/02", snapshot.Candle.DateText);
        Assert.Equal("102.000", snapshot.Candle.OpenText);
        Assert.Equal("110.000", snapshot.Candle.HighText);
        Assert.Equal("101.000", snapshot.Candle.LowText);
        Assert.Equal("108.000", snapshot.Candle.CloseText);
        Assert.Equal("2,500,000", snapshot.Candle.VolumeText);
        Assert.Equal("+6.000", snapshot.Candle.YesterdayChangeText);
        Assert.Equal("+5.88%", snapshot.Candle.YesterdayChangeRatioText);
        Assert.Equal(ThemeColors.Dark.SemanticPlus, snapshot.Candle.YesterdayChangeColor);
    }

    [Fact]
    public void Extract_NegativeChange_FormatsWithMinusAndSemanticMinus()
    {
        var d1 = new DateTime(2026, 1, 1);
        var d2 = new DateTime(2026, 1, 2);

        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(d1, 100m, 105m, 99m, 100m, 1000),
            new CoreCandleData(d2, 100m, 102m, 90m, 95m, 1500)
        };

        var snapshot = ChartInformationDataProvider.Extract(
            candles: candles,
            indicators: null,
            indicatorResults: null,
            objectManager: null,
            targetTime: d2,
            targetPrice: 95m,
            theme: ThemeColors.Dark
        );

        Assert.NotNull(snapshot.Candle);
        Assert.Equal("-5.000", snapshot.Candle.YesterdayChangeText);
        Assert.Equal("-5.00%", snapshot.Candle.YesterdayChangeRatioText);
        Assert.Equal(ThemeColors.Dark.SemanticMinus, snapshot.Candle.YesterdayChangeColor);
    }

    [Fact]
    public void Extract_WithIndicator_ReturnsFormattedIndicatorItem()
    {
        var d1 = new DateTime(2026, 1, 1);
        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(d1, 100m, 105m, 99m, 100m, 1000)
        };

        var indicatorSetting = new CoreIndicatorSettings
        {
            Id = "ind-sma-20",
            DisplayName = "SMA(20)",
            IsEnabled = true,
            Color = IndicatorColor.FromRgb(255, 128, 0)
        };

        var result = IndicatorResult.Success(new decimal?[] { 105.789m });
        var indicatorResults = new Dictionary<string, IIndicatorResult>
        {
            { indicatorSetting.Id, result }
        };

        var snapshot = ChartInformationDataProvider.Extract(
            candles: candles,
            indicators: new[] { indicatorSetting },
            indicatorResults: indicatorResults,
            objectManager: null,
            targetTime: d1,
            targetPrice: 100m
        );

        Assert.Single(snapshot.Indicators);
        Assert.Equal("SMA(20)", snapshot.Indicators[0].Name);
        Assert.Equal("105.789", snapshot.Indicators[0].FormattedValue);
        Assert.Equal(IndicatorColor.FromRgb(255, 128, 0), snapshot.Indicators[0].Color);
    }

    [Fact]
    public void Extract_WithDrawingObject_ReturnsCalculatedValues()
    {
        var dt = new DateTime(2026, 1, 1);
        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(dt, 100m, 105m, 99m, 100m, 1000)
        };

        var objectManager = new ChartObjectManager();
        var hline = new HorizontalLineObject(new ChartPoint(dt, 150.5m));
        objectManager.AddObject(hline);

        var snapshot = ChartInformationDataProvider.Extract(
            candles: candles,
            indicators: null,
            indicatorResults: null,
            objectManager: objectManager,
            targetTime: dt,
            targetPrice: 100m
        );

        Assert.Single(snapshot.Drawings);
        Assert.Equal("Price", snapshot.Drawings[0].MetricLabel);
        Assert.Equal("150.500", snapshot.Drawings[0].FormattedValue);
    }

    [Fact]
    public void Extract_EmptyCandles_DoesNotThrowAndReturnsNullCandle()
    {
        var snapshot = ChartInformationDataProvider.Extract(
            candles: Array.Empty<CoreCandleData>(),
            indicators: null,
            indicatorResults: null,
            objectManager: null,
            targetTime: new DateTime(2026, 1, 1),
            targetPrice: null
        );

        Assert.Null(snapshot.Candle);
        Assert.Empty(snapshot.Indicators);
        Assert.Empty(snapshot.Drawings);
    }
}
