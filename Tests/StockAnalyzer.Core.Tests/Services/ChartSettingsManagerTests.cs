using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Models.Settings;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class ChartSettingsManagerTests
{
    [Fact]
    public async Task LoadAsync_ShouldRaiseSettingsChangedEvent()
    {
        // Arrange
        var manager = new ChartSettingsManager();
        bool eventRaised = false;
        manager.SettingsChanged += () => eventRaised = true;

        // Act
        await manager.LoadAsync();

        // Assert
        Assert.True(eventRaised, "LoadAsync should raise the SettingsChanged event to notify UI views.");
    }

    [Fact]
    public async Task UpdateAsync_ShouldPersistSettingsAndReadBackCorrectly()
    {
        // Arrange
        string testPath = Path.Combine(Path.GetTempPath(), $"chart_settings_{Guid.NewGuid()}.json");
        var manager = new ChartSettingsManager(testPath);
        var settings = new GlobalChartSettings();
        var customSettings = settings with { DefaultStrokeThickness = 2.5 };

        try
        {
            // Act
            await manager.UpdateAsync(customSettings);

            // Assert
            Assert.True(File.Exists(testPath));
            var loadedManager = new ChartSettingsManager(testPath);
            await loadedManager.LoadAsync();
            Assert.Equal(2.5, loadedManager.Current.DefaultStrokeThickness);
        }
        finally
        {
            if (File.Exists(testPath)) File.Delete(testPath);
            if (File.Exists(testPath + ".bak")) File.Delete(testPath + ".bak");
        }
    }
}
