using Xunit;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Interfaces;
using Moq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using System.Linq;
using System;

namespace StockAnalyzer.Core.Tests.Services;

public class PortfolioAggregationTests
{
    [Fact]
    public async Task GetAllocationAsync_ShouldApplyLRM_WhenPercentagesDoNotSumTo100()
    {
        // 3 categories with exact 1/3 weight each
        // 1/3 * 100.00 = 33.3333...
        // 0.3333 * 3 = 0.9999 -> deficit of 0.0001 (or 1 unit in 10000)
        
        var portfolio = new Portfolio(
            0,
            new Dictionary<string, Position>
            {
                { "A", new Position("A", 1, 100) },
                { "B", new Position("B", 1, 100) },
                { "C", new Position("C", 1, 100) }
            }.ToImmutableDictionary(),
            ImmutableList<Transaction>.Empty
        );

        var latestPrices = new Dictionary<string, decimal>
        {
            { "A", 100m },
            { "B", 100m },
            { "C", 100m }
        };

        var mockProvider = new Mock<IMarketDataProvider>();
        mockProvider.Setup(p => p.GetMetadataAsync("A")).ReturnsAsync(new TickerMetadata("A", "A", "US", "SecA", "IndA", "USD"));
        mockProvider.Setup(p => p.GetMetadataAsync("B")).ReturnsAsync(new TickerMetadata("B", "B", "US", "SecB", "IndB", "USD"));
        mockProvider.Setup(p => p.GetMetadataAsync("C")).ReturnsAsync(new TickerMetadata("C", "C", "US", "SecC", "IndC", "USD"));

        var sut = new PortfolioManager();
        var result = await sut.GetAllocationAsync(portfolio, latestPrices, mockProvider.Object);

        // Assert
        Assert.Equal(300m, result.TotalValue);
        Assert.Equal(3, result.SectorAllocations.Count);
        
        var sum = result.SectorAllocations.Sum(a => a.Percentage);
        Assert.Equal(100.00m, sum);

        // LRM should distribute the extra 0.01 to one of the entries (since they have same remainder, order in temp list matters)
        Assert.Contains(result.SectorAllocations, a => a.Percentage == 33.34m);
        Assert.Equal(2, result.SectorAllocations.Count(a => a.Percentage == 33.33m));
    }

    [Fact]
    public async Task GetAllocationAsync_ShouldIncludeCashAsAssetAndSector()
    {
        var portfolio = new Portfolio(
            1000,
            new Dictionary<string, Position>
            {
                { "A", new Position("A", 10, 100) } // Value: 1000
            }.ToImmutableDictionary(),
            ImmutableList<Transaction>.Empty
        );

        var latestPrices = new Dictionary<string, decimal> { { "A", 100m } };

        var mockProvider = new Mock<IMarketDataProvider>();
        mockProvider.Setup(p => p.GetMetadataAsync("A")).ReturnsAsync(new TickerMetadata("A", "A", "US", "Technology", "Software", "USD"));

        var sut = new PortfolioManager();
        var result = await sut.GetAllocationAsync(portfolio, latestPrices, mockProvider.Object);

        // Assert
        Assert.Equal(2000m, result.TotalValue);
        
        // Asset Allocation: 50% Equity, 50% Cash
        Assert.Equal(2, result.AssetAllocations.Count);
        Assert.Contains(result.AssetAllocations, a => a.Category == "Equity" && a.Percentage == 50.00m);
        Assert.Contains(result.AssetAllocations, a => a.Category == "Cash" && a.Percentage == 50.00m);

        // Sector Allocation: 50% Technology, 50% Cash
        Assert.Equal(2, result.SectorAllocations.Count);
        Assert.Contains(result.SectorAllocations, a => a.Category == "Technology" && a.Percentage == 50.00m);
        Assert.Contains(result.SectorAllocations, a => a.Category == "Cash" && a.Percentage == 50.00m);
    }

    [Fact]
    public void RebuildPortfolio_ShouldReconstructPortfolioCorrectly_WhenValidTransactions()
    {
        var sut = new PortfolioManager();
        var transactions = new List<Transaction>
        {
            new Transaction(DateTimeOffset.UtcNow.AddDays(-5), TransactionType.Deposit, null, 0, 0, 10000m),
            new Transaction(DateTimeOffset.UtcNow.AddDays(-3), TransactionType.Buy, "AAPL", 10, 150m, 0, 10m), // Cost: 1510
            new Transaction(DateTimeOffset.UtcNow.AddDays(-1), TransactionType.Sell, "AAPL", 5, 200m, 0, 5m)   // Proceeds: 995
        };

        var portfolio = sut.RebuildPortfolio(0m, transactions);

        // Expected Cash: 0 + 10000 (Deposit) - 1510 (Buy) + 995 (Sell) = 9485
        Assert.Equal(9485m, portfolio.CashBalance);
        Assert.Single(portfolio.Positions);
        Assert.True(portfolio.Positions.TryGetValue("AAPL", out var aaplPosition));
        Assert.Equal(5m, aaplPosition.Quantity);
        Assert.Equal(150m, aaplPosition.AverageCostPerUnit); // Existing design: fee is excluded from average cost per unit
    }

