using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class BacktestSettingsViewModelTests
{
    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"backtest-settings-vm-{Guid.NewGuid():N}.json");

    private static async Task<(BacktestSettingsViewModel Vm, BacktestDefaultSettingsManager Manager, string Path)> CreateAsync()
    {
        string path = NewTempPath();
        var manager = new BacktestDefaultSettingsManager(path);
        var vm = new BacktestSettingsViewModel(manager);
        await vm.LoadTask;
        return (vm, manager, path);
    }

    [Fact]
    public void Category_IsListedImmediatelyAboveNotes()
    {
        var keys = SettingsConstants.Categories.Select(c => c.Key).ToList();

        int backtest = keys.IndexOf(SettingsConstants.Keys.Backtest);
        int notes = keys.IndexOf(SettingsConstants.Keys.Notes);

        Assert.True(backtest >= 0);
        Assert.Equal(notes - 1, backtest);
    }

    [Fact]
    public void SettingsViewModel_NavigatesToTheBacktestPage()
    {
        string path = NewTempPath();
        var services = new ServiceCollection();
        // The constructor opens the first category (Theme) before the test navigates to Backtest.
        services.AddSingleton<IThemeManager>(new ThemeManager());
        services.AddTransient<ThemeSettingsViewModel>();
        services.AddSingleton<IBacktestDefaultSettingsManager>(new BacktestDefaultSettingsManager(path));
        services.AddTransient<BacktestSettingsViewModel>();
        var settings = new SettingsViewModel(services.BuildServiceProvider());

        settings.SelectedCategory = SettingsConstants.Categories.First(c => c.Key == SettingsConstants.Keys.Backtest);

        Assert.IsType<BacktestSettingsViewModel>(settings.CurrentPage);
    }

    [Fact]
    public async Task NoSavedFile_ShowsFactoryDefaults_AndIsNotModified()
    {
        var (vm, _, _) = await CreateAsync();

        Assert.Equal(1_000_000m, vm.InitialCapital);
        Assert.Equal(0.30m, vm.InitialMarginRatio);
        Assert.Equal(252, vm.AnnualPeriodsD1);
        Assert.Equal(2000, vm.BootstrapIterations);
        Assert.False(vm.IsModified);
        Assert.True(vm.IsValid);
        Assert.False(vm.HasValidationMessage);
    }

    [Fact]
    public async Task Edit_MarksModified_AndRevertRestoresSnapshot()
    {
        var (vm, _, _) = await CreateAsync();

        vm.InitialCapital = 5m;

        Assert.True(vm.IsModified);
        vm.RevertChanges();
        Assert.Equal(1_000_000m, vm.InitialCapital);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task ResetToDefault_ReturnsToFactoryValues()
    {
        var (vm, _, _) = await CreateAsync();
        vm.SlippageRatio = 0.01m;
        vm.BootstrapSeed = 99;

        vm.ResetToDefault();

        Assert.Equal(0m, vm.SlippageRatio);
        Assert.Equal(42, vm.BootstrapSeed);
    }

    [Fact]
    public async Task ValidEdit_SavesAndClearsModified_AndIsLoadedByANewPage()
    {
        var (vm, manager, path) = await CreateAsync();
        try
        {
            vm.InitialCapital = 500_000m;
            vm.SizingModel = PositionSizingModel.PercentOfEquity;
            vm.SizingParameter = 0.5m;

            await vm.SaveChangesAsync();

            Assert.False(vm.IsModified);
            Assert.True(vm.IsValid);
            var reloaded = new BacktestSettingsViewModel(manager);
            await reloaded.LoadTask;
            Assert.Equal(500_000m, reloaded.InitialCapital);
            Assert.Equal(PositionSizingModel.PercentOfEquity, reloaded.SizingModel);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task InvalidEdit_ShowsReasonOnTheItsOwnItemOnly_AndSaveDoesNotWriteTheFile()
    {
        var (vm, _, path) = await CreateAsync();

        vm.MaintenanceMarginRatio = vm.InitialMarginRatio;

        Assert.False(vm.IsValid);
        Assert.NotEmpty(vm.MaintenanceMarginRatioError);
        Assert.Empty(vm.BootstrapIterationsError);
        await vm.SaveChangesAsync();
        Assert.False(vm.IsValid);
        Assert.True(vm.IsModified);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task InvalidBootstrapIterations_IsReportedOnThatItemNotOnMargin()
    {
        var (vm, _, _) = await CreateAsync();

        vm.BootstrapIterations = 10;

        Assert.NotEmpty(vm.BootstrapIterationsError);
        Assert.Empty(vm.MaintenanceMarginRatioError);
        Assert.Empty(vm.InitialMarginRatioError);
    }

    [Fact]
    public async Task FixingTheInvalidValue_ClearsTheMessage()
    {
        var (vm, _, _) = await CreateAsync();
        vm.BootstrapIterations = 10;
        Assert.False(vm.IsValid);

        vm.BootstrapIterations = 3000;

        Assert.True(vm.IsValid);
        Assert.Empty(vm.BootstrapIterationsError);
    }
}
