using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Factories;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class UserPortfolioRepositoryTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _customFilePath;
    private readonly string _backupFilePath;
    private readonly string _tempFilePath;

    public UserPortfolioRepositoryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "StockAnalyzer_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _customFilePath = Path.Combine(_tempDirectory, "portfolio.json");
        _backupFilePath = _customFilePath + ".bak";
        _tempFilePath = _customFilePath + ".tmp";
    }

    [Fact]
    public async Task LoadPortfolioAsync_BothFilesMissing_ReturnsEmptyPortfolio()
    {
        // Arrange
        using var repository = new UserPortfolioRepository(_customFilePath);

        // Act
        var portfolio = await repository.LoadPortfolioAsync();

        // Assert
        Assert.NotNull(portfolio);
        Assert.Equal(0m, portfolio.CashBalance);
        Assert.Empty(portfolio.Positions);
        Assert.Empty(portfolio.History);
    }



    [Fact]
    public async Task SavePortfolioAsync_ConcurrentCalls_DoesNotThrowIOException()
    {
        // Arrange
        using var repository = new UserPortfolioRepository(_customFilePath);
        
        var portfolio = new Portfolio(
            50000m,
            new Dictionary<string, Position> { { "AAPL", new Position("AAPL", 50, 180m) } }
        );

        // Act
        var task1 = repository.SavePortfolioAsync(portfolio).AsTask();
        var task2 = repository.SavePortfolioAsync(portfolio).AsTask();
        var task3 = repository.SavePortfolioAsync(portfolio).AsTask();

        // Assert
        await Task.WhenAll(task1, task2, task3);
        
        var loaded = await repository.LoadPortfolioAsync();
        Assert.NotNull(loaded);
        Assert.Equal(50000m, loaded.CashBalance);
        Assert.Single(loaded.Positions);
        Assert.Equal(50, loaded.Positions["AAPL"].Quantity);
    }

    [Fact]
    public async Task SavePortfolioAsync_Cancellation_CleansUpTempFile()
    {
        // Arrange
        using var repository = new UserPortfolioRepository(_customFilePath);
        var portfolio = new Portfolio(10000m);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel the token

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await repository.SavePortfolioAsync(portfolio, cts.Token);
        });

        // Verify the .tmp file does not exist or has been cleaned up
        Assert.False(File.Exists(_tempFilePath), "Temporary swap file was not deleted upon cancellation.");
    }

    [Fact]
    public async Task SaveAndLoad_ActiveShortPosition_ShouldPreserveIsShort()
    {
        // Arrange
        using var repository = new UserPortfolioRepository(_customFilePath);
        var original = new Portfolio(
            20000m,
            new Dictionary<string, Position> { { "AAPL", new Position("AAPL", 10, 150m, isShort: true) } }
        );

        // Act
        await repository.SavePortfolioAsync(original);
        var loaded = await repository.LoadPortfolioAsync();

        // Assert
        Assert.NotNull(loaded);
        Assert.Single(loaded.Positions);
        var aapl = loaded.Positions["AAPL_Short"];
        Assert.Equal(10m, aapl.Quantity);
        Assert.Equal(150m, aapl.AverageCostPerUnit);
        Assert.True(aapl.IsShort, "IsShort should be preserved through save and load.");
    }

    [Fact]
    public async Task SaveAndLoad_WithTransactions_ShouldPreserveTransactions()
    {
        // Arrange
        using var repository = new UserPortfolioRepository(_customFilePath);
        var original = new Portfolio(
            15000m,
            new Dictionary<string, Position>(),
            new List<Transaction>
            {
                new Transaction(
                    DateTimeOffset.UtcNow,
                    TransactionType.Buy,
                    "MSFT",
                    10m,
                    400m,
                    -4000m,
                    5m,
                    "Initial Buy Note",
                    450m,
                    380m,
                    Guid.NewGuid(),
                    null
                )
            }
        );

        // Act
        await repository.SavePortfolioAsync(original);
        var loaded = await repository.LoadPortfolioAsync();

        // Assert
        Assert.NotNull(loaded);
        Assert.Single(loaded.History);
        var tx = loaded.History[0];
        Assert.Equal("MSFT", tx.Ticker);
        Assert.Equal(TransactionType.Buy, tx.Type);
        Assert.Equal(10m, tx.Quantity);
        Assert.Equal(400m, tx.PricePerUnit);
        Assert.Equal(-4000m, tx.CashAmount);
        Assert.Equal(5m, tx.Fee);
        Assert.Equal("Initial Buy Note", tx.Notes);
        Assert.Equal(450m, tx.TargetPrice);
        Assert.Equal(380m, tx.StopLoss);
        Assert.Null(tx.RelatedTransactionId);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Suppress cleanup exceptions in tests
        }
    }
}
