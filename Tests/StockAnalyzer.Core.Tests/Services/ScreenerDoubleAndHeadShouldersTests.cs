using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class ScreenerDoubleAndHeadShouldersTests
{
    private static CandleData Candle(int index, decimal open, decimal high, decimal low, decimal close)
        => new(new DateTime(2026, 1, 1).AddDays(index), open, high, low, close, 1000);

    public static List<CandleData> CreateDoubleTopCandles()
    {
        return new List<CandleData>
        {
            Candle(0, 100m, 100m, 100m, 100m),
            Candle(1, 118m, 120m, 118m, 120m), // Peak 1: 120
            Candle(2, 100m, 100m, 90m, 90m),   // Reversal down
            Candle(3, 85m, 86m, 85m, 85m),     // Valley 1: 85
            Candle(4, 100m, 102m, 100m, 102m), // Reversal up
            Candle(5, 118m, 120m, 118m, 120m), // Peak 2: 120
            Candle(6, 70m, 72m, 70m, 70m),     // Valley 2: 70 (> 17% away from Valley 1: 85)
            Candle(7, 70m, 71m, 70m, 70m)
        };
    }

    public static List<CandleData> CreateDoubleBottomCandles()
    {
        return new List<CandleData>
        {
            Candle(0, 105m, 105m, 105m, 105m), // Peak 1: 105
            Candle(1, 82m, 82m, 80m, 80m),     // Trough 1: 80
            Candle(2, 95m, 95m, 85m, 95m),     // Reversal up
            Candle(3, 118m, 120m, 118m, 120m), // Peak 2: 120 (> 12% away from Peak 1: 105)
            Candle(4, 110m, 110m, 105m, 105m), // Reversal down
            Candle(5, 82m, 82m, 80m, 80m),     // Trough 2: 80
            Candle(6, 95m, 95m, 85m, 95m),     // Reversal up
            Candle(7, 95m, 96m, 95m, 95m)
        };
    }

    public static List<CandleData> CreateHeadAndShouldersTopCandles()
    {
        return new List<CandleData>
        {
            Candle(0, 90m, 90m, 90m, 90m),
            Candle(1, 108m, 110m, 108m, 110m), // Left shoulder: 110
            Candle(2, 100m, 100m, 95m, 95m),   // Trough 1: 95
            Candle(3, 105m, 108m, 105m, 108m), // Reversal up
            Candle(4, 128m, 130m, 128m, 130m), // Head: 130
            Candle(5, 100m, 100m, 95m, 95m),   // Trough 2: 95
            Candle(6, 105m, 108m, 105m, 108m), // Reversal up
            Candle(7, 108m, 110m, 108m, 110m), // Right shoulder: 110
            Candle(8, 100m, 100m, 98m, 98m),   // Reversal down
            Candle(9, 100m, 100m, 98m, 98m)
        };
    }

    public static List<CandleData> CreateInverseHeadAndShouldersBottomCandles()
    {
        return new List<CandleData>
        {
            Candle(0, 110m, 110m, 110m, 110m),
            Candle(1, 92m, 92m, 90m, 90m),     // Left shoulder: 90
            Candle(2, 100m, 105m, 100m, 105m), // Peak 1: 105
            Candle(3, 95m, 95m, 92m, 92m),     // Reversal down
            Candle(4, 72m, 72m, 70m, 70m),     // Head: 70
            Candle(5, 100m, 105m, 100m, 105m), // Peak 2: 105
            Candle(6, 95m, 95m, 92m, 92m),     // Reversal down
            Candle(7, 92m, 92m, 90m, 90m),     // Right shoulder: 90
            Candle(8, 100m, 102m, 100m, 102m), // Reversal up
            Candle(9, 100m, 102m, 100m, 102m)
        };
    }

    [Fact]
    public void VerifyDirectDetectors_IsolateTopAndBottom()
    {
        var dt = CreateDoubleTopCandles();
        var db = CreateDoubleBottomCandles();
        var hst = CreateHeadAndShouldersTopCandles();
        var hsb = CreateInverseHeadAndShouldersBottomCandles();

        Assert.True(MarketStructureDetector.IsDoubleTop(dt));
        Assert.False(MarketStructureDetector.IsDoubleBottom(dt));

        Assert.True(MarketStructureDetector.IsDoubleBottom(db));
        Assert.False(MarketStructureDetector.IsDoubleTop(db));

        Assert.True(MarketStructureDetector.IsHeadAndShouldersTop(hst));
        Assert.False(MarketStructureDetector.IsHeadAndShouldersBottom(hst));

        Assert.True(MarketStructureDetector.IsHeadAndShouldersBottom(hsb));
        Assert.False(MarketStructureDetector.IsHeadAndShouldersTop(hsb));
    }

    [Fact]
    public void CatalogItem_DoubleTopBottom_BeforeFix_FailsOnDoubleTop()
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            OutputName = "Double Top / Bottom",
            CustomDisplayName = "Double Top or Double Bottom Reversal Pattern"
        };

        var dtCandles = CreateDoubleTopCandles();
        decimal? result = extractor.ExtractValueNullable(config, dtCandles);

        // Before fix: name contains "Bottom", so it only calls IsDoubleBottom, returning 0m.
        // After fix: name has both Top and Bottom, evaluating (IsDoubleTop || IsDoubleBottom), returning 1m.
        Assert.Equal(1m, result);
    }

    [Fact]
    public void CatalogItem_HeadAndShoulders_BeforeFix_FailsOnInverseHeadAndShoulders()
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            OutputName = "Head and Shoulders",
            CustomDisplayName = "Classical Head & Shoulders Top/Bottom Pattern"
        };

        var hsbCandles = CreateInverseHeadAndShouldersBottomCandles();
        decimal? result = extractor.ExtractValueNullable(config, hsbCandles);

        // Before fix: OutputName is "Head and Shoulders" (neither Bottom nor Inverse), so it only calls IsHeadAndShouldersTop, returning 0m.
        // After fix: generic Head and Shoulders evaluates (IsHeadAndShouldersTop || IsHeadAndShouldersBottom), returning 1m.
        Assert.Equal(1m, result);
    }

    [Fact]
    public void CatalogItem_HeadAndShoulders_DisplayNameFallback_EvaluatesBothTopAndBottom()
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            OutputName = "Main", // Fallback to CustomDisplayName
            CustomDisplayName = "Classical Head & Shoulders Top/Bottom Pattern"
        };

        var hstCandles = CreateHeadAndShouldersTopCandles();
        var hsbCandles = CreateInverseHeadAndShouldersBottomCandles();

        // Before fix: contains "Bottom", so only evaluates IsHeadAndShouldersBottom (fails on Top).
        // After fix: contains both "Top" and "Bottom", evaluating (IsHeadAndShouldersTop || IsHeadAndShouldersBottom).
        Assert.Equal(1m, extractor.ExtractValueNullable(config, hstCandles));
        Assert.Equal(1m, extractor.ExtractValueNullable(config, hsbCandles));
    }

    [Fact]
    public void CatalogItem_DoubleTopBottom_EvaluatesBothTopAndBottom()
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            OutputName = "Double Top / Bottom",
            CustomDisplayName = "Double Top or Double Bottom Reversal Pattern"
        };

        var dtCandles = CreateDoubleTopCandles();
        var dbCandles = CreateDoubleBottomCandles();

        Assert.Equal(1m, extractor.ExtractValueNullable(config, dtCandles));
        Assert.Equal(1m, extractor.ExtractValueNullable(config, dbCandles));
    }

    [Theory]
    [InlineData("Double Top", true, false)]
    [InlineData("Double Bottom", false, true)]
    public void OneSided_Double_EvaluatesOnlySpecifiedSide(string criteriaName, bool expectDt, bool expectDb)
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            OutputName = criteriaName
        };

        var dtCandles = CreateDoubleTopCandles();
        var dbCandles = CreateDoubleBottomCandles();

        Assert.Equal(expectDt ? 1m : 0m, extractor.ExtractValueNullable(config, dtCandles));
        Assert.Equal(expectDb ? 1m : 0m, extractor.ExtractValueNullable(config, dbCandles));
    }

    [Theory]
    [InlineData("Head and Shoulders Top", true, false)]
    [InlineData("Head and Shoulders Bottom", false, true)]
    [InlineData("Inverse Head and Shoulders", false, true)]
    public void OneSided_HeadAndShoulders_EvaluatesOnlySpecifiedSide(string criteriaName, bool expectHst, bool expectHsb)
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            OutputName = criteriaName
        };

        var hstCandles = CreateHeadAndShouldersTopCandles();
        var hsbCandles = CreateInverseHeadAndShouldersBottomCandles();

        Assert.Equal(expectHst ? 1m : 0m, extractor.ExtractValueNullable(config, hstCandles));
        Assert.Equal(expectHsb ? 1m : 0m, extractor.ExtractValueNullable(config, hsbCandles));
    }
}
