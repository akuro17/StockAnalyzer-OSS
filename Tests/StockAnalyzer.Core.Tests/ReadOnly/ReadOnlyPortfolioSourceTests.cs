using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

/// <summary>
/// <see cref="FileReadOnlyPortfolioSource"/>: the saved portfolio as the application shows it, read without recovering, creating or writing anything.
/// </summary>
public sealed class ReadOnlyPortfolioSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_ro_portfolio_" + Guid.NewGuid().ToString("N"));
    private readonly DuckDBConnectionManager _database = new();

    private string PortfolioDirectory => Path.Combine(_root, "Portfolios");

    public ReadOnlyPortfolioSourceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static readonly DateTimeOffset Day1 = new(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private static List<Transaction> Transactions() => new()
    {
        new Transaction(Day1, TransactionType.Deposit, null, 0m, 0m, 1_000_000m),
        new Transaction(Day1.AddDays(1), TransactionType.Long, "MSFT", 10m, 100m, 0m),
        new Transaction(Day1.AddDays(2), TransactionType.Long, "7203-T", 5m, 2000m, 0m),
        new Transaction(Day1.AddDays(3), TransactionType.ExitLong, "MSFT", 4m, 130m, 0m),
    };

    private async Task SaveAsync()
    {
        Portfolio portfolio = new PortfolioManager().RebuildPortfolio(0m, Transactions());
        using var repository = new UserPortfolioRepository(_root);
        await repository.SavePortfolioAsync(portfolio);
    }

    private Dictionary<string, byte[]> Snapshot()
        => Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes)
            : new Dictionary<string, byte[]>();

    [Fact]
    public async Task NoPortfolioFolder_IsFoundFalseAndNothingIsCreated()
    {
        var source = new FileReadOnlyPortfolioSource(PortfolioDirectory, _database);

        PortfolioReadResult result = await source.LoadAsync();

        Assert.False(result.Found);
        Assert.Empty(result.Portfolio.Positions);
        Assert.False(Directory.Exists(PortfolioDirectory));
    }

    [Fact]
    public async Task ASavedPortfolio_IsReadAsTheApplicationShowsIt_WithClosedPositionsRebuiltFromTheHistory()
    {
        await SaveAsync();
        Portfolio expected = new PortfolioManager().RebuildPortfolio(0m, Transactions());
        var source = new FileReadOnlyPortfolioSource(PortfolioDirectory, _database);
        var before = Snapshot();

        PortfolioReadResult result = await source.LoadAsync();

        Assert.True(result.Found);
        Assert.Equal(expected.Positions.Keys.OrderBy(k => k), result.Portfolio.Positions.Keys.OrderBy(k => k));
        Assert.Equal(6m, result.Portfolio.Positions["MSFT"].Quantity);
        Assert.Equal(expected.CashBalance, result.Portfolio.CashBalance);
        Assert.Equal(expected.TotalRealizedPnL, result.Portfolio.TotalRealizedPnL);
        Assert.Equal(120m, result.Portfolio.TotalRealizedPnL); // 4 shares sold 30 above the cost of 100
        Assert.Single(result.Portfolio.ClosedPositions);
        Assert.Equal(Transactions().Count, result.Portfolio.History.Count);
        Assert.Equal(before.Keys.OrderBy(k => k), Snapshot().Keys.OrderBy(k => k));
        foreach ((string path, byte[] bytes) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public async Task ASaveInProgress_IsDataUnavailable_AndTheMarkerIsLeftAlone()
    {
        await SaveAsync();
        string marker = Path.Combine(PortfolioDirectory, UserPortfolioRepository.SwapMarkerFileName);
        File.WriteAllLines(marker, new[] { "1|positions.parquet" });
        var before = Snapshot();
        var source = new FileReadOnlyPortfolioSource(PortfolioDirectory, _database);

        await Assert.ThrowsAsync<DataUnavailableException>(() => source.LoadAsync());

        Assert.Equal(before.Keys.OrderBy(k => k), Snapshot().Keys.OrderBy(k => k)); // no recovery: the application's own recovery would have changed the files
    }

    [Fact]
    public async Task AnIncompleteSetOfFiles_IsDataUnavailableNotAnEmptyPortfolio()
    {
        await SaveAsync();
        File.Delete(Path.Combine(PortfolioDirectory, UserPortfolioRepository.CashFileName));
        var source = new FileReadOnlyPortfolioSource(PortfolioDirectory, _database);

        await Assert.ThrowsAsync<DataUnavailableException>(() => source.LoadAsync());
    }

    [Fact]
    public async Task AnUnreadableFile_IsDataUnavailable()
    {
        await SaveAsync();
        File.WriteAllBytes(Path.Combine(PortfolioDirectory, UserPortfolioRepository.PositionsFileName), new byte[] { 1, 2, 3 });
        var source = new FileReadOnlyPortfolioSource(PortfolioDirectory, _database);

        await Assert.ThrowsAsync<DataUnavailableException>(() => source.LoadAsync());
    }

    [Fact]
    public void TheLatestExchangeRates_AreTheNewestPerCurrencyWithTheirInverses()
    {
        var older = new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 140m, new DateTime(2024, 1, 3));
        var newer = new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 150m, new DateTime(2024, 1, 9));
        var history = new List<Transaction>
        {
            new(Day1, TransactionType.Deposit, null, 0m, 0m, 1m, appliedRate: older),
            new(Day1.AddDays(1), TransactionType.Deposit, null, 0m, 0m, 1m),
            new(Day1.AddDays(5), TransactionType.Deposit, null, 0m, 0m, 1m, appliedRate: newer),
        };

        IReadOnlyDictionary<CurrencyCode, ExchangeRate> rates = PortfolioExchangeRates.ExtractLatest(history);

        Assert.Equal(150m, rates[CurrencyCode.USD].Rate);
        Assert.Equal(CurrencyCode.JPY, rates[CurrencyCode.USD].QuoteCurrency);
        Assert.Equal(1m / 150m, rates[CurrencyCode.JPY].Rate);
        Assert.Equal(2, rates.Count);
        Assert.Empty(PortfolioExchangeRates.ExtractLatest(new List<Transaction>()));
    }
}
