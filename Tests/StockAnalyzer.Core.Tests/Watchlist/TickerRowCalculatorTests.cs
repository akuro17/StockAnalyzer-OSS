using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Watchlist;
using Xunit;

namespace StockAnalyzer.Core.Tests.Watchlist;

public class TickerRowCalculatorTests
{
    [Fact]
    public void Compute_MetadataOnly_FallbackCloseAndChangeFromCurrentPrice()
    {
        var meta = new TickerMetadata(
            ShortName: "Apple Inc.",
            LongName: "Apple Incorporated",
            Region: "US",
            Sector: "Technology",
            Industry: "Consumer Electronics",
            Currency: "USD",
            CurrentPrice: 150m,
            LastClose: 140m
        )
        {
            Tag = "MegaCap, Tech",
            Notes = "Core holding",
            Reminder = "Check quarterly report",
            ReturnOnEquity = 15.5m,
            SharesOutstanding = 1_000_000m,
            Long = 135m,
            ExitLong = 165m,
            IsLong = true
        };

        var row = TickerRowCalculator.Compute("AAPL", meta, null);

        Assert.Equal("AAPL", row.Symbol);
        Assert.Equal("Apple Inc.", row.Name);
        Assert.Equal("Technology", row.Sector);
        Assert.Equal("Consumer Electronics", row.Industry);
        Assert.Equal("MegaCap, Tech", row.Tag);
        Assert.Null(row.Notes);
        Assert.Null(row.Reminder);
        Assert.Equal(150m, row.Close);
        Assert.Equal(10m, row.Change);
        Assert.Equal((double)(10m / 140m * 100m), row.ChangePercent, precision: 4);
        Assert.Equal(15.5m, row.ReturnOnEquity);
        Assert.Equal(135m, row.Long);
        Assert.Equal(135m, row.EntryPrice);
        Assert.Equal(165m, row.ExitLong);
        Assert.Equal(165m, row.TargetPrice);
        Assert.True(row.IsLong);

        // Without candles, 13 price-dependent metrics retain their metadata values (null here)
        Assert.Null(row.MarketCap);
    }

    [Fact]
    public void Compute_WithTwoCandles_ComputesOhlcvAndPriceChangeAndPriceDependentMetrics()
    {
        var meta = new TickerMetadata(
            ShortName: "Microsoft",
            LongName: "Microsoft Corporation",
            Region: "US",
            Sector: "Technology",
            Industry: "Software",
            Currency: "USD",
            CurrentPrice: 400m,
            LastClose: 395m
        )
        {
            SharesOutstanding = 2_000_000m,
            FloatShares = 1_800_000m,
            BookValue = 50m,
            DividendRate = 8m,
            TrailingEps = 10m,
            ForwardEps = 12m,
            FreeCashflow = 50_000_000m,
            FiftyTwoWeekHigh = 500m,
            FiftyTwoWeekLow = 300m,
            FullTimeEmployees = 100_000,
            OperatingCashflow = 80_000_000m,
            TotalCash = 200_000_000m,
            TotalDebt = 50_000_000m
        };

        var candles = new List<CandleData>
        {
            new CandleData { Timestamp = new DateTime(2026, 1, 1), Open = 390m, High = 398m, Low = 388m, Close = 395m, Volume = 50_000 },
            new CandleData { Timestamp = new DateTime(2026, 1, 2), Open = 396m, High = 405m, Low = 394m, Close = 400m, Volume = 60_000 }
        };

        var row = TickerRowCalculator.Compute("MSFT", meta, candles);

        Assert.Equal("MSFT", row.Symbol);
        Assert.Equal(400m, row.Close);
        Assert.Equal(396m, row.Open);
        Assert.Equal(405m, row.High);
        Assert.Equal(394m, row.Low);
        Assert.Equal(60_000L, row.Volume);
        Assert.Equal(5m, row.Change); // 400 - 395
        Assert.Equal((double)(5m / 395m * 100m), row.ChangePercent, precision: 4);

        // Turnovers
        Assert.Equal((60_000m / 2_000_000m) * 100m, row.DailyTurnoverRate);
        Assert.Equal((60_000m / 1_800_000m) * 100m, row.DailyFloatShareTurnoverRatio);

        // 13 price-dependent metrics
        decimal close = 400m;
        decimal marketCap = close * 2_000_000m;
        Assert.Equal(marketCap, row.MarketCap);
        Assert.Equal(close / 50m, row.PbrCalculated);
        Assert.Equal((8m / close) * 100m, row.DividendYieldCalculated);
        Assert.Equal((10m / close) * 100m, row.EarningsYield);
        Assert.Equal((50_000_000m / marketCap) * 100m, row.FcfYield);
        Assert.Equal(((close / 500m) - 1m) * 100m, row.PctFromFiftyTwoWeekHigh);
        Assert.Equal(marketCap / 100_000m, row.MarketCapPerEmployee);
        Assert.Equal((80_000_000m / marketCap) * 100m, row.OperatingCashFlowYield);
        Assert.Equal(((200_000_000m - 50_000_000m) / marketCap) * 100m, row.NetCashRatio);
        Assert.Equal(marketCap / 80_000_000m, row.PriceToCashFlowRatio);
        Assert.Equal((close - 300m) / (500m - 300m), row.FiftyTwoWeekRangePosition);
        Assert.Equal(close / 10m, row.TrailingPE);
        Assert.Equal(close / 12m, row.ForwardPE);
    }

