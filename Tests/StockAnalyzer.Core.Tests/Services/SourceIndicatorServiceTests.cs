using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class SourceIndicatorServiceTests : IDisposable
{
    private readonly SourceIndicatorService _service;
    private readonly string _filePath;

    public SourceIndicatorServiceTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"source_indicators_test_{Guid.NewGuid():N}.json");
        _service = new SourceIndicatorService(_filePath);
    }

    public void Dispose()
    {
        _service.Dispose();
        if (File.Exists(_filePath))
        {
            try { File.Delete(_filePath); } catch { }
        }
    }

    [Fact]
    public async Task GetSourceIndicatorsAsync_WhenEmpty_ReturnsEmptyList()
    {
        var list = await _service.GetSourceIndicatorsAsync();
        Assert.NotNull(list);
        Assert.Empty(list);

        var syncList = _service.GetSourceIndicators();
        Assert.NotNull(syncList);
        Assert.Empty(syncList);
    }

    [Fact]
    public async Task SaveSourceIndicatorAsync_And_GetSourceIndicators_RoundTripsSuccessfully()
    {
        var rsiSettings = new CoreIndicatorSettings
        {
            Id = "RSI_Source_14",
            TypeEnum = IndicatorType.RSI,
            DisplayName = "RSI (14)",
            Category = CoreIndicatorCategory.Oscillator,
            IsEnabled = true,
            IsOverlay = false,
            ParameterObject = new CoreRsiParameter { Period = 14 }
        };

        await _service.SaveSourceIndicatorAsync(rsiSettings);

        var all = await _service.GetSourceIndicatorsAsync();
        Assert.Single(all);
        var loaded = all[0];
        Assert.Equal("RSI_Source_14", loaded.Id);
        Assert.Equal(IndicatorType.RSI, loaded.TypeEnum);
        Assert.Equal("RSI(14)", loaded.ShortDisplayName);
        Assert.False(loaded.IsOverlay);
        var param = Assert.IsType<CoreRsiParameter>(loaded.ParameterObject);
        Assert.Equal(14, param.Period);

        // Synchronous retrieval
        var syncItem = _service.GetSourceIndicator("RSI_Source_14");
        Assert.NotNull(syncItem);
        Assert.Equal("RSI_Source_14", syncItem.Id);
    }

    [Fact]
    public async Task SaveSourceIndicatorAsync_WhenUpdatingExisting_ReplacesSettings()
    {
        var initial = new CoreIndicatorSettings
        {
            Id = "SMA_Source",
            TypeEnum = IndicatorType.SMA,
            ParameterObject = new CoreSmaParameter { Period = 20 }
        };
        await _service.SaveSourceIndicatorAsync(initial);

        var updated = new CoreIndicatorSettings
        {
            Id = "SMA_Source",
            TypeEnum = IndicatorType.SMA,
            ParameterObject = new CoreSmaParameter { Period = 50 }
        };
        await _service.SaveSourceIndicatorAsync(updated);

        var all = _service.GetSourceIndicators();
        Assert.Single(all);
        Assert.Equal("SMA(50)", all[0].ShortDisplayName);
        var param = Assert.IsType<CoreSmaParameter>(all[0].ParameterObject);
        Assert.Equal(50, param.Period);
    }

    [Fact]
    public async Task DeleteSourceIndicatorAsync_RemovesIndicator()
    {
        var ind1 = new CoreIndicatorSettings
        {
            Id = "IND_1",
            TypeEnum = IndicatorType.RSI,
            DisplayName = "RSI 1"
        };
        var ind2 = new CoreIndicatorSettings
        {
            Id = "IND_2",
            TypeEnum = IndicatorType.EMA,
            DisplayName = "EMA 2"
        };

        await _service.SaveSourceIndicatorAsync(ind1);
        await _service.SaveSourceIndicatorAsync(ind2);

        Assert.Equal(2, _service.GetSourceIndicators().Count);

        var removed = await _service.DeleteSourceIndicatorAsync("IND_1");
        Assert.True(removed);

        var remaining = _service.GetSourceIndicators();
        Assert.Single(remaining);
        Assert.Equal("IND_2", remaining[0].Id);

        var notFound = await _service.DeleteSourceIndicatorAsync("NON_EXISTENT");
        Assert.False(notFound);
    }
}
