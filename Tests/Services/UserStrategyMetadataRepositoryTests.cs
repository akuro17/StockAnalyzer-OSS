using Xunit;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Tests.Services;

public class UserStrategyMetadataRepositoryTests
{
    [Fact]
    public void SaveAndGetStrategy_PersistsAndRetrievesDataCorrectly()
    {
        // Arrange
        var repo = UserStrategyMetadataRepository.Instance;
        string ticker = "TEST_TICKER_" + System.Guid.NewGuid().ToString("N");

        // Act
        repo.SaveStrategy(ticker, 150.25m, 200.00m, 130.00m, "Test strategy text");
        var result = repo.GetStrategy(ticker);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(150.25m, result!.EntryPrice);
        Assert.Equal(200.00m, result.TargetPrice);
        Assert.Equal(130.00m, result.StopLoss);
        Assert.Equal("Test strategy text", result.Notes);
    }

    [Fact]
    public void SaveSignalBundles_PersistsDataAtomically()
    {
        // Arrange
        var repo = UserStrategyMetadataRepository.Instance;
        string ticker = "TEST_BUNDLE_" + System.Guid.NewGuid().ToString("N");
        var bundles = new System.Collections.Generic.List<StockAnalyzer.Core.Models.Screener.BundledSignalCondition>
        {
            new StockAnalyzer.Core.Models.Screener.BundledSignalCondition(
                "Set1",
                StockAnalyzer.Core.Models.Screener.SignalTargetType.Long,
                System.Array.Empty<StockAnalyzer.Core.Models.Screener.ScreenerIndicatorEntry>())
        };

        // Act
        repo.SaveSignalBundles(ticker, bundles);

        // Allow background persistence task to execute
        System.Threading.Thread.Sleep(200);

        var loaded = repo.GetSignalBundles(ticker);

        // Assert
        Assert.NotNull(loaded);
        Assert.Single(loaded);
        Assert.Equal("Set1", loaded[0].Name);
    }

    [Fact]
    public void WatchlistItem_WithFalseSignalBundles_ShouldDisplayFalse()
    {
        // Arrange
        string ticker = "TEST_FALSE_" + System.Guid.NewGuid().ToString("N");
        var bundles = new System.Collections.Generic.List<StockAnalyzer.Core.Models.Screener.BundledSignalCondition>
        {
            new StockAnalyzer.Core.Models.Screener.BundledSignalCondition(
                "Condition Set 1",
                StockAnalyzer.Core.Models.Screener.SignalTargetType.Long,
                System.Array.Empty<StockAnalyzer.Core.Models.Screener.ScreenerIndicatorEntry>())
            {
                IsHit = false
            },
            new StockAnalyzer.Core.Models.Screener.BundledSignalCondition(
                "Condition Set 2",
                StockAnalyzer.Core.Models.Screener.SignalTargetType.Long,
                System.Array.Empty<StockAnalyzer.Core.Models.Screener.ScreenerIndicatorEntry>())
            {
                IsHit = false
            }
        };

        UserStrategyMetadataRepository.Instance.SaveSignalBundles(ticker, bundles);
        System.Threading.Thread.Sleep(200);

        // Act
        var item = new StockAnalyzer.Avalonia.ViewModels.Watchlist.WatchlistItemViewModel(
            ticker, "Test Ticker", "Tech", "Software", 100, 105, 95, 102, 1000, 2, 2);

        // Assert
        Assert.False(item.IsLong);
        Assert.Equal("False", item.DisplayIsLong);
    }
}