    [Fact]
    public void RebuildPortfolio_ShouldThrowException_WhenWithdrawalExceedsCash()
    {
        var sut = new PortfolioManager();
        var transactions = new List<Transaction>
        {
            new Transaction(DateTimeOffset.UtcNow.AddDays(-2), TransactionType.Deposit, null, 0, 0, 100m),
            new Transaction(DateTimeOffset.UtcNow.AddDays(-1), TransactionType.Withdrawal, null, 0, 0, 150m)
        };

        Assert.Throws<InvalidOperationException>(() => sut.RebuildPortfolio(0m, transactions));
    }

    [Fact]
    public void RebuildPortfolio_ShouldThrowException_WhenOverselling()
    {
        var sut = new PortfolioManager();
        var transactions = new List<Transaction>
        {
            new Transaction(DateTimeOffset.UtcNow.AddDays(-3), TransactionType.Deposit, null, 0, 0, 1000m),
            new Transaction(DateTimeOffset.UtcNow.AddDays(-2), TransactionType.Buy, "AAPL", 5, 100m, 0, 0m),
            new Transaction(DateTimeOffset.UtcNow.AddDays(-1), TransactionType.Sell, "AAPL", 10, 120m, 0, 0m)
        };

        Assert.Throws<InvalidOperationException>(() => sut.RebuildPortfolio(0m, transactions));
    }

    [Fact]
    public void RebuildPortfolio_ShouldReturnInitialState_WhenTransactionHistoryIsEmpty()
    {
        var sut = new PortfolioManager();
        var portfolio = sut.RebuildPortfolio(1000m, new List<Transaction>());

        Assert.Equal(1000m, portfolio.CashBalance);
        Assert.Empty(portfolio.Positions);
        Assert.Empty(portfolio.History);
    }

    [Fact]
    public void RebuildPortfolio_ShouldPreserveChronologicalOrder_WhenTransactionsAreInputOutOfOrder()
    {
        var sut = new PortfolioManager();
        var now = DateTimeOffset.UtcNow;
        var transactions = new List<Transaction>
        {
            // Buy transaction (Second)
            new Transaction(now.AddDays(-1), TransactionType.Buy, "AAPL", 10, 150m, 0, 0m),
            // Deposit transaction (First)
            new Transaction(now.AddDays(-2), TransactionType.Deposit, null, 0, 0, 10000m)
        };

        var portfolio = sut.RebuildPortfolio(0m, transactions);

        // Verification: Deposit should be applied first, then Buy. If out of order, Buy throws exception due to insufficient funds!
        Assert.Equal(8500m, portfolio.CashBalance);
        Assert.Single(portfolio.Positions);
        Assert.Equal("AAPL", portfolio.History[1].Ticker);
    }

    [Fact]
    public void RebuildPortfolio_ShouldResetAverageCostToZero_WhenPositionIsFullyLiquidated()
    {
        var sut = new PortfolioManager();
        var now = DateTimeOffset.UtcNow;
        var transactions = new List<Transaction>
        {
            new Transaction(now.AddDays(-3), TransactionType.Deposit, null, 0, 0, 10000m),
            new Transaction(now.AddDays(-2), TransactionType.Buy, "AAPL", 10, 150m, 0, 0m),
            new Transaction(now.AddDays(-1), TransactionType.Sell, "AAPL", 10, 180m, 0, 0m)
        };

        var portfolio = sut.RebuildPortfolio(0m, transactions);

        Assert.Equal(10300m, portfolio.CashBalance);
        Assert.Empty(portfolio.Positions);
    }

