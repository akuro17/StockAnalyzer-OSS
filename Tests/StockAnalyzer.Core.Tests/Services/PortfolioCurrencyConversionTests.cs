using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Pins how <see cref="PortfolioManager.Evaluate(Portfolio, IReadOnlyDictionary{string, decimal}, IReadOnlyDictionary{CurrencyCode, ExchangeRate}, CurrencyCode)"/>
/// converts holdings and realized PnL into the target currency. The rate dictionaries mirror what the portfolio screen builds
/// (PortfolioSummaryViewModel.GetLatestExchangeRates): key X = newest X-&gt;USD rate, key USD = inverse of the newest rate-bearing transaction.
/// </summary>
public sealed class PortfolioCurrencyConversionTests
{
    private static readonly DateTime AsOf = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTimeOffset Day(int daysAgo) => DateTimeOffset.UtcNow.AddDays(-daysAgo);

    private static ExchangeRate Rate(CurrencyCode from, CurrencyCode to, decimal rate) => new(from, to, rate, AsOf);

    private static Portfolio PortfolioWith(decimal jpyCash, params (string Ticker, CurrencyCode Currency)[] positions)
    {
        var map = new Dictionary<string, Position>();
        foreach (var (ticker, currency) in positions)
        {
            map[ticker] = new Position(ticker, 1m, 100m, false, new Money(100m, currency));
        }
        return new Portfolio(jpyCash, map, cashBalances: new Dictionary<CurrencyCode, decimal> { { CurrencyCode.JPY, jpyCash } });
    }

    private static Dictionary<string, decimal> Prices(params string[] tickers)
    {
        var prices = new Dictionary<string, decimal>();
        foreach (var t in tickers) prices[t] = 100m;
        return prices;
    }

    // History: JPY transaction (JPY->USD 0.0064), USD transaction, newest transaction is EUR (EUR->USD 1.08).
    private static Dictionary<CurrencyCode, ExchangeRate> RatesWithNewestEur() => new()
    {
        { CurrencyCode.EUR, Rate(CurrencyCode.EUR, CurrencyCode.USD, 1.08m) },
        { CurrencyCode.USD, Rate(CurrencyCode.EUR, CurrencyCode.USD, 1.08m).Inverse() },
        { CurrencyCode.JPY, Rate(CurrencyCode.JPY, CurrencyCode.USD, 0.0064m) },
    };

    [Fact]
    public void Evaluate_UsdHoldingWhenNewestRateIsEur_ConvertsViaUsdNotWithTheInverseEurRate()
    {
        var result = new PortfolioManager().Evaluate(PortfolioWith(10000m, ("AAPL", CurrencyCode.USD)), Prices("AAPL"), RatesWithNewestEur(), CurrencyCode.JPY);

        Assert.Equal(15625m, result.PositionValues["AAPL"]);   // 100 USD / 0.0064 (JPY->USD)
    }

    [Fact]
    public void Evaluate_EurHoldingWhenNewestRateIsEur_ConvertsViaUsdIntoJpy()
    {
        var result = new PortfolioManager().Evaluate(PortfolioWith(10000m, ("SAP", CurrencyCode.EUR)), Prices("SAP"), RatesWithNewestEur(), CurrencyCode.JPY);

        Assert.Equal(16875m, result.PositionValues["SAP"]);    // 100 EUR x 1.08 / 0.0064
        Assert.Equal(26875m, result.Metrics.TotalValue);
    }

    [Fact]
    public void Evaluate_UsdOnlyHoldingsWithoutAnyRate_KeepsTheNativeAmounts()
    {
        var portfolio = new Portfolio(
            0m,
            new Dictionary<string, Position> { { "AAPL", new Position("AAPL", 1m, 100m, false, new Money(100m, CurrencyCode.USD)) } },
            cashBalances: new Dictionary<CurrencyCode, decimal> { { CurrencyCode.JPY, 0m }, { CurrencyCode.USD, 1000m } });

        var result = new PortfolioManager().Evaluate(portfolio, Prices("AAPL"), new Dictionary<CurrencyCode, ExchangeRate>(), CurrencyCode.JPY);

        Assert.Equal(100m, result.PositionValues["AAPL"]);
        Assert.Equal(1000m, result.Metrics.CashBalance);
        Assert.Equal(1100m, result.Metrics.TotalValue);
    }

    [Fact]
    public void Evaluate_JpyHoldingsWithDirectUsdRate_AreUnchanged()
    {
        var rates = new Dictionary<CurrencyCode, ExchangeRate> { { CurrencyCode.USD, Rate(CurrencyCode.USD, CurrencyCode.JPY, 150m) } };
        var portfolio = new Portfolio(
            50000m,
            new Dictionary<string, Position>
            {
                { "7203", new Position("7203", 10m, 100m, false, new Money(100m, CurrencyCode.JPY)) },
                { "AAPL", new Position("AAPL", 1m, 100m, false, new Money(100m, CurrencyCode.USD)) },
            },
            cashBalances: new Dictionary<CurrencyCode, decimal> { { CurrencyCode.JPY, 50000m }, { CurrencyCode.USD, 10m } });

        var result = new PortfolioManager().Evaluate(portfolio, new Dictionary<string, decimal> { { "7203", 120m }, { "AAPL", 110m } }, rates, CurrencyCode.JPY);

        Assert.Equal(1200m, result.PositionValues["7203"]);
        Assert.Equal(16500m, result.PositionValues["AAPL"]);   // 110 USD x 150
        Assert.Equal(51500m, result.Metrics.CashBalance);      // 50,000 + 10 USD x 150
        Assert.Equal(69200m, result.Metrics.TotalValue);
    }

