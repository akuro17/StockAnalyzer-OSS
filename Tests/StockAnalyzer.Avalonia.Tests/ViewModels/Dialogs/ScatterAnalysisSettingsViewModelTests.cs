using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Dialogs;

public class ScatterAnalysisSettingsViewModelTests
{
    private static ScatterAnalysisSettingsViewModel Create(MockChartSettingsManager manager)
        => new(manager, NullLogger<ScatterAnalysisSettingsViewModel>.Instance);

    [Fact]
    public void Load_TakesEveryValueFromGlobalChartSettingsDefaults()
    {
        var vm = Create(new MockChartSettingsManager());

        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapUpColor), vm.UpColor);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapNeutralColor), vm.NeutralColor);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapDownColor), vm.DownColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapThresholdPercent, vm.ThresholdPercent);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapMultiplier, vm.HeatMultiplier);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapStepCount, vm.StepCount);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapFontSize, vm.FontSize);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapTextColor), vm.TextColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapTextAlignment, vm.TextAlignment);

        Assert.Equal((decimal)ChartSettingsConstants.MinScatterHeatmapThresholdPercent, vm.MinThresholdPercent);
        Assert.Equal((decimal)ChartSettingsConstants.MaxScatterHeatmapThresholdPercent, vm.MaxThresholdPercent);
        Assert.Equal((decimal)ChartSettingsConstants.ScatterHeatmapThresholdPercentStep, vm.ThresholdPercentStep);

        Assert.Equal((decimal)ChartSettingsConstants.MinScatterHeatmapMultiplier, vm.MinMultiplier);
        Assert.Equal((decimal)ChartSettingsConstants.MaxScatterHeatmapMultiplier, vm.MaxMultiplier);
        Assert.Equal((decimal)ChartSettingsConstants.ScatterHeatmapMultiplierStep, vm.MultiplierStep);

        Assert.Equal((decimal)ChartSettingsConstants.MinScatterHeatmapStepCount, vm.MinStepCount);
        Assert.Equal((decimal)ChartSettingsConstants.MaxScatterHeatmapStepCount, vm.MaxStepCount);
        Assert.Equal(1m, vm.StepCountStep);

        Assert.Equal((decimal)ChartSettingsConstants.MinScatterHeatmapFontSize, vm.MinFontSize);
        Assert.Equal((decimal)ChartSettingsConstants.MaxScatterHeatmapFontSize, vm.MaxFontSize);
        Assert.Equal((decimal)ChartSettingsConstants.ScatterHeatmapFontSizeStep, vm.FontSizeStep);
        Assert.Equal(3, vm.AvailableTextAlignments.Count);

        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ChangingUpColor_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());
        vm.UpColor = HsvData.FromHtmlSafe("#FF123456");
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingNeutralColor_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());
        vm.NeutralColor = HsvData.FromHtmlSafe("#FF654321");
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingDownColor_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());
        vm.DownColor = HsvData.FromHtmlSafe("#FFABCDEF");
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingThresholdPercent_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());
        vm.ThresholdPercent = 8.5;
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingHeatMultiplier_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());
        vm.HeatMultiplier = 2.0;
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingStepCount_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());
        vm.StepCount = 20;
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingFontSize_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());
        vm.FontSize = 20.0;
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingTextColor_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());
        vm.TextColor = HsvData.FromHtmlSafe("#FFFF1122");
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void ChangingTextAlignment_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());
        vm.TextAlignment = StockAnalyzer.Core.Models.Analysis.HeatmapTextAlignment.Right;
        Assert.True(vm.IsModified);
    }

    [Fact]
    public async Task Save_RoundTripsEveryValueOntoGlobalChartSettings()
    {
        var manager = new MockChartSettingsManager();
        var vm = Create(manager);

        vm.UpColor = HsvData.FromHtmlSafe("#FF00FF00");
        vm.NeutralColor = HsvData.FromHtmlSafe("#FF333333");
        vm.DownColor = HsvData.FromHtmlSafe("#FFFF0000");
        vm.ThresholdPercent = 7.5;
        vm.HeatMultiplier = 1.5;
        vm.StepCount = 25;
        vm.FontSize = 20.0;
        vm.TextColor = HsvData.FromHtmlSafe("#FFEEEEEE");
        vm.TextAlignment = StockAnalyzer.Core.Models.Analysis.HeatmapTextAlignment.Left;

        await vm.SaveChangesAsync();

        GlobalChartSettings s = manager.Current;
        Assert.Equal(HsvData.FromHtmlSafe("#FF00FF00"), HsvData.FromHtmlSafe(s.ScatterHeatmapUpColor));
        Assert.Equal(HsvData.FromHtmlSafe("#FF333333"), HsvData.FromHtmlSafe(s.ScatterHeatmapNeutralColor));
        Assert.Equal(HsvData.FromHtmlSafe("#FFFF0000"), HsvData.FromHtmlSafe(s.ScatterHeatmapDownColor));
        Assert.Equal(7.5, s.ScatterHeatmapThresholdPercent);
        Assert.Equal(1.5, s.ScatterHeatmapMultiplier);
        Assert.Equal(25u, s.ScatterHeatmapStepCount);
        Assert.Equal(20.0, s.ScatterHeatmapFontSize);
        Assert.Equal(HsvData.FromHtmlSafe("#FFEEEEEE"), HsvData.FromHtmlSafe(s.ScatterHeatmapTextColor));
        Assert.Equal(StockAnalyzer.Core.Models.Analysis.HeatmapTextAlignment.Left, s.ScatterHeatmapTextAlignment);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ResetToDefault_RestoresThePageDefaultsAndFlagsModified()
    {
        var manager = new MockChartSettingsManager();
        manager.UpdatePreview(manager.Current with
        {
            ScatterHeatmapThresholdPercent = 12.0,
            ScatterHeatmapStepCount = 30,
            ScatterHeatmapFontSize = 22.0,
            ScatterHeatmapTextAlignment = StockAnalyzer.Core.Models.Analysis.HeatmapTextAlignment.Right
        });
        var vm = Create(manager);
        Assert.Equal(12.0, vm.ThresholdPercent);
        Assert.Equal(30u, vm.StepCount);
        Assert.Equal(22.0, vm.FontSize);
        Assert.Equal(StockAnalyzer.Core.Models.Analysis.HeatmapTextAlignment.Right, vm.TextAlignment);
        Assert.False(vm.IsModified);

        vm.ResetToDefault();

        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapThresholdPercent, vm.ThresholdPercent);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapStepCount, vm.StepCount);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapFontSize, vm.FontSize);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapTextAlignment, vm.TextAlignment);
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void Categories_ContainsScatterAnalysisDirectlyAfterSeasonalityChartAndBeforeChart()
    {
        var categories = SettingsConstants.Categories.ToList();
        var seasonalityIndex = categories.FindIndex(c => c.Key == SettingsConstants.Keys.SeasonalityChart);
        var scatterIndex = categories.FindIndex(c => c.Key == SettingsConstants.Keys.ScatterAnalysis);
        var chartIndex = categories.FindIndex(c => c.Key == SettingsConstants.Keys.Chart);

        Assert.True(seasonalityIndex >= 0);
        Assert.True(scatterIndex >= 0);
        Assert.True(chartIndex >= 0);
        Assert.Equal(seasonalityIndex + 1, scatterIndex);
        Assert.Equal(scatterIndex + 1, chartIndex);

        var category = categories[scatterIndex];
        Assert.Equal("Tab_ScatterAnalysis", category.TitleKey);
        Assert.Equal("SettingsChartIcon", category.IconKey);
    }

    [Fact]
    public void SettingsViewModel_ResolvesScatterAnalysisPage()
    {
        var services = new ServiceCollection();
        var manager = new MockChartSettingsManager();
        services.AddSingleton<IChartSettingsManager>(manager);
        services.AddSingleton<IThemeManager>(new ThemeManager());
        services.AddTransient<ThemeSettingsViewModel>();
        services.AddTransient<ScatterAnalysisSettingsViewModel>();
        var provider = services.BuildServiceProvider();

        using var sut = new SettingsViewModel(provider);
        var category = SettingsConstants.Categories.First(c => c.Key == SettingsConstants.Keys.ScatterAnalysis);

        sut.SelectedCategory = category;

        Assert.NotNull(sut.CurrentPage);
        Assert.IsType<ScatterAnalysisSettingsViewModel>(sut.CurrentPage);
    }
}
