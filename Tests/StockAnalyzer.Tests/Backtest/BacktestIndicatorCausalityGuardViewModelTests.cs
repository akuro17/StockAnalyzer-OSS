using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Y:\Temp\sa_implementation_plan_BacktestIndicatorCausalityGuard.md phase 4: the backtest indicator picker must not offer non-causal indicators or
/// Ichimoku's ChikouSpan output (single definition: BacktestIndicatorEligibility). The shared Screener catalog itself stays untouched.
/// </summary>
public class BacktestIndicatorCausalityGuardViewModelTests
{
    private static ScreenerCatalogItem Item(IndicatorType type, string shortName) => new()
    {
        CategoryType = ScreenerItemCategoryType.Indicator,
        GroupName = "Test",
        ShortName = shortName,
        DisplayName = shortName,
        IndicatorType = type,
    };

    private static (BacktestIndicatorSelectionViewModel Vm, List<ScreenerCatalogItem> Source) Create()
    {
        var source = new List<ScreenerCatalogItem>
        {
            Item(IndicatorType.SMA, "SMA"),
            Item(IndicatorType.Ichimoku, "Ichimoku"),
            Item(IndicatorType.ZigZag, "ZigZag"),
            Item(IndicatorType.VolumeProfile, "VolumeProfile"),
            Item(IndicatorType.TimeAtPrice, "TimeAtPrice"),
            Item(IndicatorType.Garch11, "Garch11"),
        };
        var provider = new FakeScreenerCatalogProvider(source, type => type == IndicatorType.Ichimoku
            ? new[] { "Main", "TenkanSen", "KijunSen", "SenkouSpanA", "SenkouSpanB", CoreIchimokuIndicator.ChikouSpanSeriesName }
            : new[] { "Main" });
        return (new BacktestIndicatorSelectionViewModel(provider, NullLocalizationService.Instance), source);
    }

    [Fact]
    public void Catalog_HidesNonCausalAndSynchronouslyUncomputableIndicators_ButKeepsIchimoku()
    {
        (BacktestIndicatorSelectionViewModel vm, List<ScreenerCatalogItem> source) = Create();

        string[] shown = vm.FilteredItems.Select(i => i.ShortName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "Ichimoku", "SMA" }, shown);
        Assert.Equal(6, source.Count); // the shared catalog list is not mutated
    }

    [Fact]
    public void IchimokuOutputPicker_OffersEverySeriesExceptTheChikouSpan()
    {
        (BacktestIndicatorSelectionViewModel vm, _) = Create();

        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "Ichimoku");

        Assert.Equal(new[] { "Main", "TenkanSen", "KijunSen", "SenkouSpanA", "SenkouSpanB" }, vm.LeftConditionAvailableOutputs.ToArray());
        Assert.DoesNotContain(CoreIchimokuIndicator.ChikouSpanSeriesName, vm.LeftConditionAvailableOutputs);
    }

    [Fact]
    public void OtherIndicatorsKeepTheirOutputs()
    {
        (BacktestIndicatorSelectionViewModel vm, _) = Create();

        vm.SelectedCatalogItem = vm.FilteredItems.Single(i => i.ShortName == "SMA");

        Assert.Equal(new[] { "Main" }, vm.LeftConditionAvailableOutputs.ToArray());
    }
}
