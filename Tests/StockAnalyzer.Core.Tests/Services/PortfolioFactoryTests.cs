using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Core.Factories;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class PortfolioFactoryTests
{
    private readonly Mock<IMarketDataProvider> _mockProvider;

    public PortfolioFactoryTests()
    {
        _mockProvider = new Mock<IMarketDataProvider>();
    }

    [Fact]
    public async Task CreateFromProfileAsync_ShouldThrow_WhenProfileIsNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            PortfolioFactory.CreateFromProfileAsync(null!, _mockProvider.Object));
    }

    [Fact]
    public async Task CreateFromProfileAsync_ShouldThrow_WhenProviderIsNull()
    {
        var profile = new WatchlistProfile(Guid.NewGuid(), "Test", IndicatorColor.Gray);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            PortfolioFactory.CreateFromProfileAsync(profile, null!));
    }

    [Fact]
    public async Task CreateFromProfileAsync_ShouldReturnPortfolioWithCash_WhenProfileIsEmpty()
    {
        var profile = new WatchlistProfile(Guid.NewGuid(), "Test", IndicatorColor.Gray);
        var portfolio = await PortfolioFactory.CreateFromProfileAsync(profile, _mockProvider.Object, 50000m);

        Assert.Equal(50000m, portfolio.CashBalance);
        Assert.Empty(portfolio.Positions);
    }

    [Fact]
    public async Task CreateFromProfileAsync_ShouldMapMockPositionsCorrectly()
    {
        var items = new List<WatchlistItem>
        {
            new WatchlistItem("AAPL", DateTimeOffset.UtcNow),
            new WatchlistItem("MSFT", DateTimeOffset.UtcNow)
        };
        var profile = new WatchlistProfile(Guid.NewGuid(), "Test", IndicatorColor.Gray, isPortfolio: true, items: items);

        var portfolio = await PortfolioFactory.CreateFromProfileAsync(profile, _mockProvider.Object, 100000m);

        // AAPL mock: 100 qty @ 180m cost = 18000m
        // MSFT mock: 30 qty @ 400m cost = 12000m
        // Total invested: 30000m
        // Remaining Cash: 70000m
        Assert.Equal(70000m, portfolio.CashBalance);
        Assert.Equal(2, portfolio.Positions.Count);

        Assert.True(portfolio.Positions.TryGetValue("AAPL", out var aaplPos));
        Assert.Equal(100m, aaplPos.Quantity);
        Assert.Equal(180m, aaplPos.AverageCostPerUnit);

        Assert.True(portfolio.Positions.TryGetValue("MSFT", out var msftPos));
        Assert.Equal(30m, msftPos.Quantity);
        Assert.Equal(400m, msftPos.AverageCostPerUnit);
    }

    [Fact]
    public async Task CreateFromProfileAsync_ShouldFetchLatestPricesForNonMockTickers()
    {
        var items = new List<WatchlistItem>
        {
            new WatchlistItem("NEWT", DateTimeOffset.UtcNow)
        };
        var profile = new WatchlistProfile(Guid.NewGuid(), "Test", IndicatorColor.Gray, isPortfolio: true, items: items);

        var latestPrices = new Dictionary<string, decimal>
        {
            { "NEWT", 50m }
        };

        _mockProvider.Setup(p => p.GetLatestPricesAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(latestPrices);

        var portfolio = await PortfolioFactory.CreateFromProfileAsync(profile, _mockProvider.Object, 100000m);

        // NEWT (non-mock): 100 qty @ 50m price = 5000m
        // Remaining Cash: 95000m
        Assert.Equal(95000m, portfolio.CashBalance);
        Assert.True(portfolio.Positions.TryGetValue("NEWT", out var newtPos));
        Assert.Equal(100m, newtPos.Quantity);
        Assert.Equal(50m, newtPos.AverageCostPerUnit);
    }

    [Fact]
    public async Task CreateFromProfilesAsync_ShouldMergeDuplicatesWithWeightedAverage()
    {
        var items1 = new List<WatchlistItem>
        {
            new WatchlistItem("AAPL", DateTimeOffset.UtcNow), // AAPL mock: 100 qty @ 180m cost = 18000m
            new WatchlistItem("XYZ", DateTimeOffset.UtcNow)
        };
        var profile1 = new WatchlistProfile(Guid.NewGuid(), "Test1", IndicatorColor.Gray, isPortfolio: true, items: items1);

        var items2 = new List<WatchlistItem>
        {
            new WatchlistItem("AAPL", DateTimeOffset.UtcNow), // AAPL mock: 100 qty @ 180m cost = 18000m
            new WatchlistItem("XYZ", DateTimeOffset.UtcNow)
        };
        var profile2 = new WatchlistProfile(Guid.NewGuid(), "Test2", IndicatorColor.Gray, isPortfolio: true, items: items2);

        var latestPrices = new Dictionary<string, decimal>
        {
            { "XYZ", 200m } // XYZ (non-mock): 100 qty @ 200m cost = 20000m
        };

        _mockProvider.Setup(p => p.GetLatestPricesAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(latestPrices);

        var portfolio = await PortfolioFactory.CreateFromProfilesAsync(
            new[] { profile1, profile2 }, _mockProvider.Object, 100000m);

        // Total AAPL: 200 qty @ 180m = 36000m
        // Total XYZ: 200 qty @ 200m = 40000m
        // Total Invested: 76000m
        // Remaining Cash: 24000m
        Assert.Equal(24000m, portfolio.CashBalance);

        Assert.True(portfolio.Positions.TryGetValue("AAPL", out var aaplPos));
        Assert.Equal(200m, aaplPos.Quantity);
        Assert.Equal(180m, aaplPos.AverageCostPerUnit);

        Assert.True(portfolio.Positions.TryGetValue("XYZ", out var xyzPos));
        Assert.Equal(200m, xyzPos.Quantity);
        Assert.Equal(200m, xyzPos.AverageCostPerUnit);
    }
}
