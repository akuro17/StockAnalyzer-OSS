using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class ScreenerCatalogProviderTests
{
    [Fact]
    public void GetOutputSeriesNames_SMA_ReturnsSmaOnly()
    {
        // Arrange
        var provider = new ScreenerCatalogProvider();

        // Act
        var outputs = provider.GetOutputSeriesNames(IndicatorType.SMA);

        // Assert
        Assert.NotNull(outputs);
        Assert.Single(outputs);
        Assert.Equal("SMA", outputs[0]);
    }

    [Fact]
    public void GetOutputSeriesNames_MACD_ReturnsMacdSignalAndHistogramWithoutMain()
    {
        // Arrange
        var provider = new ScreenerCatalogProvider();

        // Act
        var outputs = provider.GetOutputSeriesNames(IndicatorType.MACD);

        // Assert
        Assert.NotNull(outputs);
        Assert.Equal(3, outputs.Count);
        Assert.Equal("MacdLine", outputs[0]);
        Assert.Equal("Signal", outputs[1]);
        Assert.Equal("Histogram", outputs[2]);
        Assert.DoesNotContain("Main", outputs);
        Assert.DoesNotContain("BullishSignals", outputs);
        Assert.DoesNotContain("BearishSignals", outputs);
    }

    [Fact]
    public void GetOutputSeriesNames_BollingerBands_DeduplicatesMiddleBandAndExcludesMain()
    {
        // Arrange
        var provider = new ScreenerCatalogProvider();

        // Act
        var outputs = provider.GetOutputSeriesNames(IndicatorType.BB);

        // Assert
        Assert.NotNull(outputs);
        Assert.Equal(3, outputs.Count);
        Assert.Contains("MiddleBand", outputs);
        Assert.Contains("UpperBand", outputs);
        Assert.Contains("LowerBand", outputs);
        Assert.DoesNotContain("Main", outputs);
        // Ensure no duplicate entries exist in the list
        Assert.Equal(outputs.Count, outputs.Distinct().Count());
    }

    [Fact]
    public void GetOutputSeriesNames_RSI_ExcludesSignalMarkersAndReturnsRsiOnly()
    {
        // Arrange
        var provider = new ScreenerCatalogProvider();

        // Act
        var outputs = provider.GetOutputSeriesNames(IndicatorType.RSI);

        // Assert
        Assert.NotNull(outputs);
        Assert.Single(outputs);
        Assert.Equal("RSI", outputs[0]);
    }

    [Fact]
    public void GetOutputSeriesNames_MovingAverageCross_ExcludesSignalMarkers()
    {
        // Arrange
        var provider = new ScreenerCatalogProvider();

        // Act
        var outputs = provider.GetOutputSeriesNames(IndicatorType.MovingAverageCross);

        // Assert
        Assert.NotNull(outputs);
        Assert.Equal(2, outputs.Count);
        Assert.Contains("ShortMA", outputs);
        Assert.Contains("LongMA", outputs);
        Assert.DoesNotContain("Main", outputs);
        Assert.DoesNotContain("BullishSignals", outputs);
        Assert.DoesNotContain("BearishSignals", outputs);
    }
}