    [Fact]
    public void Evaluate_SingleForeignCurrencyWithoutRate_ReportsNothingUnconverted()
    {
        var portfolio = new Portfolio(
            0m,
            new Dictionary<string, Position> { { "AAPL", new Position("AAPL", 1m, 100m, false, new Money(100m, CurrencyCode.USD)) } },
            cashBalances: new Dictionary<CurrencyCode, decimal> { { CurrencyCode.JPY, 0m }, { CurrencyCode.USD, 1000m } });

        var result = new PortfolioManager().Evaluate(portfolio, Prices("AAPL"), new Dictionary<CurrencyCode, ExchangeRate>(), CurrencyCode.JPY);

        Assert.Null(result.UnconvertedCurrencies);
    }

    // The portfolio screen evaluates a ticker node on a rebuilt portfolio of "all deposits and withdrawals + that ticker's transactions"
    // (PortfolioSummaryViewModel.EvaluateAndSelectNodeAsync). The single-currency rule looks at the evaluated portfolio, so these tests pin
    // that the aggregate node and a ticker node agree for realistic histories, because the deposits stay in every node.
    private static List<Transaction> UsdAndEurHistory(decimal eurDeposit) => new()
    {
        new(Day(9), TransactionType.Deposit, null, 0m, 0m, 1000m, price: new Money(0m, CurrencyCode.USD)),
        new(Day(8), TransactionType.Deposit, null, 0m, 0m, eurDeposit, price: new Money(0m, CurrencyCode.EUR)),
        new(Day(7), TransactionType.Long, "AAPL", 1m, 100m, 0m, price: new Money(100m, CurrencyCode.USD)),
        new(Day(6), TransactionType.Long, "SAP", 1m, 100m, 0m, price: new Money(100m, CurrencyCode.EUR)),
    };

    private static List<Transaction> TickerNodeHistory(IEnumerable<Transaction> all, string ticker)
        => all.Where(t => t.Type == TransactionType.Deposit
                          || t.Type == TransactionType.Withdrawal
                          || string.Equals(t.Ticker, ticker, StringComparison.OrdinalIgnoreCase)).ToList();

    [Theory]
    [InlineData(1000)]   // EUR cash is left over after buying SAP
    [InlineData(100)]    // the EUR deposit is spent exactly on SAP: EUR cash is zero in the aggregate, not in the AAPL node
    public void Evaluate_AggregateAndTickerNode_AgreeOnWhatIsUnconverted(int eurDeposit)
    {
        var manager = new PortfolioManager();
        var history = UsdAndEurHistory(eurDeposit);
        var rates = new Dictionary<CurrencyCode, ExchangeRate>
        {
            { CurrencyCode.EUR, Rate(CurrencyCode.EUR, CurrencyCode.USD, 1.08m) },
            { CurrencyCode.USD, Rate(CurrencyCode.EUR, CurrencyCode.USD, 1.08m).Inverse() },
        };

        var aggregate = manager.Evaluate(manager.RebuildPortfolio(0m, history), Prices("AAPL", "SAP"), rates, CurrencyCode.JPY);
        var tickerNode = manager.Evaluate(manager.RebuildPortfolio(0m, TickerNodeHistory(history, "AAPL")), Prices("AAPL", "SAP"), rates, CurrencyCode.JPY);

        Assert.Equal(new[] { CurrencyCode.EUR, CurrencyCode.USD }, aggregate.UnconvertedCurrencies);
        Assert.Equal(aggregate.UnconvertedCurrencies, tickerNode.UnconvertedCurrencies);
        Assert.DoesNotContain("AAPL", aggregate.PositionValues.Keys);
        Assert.DoesNotContain("AAPL", tickerNode.PositionValues.Keys);
    }

    [Fact]
    public void Evaluate_UsdOnlyHistory_AggregateAndTickerNodeBothKeepNativeAmounts()
    {
        var manager = new PortfolioManager();
        var history = new List<Transaction>
        {
            new(Day(9), TransactionType.Deposit, null, 0m, 0m, 1000m, price: new Money(0m, CurrencyCode.USD)),
            new(Day(8), TransactionType.Long, "AAPL", 1m, 100m, 0m, price: new Money(100m, CurrencyCode.USD)),
            new(Day(7), TransactionType.Long, "MSFT", 1m, 100m, 0m, price: new Money(100m, CurrencyCode.USD)),
        };
        var noRates = new Dictionary<CurrencyCode, ExchangeRate>();

        var aggregate = manager.Evaluate(manager.RebuildPortfolio(0m, history), Prices("AAPL", "MSFT"), noRates, CurrencyCode.JPY);
        var tickerNode = manager.Evaluate(manager.RebuildPortfolio(0m, TickerNodeHistory(history, "AAPL")), Prices("AAPL", "MSFT"), noRates, CurrencyCode.JPY);

        Assert.Null(aggregate.UnconvertedCurrencies);
        Assert.Null(tickerNode.UnconvertedCurrencies);
        Assert.Equal(100m, tickerNode.PositionValues["AAPL"]);
    }

