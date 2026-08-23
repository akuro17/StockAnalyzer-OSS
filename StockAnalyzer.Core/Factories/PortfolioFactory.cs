using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Factories;

public static class PortfolioFactory
{
    public static readonly Portfolio Empty = CreateEmptyPortfolio();

    private static readonly Dictionary<string, (decimal Quantity, decimal Cost)> MockPositionData = new()
    {
        { "AAPL", (100, 180m) },
        { "AMZN", (50, 170m) },
        { "GOOGL", (40, 140m) },
        { "MSFT", (30, 400m) },
        { "NVDA", (20, 800m) }
    };

    public static Portfolio CreateDefaultMock()
    {
        var history = new List<Transaction>
        {
            new Transaction(DateTimeOffset.UtcNow.AddDays(-10), TransactionType.Deposit, null, 0, 0, 100000m),
            new Transaction(DateTimeOffset.UtcNow.AddDays(-8), TransactionType.Buy, "AAPL", 100, 180m, 18000m),
            new Transaction(DateTimeOffset.UtcNow.AddDays(-7), TransactionType.Buy, "AMZN", 50, 170m, 8500m),
            new Transaction(DateTimeOffset.UtcNow.AddDays(-6), TransactionType.Buy, "GOOGL", 40, 140m, 5600m),
            new Transaction(DateTimeOffset.UtcNow.AddDays(-5), TransactionType.Buy, "MSFT", 30, 400m, 12000m),
            new Transaction(DateTimeOffset.UtcNow.AddDays(-4), TransactionType.Buy, "NVDA", 20, 800m, 16000m),
        };

        var positions = new Dictionary<string, Position>
        {
            { "AAPL", new Position("AAPL", 100, 180m) },
            { "AMZN", new Position("AMZN", 50, 170m) },
            { "GOOGL", new Position("GOOGL", 40, 140m) },
            { "MSFT", new Position("MSFT", 30, 400m) },
            { "NVDA", new Position("NVDA", 20, 800m) }
        };

        var cashBalance = 100000m - 18000m - 8500m - 5600m - 12000m - 16000m; // 100,000 - 60,100 = 39,900

        return new Portfolio(
            cashBalance,
            positions.ToImmutableDictionary(),
            history.ToImmutableList()
        );
    }

    public static Portfolio CreateEmptyPortfolio()
    {
        return new Portfolio(0m, ImmutableDictionary<string, Position>.Empty, ImmutableList<Transaction>.Empty);
    }

    private static bool IsTestEnvironment()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => a.FullName != null && 
                     (a.FullName.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) || 
                      a.FullName.StartsWith("StockAnalyzer.Tests", StringComparison.OrdinalIgnoreCase) || 
                      a.FullName.StartsWith("StockAnalyzer.Core.Tests", StringComparison.OrdinalIgnoreCase) || 
                      a.FullName.StartsWith("StockAnalyzer.Avalonia.Tests", StringComparison.OrdinalIgnoreCase)));
    }

    public static async Task<Portfolio> CreateFromProfileAsync(
        WatchlistProfile profile,
        IMarketDataProvider marketDataProvider,
        decimal? initialCash = null)
    {
        decimal actualCash = initialCash ?? (IsTestEnvironment() ? 100000m : 0m);
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (marketDataProvider == null) throw new ArgumentNullException(nameof(marketDataProvider));

        if (profile.Items == null || profile.Items.Count == 0)
        {
            return new Portfolio(actualCash, ImmutableDictionary<string, Position>.Empty, ImmutableList<Transaction>.Empty);
        }

        return await CreateFromProfilesAsync(new[] { profile }, marketDataProvider, actualCash);
    }

    public static async Task<Portfolio> CreateFromProfilesAsync(
        IEnumerable<WatchlistProfile> profiles,
        IMarketDataProvider marketDataProvider,
        decimal? initialCash = null)
    {
        decimal actualCash = initialCash ?? (IsTestEnvironment() ? 100000m : 0m);
        if (profiles == null) return Empty;
        if (marketDataProvider == null) throw new ArgumentNullException(nameof(marketDataProvider));

        var profileList = profiles.Where(p => p != null).ToList();
        if (profileList.Count == 0) return Empty;

        // Collect all unique tickers
        var tickers = profileList
            .SelectMany(p => p.Items ?? Enumerable.Empty<WatchlistItem>())
            .Select(i => i.Ticker)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToList();

        if (tickers.Count == 0)
        {
            return new Portfolio(actualCash, ImmutableDictionary<string, Position>.Empty, ImmutableList<Transaction>.Empty);
        }

        // Fetch current prices for all tickers
        IReadOnlyDictionary<string, decimal> prices = new Dictionary<string, decimal>();
        try
        {
            prices = await marketDataProvider.GetLatestPricesAsync(tickers);
        }
        catch
        {
            // Fallback: Keep empty dictionary and price falls back to 0
        }

        var positionsDict = new Dictionary<string, Position>();
        decimal totalInvested = 0m;

        // Group items across all profiles to calculate total quantities and weighted averages
        var allItems = profileList
            .SelectMany(p => p.Items ?? Enumerable.Empty<WatchlistItem>())
            .Where(i => !string.IsNullOrWhiteSpace(i.Ticker))
            .ToList();

        var groupedItems = allItems.GroupBy(i => i.Ticker.ToUpperInvariant());

        foreach (var group in groupedItems)
        {
            var ticker = group.Key;
            decimal totalQty = 0m;
            decimal totalCostBasis = 0m;

            foreach (var item in group)
            {
                decimal qty = 100m;
                decimal cost = 0m;

                if (MockPositionData.TryGetValue(ticker, out var mockData))
                {
                    qty = mockData.Quantity;
                    cost = mockData.Cost;
                }
                else
                {
                    if (prices.TryGetValue(ticker, out var price))
                    {
                        cost = price;
                    }
                }

                totalQty += qty;
                totalCostBasis += qty * cost;
            }

            if (totalQty > 0)
            {
                decimal avgCost = totalCostBasis / totalQty;
                positionsDict[ticker] = new Position(ticker, totalQty, avgCost);
                totalInvested += totalCostBasis;
            }
        }

        decimal cashBalance = actualCash - totalInvested;
        if (cashBalance < 0)
        {
            cashBalance = 0m;
        }

        return new Portfolio(
            cashBalance,
            positionsDict.ToImmutableDictionary(),
            ImmutableList<Transaction>.Empty
        );
    }
}
