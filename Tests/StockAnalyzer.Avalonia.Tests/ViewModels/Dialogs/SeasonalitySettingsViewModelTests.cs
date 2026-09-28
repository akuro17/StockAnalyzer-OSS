using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Dialogs;

public class SeasonalitySettingsViewModelTests
{
    private static SeasonalitySettingsViewModel Create(MockChartSettingsManager manager)
        => new(manager, NullLogger<SeasonalitySettingsViewModel>.Instance);

    [Fact]
    public void Load_TakesEveryValueFromGlobalChartSettingsDefaults()
    {
        var vm = Create(new MockChartSettingsManager());

        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityYearsToOverlay, vm.YearsToOverlay);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityStartMonth, vm.StartMonth);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityLineThickness, vm.LineThickness);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityEndpointMarkerRadius, vm.EndpointMarkerRadius);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityMonthlyTableOrientation, vm.MonthlyTableOrientation);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityFontSize, vm.LegendFontSize);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityFontSize, vm.AxisFontSize);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityFontSize, vm.MonthlyTableFontSize);
        Assert.Equal(10, vm.YearColors.Count);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityMonthlyUpColor), vm.MonthlyUpColor);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityMonthlyDownColor), vm.MonthlyDownColor);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityMonthlyNeutralColor), vm.MonthlyNeutralColor);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor1), vm.YearColors[0]);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor8), vm.YearColors[7]);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor9), vm.YearColors[8]);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor10), vm.YearColors[9]);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ChangingYearsToOverlay_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());

        vm.YearsToOverlay = 25;

        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingStartMonth_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());

        vm.StartMonth = 4;

        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingLineThickness_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());

        vm.LineThickness = 3.5f;

        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingEndpointMarkerRadius_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());

        vm.EndpointMarkerRadius = 8.5f;

        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingMonthlyTableOrientation_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());

        vm.MonthlyTableOrientation = SeasonalityMonthlyTableOrientation.MonthsAsColumns;

        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingAFontSize_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());

        vm.LegendFontSize = 20f;

        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingAYearColor_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());

        vm.YearColors[3] = HsvData.FromHtmlSafe("#FF123456");

        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingTheNeutralColor_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());

        vm.MonthlyNeutralColor = HsvData.FromHtmlSafe("#FF654321");

        Assert.True(vm.IsModified);
    }

    [Fact]
    public async Task Save_RoundTripsEveryValueOntoGlobalChartSettings()
    {
        var manager = new MockChartSettingsManager();
        var vm = Create(manager);

        vm.YearsToOverlay = 42;
        vm.StartMonth = 4;
        vm.LineThickness = 2.5f;
        vm.EndpointMarkerRadius = 8.5f;
        vm.MonthlyTableOrientation = SeasonalityMonthlyTableOrientation.MonthsAsColumns;
        vm.LegendFontSize = 18f;
        vm.AxisFontSize = 22f;
        vm.MonthlyTableFontSize = 11f;
        vm.MonthlyUpColor = HsvData.FromHtmlSafe("#FF0000FF");
        vm.MonthlyDownColor = HsvData.FromHtmlSafe("#FFFF00FF");
        vm.MonthlyNeutralColor = HsvData.FromHtmlSafe("#FF808080");
        vm.YearColors[0] = HsvData.FromHtmlSafe("#FF112233");
        vm.YearColors[9] = HsvData.FromHtmlSafe("#FF445566");

        await vm.SaveChangesAsync();

        GlobalChartSettings s = manager.Current;
        Assert.Equal(42u, s.SeasonalityYearsToOverlay);
        Assert.Equal(4u, s.SeasonalityStartMonth);
        Assert.Equal(2.5f, s.SeasonalityLineThickness);
        Assert.Equal(8.5f, s.SeasonalityEndpointMarkerRadius);
        Assert.Equal(SeasonalityMonthlyTableOrientation.MonthsAsColumns, s.SeasonalityMonthlyTableOrientation);
        Assert.Equal(18f, s.SeasonalityLegendFontSize);
        Assert.Equal(22f, s.SeasonalityAxisFontSize);
        Assert.Equal(11f, s.SeasonalityMonthlyTableFontSize);
        Assert.Equal(HsvData.FromHtmlSafe("#FF0000FF"), HsvData.FromHtmlSafe(s.SeasonalityMonthlyUpColor));
        Assert.Equal(HsvData.FromHtmlSafe("#FFFF00FF"), HsvData.FromHtmlSafe(s.SeasonalityMonthlyDownColor));
        Assert.Equal(HsvData.FromHtmlSafe("#FF808080"), HsvData.FromHtmlSafe(s.SeasonalityMonthlyNeutralColor));
        Assert.Equal(HsvData.FromHtmlSafe("#FF112233"), HsvData.FromHtmlSafe(s.SeasonalityYearColor1));
        Assert.Equal(HsvData.FromHtmlSafe("#FF445566"), HsvData.FromHtmlSafe(s.SeasonalityYearColor10));
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ResetToDefault_RestoresThePageDefaultsAndFlagsTheDifferenceFromTheSnapshot()
    {
        var manager = new MockChartSettingsManager();
        manager.UpdatePreview(manager.Current with { SeasonalityLegendFontSize = 30f });
        var vm = Create(manager);
        Assert.Equal(30f, vm.LegendFontSize);

        vm.ResetToDefault();

        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityFontSize, vm.LegendFontSize);
        Assert.True(vm.IsModified);
    }
}
