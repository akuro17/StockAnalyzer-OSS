using System;
using System.IO;
using Xunit;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Tests.Models;

public class MultiCurrencyModelsTests
{
    [Fact]
    public void CurrencyCode_ShouldEnforceUppercaseThreeCharacters()
    {
        var code = new CurrencyCode("usd");
        Assert.Equal("USD", code.Value);

        Assert.Throws<ArgumentException>(() => new CurrencyCode("US"));
        Assert.Throws<ArgumentException>(() => new CurrencyCode("USDa"));
        Assert.Throws<ArgumentException>(() => new CurrencyCode(""));
    }

    [Fact]
    public void Money_AdditionSubtraction_ShouldSucceedForSameCurrency()
    {
        var usd10 = new Money(10m, CurrencyCode.USD);
        var usd5 = new Money(5m, CurrencyCode.USD);

        var sum = usd10 + usd5;
        Assert.Equal(15m, sum.Amount);
        Assert.Equal(CurrencyCode.USD, sum.Currency);

        var diff = usd10 - usd5;
        Assert.Equal(5m, diff.Amount);
        Assert.Equal(CurrencyCode.USD, diff.Currency);
    }

    [Fact]
    public void Money_AdditionSubtraction_ShouldThrowForDifferentCurrencies()
    {
        var usd10 = new Money(10m, CurrencyCode.USD);
        var jpy1000 = new Money(1000m, CurrencyCode.JPY);

        Assert.Throws<InvalidOperationException>(() => usd10 + jpy1000);
        Assert.Throws<InvalidOperationException>(() => usd10 - jpy1000);
    }

    [Fact]
    public void ExchangeRate_ShouldConvertCorrectly()
    {
        var rate = new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 150m, DateTime.UtcNow);
        var usd10 = new Money(10m, CurrencyCode.USD);

