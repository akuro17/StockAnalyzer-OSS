using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Microsoft.Extensions.Options;

namespace StockAnalyzer.Core.Tests.Services;

public class ReliabilityTests : IDisposable
{
    private readonly string _testBaseDir;

    public ReliabilityTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), $"SA_ReliabilityTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testBaseDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testBaseDir))
        {
            Directory.Delete(_testBaseDir, true);
        }
    }

    [Fact]
    public void MarketDataSettings_Validate_ShouldIncludeTickerListPath()
    {
        // Arrange
        var settings = new MarketDataSettings
        {
            DailyDataPath = "C:\\Data\\Daily",
            TickerListPath = "C:\\Data\\tickers.json|"
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => settings.Validate());
        Assert.Contains("TickerListPath", ex.ParamName ?? ex.Message);
    }

    [Fact]
    public void MarketDataSettings_Validate_ShouldPassForValidPaths()
    {
        // Arrange
        var settings = new MarketDataSettings
        {
            DailyDataPath = "C:\\Data\\Daily",
            TickerListPath = "C:\\Data\\tickers.json"
        };

        // Act & Assert
        settings.Validate(); // Should not throw
    }

    [Fact]
    public async Task AtomicSwap_Pattern_ShouldBeRobust()
    {
        // This test verifies the pattern used in ParquetMarketDataProvider and IndicatorSettingsPersistence
        string finalPath = Path.Combine(_testBaseDir, "final.json");
        string tempPath = finalPath + ".tmp";

        // Scenario 1: New File
        await File.WriteAllTextAsync(tempPath, "new content");
        if (File.Exists(finalPath))
        {
            File.Replace(tempPath, finalPath, null);
        }
        else
        {
            File.Move(tempPath, finalPath);
        }
        Assert.Equal("new content", await File.ReadAllTextAsync(finalPath));
        Assert.False(File.Exists(tempPath));

        // Scenario 2: Existing File Replacement
        await File.WriteAllTextAsync(tempPath, "updated content");
        if (File.Exists(finalPath))
        {
            File.Replace(tempPath, finalPath, null);
        }
        else
        {
            File.Move(tempPath, finalPath);
        }
        Assert.Equal("updated content", await File.ReadAllTextAsync(finalPath));
        Assert.False(File.Exists(tempPath));
    }
}
