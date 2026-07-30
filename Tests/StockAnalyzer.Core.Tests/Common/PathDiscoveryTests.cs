using System;
using System.IO;
using Xunit;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Tests.Common;

public class PathDiscoveryTests
{
    [Fact]
    public void ResolveConfigPath_ShouldReturnValidPathAndNotThrow()
    {
        // Act
        var result = PathDiscovery.ResolveConfigPath("test_config.json");

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.True(Path.IsPathRooted(result));
        Assert.Equal("test_config.json", Path.GetFileName(result));
    }

    [Fact]
    public void ResolvePortfolioPath_ShouldReturnValidPathAndNotThrow()
    {
        // Act
        var result = PathDiscovery.ResolvePortfolioPath("test_portfolio.json");

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.True(Path.IsPathRooted(result));
        Assert.Equal("test_portfolio.json", Path.GetFileName(result));
    }
}