    [Fact]
    public void Compute_ZeroDivisorEdgeCases_DoesNotThrowAndLeavesNull()
    {
        var meta = new TickerMetadata(
            ShortName: "Zero Divisor",
            LongName: "Zero Divisor Corp",
            Region: "US",
            Sector: "Tech",
            Industry: "Test",
            Currency: "USD"
        )
        {
            SharesOutstanding = 0m,
            BookValue = 0m,
            FiftyTwoWeekHigh = 0m,
            FiftyTwoWeekLow = 100m, // 52W High=0, range is negative or invalid
            FullTimeEmployees = 0,
            TrailingEps = 0m,
            ForwardEps = 0m
        };

        var candles = new List<CandleData>
        {
            new CandleData { Timestamp = DateTime.UtcNow, Open = 10m, High = 10m, Low = 10m, Close = 10m, Volume = 1000 }
        };

        var row = TickerRowCalculator.Compute("ZERO", meta, candles);

        Assert.Null(row.MarketCap);
        Assert.Null(row.PbrCalculated);
        Assert.Null(row.PctFromFiftyTwoWeekHigh);
        Assert.Null(row.MarketCapPerEmployee);
        Assert.Null(row.TrailingPE);
        Assert.Null(row.ForwardPE);
        Assert.Null(row.DailyTurnoverRate);
    }

    [Fact]
    public void TickerRow_GetPropertyValue_ResolvesCanonicalNamesAndAliases()
    {
        var row = new TickerRow
        {
            Symbol = "AAPL",
            Close = 150m,
            ReturnOnAssets = 12.5m,
            FiftyTwoWeekHigh = 200m,
            ChangePercent = 2.5,
            ExitLong = 175m,
            Ebitda = 500m
        };

        // Canonical property names
        Assert.Equal("AAPL", row.GetPropertyValue("Symbol"));
        Assert.Equal(150m, row.GetPropertyValue("Close"));
        Assert.Equal(12.5m, row.GetPropertyValue("ReturnOnAssets"));
        Assert.Equal(200m, row.GetPropertyValue("FiftyTwoWeekHigh"));
        Assert.Equal(2.5, row.GetPropertyValue("ChangePercent"));
        Assert.Equal(175m, row.GetPropertyValue("ExitLong"));
        Assert.Equal(500m, row.GetPropertyValue("Ebitda"));

        // Case-insensitive
        Assert.Equal(150m, row.GetPropertyValue("close"));
        Assert.Equal(12.5m, row.GetPropertyValue("returnonassets"));

        // Aliases
        Assert.Equal(12.5m, row.GetPropertyValue("ROA"));
        Assert.Equal(200m, row.GetPropertyValue("52W High"));
        Assert.Equal(200m, row.GetPropertyValue("52-Week High"));
        Assert.Equal(2.5, row.GetPropertyValue("Change %"));
        Assert.Equal(175m, row.GetPropertyValue("Take Profit Long"));
        Assert.Equal(175m, row.GetPropertyValue("TP Long"));
        Assert.Equal(175m, row.GetPropertyValue("TargetPrice"));
        Assert.Equal(500m, row.GetPropertyValue("EBITDA"));

        // Unknown property
        Assert.Null(row.GetPropertyValue("NonExistentProperty"));
    }

