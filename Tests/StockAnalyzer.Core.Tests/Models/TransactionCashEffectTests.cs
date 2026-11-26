using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// The displayed amount of a transaction must be the cash the engine books for it. Every case rebuilds a portfolio without and with the transaction
/// and compares the change of the balance in the transaction's currency with <see cref="TransactionCashEffect.GetSettlementAmount"/>.
/// </summary>
public sealed class TransactionCashEffectTests
{
    private const decimal InitialJpyCash = 1_000_000m;
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddDays(-10);

    private static Transaction At(int day, TransactionType type, string? ticker, decimal quantity, decimal price, decimal cash, decimal fee, CurrencyCode currency, CurrencyCode? commissionCurrency = null) =>
        new(Base.AddDays(day), type, ticker, quantity, price, cash, fee, null, null, null, Guid.NewGuid(), null,
            price: new Money(type is TransactionType.Deposit or TransactionType.Withdrawal ? cash : price, currency),
            commission: new Money(fee, commissionCurrency ?? currency));

    private static decimal Balance(IReadOnlyList<Transaction> history, CurrencyCode currency)
    {
        var portfolio = new PortfolioManager().RebuildPortfolio(InitialJpyCash, history);
        return portfolio.CashBalances.GetValueOrDefault(currency, 0m);
    }

    private static void AssertMatchesTheEngine(IReadOnlyList<Transaction> before, Transaction transaction)
    {
        var after = before.Concat(new[] { transaction }).ToList();
        decimal booked = Balance(after, transaction.Price.Currency) - Balance(before, transaction.Price.Currency);

        Assert.Equal(booked, TransactionCashEffect.GetSettlementAmount(transaction));
    }

    private static readonly Transaction UsdFunds = At(0, TransactionType.Deposit, null, 0m, 0m, 10_000m, 0m, CurrencyCode.USD);

    [Fact]
    public void Deposit_MatchesTheEngine() =>
        AssertMatchesTheEngine(new List<Transaction>(), At(1, TransactionType.Deposit, null, 0m, 0m, 500.25m, 0m, CurrencyCode.USD));

    [Fact]
    public void Withdrawal_MatchesTheEngine() =>
        AssertMatchesTheEngine(new List<Transaction> { UsdFunds }, At(1, TransactionType.Withdrawal, null, 0m, 0m, 200m, 0m, CurrencyCode.USD));

    [Fact]
    public void Long_MatchesTheEngine_WithAFractionalQuantityAndACommission() =>
        AssertMatchesTheEngine(new List<Transaction> { UsdFunds }, At(1, TransactionType.Long, "AAPL", 0.5m, 150.25m, 0m, 2.5m, CurrencyCode.USD));

    [Fact]
    public void Short_MatchesTheEngine() =>
        AssertMatchesTheEngine(new List<Transaction> { UsdFunds }, At(1, TransactionType.Short, "AAPL", 5m, 80m, 0m, 2m, CurrencyCode.USD));

    [Fact]
    public void ExitLong_MatchesTheEngine() =>
        AssertMatchesTheEngine(
            new List<Transaction> { UsdFunds, At(1, TransactionType.Long, "AAPL", 10m, 100m, 0m, 1m, CurrencyCode.USD) },
            At(2, TransactionType.ExitLong, "AAPL", 4m, 120m, 0m, 1.5m, CurrencyCode.USD));

    [Fact]
    public void ExitShort_MatchesTheEngine() =>
        AssertMatchesTheEngine(
            new List<Transaction> { UsdFunds, At(1, TransactionType.Short, "AAPL", 5m, 80m, 0m, 1m, CurrencyCode.USD) },
            At(2, TransactionType.ExitShort, "AAPL", 2m, 70m, 0m, 1m, CurrencyCode.USD));

    [Fact]
    public void Long_InJpy_MatchesTheEngine() =>
        AssertMatchesTheEngine(new List<Transaction>(), At(1, TransactionType.Long, "7203", 100m, 2100m, 0m, 110m, CurrencyCode.JPY));

    [Fact]
    public void Long_InUsdWithTheDefaultJpyCommission_MatchesTheEngine()
    {
        // The pinned legacy case: the commission amount is taken from the USD balance although its currency is JPY.
        AssertMatchesTheEngine(
            new List<Transaction> { UsdFunds },
            At(1, TransactionType.Long, "AAPL", 10m, 150m, 0m, 1m, CurrencyCode.USD, commissionCurrency: CurrencyCode.JPY));
    }

    [Fact]
    public void ATypeThatIsNoMember_HasNoAmount()
    {
        var transaction = At(1, (TransactionType)99, null, 0m, 0m, 0m, 0m, CurrencyCode.USD);

        Assert.Null(TransactionCashEffect.GetSettlementAmount(transaction));
    }

    [Fact]
    public void ANullTransaction_IsRejected() =>
        Assert.Throws<ArgumentNullException>(() => TransactionCashEffect.GetSettlementAmount(null!));
}