    [Fact]
    public void Evaluate_UsdAndEurWithoutAJpyRate_AreLeftOutOfTheTotalsAndReported()
    {
        var rates = new Dictionary<CurrencyCode, ExchangeRate>
        {
            { CurrencyCode.EUR, Rate(CurrencyCode.EUR, CurrencyCode.USD, 1.08m) },
            { CurrencyCode.USD, Rate(CurrencyCode.EUR, CurrencyCode.USD, 1.08m).Inverse() },
        };
        var portfolio = PortfolioWith(5000m, ("AAPL", CurrencyCode.USD), ("SAP", CurrencyCode.EUR));

        var result = new PortfolioManager().Evaluate(portfolio, Prices("AAPL", "SAP"), rates, CurrencyCode.JPY);

        Assert.Equal(new[] { CurrencyCode.EUR, CurrencyCode.USD }, result.UnconvertedCurrencies);
        Assert.DoesNotContain("AAPL", result.PositionValues.Keys);
        Assert.DoesNotContain("SAP", result.PositionPLs.Keys);
        Assert.Equal(5000m, result.Metrics.TotalValue);
        Assert.Equal(100m, result.Metrics.CashRatio);
    }

    [Fact]
    public void Evaluate_AllHoldingsConvertible_ReportsNothingUnconverted()
    {
        var result = new PortfolioManager().Evaluate(PortfolioWith(10000m, ("AAPL", CurrencyCode.USD)), Prices("AAPL"), RatesWithNewestEur(), CurrencyCode.JPY);

        Assert.Null(result.UnconvertedCurrencies);
    }

    private static List<Transaction> JpyRoundTrip(decimal exitRate) => new()
    {
        new(Day(5), TransactionType.Deposit, null, 0m, 0m, 200000m),
        new(Day(4), TransactionType.Long, "7203", 100m, 1000m, 0m),
        new(Day(3), TransactionType.ExitLong, "7203", 100m, 1100m, 0m, appliedRate: Rate(CurrencyCode.JPY, CurrencyCode.USD, exitRate)),
    };

    [Fact]
    public void Evaluate_JpyOnlyRealizedPnl_IsNotRevaluedWithTheLatestFxRate()
    {
        var manager = new PortfolioManager();
        var portfolio = manager.RebuildPortfolio(0m, JpyRoundTrip(0.0067m));
        var latestRates = new Dictionary<CurrencyCode, ExchangeRate>
        {
            { CurrencyCode.JPY, Rate(CurrencyCode.JPY, CurrencyCode.USD, 0.0064m) },
            { CurrencyCode.USD, Rate(CurrencyCode.JPY, CurrencyCode.USD, 0.0064m).Inverse() },
        };

        var result = manager.Evaluate(portfolio, new Dictionary<string, decimal>(), latestRates, CurrencyCode.JPY);

        Assert.Equal(10000m, result.Metrics.TotalRealizedPL);   // the old path gave 10,468.75 (exit-time USD booking revalued at the latest rate)
    }

    [Fact]
    public void Evaluate_MixedJpyAndUsdRealizedPnl_ConvertsEachCurrencyAtTheLatestRate()
    {
        var manager = new PortfolioManager();
        var history = new List<Transaction>
        {
            new(Day(9), TransactionType.Deposit, null, 0m, 0m, 200000m),
            new(Day(8), TransactionType.Deposit, null, 0m, 0m, 1000m, price: new Money(0m, CurrencyCode.USD)),
            new(Day(7), TransactionType.Long, "7203", 100m, 1000m, 0m),
            new(Day(6), TransactionType.Long, "AAPL", 1m, 100m, 0m, price: new Money(100m, CurrencyCode.USD)),
            new(Day(5), TransactionType.ExitLong, "7203", 100m, 1100m, 0m, appliedRate: Rate(CurrencyCode.JPY, CurrencyCode.USD, 0.0067m)),
            new(Day(4), TransactionType.ExitLong, "AAPL", 1m, 200m, 0m, price: new Money(200m, CurrencyCode.USD)),
        };
        var portfolio = manager.RebuildPortfolio(0m, history);
        var latestRates = new Dictionary<CurrencyCode, ExchangeRate>
        {
            { CurrencyCode.USD, Rate(CurrencyCode.USD, CurrencyCode.JPY, 156.25m) },
            { CurrencyCode.JPY, Rate(CurrencyCode.JPY, CurrencyCode.USD, 0.0064m) },
        };

        var result = manager.Evaluate(portfolio, new Dictionary<string, decimal>(), latestRates, CurrencyCode.JPY);

        Assert.Equal(25625m, result.Metrics.TotalRealizedPL);   // 10,000 JPY + 100 USD x 156.25
    }
}