    [Fact]
    public void Compute_WhenMetadataCausesDecimalOverflow_ReturnsNullForOverflowedMetricWithoutThrowing()
    {
        var meta = new TickerMetadata(
            ShortName: "Overflow Corp",
            LongName: "Overflow Test Corp",
            Region: "US",
            Sector: "Tech",
            Industry: "Test",
            Currency: "USD"
        )
        {
            SharesOutstanding = decimal.MaxValue,
            BookValue = 1e-28m,
            TrailingEps = 1e-28m,
            ForwardEps = 1e-28m,
            FullTimeEmployees = 1,
            FiftyTwoWeekHigh = 1e-28m,
            FiftyTwoWeekLow = 0m,
            DividendRate = decimal.MaxValue,
            FreeCashflow = decimal.MaxValue,
            OperatingCashflow = decimal.MaxValue,
            TotalCash = decimal.MaxValue,
            TotalDebt = 0m
        };

        var candles = new List<CandleData>
        {
            new CandleData { Timestamp = DateTime.UtcNow, Open = 10m, High = 10m, Low = 10m, Close = 10m, Volume = 1000 }
        };

        var row = TickerRowCalculator.Compute("OVERFLOW", meta, candles);

        Assert.Equal("OVERFLOW", row.Symbol);
        Assert.Null(row.MarketCap);
        Assert.Null(row.PbrCalculated);
        Assert.Null(row.TrailingPE);
        Assert.Null(row.ForwardPE);
        Assert.Null(row.DividendYieldCalculated);
        Assert.Null(row.PctFromFiftyTwoWeekHigh);
    }

    [Fact]
    public void Compute_WhenLastCloseDifferenceCausesDecimalOverflow_HandlesGracefullyWithoutThrowing()
    {
        var meta = new TickerMetadata(
            ShortName: "LastClose Overflow",
            LongName: "LastClose Overflow Corp",
            Region: "US",
            Sector: "Tech",
            Industry: "Test",
            Currency: "USD",
            CurrentPrice: decimal.MaxValue,
            LastClose: 1e-28m
        );

        var row = TickerRowCalculator.Compute("OFLAST", meta, null);

        Assert.Equal("OFLAST", row.Symbol);
        Assert.Equal(0m, row.Change);
        Assert.Equal(0.0, row.ChangePercent);
    }

