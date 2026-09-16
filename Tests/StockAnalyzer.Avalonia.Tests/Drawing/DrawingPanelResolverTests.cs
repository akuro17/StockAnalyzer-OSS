using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DrawingPanelResolverTests
{
    [Fact]
    public void TryResolvePanelKey_MainChartIndex_ReturnsMainKey()
    {
        var key = DrawingPanelResolver.TryResolvePanelKey(-1, null, isSubWindowVisible: true);
        Assert.NotNull(key);
        Assert.Equal(PanelKey.Main, key.Value);
    }

    [Fact]
    public void TryResolvePanelKey_StandaloneIndicator_ReturnsIndicatorKey()
    {
        var ind1 = new CoreIndicatorSettings
        {
            Id = "rsi-1",
            TypeEnum = IndicatorType.RSI,
            IsEnabled = true,
            IsOverlay = false
        };

        var settings = new List<CoreIndicatorSettings> { ind1 };
        var key = DrawingPanelResolver.TryResolvePanelKey(0, settings, isSubWindowVisible: true);

        Assert.NotNull(key);
        Assert.Equal(PanelKind.Indicator, key.Value.Kind);
        Assert.Equal("rsi-1", key.Value.IndicatorId);
    }

    [Fact]
    public void TryResolvePanelKey_OverlayGroup_ReturnsOverlayGroupKey()
    {
        var ind1 = new CoreIndicatorSettings
        {
            Id = "rsi-1",
            TypeEnum = IndicatorType.RSI,
            IsEnabled = true,
            IsOverlay = false,
            OverlayPanelId = "2"
        };
        var ind2 = new CoreIndicatorSettings
        {
            Id = "macd-1",
            TypeEnum = IndicatorType.MACD,
            IsEnabled = true,
            IsOverlay = false,
            OverlayPanelId = "2"
        };

        var settings = new List<CoreIndicatorSettings> { ind1, ind2 };
        var key = DrawingPanelResolver.TryResolvePanelKey(0, settings, isSubWindowVisible: true);

        Assert.NotNull(key);
        Assert.Equal(PanelKind.OverlayGroup, key.Value.Kind);
        Assert.Equal(2, key.Value.OverlayPanelId);
    }

    [Fact]
    public void TryResolvePanelKey_SubWindowNotVisible_ReturnsNullForSubPanels()
    {
        var ind1 = new CoreIndicatorSettings
        {
            Id = "rsi-1",
            TypeEnum = IndicatorType.RSI,
            IsEnabled = true,
            IsOverlay = false
        };

        var settings = new List<CoreIndicatorSettings> { ind1 };
        var key = DrawingPanelResolver.TryResolvePanelKey(0, settings, isSubWindowVisible: false);

        Assert.Null(key);
    }

    [Fact]
    public void TryResolvePanelKey_UnallocatedIndex_ReturnsNull()
    {
        var ind1 = new CoreIndicatorSettings
        {
            Id = "rsi-1",
            TypeEnum = IndicatorType.RSI,
            IsEnabled = true,
            IsOverlay = false
        };

        var settings = new List<CoreIndicatorSettings> { ind1 };
        var key = DrawingPanelResolver.TryResolvePanelKey(5, settings, isSubWindowVisible: true);

        Assert.Null(key);
    }

    [Fact]
    public void ResolvePanelKey_UnallocatedIndex_ThrowsKeyNotFoundException()
    {
        var settings = new List<CoreIndicatorSettings>();
        Assert.Throws<KeyNotFoundException>(() =>
            DrawingPanelResolver.ResolvePanelKey(0, settings, isSubWindowVisible: true));
    }

    [Fact]
    public void TryGetPanelIndex_Main_ReturnsNegativeOne()
    {
        int? index = DrawingPanelResolver.TryGetPanelIndex(PanelKey.Main, null, isSubWindowVisible: true);
        Assert.Equal(-1, index);
    }

    [Fact]
    public void TryGetPanelIndex_ActiveIndicator_ReturnsCorrectIndex()
    {
        var ind1 = new CoreIndicatorSettings
        {
            Id = "rsi-1",
            TypeEnum = IndicatorType.RSI,
            IsEnabled = true,
            IsOverlay = false
        };
        var ind2 = new CoreIndicatorSettings
        {
            Id = "atr-1",
            TypeEnum = IndicatorType.ATR,
            IsEnabled = true,
            IsOverlay = false
        };

        var settings = new List<CoreIndicatorSettings> { ind1, ind2 };

        int? rsiIndex = DrawingPanelResolver.TryGetPanelIndex(PanelKey.Indicator("rsi-1"), settings, isSubWindowVisible: true);
        int? atrIndex = DrawingPanelResolver.TryGetPanelIndex(PanelKey.Indicator("atr-1"), settings, isSubWindowVisible: true);

        Assert.Equal(0, rsiIndex);
        Assert.Equal(1, atrIndex);
    }

    [Fact]
    public void TryGetPanelIndex_ClosedIndicator_ReturnsNull()
    {
        var ind1 = new CoreIndicatorSettings
        {
            Id = "rsi-1",
            TypeEnum = IndicatorType.RSI,
            IsEnabled = true,
            IsOverlay = false
        };

        var settings = new List<CoreIndicatorSettings> { ind1 };
        int? macdIndex = DrawingPanelResolver.TryGetPanelIndex(PanelKey.Indicator("macd-missing"), settings, isSubWindowVisible: true);

        Assert.Null(macdIndex);
    }

    [Fact]
    public void BuildPanelMap_PopulatesBiDirectionalMappings()
    {
        var ind1 = new CoreIndicatorSettings
        {
            Id = "rsi-1",
            TypeEnum = IndicatorType.RSI,
            IsEnabled = true,
            IsOverlay = false
        };
        var ind2 = new CoreIndicatorSettings
        {
            Id = "macd-1",
            TypeEnum = IndicatorType.MACD,
            IsEnabled = true,
            IsOverlay = false
        };

        var settings = new List<CoreIndicatorSettings> { ind1, ind2 };
        var keyToIndex = new Dictionary<PanelKey, int>();
        var indexToKey = new Dictionary<int, PanelKey>();

        DrawingPanelResolver.BuildPanelMap(settings, isSubWindowVisible: true, keyToIndex, indexToKey);

        Assert.Equal(3, keyToIndex.Count); // Main + 2 indicators
        Assert.Equal(3, indexToKey.Count);

        Assert.Equal(-1, keyToIndex[PanelKey.Main]);
        Assert.Equal(PanelKey.Main, indexToKey[-1]);

        var rsiKey = PanelKey.Indicator("rsi-1");
        Assert.Equal(0, keyToIndex[rsiKey]);
        Assert.Equal(rsiKey, indexToKey[0]);

        var macdKey = PanelKey.Indicator("macd-1");
        Assert.Equal(1, keyToIndex[macdKey]);
        Assert.Equal(macdKey, indexToKey[1]);
    }
}
