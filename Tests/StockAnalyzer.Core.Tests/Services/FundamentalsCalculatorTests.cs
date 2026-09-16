using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class FundamentalsCalculatorTests
{
    [Fact]
    public void CalculateDerived_ShouldComputeCorrectMetrics()
    {
        // Arrange
        var meta = new TickerMetadata("AAPL", "Apple Inc", "US", "Tech", "Consumer Electronics", "USD")
        {
            CurrentPrice = 150m,
            BookValue = 30m,
            DividendRate = 3m,
            TrailingEps = 6m,
            FreeCashflow = 3000m,
            SharesOutstanding = 100m, // MarketCap = 150 * 100 = 15000
            TotalRevenue = 12000m,
            TotalDebt = 2000m,
            TotalCash = 5000m, // NetDebt = 2000 - 5000 = -3000 (Net cash)
            Ebitda = 1000m,
            FiftyTwoWeekHigh = 200m,
            FloatShares = 80m, // Float Ratio = 80%
            FullTimeEmployees = 100,
            TrailingPE = 25m,
            EarningsGrowth = 0.10m, // 10%
            OperatingCashflow = 3000m
        };

        // Act
        var result = FundamentalsCalculator.CalculateDerived(meta);

        // Assert
        Assert.Equal(15000m, result.MarketCap);
        Assert.Equal(5m, result.PbrCalculated); // 150 / 30
        Assert.Equal(2m, result.DividendYieldCalculated); // (3 / 150) * 100
        Assert.Equal(4m, result.EarningsYield); // (6 / 150) * 100
        Assert.Equal(20m, result.FcfYield); // (3000 / 15000) * 100
        Assert.Equal(25m, result.FcfMargin); // (3000 / 12000) * 100
        Assert.Equal(-3000m, result.NetDebt); // 2000 - 5000
        Assert.Equal(-3m, result.NetDebtToEbitda); // -3000 / 1000
        Assert.Equal(2m, result.DividendCoverage); // 6 / 3
        Assert.Equal(-25m, result.PctFromFiftyTwoWeekHigh); // ((150 / 200) - 1) * 100
        Assert.Equal(80m, result.FloatRatio); // (80 / 100) * 100
        Assert.Equal(150m, result.MarketCapPerEmployee); // 15000 / 100
        Assert.Equal(2.5m, result.PegRatio); // 25 / (0.10 * 100) = 2.5
        Assert.Equal(20m, result.OperatingCashFlowYield); // (3000 / 15000) * 100
        Assert.Equal(20m, result.NetCashRatio); // (5000 - 2000) / 15000 = 3000 / 15000 = 20%
    }

    [Fact]
    public void CalculateDerived_WithNulls_ShouldHandleGracefully()
    {
        // Arrange
        var meta = TickerMetadata.Unknown;

        // Act
        var result = FundamentalsCalculator.CalculateDerived(meta);

        // Assert
        Assert.Null(result.MarketCap);
        Assert.Null(result.PbrCalculated);
        Assert.Null(result.DividendYieldCalculated);
        Assert.Null(result.EarningsYield);
        Assert.Null(result.FcfYield);
        Assert.Null(result.FcfMargin);
        Assert.Null(result.NetDebt);
        Assert.Null(result.NetDebtToEbitda);
        Assert.Null(result.DividendCoverage);
        Assert.Null(result.PctFromFiftyTwoWeekHigh);
        Assert.Null(result.FloatRatio);
        Assert.Null(result.MarketCapPerEmployee);
        Assert.Null(result.PegRatio);
        Assert.Null(result.OperatingCashFlowYield);
        Assert.Null(result.NetCashRatio);
    }

    [Fact]
    public void CalculateDerived_WithCandles_ShouldComputeDynamicFiftyTwoWeekMetrics()
    {
        // Arrange
        var baseDate = new DateTime(2026, 7, 1);
        var candles = new List<StockAnalyzer.Core.Models.CandleData>
        {
            new(baseDate.AddYears(-2), 100m, 120m, 90m, 110m, 1000), // Older than 1 year (should be excluded)
            new(baseDate.AddMonths(-6), 150m, 250m, 140m, 200m, 1000), // Within 1 year (High: 250, Low: 140)
            new(baseDate.AddMonths(-3), 200m, 300m, 180m, 220m, 1000), // Within 1 year (High: 300, Low: 180) -> Max High: 300
            new(baseDate, 220m, 260m, 100m, 250m, 1000)                // Within 1 year (High: 260, Low: 100) -> Min Low: 100
        };

        var meta = new TickerMetadata("TEST", "Test Ticker", "US", "Tech", "Software", "USD")
        {
            CurrentPrice = 250m,
            FiftyTwoWeekHigh = 500m, // Stale Metadata value
            FiftyTwoWeekLow = 50m    // Stale Metadata value
        };

        // Act
        var result = FundamentalsCalculator.CalculateDerived(meta, candles);

        // Assert
        Assert.Equal(300m, result.FiftyTwoWeekHigh); // Dynamic Max High in past 1 year
        Assert.Equal(100m, result.FiftyTwoWeekLow);  // Dynamic Min Low in past 1 year

        // PctFromFiftyTwoWeekHigh = ((250 / 300) - 1) * 100 = -16.6666... %
        Assert.NotNull(result.PctFromFiftyTwoWeekHigh);
        Assert.Equal(-16.66666666666666666666666667m, result.PctFromFiftyTwoWeekHigh.Value);

        // FiftyTwoWeekRangePosition = (250 - 100) / (300 - 100) = 150 / 200 = 0.75
        Assert.NotNull(result.FiftyTwoWeekRangePosition);
        Assert.Equal(0.75m, result.FiftyTwoWeekRangePosition.Value);
    }
}