    [Theory]
    [InlineData("Close", true)]
    [InlineData("close", true)]
    [InlineData("Open", true)]
    [InlineData("High", true)]
    [InlineData("Low", true)]
    [InlineData("Volume", true)]
    [InlineData("Change", true)]
    [InlineData("ChangePercent", true)]
    [InlineData("Change %", true)]
    [InlineData("MarketCap", true)]
    [InlineData("Market Cap", true)]
    [InlineData("PbrCalculated", true)]
    [InlineData("P/B (Calculated)", true)]
    [InlineData("TrailingPE", true)]
    [InlineData("Trailing P/E", true)]
    [InlineData("DailyTurnoverRate", true)]
    [InlineData("Daily Turnover %", true)]
    [InlineData("Sector", false)]
    [InlineData("Industry", false)]
    [InlineData("Tag", false)]
    [InlineData("ReturnOnEquity", false)]
    [InlineData("ROE", false)]
    [InlineData("DebtToEquity", false)]
    [InlineData("D/E Ratio", false)]
    [InlineData("NonExistentField", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TickerRow_RequiresCandles_CorrectlyClassifiesPropertiesAndAliases(string? propertyName, bool expected)
    {
        bool actual = TickerRow.RequiresCandles(propertyName);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SSoT_DelegationParity_BetweenCalculatorsAndExtractor()
    {
        var meta = new TickerMetadata(
            ShortName: "Parity Corp",
            LongName: "Parity Corporation",
            Region: "US",
            Sector: "Technology",
            Industry: "Software",
            Currency: "USD",
            CurrentPrice: 100m,
            LastClose: 95m)
        {
            SharesOutstanding = 1_000_000m,
            BookValue = 25m,
            TotalDebt = 20_000_000m,
            TotalCash = 50_000_000m,
            Ebitda = 10_000_000m,
            FiftyTwoWeekHigh = 120m,
            FiftyTwoWeekLow = 80m,
            FullTimeEmployees = 500
        };

        var candles = new List<CandleData>
        {
            new CandleData { Timestamp = new DateTime(2026, 1, 1), Open = 98m, High = 102m, Low = 97m, Close = 100m, Volume = 50_000 }
        };

        var derivedMeta = FundamentalsCalculator.CalculateDerived(meta, candles);
        var row = TickerRowCalculator.Compute("PARITY", meta, candles);

        // Assert NetCashRatio parity across FundamentalsCalculator and TickerRowCalculator
        Assert.NotNull(derivedMeta.NetCashRatio);
        Assert.Equal(derivedMeta.NetCashRatio, row.NetCashRatio);
        Assert.Equal(30m, row.NetCashRatio); // (50M - 20M) / (100 * 1M) * 100 = 30M / 100M * 100 = 30%

        // Assert ScreenerValueExtractor fallback returns exact SSoT value when derived property is null
        var metaWithoutDerived = new TickerMetadata("RAW", "Raw", "US", "Tech", "Software", "USD", CurrentPrice: 100m)
        {
            BookValue = 25m,
            TotalDebt = 20_000_000m,
            TotalCash = 50_000_000m,
            SharesOutstanding = 1_000_000m,
            Ebitda = 10_000_000m,
            FiftyTwoWeekHigh = 120m,
            FiftyTwoWeekLow = 80m,
            FullTimeEmployees = 500
        };

        var configPbr = new StockAnalyzer.Core.Models.Screener.ScreenerIndicatorSideConfig
        {
            CategoryType = StockAnalyzer.Core.Models.Screener.ScreenerItemCategoryType.Column,
            OutputName = "PbrCalculated"
        };
        var extractedPbr = StockAnalyzer.Core.Services.ScreenerValueExtractor.Default.ExtractValueNullable(configPbr, candles, metaWithoutDerived);
        Assert.Equal(4m, extractedPbr); // 100 / 25 = 4
        Assert.Equal(derivedMeta.PbrCalculated, extractedPbr);

        // Assert ScreenerValueExtractor fallback handles overflow gracefully without throwing
        var overflowMeta = new TickerMetadata("OFL", "Ofl", "US", "Tech", "Software", "USD", CurrentPrice: decimal.MaxValue)
        {
            BookValue = 1e-28m
        };
        var extractedOflPbr = StockAnalyzer.Core.Services.ScreenerValueExtractor.Default.ExtractValueNullable(configPbr, candles, overflowMeta);
        Assert.Null(extractedOflPbr);
    }

    [Fact]
    public void Compute_WithExtremeFiftyTwoWeekHighLow_DoesNotThrowOverflowException()
    {
        var meta = new TickerMetadata("EXTREME", "Extreme", "US", "Tech", "Software", "USD", CurrentPrice: 100m)
        {
            FiftyTwoWeekHigh = decimal.MaxValue,
            FiftyTwoWeekLow = decimal.MinValue,
            TotalDebt = decimal.MaxValue,
            TotalCash = decimal.MinValue,
            SharesOutstanding = 1_000_000m
        };

        var candles = new List<CandleData>
        {
            new CandleData { Timestamp = new DateTime(2026, 1, 1), Open = 98m, High = 102m, Low = 97m, Close = 100m, Volume = 50_000 }
        };

        // Act & Assert: Must not throw OverflowException on extreme high/low range or net cash calculation
        var row = TickerRowCalculator.Compute("EXTREME", meta, candles);

        Assert.NotNull(row);
        Assert.Null(row.FiftyTwoWeekRangePosition);
        Assert.Null(row.NetCashRatio);
    }
}
