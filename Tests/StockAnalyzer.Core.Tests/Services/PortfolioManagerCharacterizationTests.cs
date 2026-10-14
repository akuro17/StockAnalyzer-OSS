using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Characterization tests: they pin what <see cref="PortfolioManager"/>, <see cref="Transaction"/>, <see cref="Position"/> and <see cref="Portfolio"/> do today
/// in the places where the specification is silent, so that a later change is a conscious one and not a side effect. They are not a statement that the
/// behavior is the specified one.
/// <list type="bullet">
/// <item>Zero quantity. The Transaction constructor accepts it for every type (Deposit and Withdrawal need it) and the UI refuses to save a buy or sell with a
/// quantity of 0 or less. A Long of quantity 0 that would open a position is stopped by the Position invariant (quantity greater than 0), so the division by
/// the summed quantity in PortfolioManager is never reached with a zero total. The Transaction constructor was left alone on purpose: the loader turns any
/// exception into an empty portfolio (UserPortfolioRepository.LoadPortfolioAsync).</item>
/// <item>The default currency of the commission is JPY even when the price is in another currency. The production callers always pass the commission with its currency.</item>
/// <item>A negative JPY cash balance is refused by the Portfolio invariant when the portfolio is built; a foreign-currency balance is not checked.</item>
/// </list>
/// </summary>
public sealed class PortfolioManagerCharacterizationTests
{
    private static DateTimeOffset Day(int daysAgo) => DateTimeOffset.UtcNow.AddDays(-daysAgo);

    [Fact]
    public void RebuildPortfolio_ZeroQuantityLongThatWouldOpenAPosition_IsStoppedByThePositionInvariant()
    {
        var transactions = new List<Transaction>
        {
            new(Day(2), TransactionType.Deposit, null, 0m, 0m, 1000m),
            new(Day(1), TransactionType.Long, "AAPL", 0m, 10m, 0m),
        };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioManager().RebuildPortfolio(0m, transactions));

        Assert.Equal("quantity", error.ParamName);
    }

    [Fact]
    public void RebuildPortfolio_TwoZeroQuantityLongsOnOneTicker_NeverReachTheDivisionByAZeroTotal()
    {
        var transactions = new List<Transaction>
        {
            new(Day(3), TransactionType.Deposit, null, 0m, 0m, 1000m),
            new(Day(2), TransactionType.Long, "AAPL", 0m, 10m, 0m),
            new(Day(1), TransactionType.Long, "AAPL", 0m, 10m, 0m),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioManager().RebuildPortfolio(0m, transactions));
    }

    [Fact]
    public void RebuildPortfolio_ZeroQuantityLongOnAnExistingPosition_CostsOnlyTheCommissionAndKeepsThePosition()
    {
        var transactions = new List<Transaction>
        {
            new(Day(3), TransactionType.Deposit, null, 0m, 0m, 1000m),
            new(Day(2), TransactionType.Long, "AAPL", 5m, 10m, 0m),
            new(Day(1), TransactionType.Long, "AAPL", 0m, 10m, 0m, fee: 2m),
        };

        var portfolio = new PortfolioManager().RebuildPortfolio(0m, transactions);

        Assert.Equal(948m, portfolio.CashBalance);   // 1000 - 5 x 10 - 2
        Assert.Equal(5m, portfolio.Positions["AAPL"].Quantity);
        Assert.Equal(10m, portfolio.Positions["AAPL"].AverageCostPerUnit);
    }

    [Fact]
    public void Transaction_PriceInUsdWithoutAnExplicitCommission_HasAJpyCommission()
    {
        var transaction = new Transaction(Day(1), TransactionType.Long, "AAPL", 1m, 10m, 0m, fee: 5m, price: new Money(10m, CurrencyCode.USD));

        Assert.Equal(CurrencyCode.USD, transaction.Price.Currency);
        Assert.Equal(CurrencyCode.JPY, transaction.Commission.Currency);
        Assert.Equal(5m, transaction.Commission.Amount);
    }

    [Fact]
    public void RebuildPortfolio_UsdLongWithAJpyDefaultCommission_SubtractsTheCommissionAmountFromTheUsdBalance()
    {
        var transactions = new List<Transaction>
        {
            new(Day(2), TransactionType.Deposit, null, 0m, 0m, 1000m, price: new Money(0m, CurrencyCode.USD)),
            new(Day(1), TransactionType.Long, "AAPL", 1m, 10m, 0m, fee: 5m, price: new Money(10m, CurrencyCode.USD)),
        };

        var portfolio = new PortfolioManager().RebuildPortfolio(0m, transactions);

        Assert.Equal(985m, portfolio.CashBalances[CurrencyCode.USD]);   // 1000 - (1 x 10 + 5): the 5 is a JPY amount subtracted from USD
    }

    [Fact]
    public void RebuildPortfolio_JpyLongBeyondTheCash_IsRefusedByThePortfolioInvariant()
    {
        var transactions = new List<Transaction>
        {
            new(Day(1), TransactionType.Long, "AAPL", 1m, 10m, 0m),
        };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioManager().RebuildPortfolio(0m, transactions));

        Assert.Equal("cashBalance", error.ParamName);
    }

    [Fact]
    public void RebuildPortfolio_UsdLongBeyondTheUsdCash_LeavesANegativeUsdBalanceWithoutAnError()
    {
        var transactions = new List<Transaction>
        {
            new(Day(1), TransactionType.Long, "AAPL", 1m, 10m, 0m, price: new Money(10m, CurrencyCode.USD), commission: new Money(0m, CurrencyCode.USD)),
        };

        var portfolio = new PortfolioManager().RebuildPortfolio(0m, transactions);

        Assert.Equal(-10m, portfolio.CashBalances[CurrencyCode.USD]);
    }

    [Fact]
    public void RebuildPortfolio_WithdrawalBeyondTheBalance_IsRefused()
    {
        var transactions = new List<Transaction>
        {
            new(Day(2), TransactionType.Deposit, null, 0m, 0m, 100m),
            new(Day(1), TransactionType.Withdrawal, null, 0m, 0m, 150m),
        };

        Assert.Throws<InvalidOperationException>(() => new PortfolioManager().RebuildPortfolio(0m, transactions));
    }
}
