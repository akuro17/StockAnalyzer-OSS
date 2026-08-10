using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class RelativePerformanceSettingsViewModelTests
{
    [Fact]
    public void SavePresetCommand_SavesCurrentSymbolListAsPreset()
    {
        // Arrange
        var settingsManager = new MockChartSettingsManager();
        var vm = new RelativePerformanceSettingsViewModel(
            settingsManager,
            marketDataProvider: null!,
            dispatcherService: null!,
            logger: NullLogger<RelativePerformanceSettingsViewModel>.Instance);

        vm.SymbolList.Add("AAPL");
        vm.SymbolList.Add("MSFT");
        vm.NewPresetName = "Tech Leaders";

        // Act
        vm.SavePresetCommand.Execute(null);

        // Assert
        Assert.Single(vm.SavedPresets);
        Assert.Equal("Tech Leaders", vm.SavedPresets[0].Name);
        Assert.Equal(new List<string> { "AAPL", "MSFT" }, vm.SavedPresets[0].Targets);
        Assert.Empty(vm.NewPresetName);
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void LoadPresetCommand_ReplacesSymbolListWithPresetTargets()
    {
        // Arrange
        var settingsManager = new MockChartSettingsManager();
        var vm = new RelativePerformanceSettingsViewModel(
            settingsManager,
            marketDataProvider: null!,
            dispatcherService: null!,
            logger: NullLogger<RelativePerformanceSettingsViewModel>.Instance);

        vm.SymbolList.Add("OLD1");

        var preset = new RelativePerformancePreset
        {
            Name = "Semiconductors",
            Targets = new List<string> { "NVDA", "AMD", "TSM" }
        };
        vm.SavedPresets.Add(preset);

        // Act
        vm.LoadPresetCommand.Execute(preset);

        // Assert
        Assert.Equal(new List<string> { "NVDA", "AMD", "TSM" }, vm.SymbolList);
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void DeletePresetCommand_RemovesPresetFromSavedPresets()
    {
        // Arrange
        var settingsManager = new MockChartSettingsManager();
        var vm = new RelativePerformanceSettingsViewModel(
            settingsManager,
            marketDataProvider: null!,
            dispatcherService: null!,
            logger: NullLogger<RelativePerformanceSettingsViewModel>.Instance);

        var preset = new RelativePerformancePreset
        {
            Name = "Crypto",
            Targets = new List<string> { "BTC", "ETH" }
        };
        vm.SavedPresets.Add(preset);

        // Act
        vm.DeletePresetCommand.Execute(preset);

        // Assert
        Assert.Empty(vm.SavedPresets);
        Assert.True(vm.IsModified);
    }
}
