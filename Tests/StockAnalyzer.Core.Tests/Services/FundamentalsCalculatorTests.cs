using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
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

    [Fact]
    public void CalculateDerived_WhenMetadataCausesOverflow_ReturnsNullForOverflowedMetricsWithoutThrowing()
    {
        var meta = new TickerMetadata("OVERFLOW", "Overflow Corp", "US", "Tech", "Software", "USD")
        {
            CurrentPrice = decimal.MaxValue,
            SharesOutstanding = 2m, // MarketCap overflows
            BookValue = 1e-28m, // PbrCalculated overflows
            DividendRate = decimal.MaxValue, // DividendYieldCalculated overflows when divided by 10m * 100m
            TrailingEps = decimal.MaxValue,
            FreeCashflow = decimal.MaxValue,
            TotalRevenue = 1e-28m, // FcfMargin overflows
            TotalDebt = decimal.MaxValue,
            TotalCash = -1m, // NetDebt: MaxValue - (-1) overflows
            Ebitda = 1e-28m,
            FiftyTwoWeekHigh = 1e-28m,
            FloatShares = decimal.MaxValue,
            FullTimeEmployees = 1,
            TrailingPE = decimal.MaxValue,
            EarningsGrowth = 1e-28m,
            OperatingCashflow = decimal.MaxValue
        };

        var result = FundamentalsCalculator.CalculateDerived(meta);

        Assert.Null(result.MarketCap);
        Assert.Null(result.PbrCalculated);
        Assert.Null(result.FcfMargin);
        Assert.Null(result.NetDebt);
        Assert.Null(result.NetDebtToEbitda);
        Assert.Null(result.PctFromFiftyTwoWeekHigh);
        Assert.Null(result.FloatRatio);
        Assert.Null(result.MarketCapPerEmployee);
        Assert.Null(result.PegRatio);
        Assert.Null(result.OperatingCashFlowYield);
        Assert.Null(result.NetCashRatio);
        Assert.Null(result.PriceToCashFlowRatio);
    }

    [Fact]
    public void AtomicSSoT_BoundaryAndEdgeCases_BehaveDeterministically()
    {
        // Safe arithmetic primitives
        Assert.Null(FundamentalsCalculator.TryDivide(10m, 0m));
        Assert.Null(FundamentalsCalculator.TryDividePercent(10m, 0m));
        Assert.Null(FundamentalsCalculator.TryMultiply(decimal.MaxValue, 2m));
        Assert.Null(FundamentalsCalculator.TryMultiply((decimal?)null, 10m));
        Assert.Null(FundamentalsCalculator.TryMultiply(10m, (decimal?)null));
        Assert.Equal(50m, FundamentalsCalculator.TryMultiply((decimal?)5m, (decimal?)10m));
        Assert.Null(FundamentalsCalculator.TryDividePercent(decimal.MaxValue, 10m)); // (MaxValue / 10) * 100 strictly overflows per lessons.md!

        // ComputeMarketCap
        Assert.Equal(200m, FundamentalsCalculator.ComputeMarketCap(100m, 2m));
        Assert.Equal(0m, FundamentalsCalculator.ComputeMarketCap(100m, 0m, requireNonZeroShares: false));
        Assert.Null(FundamentalsCalculator.ComputeMarketCap(100m, 0m, requireNonZeroShares: true));
        Assert.Null(FundamentalsCalculator.ComputeMarketCap(null, 100m));

        // ComputeNetDebtToEbitda
        Assert.Equal(-2m, FundamentalsCalculator.ComputeNetDebtToEbitda(-20m, 10m, requirePositiveEbitda: true));
        Assert.Null(FundamentalsCalculator.ComputeNetDebtToEbitda(-20m, -10m, requirePositiveEbitda: true)); // Negative EBITDA rejected when true
        Assert.Equal(2m, FundamentalsCalculator.ComputeNetDebtToEbitda(-20m, -10m, requirePositiveEbitda: false)); // Allowed when false (Screener fallback)
        Assert.Null(FundamentalsCalculator.ComputeNetDebtToEbitda(10m, 0m, requirePositiveEbitda: false));

        // ComputeTurnoverRate
        Assert.Equal(50m, FundamentalsCalculator.ComputeTurnoverRate(500, 1000m));
        Assert.Null(FundamentalsCalculator.ComputeTurnoverRate(0, 1000m));
        Assert.Null(FundamentalsCalculator.ComputeTurnoverRate(500, 0m));
        Assert.Null(FundamentalsCalculator.ComputeTurnoverRate(500, null));

        // ComputeNetDebtEquityRatio & ComputeDebtToEquityRatio
        Assert.Equal(0.5m, FundamentalsCalculator.ComputeDebtToEquityRatio(100m, 10m, 20m)); // 100 / (10 * 20) = 100 / 200 = 0.5
        Assert.Null(FundamentalsCalculator.ComputeDebtToEquityRatio(100m, 0m, 20m));
        Assert.Null(FundamentalsCalculator.ComputeDebtToEquityRatio(decimal.MaxValue, 1e-28m, 1e-28m));

        // ComputeFiftyTwoWeekRangePosition
        Assert.Equal(0.5m, FundamentalsCalculator.ComputeFiftyTwoWeekRangePosition(150m, 100m, 200m));
        Assert.Null(FundamentalsCalculator.ComputeFiftyTwoWeekRangePosition(150m, 100m, 100m)); // High == Low (range 0)
    }
}
