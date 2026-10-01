using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>Group E (equity curve colors) of the Settings > Backtest page: stored in the global chart settings, not in the Backtest defaults.</summary>
public class BacktestSettingsViewModelEquityColorTests
{
    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"backtest-settings-equity-{Guid.NewGuid():N}.json");

    private static async Task<(BacktestSettingsViewModel Vm, FakeChartSettingsManager Chart, string Path)> CreateAsync(GlobalChartSettings? initial = null)
    {
        var chart = new FakeChartSettingsManager();
        if (initial is not null) chart.Publish(initial);
        string path = NewTempPath();
        var vm = new BacktestSettingsViewModel(new BacktestDefaultSettingsManager(path), chart);
        await vm.LoadTask;
        return (vm, chart, path);
    }

    private static HsvData Color(string html) => HsvData.FromHtmlSafe(html);

    [Fact]
    public async Task Load_ShowsTheCurrentChartSettings_AndIsNotModified()
    {
        var (vm, _, _) = await CreateAsync(new GlobalChartSettings
        {
            BacktestEquityColorMode = BacktestEquityColorMode.PreviousBar,
            BacktestEquityLineColor = "#FF010203",
            BacktestEquityUpColor = "#040506",
            BacktestEquityDownColor = "#FF070809",
        });

        Assert.Equal(BacktestEquityColorMode.PreviousBar, vm.EquityColorMode);
        Assert.Equal("#FF010203", vm.EquityLineColor.ToHtml());
        Assert.Equal("#FF040506", vm.EquityUpColor.ToHtml()); // a 6-digit persisted value is shown opaque
        Assert.Equal("#FF070809", vm.EquityDownColor.ToHtml());
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task Defaults_AreDrawdownWithTheDocumentedColors()
    {
        var (vm, _, _) = await CreateAsync();

        Assert.Equal(BacktestEquityColorMode.Drawdown, vm.EquityColorMode);
        Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityLineColor, vm.EquityLineColor.ToHtml());
        Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityUpColor, vm.EquityUpColor.ToHtml());
        Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityDownColor, vm.EquityDownColor.ToHtml());
        Assert.Equal(3, vm.EquityColorModeOptions.Count);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task EachGroupEEdit_MarksModified_AndRevertRestoresIt()
    {
        var (vm, _, _) = await CreateAsync();
        Action[] edits =
        {
            () => vm.EquityColorMode = BacktestEquityColorMode.Single,
            () => vm.EquityLineColor = Color("#FF112233"),
            () => vm.EquityUpColor = Color("#FF112233"),
            () => vm.EquityDownColor = Color("#FF112233"),
        };

        foreach (Action edit in edits)
        {
            edit();
            Assert.True(vm.IsModified);
            vm.RevertChanges();
            Assert.False(vm.IsModified);
            Assert.Equal(BacktestEquityColorMode.Drawdown, vm.EquityColorMode);
            Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityLineColor, vm.EquityLineColor.ToHtml());
        }
    }

    [Fact]
    public async Task ModeFlags_AreMutuallyExclusive_AndFollowTheMode()
    {
        var (vm, _, _) = await CreateAsync();

        foreach (BacktestEquityColorMode mode in Enum.GetValues<BacktestEquityColorMode>())
        {
            vm.EquityColorMode = mode;
            bool[] flags = { vm.IsEquityColorModeSingle, vm.IsEquityColorModePreviousBar, vm.IsEquityColorModeDrawdown };

            Assert.Equal(1, flags.Count(f => f));
            Assert.Equal(mode == BacktestEquityColorMode.Single, vm.IsEquityColorModeSingle);
            Assert.Equal(mode == BacktestEquityColorMode.PreviousBar, vm.IsEquityColorModePreviousBar);
            Assert.Equal(mode == BacktestEquityColorMode.Drawdown, vm.IsEquityColorModeDrawdown);
        }
    }

    [Fact]
    public async Task Save_WritesOnlyTheFourValues_OntoTheLatestCurrentSettings()
    {
        var (vm, chart, path) = await CreateAsync();
        try
        {
            vm.EquityColorMode = BacktestEquityColorMode.PreviousBar;
            vm.EquityUpColor = Color("#FF112233");
            // Another part of the app changes an unrelated setting after this page opened.
            chart.Publish(chart.Current with { TopMargin = 17f });

            await vm.SaveChangesAsync();

            Assert.Equal(1, chart.UpdateCount);
            Assert.Equal(17f, chart.Current.TopMargin);
            Assert.Equal(BacktestEquityColorMode.PreviousBar, chart.Current.BacktestEquityColorMode);
            Assert.Equal("#FF112233", chart.Current.BacktestEquityUpColor);
            Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityLineColor, chart.Current.BacktestEquityLineColor);
            Assert.False(vm.IsModified);
            Assert.False(vm.HasValidationMessage);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Save_WithoutAnyGroupEEdit_DoesNotWriteTheChartSettings()
    {
        var (vm, chart, path) = await CreateAsync();
        try
        {
            vm.InitialCapital = 500_000m;

            await vm.SaveChangesAsync();

            Assert.Equal(0, chart.UpdateCount);
            Assert.False(vm.IsModified);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Save_AfterTheSaved_IsNotModifiedAgain_AndANewPageLoadsTheSavedValues()
    {
        var (vm, chart, path) = await CreateAsync();
        try
        {
            vm.EquityColorMode = BacktestEquityColorMode.Single;
            vm.EquityLineColor = Color("#FF445566");
            await vm.SaveChangesAsync();

            var reopened = new BacktestSettingsViewModel(new BacktestDefaultSettingsManager(path), chart);
            await reopened.LoadTask;

            Assert.Equal(BacktestEquityColorMode.Single, reopened.EquityColorMode);
            Assert.Equal("#FF445566", reopened.EquityLineColor.ToHtml());
            Assert.False(reopened.IsModified);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ResetToDefault_UsesTheGlobalChartSettingsDefaults_AndNeedsASaveToPersist()
    {
        var (vm, chart, _) = await CreateAsync(new GlobalChartSettings
        {
            BacktestEquityColorMode = BacktestEquityColorMode.Single,
            BacktestEquityLineColor = "#FF445566",
        });
        Assert.False(vm.IsModified);

        vm.ResetToDefault();

        Assert.Equal(BacktestEquityColorMode.Drawdown, vm.EquityColorMode);
        Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityLineColor, vm.EquityLineColor.ToHtml());
        Assert.True(vm.IsModified);
        Assert.Equal(BacktestEquityColorMode.Single, chart.Current.BacktestEquityColorMode);
        Assert.Equal(0, chart.UpdateCount);
    }

    [Fact]
    public async Task GroupEEdit_DoesNotChangeTheDefaultsValidity()
    {
        var (vm, _, _) = await CreateAsync();
        bool validBefore = vm.IsValid;

        vm.EquityColorMode = BacktestEquityColorMode.Single;
        vm.EquityDownColor = Color("#FF000000");

        Assert.Equal(validBefore, vm.IsValid);
        Assert.True(vm.IsValid);
    }

    [Fact]
    public async Task InvalidDefaults_BlockTheWholeSave_IncludingGroupE()
    {
        var (vm, chart, path) = await CreateAsync();
        vm.EquityColorMode = BacktestEquityColorMode.Single;
        vm.MaintenanceMarginRatio = vm.InitialMarginRatio;

        await vm.SaveChangesAsync();

        Assert.Equal(0, chart.UpdateCount);
        Assert.True(vm.IsModified);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WithoutAChartSettingsManager_GroupEIsInert()
    {
        string path = NewTempPath();
        try
        {
            var vm = new BacktestSettingsViewModel(new BacktestDefaultSettingsManager(path));
            await vm.LoadTask;
            Assert.Equal(BacktestEquityColorMode.Drawdown, vm.EquityColorMode);

            vm.EquityColorMode = BacktestEquityColorMode.Single;
            Assert.False(vm.IsModified);

            await vm.SaveChangesAsync(); // must not throw
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
