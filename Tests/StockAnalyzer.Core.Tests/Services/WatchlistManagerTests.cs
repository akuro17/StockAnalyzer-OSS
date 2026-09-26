using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services;

public class WatchlistManagerTests
{
    [Fact]
    public void AddTickerToProfile_ShouldDeduplicateCaseInsensitively()
    {
        // Arrange
        var manager = new WatchlistManager();
        var profile = manager.CreateProfile("Test", IndicatorColor.Gray);
        
        // Act
        manager.AddTickerToProfile(profile.Id, "aapl");
        manager.AddTickerToProfile(profile.Id, "AAPL");
        manager.AddTickerToProfile(profile.Id, "  Aapl  ");
        
        // Assert
        var updatedProfile = manager.GetProfileById(profile.Id);
        Assert.NotNull(updatedProfile);
        Assert.Single(updatedProfile.Items);
        Assert.Equal("AAPL", updatedProfile.Items[0].Ticker);
    }

    [Fact]
    public void AddTickerToProfile_ShouldBeThreadSafe()
    {
        // Arrange
        var manager = new WatchlistManager();
        var profile = manager.CreateProfile("Concurrent", IndicatorColor.Gray);
        int count = 100;

        // Act
        Parallel.For(0, count, i =>
        {
            manager.AddTickerToProfile(profile.Id, $"TICKER_{i}");
        });

        // Assert
        var updatedProfile = manager.GetProfileById(profile.Id);
        Assert.NotNull(updatedProfile);
        Assert.Equal(count, updatedProfile.Items.Count);
    }

    [Fact]
    public void RemoveTickerFromProfile_ShouldWork()
    {
        // Arrange
        var manager = new WatchlistManager();
        var profile = manager.CreateProfile("RemoveTest", IndicatorColor.Gray);
        manager.AddTickerToProfile(profile.Id, "MSFT");
        
        // Act
        manager.RemoveTickerFromProfile(profile.Id, "msft");
        
        // Assert
        var updatedProfile = manager.GetProfileById(profile.Id);
        Assert.Empty(updatedProfile!.Items);
    }

    [Fact]
    public void UpdateProfileName_ShouldWork()
    {
        // Arrange
        var manager = new WatchlistManager();
        var profile = manager.CreateProfile("OldName", IndicatorColor.Gray);
        
        // Act
        manager.UpdateProfileName(profile.Id, "NewName");
        
        // Assert
        var updatedProfile = manager.GetProfileById(profile.Id);
        Assert.NotNull(updatedProfile);
        Assert.Equal("NewName", updatedProfile.Name);
    }

    [Fact]
    public void Initialize_ShouldNotFireEventIfProfilesAreEqual()
    {
        // Arrange
        var manager = new WatchlistManager();
        var profiles = new List<WatchlistProfile> 
        { 
            new WatchlistProfile(Guid.NewGuid(), "P1", IndicatorColor.Gray) 
        };
        manager.Initialize(profiles);
        
        int eventCount = 0;
        manager.WatchlistsChanged += (s, e) => eventCount++;

        // Act
        manager.Initialize(profiles.ToList()); // Pass the same logical contents

        // Assert
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public void RemoveTickersFromAllProfiles_ShouldCascade()
    {
        // Arrange
        var manager = new WatchlistManager();
        var p1 = manager.CreateProfile("List 1", IndicatorColor.Gray);
        var p2 = manager.CreateProfile("List 2", IndicatorColor.Gray);
        
        manager.AddTickerToProfile(p1.Id, "AAPL");
        manager.AddTickerToProfile(p1.Id, "MSFT");
        manager.AddTickerToProfile(p2.Id, "AAPL");
        manager.AddTickerToProfile(p2.Id, "GOOG");

        int eventCount = 0;
        manager.WatchlistsChanged += (s, e) => eventCount++;

        // Act
        manager.RemoveTickersFromAllProfiles(new[] { "AAPL", "GOOG" });

        // Assert
        Assert.Equal(1, eventCount);
        
        var u1 = manager.GetProfileById(p1.Id);
        var u2 = manager.GetProfileById(p2.Id);

        // AAPL should be gone from both
        Assert.DoesNotContain(u1!.Items, i => i.Ticker == "AAPL");
        Assert.DoesNotContain(u2!.Items, i => i.Ticker == "AAPL");

        // MSFT should stay in p1
        Assert.Contains(u1.Items, i => i.Ticker == "MSFT");

        // GOOG should be gone from p2
        Assert.DoesNotContain(u2.Items, i => i.Ticker == "GOOG");
        
        // p2 should be empty now
        Assert.Empty(u2.Items);
    }
}