    [Fact]
    public void RebuildPortfolio_ShouldCalculateRealizedPnLAndClosedPositions_FIFO()
    {
        var sut = new PortfolioManager();
        var now = DateTimeOffset.UtcNow;
        
        var buy1 = new Transaction(now.AddDays(-5), TransactionType.Buy, "AAPL", 10, 150m, 1500m, fee: 10m);
        var buy2 = new Transaction(now.AddDays(-4), TransactionType.Buy, "AAPL", 5, 160m, 800m, fee: 5m);
        var sell = new Transaction(now.AddDays(-3), TransactionType.Sell, "AAPL", 12, 180m, 2160m, fee: 12m);

        var transactions = new List<Transaction>
        {
            new Transaction(now.AddDays(-6), TransactionType.Deposit, null, 0, 0, 10000m),
            buy1,
            buy2,
            sell
        };

        var portfolio = sut.RebuildPortfolio(0m, transactions);

        // FIFO: Sell of 12 AAPL is matched to:
        // - 10 shares of buy1 (Cost: 150 each, Fee: 10)
        // - 2 shares of buy2 (Cost: 160 each, Fee: 5, allocated: 5 * (2/5) = 2)
        // Sell fees: allocated: 12 * (10/12) = 10 for buy1 portion, 12 * (2/12) = 2 for buy2 portion
        // PnL 1 (buy1): 10 * (180 - 150) - 10 (sell fee) - 10 (buy fee) = 300 - 20 = 280
        // PnL 2 (buy2): 2 * (180 - 160) - 2 (sell fee) - 2 (buy fee) = 40 - 4 = 36
        // Total PnL: 280 + 36 = 316
        // Total realized fees: 10 (buy1 fee) + 2 (buy2 entry fee) + 12 (sell fee) = 24

        Assert.Equal(316m, portfolio.TotalRealizedPnL);
        Assert.Equal(2, portfolio.ClosedPositions.Count);
        
        var closed1 = portfolio.ClosedPositions[0];
        Assert.Equal("AAPL", closed1.Ticker);
        Assert.Equal(10m, closed1.Quantity);
        Assert.Equal(150m, closed1.EntryPrice);
        Assert.Equal(180m, closed1.ExitPrice);
        Assert.Equal(280m, closed1.RealizedPnL);
        Assert.Equal(20m, closed1.TotalFees);

        var closed2 = portfolio.ClosedPositions[1];
        Assert.Equal("AAPL", closed2.Ticker);
        Assert.Equal(2m, closed2.Quantity);
        Assert.Equal(160m, closed2.EntryPrice);
        Assert.Equal(180m, closed2.ExitPrice);
        Assert.Equal(36m, closed2.RealizedPnL);
        Assert.Equal(4m, closed2.TotalFees);
    }

    [Fact]
    public void RebuildPortfolio_ShouldCalculateRealizedPnL_WithExplicitRelatedTransactionId()
    {
        var sut = new PortfolioManager();
        var now = DateTimeOffset.UtcNow;
        
        var buy1 = new Transaction(now.AddDays(-5), TransactionType.Buy, "AAPL", 10, 150m, 1500m, fee: 10m);
        var buy2 = new Transaction(now.AddDays(-4), TransactionType.Buy, "AAPL", 5, 160m, 800m, fee: 5m);
        // Specifically exit buy2 first (RelatedTransactionId pointing to buy2)
        var sell = new Transaction(now.AddDays(-3), TransactionType.Sell, "AAPL", 5, 180m, 900m, fee: 5m, relatedTransactionId: buy2.Id);

        var transactions = new List<Transaction>
        {
            new Transaction(now.AddDays(-6), TransactionType.Deposit, null, 0, 0, 10000m),
            buy1,
            buy2,
            sell
        };

        var portfolio = sut.RebuildPortfolio(0m, transactions);

        // Matching buy2: 5 shares at 160 sold at 180. Fee entry: 5, Fee exit: 5
        // PnL: 5 * (180 - 160) - 5 - 5 = 100 - 10 = 90
        Assert.Equal(90m, portfolio.TotalRealizedPnL);
        Assert.Single(portfolio.ClosedPositions);
        
        var closed = portfolio.ClosedPositions[0];
        Assert.Equal("AAPL", closed.Ticker);
        Assert.Equal(5m, closed.Quantity);
        Assert.Equal(160m, closed.EntryPrice);
        Assert.Equal(180m, closed.ExitPrice);
        Assert.Equal(90m, closed.RealizedPnL);
        Assert.Equal(10m, closed.TotalFees);
    }

    [Fact]
    public void RebuildPortfolio_ShouldCalculateRealizedPnLAndClosedPositions_ForShortAndExitShort()
    {
        var sut = new PortfolioManager();
        var now = DateTimeOffset.UtcNow;

        var short1 = new Transaction(now.AddDays(-5), TransactionType.Short, "AAPL", 10, 180m, 1800m, fee: 10m);
        var cover = new Transaction(now.AddDays(-3), TransactionType.ExitShort, "AAPL", 10, 150m, 1500m, fee: 12m);

        var transactions = new List<Transaction>
        {
            new Transaction(now.AddDays(-6), TransactionType.Deposit, null, 0, 0, 10000m),
            short1,
            cover
        };

        var portfolio = sut.RebuildPortfolio(0m, transactions);

        // Short PnL: Quantity * (EntryPrice - ExitPrice) - EntryFee - ExitFee
        // 10 * (180 - 150) - 10 - 12 = 300 - 22 = 278
        Assert.Equal(278m, portfolio.TotalRealizedPnL);
        Assert.Single(portfolio.ClosedPositions);

        var closed = portfolio.ClosedPositions[0];
        Assert.Equal("AAPL", closed.Ticker);
        Assert.Equal(10m, closed.Quantity);
        Assert.Equal(180m, closed.EntryPrice);
        Assert.Equal(150m, closed.ExitPrice);
        Assert.Equal(278m, closed.RealizedPnL);
        Assert.Equal(22m, closed.TotalFees);
    }

