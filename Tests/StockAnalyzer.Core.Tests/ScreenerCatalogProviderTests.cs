using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
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

    [Fact]
    public void GetCatalogItems_ContainsContinuationCandlePatterns()
    {
        // Arrange
        var provider = new ScreenerCatalogProvider();

        // Act
        var items = provider.GetCatalogItems();

        // Assert
        var candlePatternItems = items
            .Where(i => i.CategoryType == ScreenerItemCategoryType.Criteria && i.GroupName == ScreenerGroupNames.CandlestickPatterns)
            .Select(i => i.ShortName)
            .ToList();

        Assert.Contains("Rising Three Methods", candlePatternItems);
        Assert.Contains("Falling Three Methods", candlePatternItems);
        Assert.Contains("Mat Hold", candlePatternItems);
        Assert.Contains("Bullish Side-by-Side White Lines", candlePatternItems);
        Assert.Contains("Bearish Side-by-Side White Lines", candlePatternItems);
        Assert.Contains("Bullish Three-Line Strike", candlePatternItems);
        Assert.Contains("Bearish Three-Line Strike", candlePatternItems);
    }

    [Fact]
    public void GetCatalogItems_ContainsAdvancedReversalCandlePatterns()
    {
        // Arrange
        var provider = new ScreenerCatalogProvider();

        // Act
        var items = provider.GetCatalogItems();

        // Assert
        var candlePatternItems = items
            .Where(i => i.CategoryType == ScreenerItemCategoryType.Criteria && i.GroupName == ScreenerGroupNames.CandlestickPatterns)
            .Select(i => i.ShortName)
            .ToList();

        Assert.Contains("Bullish Abandoned Baby", candlePatternItems);
        Assert.Contains("Bearish Abandoned Baby", candlePatternItems);
        Assert.Contains("Advance Block", candlePatternItems);
        Assert.Contains("Deliberation", candlePatternItems);
        Assert.Contains("Stick Sandwich", candlePatternItems);
        Assert.Contains("Ladder Bottom", candlePatternItems);
        Assert.Contains("Homing Pigeon", candlePatternItems);
    }

    [Fact]
    public void GetCatalogItems_ContainsGapCandlePatterns()
    {
        // Arrange
        var provider = new ScreenerCatalogProvider();

        // Act
        var items = provider.GetCatalogItems();

        // Assert
        var candlePatternItems = items
            .Where(i => i.CategoryType == ScreenerItemCategoryType.Criteria && i.GroupName == ScreenerGroupNames.CandlestickPatterns)
            .Select(i => i.ShortName)
            .ToList();

        Assert.Contains("Bullish Tasuki Gap", candlePatternItems);
        Assert.Contains("Bearish Tasuki Gap", candlePatternItems);
        Assert.Contains("Bullish Gap Three Methods", candlePatternItems);
        Assert.Contains("Bearish Gap Three Methods", candlePatternItems);
        Assert.Contains("Bullish Breakaway", candlePatternItems);
        Assert.Contains("Bearish Breakaway", candlePatternItems);
    }

    [Fact]
    public void GetCatalogItems_ContainsExoticAndMinorCandlePatterns()
    {
        // Arrange
        var provider = new ScreenerCatalogProvider();

        // Act
        var items = provider.GetCatalogItems();

        // Assert
        var candlePatternItems = items
            .Where(i => i.CategoryType == ScreenerItemCategoryType.Criteria && i.GroupName == ScreenerGroupNames.CandlestickPatterns)
            .Select(i => i.ShortName)
            .ToList();

        Assert.Contains("Bullish Kicking", candlePatternItems);
        Assert.Contains("Bearish Kicking", candlePatternItems);
        Assert.Contains("Concealing Baby Swallow", candlePatternItems);
        Assert.Contains("Identical Three Crows", candlePatternItems);
    }
}

