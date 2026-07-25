using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services;

public class WorkspaceSerializationServiceTests : IDisposable
{
    private readonly string _testFilePath;
    private readonly WorkspaceSerializationService _service;

    public WorkspaceSerializationServiceTests()
    {
        _testFilePath = Path.Combine(Path.GetTempPath(), $"workspace_{Guid.NewGuid()}.json");
        _service = new WorkspaceSerializationService();
    }

    public void Dispose()
    {
        if (File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
        if (File.Exists(_testFilePath + ".bak"))
        {
            File.Delete(_testFilePath + ".bak");
        }
    }

    [Fact]
    public async Task SaveAndLoad_ShouldPreserveGlobalThemeSettings()
    {
        // Arrange
        var original = new WorkspaceSettings
        {
            Name = "Dark Theme Workspace"
        };
        original.ThemeSettings["ChartBackground"] = "#1E1E1E";
        original.ThemeSettings["GridLineColor"] = "#333333";

        // Act
        await _service.SaveAsync(original, _testFilePath);
        var loaded = await _service.LoadAsync(_testFilePath);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("Dark Theme Workspace", loaded.Name);
        Assert.Equal(2, loaded.ThemeSettings.Count);
        Assert.Equal("#1E1E1E", loaded.ThemeSettings["ChartBackground"]);
        Assert.Equal("#333333", loaded.ThemeSettings["GridLineColor"]);
    }

    [Fact]
    public async Task SaveAndLoadSync_ShouldPreserveGlobalThemeSettings()
    {
        // Arrange
        var original = new WorkspaceSettings
        {
            Name = "Dark Theme Workspace Sync"
        };
        original.ThemeSettings["ChartBackground"] = "#1E1E1E";
        original.ThemeSettings["GridLineColor"] = "#333333";

        // Act
        _service.Save(original, _testFilePath);
        var loaded = await _service.LoadAsync(_testFilePath);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("Dark Theme Workspace Sync", loaded.Name);
        Assert.Equal(2, loaded.ThemeSettings.Count);
        Assert.Equal("#1E1E1E", loaded.ThemeSettings["ChartBackground"]);
        Assert.Equal("#333333", loaded.ThemeSettings["GridLineColor"]);
    }

    [Fact]
    public void SaveSync_WithNullSettings_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _service.Save(null!, _testFilePath));
    }

    [Fact]
    public void SaveSync_WithEmptyPath_ShouldThrowArgumentException()
    {
        var original = new WorkspaceSettings();
        Assert.Throws<ArgumentException>(() => _service.Save(original, ""));
        Assert.Throws<ArgumentException>(() => _service.Save(original, "   "));
    }

    [Fact]
    public void SaveSync_WithRelativePath_ShouldThrowArgumentException()
    {
        var original = new WorkspaceSettings();
        Assert.Throws<ArgumentException>(() => _service.Save(original, "relative/path/to/workspace.json"));
    }

    [Fact]
    public async Task SaveAndLoad_ShouldPreserveIndicatorsAndPolymorphicParameters()
    {
        // Arrange
        var rsiParam = new CoreSmaParameter() { Period = 14 };
        var macdParam = new CoreMacdParameter() { ShortPeriod = 12, LongPeriod = 26, SignalPeriod = 9 };
        
        var original = new WorkspaceSettings();
        
        var rsiSetting = new CoreIndicatorSettings
        {
            Id = "ind-1",
            TypeEnum = IndicatorType.RSI,
            Color = new IndicatorColor(255, 255, 0, 0),
            ParameterObject = rsiParam,
            IsOverlay = false
        };
        
        var macdSetting = new CoreIndicatorSettings
        {
            Id = "ind-2",
            TypeEnum = IndicatorType.MACD,
            Color = new IndicatorColor(255, 0, 255, 0),
            ParameterObject = macdParam,
            IsOverlay = false
        };

        original.Indicators.Add(rsiSetting);
        original.Indicators.Add(macdSetting);

        // Act
        await _service.SaveAsync(original, _testFilePath);
        var loaded = await _service.LoadAsync(_testFilePath);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Indicators.Count);

        // Verify RSI
        var loadedRsi = loaded.Indicators.FirstOrDefault(i => i.Id == "ind-1");
        Assert.NotNull(loadedRsi);
        Assert.Equal(IndicatorType.RSI, loadedRsi.TypeEnum);
        Assert.IsType<CoreSmaParameter>(loadedRsi.ParameterObject);
        var loadedRsiParam = (CoreSmaParameter)loadedRsi.ParameterObject;
        Assert.Equal(14, loadedRsiParam.Period);

        // Verify MACD
        var loadedMacd = loaded.Indicators.FirstOrDefault(i => i.Id == "ind-2");
        Assert.NotNull(loadedMacd);
        Assert.Equal(IndicatorType.MACD, loadedMacd.TypeEnum);
        Assert.IsType<CoreMacdParameter>(loadedMacd.ParameterObject);
        var loadedMacdParam = (CoreMacdParameter)loadedMacd.ParameterObject;
        Assert.Equal(12, loadedMacdParam.ShortPeriod);
        Assert.Equal(26, loadedMacdParam.LongPeriod);
        Assert.Equal(9, loadedMacdParam.SignalPeriod);
    }

    [Fact]
    public async Task SaveAndLoad_ShouldPreserveWatchlistProfiles()
    {
        // Arrange
        var original = new WorkspaceSettings();
        original.WatchlistProfiles.Clear();
        var profile = new StockAnalyzer.Core.Models.Watchlist.WatchlistProfile(
            Guid.NewGuid(), 
            "Test Watchlist", 
            IndicatorColor.Bullish,
            false,
            new List<StockAnalyzer.Core.Models.Watchlist.WatchlistItem>
            {
                new StockAnalyzer.Core.Models.Watchlist.WatchlistItem("AAPL", DateTimeOffset.UtcNow.ToUniversalTime())
            });
        
        original.WatchlistProfiles.Add(profile);

        // Act
        await _service.SaveAsync(original, _testFilePath);
        var loaded = await _service.LoadAsync(_testFilePath);

        // Assert
        Assert.NotNull(loaded);
        Assert.Single(loaded.WatchlistProfiles);
        var loadedProfile = loaded.WatchlistProfiles[0];
        Assert.Equal(profile.Id, loadedProfile.Id);
        Assert.Equal("Test Watchlist", loadedProfile.Name);
        Assert.Equal(IndicatorColor.Bullish, loadedProfile.Color);
        Assert.Single(loadedProfile.Items);
        Assert.Equal("AAPL", loadedProfile.Items[0].Ticker);
    }

    [Fact]
    public async Task ConcurrentSavesAndLoads_ShouldNotThrow()
    {
        // Arrange
        var settings = new WorkspaceSettings { Name = "Concurrent Test" };
        var tasks = new List<Task>();

        // Act & Assert
        // Run multiple SaveAsync and LoadAsync tasks in parallel to verify no file locking IOException occurs.
        for (int i = 0; i < 10; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                var customSettings = new WorkspaceSettings { Name = $"Concurrent Test {index}" };
                await _service.SaveAsync(customSettings, _testFilePath);
            }));

            tasks.Add(Task.Run(async () =>
            {
                await _service.LoadAsync(_testFilePath);
            }));
        }

        // Wait for all tasks to complete. This should not throw IO exceptions.
        await Task.WhenAll(tasks);
    }
}