        var converted = rate.Convert(usd10);
        Assert.Equal(1500m, converted.Amount);
        Assert.Equal(CurrencyCode.JPY, converted.Currency);
    }

    [Fact]
    public void ExchangeRate_Convert_ShouldThrowIfCurrencyMismatched()
    {
        var rate = new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 150m, DateTime.UtcNow);
        var jpy1000 = new Money(1000m, CurrencyCode.JPY);

        Assert.Throws<InvalidOperationException>(() => rate.Convert(jpy1000));
    }

    [Fact]
    public void ExchangeRate_ShouldSupportInversion()
    {
        var rate = new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 200m, DateTime.UtcNow);
        var inverse = rate.Inverse();

        Assert.Equal(CurrencyCode.JPY, inverse.BaseCurrency);
        Assert.Equal(CurrencyCode.USD, inverse.QuoteCurrency);
        Assert.Equal(0.005m, inverse.Rate);
    }

    [Fact]
    public void ExchangeRate_ShouldThrowForInvalidRate()
    {
        Assert.Throws<ArgumentException>(() => new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 0m, DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, -1m, DateTime.UtcNow));
    }

    [Fact]
    public void Transaction_ShouldSupportMultiCurrencyProperties()
    {
        var usdPrice = new Money(180m, CurrencyCode.USD);
        var usdCommission = new Money(2m, CurrencyCode.USD);
        var rate = new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 150m, DateTime.UtcNow);

        var tx = new Transaction(
            DateTimeOffset.UtcNow,
            TransactionType.Buy,
            "AAPL",
            10m,
            180m,
            1800m,
            2m,
            price: usdPrice,
            commission: usdCommission,
            appliedRate: rate
        );

        Assert.Equal(usdPrice, tx.Price);
        Assert.Equal(usdCommission, tx.Commission);
        Assert.Equal(rate, tx.AppliedRate);

        // Fallback checks
        var fallbackTx = new Transaction(
            DateTimeOffset.UtcNow,
            TransactionType.Buy,
            "7203",
            100m,
            2000m,
            200000m,
            100m
        );
        Assert.Equal(new Money(2000m, CurrencyCode.JPY), fallbackTx.Price);
        Assert.Equal(new Money(100m, CurrencyCode.JPY), fallbackTx.Commission);
        Assert.Null(fallbackTx.AppliedRate);
    }

    [Fact]
    public void Position_ShouldSupportMultiCurrencyProperties()
    {
        var cost = new Money(180m, CurrencyCode.USD);
        var pos = new Position("AAPL", 10m, 180m, false, cost);

        Assert.Equal(cost, pos.AverageCost);

        var fallbackPos = new Position("7203", 100m, 2000m);
        Assert.Equal(new Money(2000m, CurrencyCode.JPY), fallbackPos.AverageCost);
    }

    [Fact]
    public void Portfolio_ShouldSupportMultiCurrencyProperties()
    {
        var balances = new Dictionary<CurrencyCode, decimal>
        {
            { CurrencyCode.JPY, 100000m },
            { CurrencyCode.USD, 1500m }
        };

        var portfolio = new Portfolio(
            cashBalance: 100000m,
            cashBalances: balances
        );

        Assert.Equal(balances, portfolio.CashBalances);
        Assert.Equal(100000m, portfolio.CashBalance);

        var fallbackPortfolio = new Portfolio(50000m);
        Assert.Equal(50000m, fallbackPortfolio.CashBalance);
        Assert.Equal(50000m, fallbackPortfolio.CashBalances[CurrencyCode.JPY]);
    }

    [Fact]
    public void PortfolioManager_ShouldProcessMultiCurrencyTransactions()
    {
        var manager = new PortfolioManager();

        var txs = new List<Transaction>
        {
            new Transaction(
                DateTimeOffset.UtcNow.AddMinutes(-10),
                TransactionType.Deposit,
                null,
                0,
                0,
                1000m,
                price: new Money(1000m, CurrencyCode.USD)
            ),
            new Transaction(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                TransactionType.Buy,
                "AAPL",
                5m,
                150m,
                750m,
                5m,
                price: new Money(150m, CurrencyCode.USD),
                commission: new Money(5m, CurrencyCode.USD)
            )
        };

        var portfolio = manager.RebuildPortfolio(100000m, txs);

        // USD cash: 1000 - (5 * 150 + 5) = 245 USD
        Assert.Equal(245m, portfolio.CashBalances[CurrencyCode.USD]);
        // JPY cash: Initial 100000 JPY is untouched
        Assert.Equal(100000m, portfolio.CashBalance);
        Assert.Equal(100000m, portfolio.CashBalances[CurrencyCode.JPY]);

        // AAPL Position cost: 150 USD avg
        Assert.True(portfolio.Positions.ContainsKey("AAPL"));
        Assert.Equal(5m, portfolio.Positions["AAPL"].Quantity);
        Assert.Equal(new Money(150m, CurrencyCode.USD), portfolio.Positions["AAPL"].AverageCost);
    }

    [Fact]
    public void PortfolioManager_ShouldEvaluateMultiCurrencyPortfolio()
    {
        var manager = new PortfolioManager();

        var balances = new Dictionary<CurrencyCode, decimal>
        {
            { CurrencyCode.JPY, 50000m },
            { CurrencyCode.USD, 100m }
        };

        var positions = new Dictionary<string, Position>
        {
            { "AAPL", new Position("AAPL", 10m, 150m, false, new Money(150m, CurrencyCode.USD)) }
        };

        var portfolio = new Portfolio(
            cashBalance: 50000m,
            positions: positions,
            cashBalances: balances
        );

        // Apple current price is 160 USD
        var prices = new Dictionary<string, decimal> { { "AAPL", 160m } };
        
        // Rate: 1 USD = 150 JPY
        var rates = new Dictionary<CurrencyCode, ExchangeRate>
        {
            { CurrencyCode.USD, new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 150m, DateTime.UtcNow) }
        };

        var result = manager.Evaluate(portfolio, prices, rates, CurrencyCode.JPY);

        // AAPL Value local: 10 * 160 = 1600 USD.
        // AAPL Value in JPY: 1600 * 150 = 240,000 JPY
        Assert.Equal(240000m, result.PositionValues["AAPL"]);

        // AAPL PL local: 10 * (160 - 150) = 100 USD.
        // AAPL PL in JPY: 100 * 150 = 15,000 JPY
        Assert.Equal(15000m, result.PositionPLs["AAPL"]);

        // Cash USD in JPY: 100 * 150 = 15,000 JPY
        // Total cash: 50,000 JPY + 15,000 JPY = 65,000 JPY
        Assert.Equal(65000m, result.Metrics.CashBalance);

        // Total Value JPY: 65,000 JPY + 240,000 JPY = 305,000 JPY
        Assert.Equal(305000m, result.Metrics.TotalValue);

        // Total Unrealized PL JPY: 15,000 JPY
        Assert.Equal(15000m, result.Metrics.TotalUnrealizedPL);
    }

    [Fact]
    public async Task UserPortfolioRepository_ShouldSaveAndLoadMultiCurrencyPortfolio()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var repo = new UserPortfolioRepository(tempDir);

            var balances = new Dictionary<CurrencyCode, decimal>
            {
                { CurrencyCode.JPY, 50000m },
                { CurrencyCode.USD, 100m }
            };

            var positions = new Dictionary<string, Position>
            {
                { "AAPL", new Position("AAPL", 10m, 150m, false, new Money(150m, CurrencyCode.USD)) }
            };

            var txs = new List<Transaction>
            {
                new Transaction(
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    TransactionType.Buy,
                    "AAPL",
                    10m,
                    150m,
                    1500m,
                    5m,
                    price: new Money(150m, CurrencyCode.USD),
                    commission: new Money(5m, CurrencyCode.USD),
                    appliedRate: new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 150m, DateTime.UtcNow)
                )
            };

            var portfolio = new Portfolio(
                cashBalance: 50000m,
                positions: positions,
                history: txs,
                cashBalances: balances
            );

            // Save
            await repo.SavePortfolioAsync(portfolio);

            // Load
            var loaded = await repo.LoadPortfolioAsync();

            // Verify
            Assert.Equal(50000m, loaded.CashBalance);
            Assert.Equal(100m, loaded.CashBalances[CurrencyCode.USD]);
            Assert.Equal(50000m, loaded.CashBalances[CurrencyCode.JPY]);

            Assert.True(loaded.Positions.ContainsKey("AAPL"));
            Assert.Equal(10m, loaded.Positions["AAPL"].Quantity);
            Assert.Equal(new Money(150m, CurrencyCode.USD), loaded.Positions["AAPL"].AverageCost);

            Assert.Single(loaded.History);
            Assert.Equal(new Money(150m, CurrencyCode.USD), loaded.History[0].Price);
            Assert.Equal(new Money(5m, CurrencyCode.USD), loaded.History[0].Commission);
            Assert.NotNull(loaded.History[0].AppliedRate);
            Assert.Equal(150m, loaded.History[0].AppliedRate.Value.Rate);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void PortfolioManager_ShouldEvaluateMultiCurrencyRealizedPnL()
    {
        var manager = new PortfolioManager();

        var entryTx = new Transaction(
            DateTimeOffset.UtcNow.AddMinutes(-10), TransactionType.Buy, "7203", 100m, 2000m, 200000m, 100m,
            price: new Money(2000m, CurrencyCode.JPY), commission: new Money(100m, CurrencyCode.JPY)
        );
        
        // JPY to USD rate at exit is 0.0067 (1 JPY = 0.0067 USD)
        var rate = new ExchangeRate(CurrencyCode.JPY, CurrencyCode.USD, 0.0067m, DateTime.UtcNow);
        var exitTx = new Transaction(
            DateTimeOffset.UtcNow.AddMinutes(-5), TransactionType.ExitLong, "7203", 100m, 2100m, 210000m, 100m,
            price: new Money(2100m, CurrencyCode.JPY), commission: new Money(100m, CurrencyCode.JPY),
            appliedRate: rate
        );

        // pnl local = 100 * (2100 - 2000) - 100 (entry fee) - 100 (exit fee) = 10000 - 200 = 9800 JPY
        // pnl in base (USD) = 9800 * 0.0067 = 65.66 USD
        
        var portfolio = manager.RebuildPortfolio(300000m, new List<Transaction> { entryTx, exitTx });
        
        // TotalRealizedPnL should be accumulated in USD
        Assert.Equal(65.66m, portfolio.TotalRealizedPnL);

        // Now evaluate in JPY base currency with USD to JPY rate as 150 (1 USD = 150 JPY)
        var latestPrices = new Dictionary<string, decimal> { { "7203", 2100m } };
        var latestRates = new Dictionary<CurrencyCode, ExchangeRate>
        {
            { CurrencyCode.USD, new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 150m, DateTime.UtcNow) }
        };

        var result = manager.Evaluate(portfolio, latestPrices, latestRates, CurrencyCode.JPY);

        // Realized PL in JPY = 65.66 * 150 = 9849 JPY
        Assert.Equal(9849m, result.Metrics.TotalRealizedPL);
    }
}
