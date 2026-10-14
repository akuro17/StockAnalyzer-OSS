using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Characterization of the transaction part of the portfolio store: every field of every transaction, including the optional ones left empty and the
/// multi-currency ones, comes back unchanged and in the saved order. It pins the stored representation so that the way rows are written to the
/// temporary table can change (performance) without changing what is persisted.
/// </summary>
public sealed class UserPortfolioRepositoryTransactionRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_portfolio_tx_roundtrip_" + Guid.NewGuid().ToString("N"));

    public UserPortfolioRepositoryTransactionRoundTripTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static Transaction FullTransaction(DateTimeOffset executedAt, Guid id, Guid related) => new(
        executedAt, TransactionType.Long, "AAPL", 12.5m, 187.25m, -2340.625m, 5.5m, "note \"quoted\" \u65E5\u672C\u8A9E", 200m, 170m, id, related,
        new Money(187.25m, CurrencyCode.USD), new Money(5.5m, CurrencyCode.USD),
        new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 151.5m, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));

    private static Transaction MinimalTransaction(DateTimeOffset executedAt, Guid id) =>
        new(executedAt, TransactionType.Deposit, null, 0m, 0m, 10000m, 0m, null, null, null, id, null);

    [Fact]
    public async Task SaveAndLoad_EveryTransactionField_IsPreservedInOrder()
    {
        var first = new DateTimeOffset(2026, 3, 2, 9, 15, 30, 123, TimeSpan.Zero);
        var second = first.AddDays(1);
        var third = first.AddDays(2);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var original = new[]
        {
            MinimalTransaction(first, ids[0]),
            FullTransaction(second, ids[1], ids[0]),
            new Transaction(third, TransactionType.ExitLong, "AAPL", 12.5m, 190m, 2375m, 0m, string.Empty, null, null, ids[2], ids[1]),
        };
        var portfolio = new Portfolio(10000m, new Dictionary<string, Position>(), original);

        using var repository = new UserPortfolioRepository(Path.Combine(_root, "portfolio.json"));
        await repository.SavePortfolioAsync(portfolio);
        var loaded = (await repository.LoadPortfolioAsync()).History;

        Assert.Equal(original.Length, loaded.Count);
        for (int i = 0; i < original.Length; i++)
        {
            var expected = original[i];
            var actual = loaded[i];
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.ExecutedAt, actual.ExecutedAt);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Ticker, actual.Ticker);
            Assert.Equal(expected.Quantity, actual.Quantity);
            Assert.Equal(expected.PricePerUnit, actual.PricePerUnit);
            Assert.Equal(expected.CashAmount, actual.CashAmount);
            Assert.Equal(expected.Fee, actual.Fee);
            Assert.Equal(expected.Notes, actual.Notes);
            Assert.Equal(expected.TargetPrice, actual.TargetPrice);
            Assert.Equal(expected.StopLoss, actual.StopLoss);
            Assert.Equal(expected.RelatedTransactionId, actual.RelatedTransactionId);
            Assert.Equal(expected.Price, actual.Price);
            Assert.Equal(expected.Commission, actual.Commission);
            Assert.Equal(expected.AppliedRate.HasValue, actual.AppliedRate.HasValue);
            if (expected.AppliedRate.HasValue)
            {
                Assert.Equal(expected.AppliedRate.Value.BaseCurrency, actual.AppliedRate!.Value.BaseCurrency);
                Assert.Equal(expected.AppliedRate.Value.QuoteCurrency, actual.AppliedRate.Value.QuoteCurrency);
                Assert.Equal(expected.AppliedRate.Value.Rate, actual.AppliedRate.Value.Rate);
            }
        }
    }

    [Fact]
    public async Task SaveAndLoad_ManyTransactions_KeepsCountAndOrder()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-400);
        var original = Enumerable.Range(0, 300)
            .Select(i => MinimalTransaction(start.AddHours(i), Guid.NewGuid()))
            .ToList();

        using var repository = new UserPortfolioRepository(Path.Combine(_root, "portfolio.json"));
        await repository.SavePortfolioAsync(new Portfolio(10000m, new Dictionary<string, Position>(), original));
        var loaded = (await repository.LoadPortfolioAsync()).History;

        Assert.Equal(original.Select(t => t.Id), loaded.Select(t => t.Id));
    }

    [Fact]
    public async Task SaveAndLoad_NoTransactions_LoadsAnEmptyHistory()
    {
        using var repository = new UserPortfolioRepository(Path.Combine(_root, "portfolio.json"));
        await repository.SavePortfolioAsync(new Portfolio(5000m));

        Assert.Empty((await repository.LoadPortfolioAsync()).History);
    }
}