    [Fact]
    public void Evaluate_ActiveShortPosition_ShouldCalculateCorrectPnLAndMarketValue()
    {
        var sut = new PortfolioManager();
        var now = DateTimeOffset.UtcNow;

        var shortTrans = new Transaction(now.AddDays(-5), TransactionType.Short, "AAPL", 10, 180m, 1800m, fee: 10m);
        var transactions = new List<Transaction>
        {
            new Transaction(now.AddDays(-6), TransactionType.Deposit, null, 0, 0, 10000m),
            shortTrans
        };

        var portfolio = sut.RebuildPortfolio(0m, transactions);
        
        var latestPrices = new Dictionary<string, decimal>
        {
            { "AAPL", 150m }
        };

        var result = sut.Evaluate(portfolio, latestPrices);

        // Cash: 10000 (deposit) + 1800 (short proceeds) - 10 (fee) = 11790
        // Short Market Value: -10 * 150 = -1500
        // Net Assets (total value): 11790 - 1500 = 10290
        // Unrealized PnL: 10 * (180 - 150) = 300 (excluding entry fee for unrealized P&L in the current model logic)
        Assert.Equal(11790m, result.Metrics.CashBalance);
        Assert.Equal(-1500m, result.PositionValues["AAPL_Short"]);
        Assert.Equal(300m, result.PositionPLs["AAPL_Short"]);
        Assert.Equal(10290m, result.Metrics.TotalValue);
        Assert.Equal(300m, result.Metrics.TotalUnrealizedPL);
    }

    [Fact]
    public void RebuildPortfolio_ShouldAllowConcurrentLongAndShortPositions_WhenBothActive()
    {
        var sut = new PortfolioManager();
        var now = DateTimeOffset.UtcNow;

        var longTrans = new Transaction(now.AddDays(-5), TransactionType.Long, "AAPL", 10, 150m, 1500m, fee: 10m);
        var shortTrans = new Transaction(now.AddDays(-4), TransactionType.Short, "AAPL", 5, 180m, 900m, fee: 8m);

        var transactions = new List<Transaction>
        {
            new Transaction(now.AddDays(-6), TransactionType.Deposit, null, 0, 0, 10000m),
            longTrans,
            shortTrans
        };

        var portfolio = sut.RebuildPortfolio(0m, transactions);

        // Cash: 10000 (deposit) - 1510 (long cost + fee) + 892 (short proceeds - fee) = 9382
        Assert.Equal(9382m, portfolio.CashBalance);

        // Should have two distinct positions under composite keys
        Assert.Equal(2, portfolio.Positions.Count);
        Assert.True(portfolio.Positions.ContainsKey("AAPL"));
        Assert.True(portfolio.Positions.ContainsKey("AAPL_Short"));

        var longPos = portfolio.Positions["AAPL"];
        Assert.Equal(10m, longPos.Quantity);
        Assert.Equal(150m, longPos.AverageCostPerUnit);
        Assert.False(longPos.IsShort);

        var shortPos = portfolio.Positions["AAPL_Short"];
        Assert.Equal(5m, shortPos.Quantity);
        Assert.Equal(180m, shortPos.AverageCostPerUnit);
        Assert.True(shortPos.IsShort);

        // Evaluate both
        var latestPrices = new Dictionary<string, decimal> { { "AAPL", 160m } };
        var result = sut.Evaluate(portfolio, latestPrices);

        // Long Value: 10 * 160 = 1600
        // Short Value: -5 * 160 = -800
        // Total Position Value: 1600 - 800 = 800
        // Net Assets: 9382 + 800 = 10182
        // Long P&L: 10 * (160 - 150) = 100
        // Short P&L: 5 * (180 - 160) = 100
        // Total P&L: 200
        Assert.Equal(1600m, result.PositionValues["AAPL"]);
        Assert.Equal(-800m, result.PositionValues["AAPL_Short"]);
        Assert.Equal(100m, result.PositionPLs["AAPL"]);
        Assert.Equal(100m, result.PositionPLs["AAPL_Short"]);
        Assert.Equal(10182m, result.Metrics.TotalValue);
        Assert.Equal(200m, result.Metrics.TotalUnrealizedPL);
    }
}
