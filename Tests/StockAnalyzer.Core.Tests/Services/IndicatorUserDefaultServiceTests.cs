using System;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class IndicatorUserDefaultServiceTests : IDisposable
{
    private readonly IndicatorUserDefaultService _service;
    private readonly string _defaultsFilePath;

    public IndicatorUserDefaultServiceTests()
    {
        _service = new IndicatorUserDefaultService();
        _defaultsFilePath = PathDiscovery.ResolveIndicatorDefaultsPath("user_indicator_defaults.json");
        // Ensure clean test baseline
        if (File.Exists(_defaultsFilePath))
        {
            try { File.Delete(_defaultsFilePath); } catch { }
        }
    }

    public void Dispose()
    {
        _service.Dispose();
        if (File.Exists(_defaultsFilePath))
        {
            try { File.Delete(_defaultsFilePath); } catch { }
        }
    }

    [Fact]
    public async Task LoadUserDefaultsAsync_WhenNoFileExists_ReturnsEmptyDictionary()
    {
        var defaults = await _service.LoadUserDefaultsAsync();
        Assert.NotNull(defaults);
        Assert.Empty(defaults);
    }

    [Fact]
    public async Task SaveUserDefaultAsync_And_LoadUserDefaultsAsync_RoundTripsSuccessfully()
    {
        var smaSettings = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.SMA,
            DisplayName = "SMA(25)",
            Category = CoreIndicatorCategory.Trend,
            IsEnabled = true,
            Color = IndicatorColor.FromArgb(255, 255, 0, 0),
            ParameterObject = new CoreSmaParameter { Period = 25 }
        };

        await _service.SaveUserDefaultAsync(smaSettings);

        var loaded = await _service.LoadUserDefaultsAsync();
        Assert.True(loaded.ContainsKey(IndicatorType.SMA));
        var loadedSma = loaded[IndicatorType.SMA];
        Assert.Equal("Simple Moving Average", loadedSma.DisplayName);
        Assert.Equal("SMA(25)", loadedSma.ShortDisplayName);
        Assert.Equal(IndicatorColor.FromArgb(255, 255, 0, 0), loadedSma.Color);

        var param = Assert.IsType<CoreSmaParameter>(loadedSma.ParameterObject);
        Assert.Equal(25, param.Period);
    }

    [Fact]
    public async Task ResetToSystemDefaultAsync_RemovesSpecificIndicatorType()
    {
        var smaSettings = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.SMA,
            DisplayName = "SMA(30)",
            ParameterObject = new CoreSmaParameter { Period = 30 }
        };
        var emaSettings = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.EMA,
            DisplayName = "EMA(12)",
            ParameterObject = new CoreEmaParameter { Period = 12 }
        };

        await _service.SaveUserDefaultAsync(smaSettings);
        await _service.SaveUserDefaultAsync(emaSettings);

        var loaded = await _service.LoadUserDefaultsAsync();
        Assert.Equal(2, loaded.Count);

        await _service.ResetToSystemDefaultAsync(IndicatorType.SMA);

        var afterReset = await _service.LoadUserDefaultsAsync();
        Assert.False(afterReset.ContainsKey(IndicatorType.SMA));
        Assert.True(afterReset.ContainsKey(IndicatorType.EMA));
    }

    [Fact]
    public async Task ResetAllToSystemDefaultAsync_RemovesAllDefaults()
    {
        var smaSettings = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.SMA,
            DisplayName = "SMA(50)",
            ParameterObject = new CoreSmaParameter { Period = 50 }
        };
        await _service.SaveUserDefaultAsync(smaSettings);

        await _service.ResetAllToSystemDefaultAsync();

        var loaded = await _service.LoadUserDefaultsAsync();
        Assert.Empty(loaded);
    }
}
