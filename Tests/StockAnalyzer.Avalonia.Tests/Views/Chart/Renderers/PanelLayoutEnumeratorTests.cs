using System.Collections.Generic;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

/// <summary>
/// PanelLayoutEnumerator is the SSoT for "which enabled indicator owns which sub-window panel index".
/// These tests pin the allocation walk to exactly what ChartRenderPipeline's indicator loop does:
/// settings order, one panel per non-overlay indicator, one shared panel per OverlayPanelId group,
/// a panel for Granville only when its sub-window bar is active, and never a panel for Volume Profile.
/// </summary>
public class PanelLayoutEnumeratorTests
{
    private sealed record PanelHit(int PanelIndex, string PrimaryId, int GroupCount);

    private static List<PanelHit> Walk(IReadOnlyList<CoreIndicatorSettings> settings, bool isSubWindowVisible)
    {
        var hits = new List<PanelHit>();
        PanelLayoutEnumerator.ForEachPanel(
            settings,
            isSubWindowVisible,
            new HashSet<string>(),
            new List<CoreIndicatorSettings>(),
            hits,
            static (int panelIndex, CoreIndicatorSettings primary, IReadOnlyList<CoreIndicatorSettings>? group, List<PanelHit> acc) =>
                acc.Add(new PanelHit(panelIndex, primary.Id, group?.Count ?? 0)));
        return hits;
    }

    private static CoreIndicatorSettings Sub(string id, IndicatorType type = IndicatorType.RSI, string? panelGroup = null) =>
        new() { Id = id, IsEnabled = true, IsOverlay = false, TypeEnum = type, OverlayPanelId = panelGroup };

    [Fact]
    public void NoIndicators_YieldsNoPanels()
    {
        Assert.Empty(Walk(new List<CoreIndicatorSettings>(), isSubWindowVisible: true));
    }

    [Fact]
    public void SubWindowHidden_YieldsNoPanels()
    {
        var settings = new List<CoreIndicatorSettings> { Sub("rsi"), Sub("macd", IndicatorType.MACD) };
        Assert.Empty(Walk(settings, isSubWindowVisible: false));
    }

    [Fact]
    public void OverlayIndicators_DoNotConsumePanels()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { Id = "sma", IsEnabled = true, IsOverlay = true, TypeEnum = IndicatorType.SMA },
            Sub("rsi"),
        };

        var hits = Walk(settings, isSubWindowVisible: true);

        Assert.Single(hits);
        Assert.Equal(new PanelHit(0, "rsi", 0), hits[0]);
    }

    [Fact]
    public void PlainSubWindowIndicators_GetSequentialPanelIndicesInSettingsOrder()
    {
        var settings = new List<CoreIndicatorSettings> { Sub("rsi"), Sub("macd", IndicatorType.MACD), Sub("atr", IndicatorType.ATR) };

        var hits = Walk(settings, isSubWindowVisible: true);

        Assert.Equal(new[] { new PanelHit(0, "rsi", 0), new PanelHit(1, "macd", 0), new PanelHit(2, "atr", 0) }, hits);
    }

    [Fact]
    public void Volume_IsAlwaysTheFirstPanel_WhileOtherPanelsKeepRegistrationOrder()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            Sub("macd", IndicatorType.MACD),
            Sub("volume", IndicatorType.Volume),
            Sub("cci", IndicatorType.CCI),
        };

        var hits = Walk(settings, isSubWindowVisible: true);

        Assert.Equal(
            new[] { new PanelHit(0, "volume", 0), new PanelHit(1, "macd", 0), new PanelHit(2, "cci", 0) },
            hits);
    }

    [Fact]
    public void DisabledIndicator_IsSkipped_AndDoesNotShiftPanelIndices()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            Sub("rsi"),
            new() { Id = "off", IsEnabled = false, IsOverlay = false, TypeEnum = IndicatorType.MACD },
            Sub("atr", IndicatorType.ATR),
        };

        var hits = Walk(settings, isSubWindowVisible: true);

        Assert.Equal(new[] { new PanelHit(0, "rsi", 0), new PanelHit(1, "atr", 0) }, hits);
    }

    [Fact]
    public void GroupedIndicators_ShareOnePanel_AllocatedAtFirstMember_WithFullGroupList()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            Sub("rsi14", IndicatorType.RSI, panelGroup: "G"),
            Sub("rsi7", IndicatorType.RSI, panelGroup: "G"),
            Sub("macd", IndicatorType.MACD),
        };

        var hits = Walk(settings, isSubWindowVisible: true);

        Assert.Equal(new[] { new PanelHit(0, "rsi14", 2), new PanelHit(1, "macd", 0) }, hits);
    }

    [Fact]
    public void VolumeProfile_NeverConsumesAPanel_EvenWhenNotOverlay()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            Sub("rsi"),
            new() { Id = "vp", IsEnabled = true, IsOverlay = false, TypeEnum = IndicatorType.VolumeProfile },
            Sub("atr", IndicatorType.ATR),
        };

        var hits = Walk(settings, isSubWindowVisible: true);

        Assert.Equal(new[] { new PanelHit(0, "rsi", 0), new PanelHit(1, "atr", 0) }, hits);
    }

    [Fact]
    public void Granville_ConsumesAPanel_OnlyWhenSubWindowBarEnabledAndSubWindowVisible()
    {
        CoreIndicatorSettings Granville(string id, bool bar) => new()
        {
            Id = id,
            IsEnabled = true,
            TypeEnum = IndicatorType.GranvilleLaw,
            ParameterObject = new CoreGranvilleLawParameter { ShowSubWindowBar = bar },
        };

        var withBar = new List<CoreIndicatorSettings> { Granville("g", bar: true), Sub("rsi") };
        Assert.Equal(
            new[] { new PanelHit(0, "g", 0), new PanelHit(1, "rsi", 0) },
            Walk(withBar, isSubWindowVisible: true));

        var withoutBar = new List<CoreIndicatorSettings> { Granville("g", bar: false), Sub("rsi") };
        Assert.Equal(
            new[] { new PanelHit(0, "rsi", 0) },
            Walk(withoutBar, isSubWindowVisible: true));
    }

    [Fact]
    public void ScratchCollectionsAreReusable_RepeatedWalksProduceIdenticalResults()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            Sub("a", IndicatorType.RSI, panelGroup: "G"),
            Sub("b", IndicatorType.RSI, panelGroup: "G"),
            Sub("c", IndicatorType.MACD),
        };

        var seen = new HashSet<string>();
        var group = new List<CoreIndicatorSettings>();

        List<PanelHit> Run()
        {
            var acc = new List<PanelHit>();
            PanelLayoutEnumerator.ForEachPanel(settings, true, seen, group, acc,
                static (i, p, g, a) => a.Add(new PanelHit(i, p.Id, g?.Count ?? 0)));
            return acc;
        }

        Assert.Equal(Run(), Run());
        Assert.Equal(Run(), Run());
    }
}
