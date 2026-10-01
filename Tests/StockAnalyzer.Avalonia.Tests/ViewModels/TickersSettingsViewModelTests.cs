using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Tickers;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>sa_implement (Tickers settings category, Phase 4/5): TickersSettingsViewModel contract and the
/// settings shell wiring (category order, page resolution).</summary>
public class TickersSettingsViewModelTests
{
    [Fact]
    public void Constructor_InitializesFromManager_AndIsNotModified()
    {
        var manager = new FakeTickersSettingsManager();
        manager.SetAutoPlayIntervalSeconds(20);
        manager.SetColumnSelectionScope(TickerColumnSelectionScope.Shared);

        var vm = new TickersSettingsViewModel(manager);

        Assert.Equal(20, vm.SelectedAutoPlayIntervalSeconds);
        Assert.Equal(TickerColumnSelectionScope.Shared, vm.SelectedColumnSelectionScope);
        Assert.True(vm.SelectedAutoPlayStopAtListEnd);
        Assert.True(vm.SelectedAutoPlayStopOnListChange);
        Assert.False(vm.IsModified);
        Assert.Equal(manager.AutoPlayMinIntervalSeconds, vm.AutoPlayMinIntervalSeconds);
        Assert.Equal(manager.AutoPlayMaxIntervalSeconds, vm.AutoPlayMaxIntervalSeconds);
    }

    [Fact]
    public void ChangingEachProperty_UpdatesManagerLive_AndMarksModified()
    {
        var manager = new FakeTickersSettingsManager();
        var vm = new TickersSettingsViewModel(manager);

        vm.SelectedAutoPlayStopAtListEnd = false;
        Assert.False(manager.AutoPlayStopAtListEnd);
        Assert.True(vm.IsModified);

        vm.SelectedAutoPlayStopOnListChange = false;
        Assert.False(manager.AutoPlayStopOnListChange);

        vm.SelectedAutoPlayIntervalSeconds = 30;
        Assert.Equal(30, manager.AutoPlayIntervalSeconds);

        vm.SelectedColumnSelectionScope = TickerColumnSelectionScope.Shared;
        Assert.Equal(TickerColumnSelectionScope.Shared, manager.ColumnSelectionScope);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100000)]
    public void RejectedInterval_RestoresTheControlToTheAppliedValue(int rejected)
    {
        var manager = new FakeTickersSettingsManager();
        var vm = new TickersSettingsViewModel(manager);

        vm.SelectedAutoPlayIntervalSeconds = rejected;

        Assert.Equal(manager.AutoPlayDefaultIntervalSeconds, vm.SelectedAutoPlayIntervalSeconds);
        Assert.Equal(manager.AutoPlayDefaultIntervalSeconds, manager.AutoPlayIntervalSeconds);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void RevertChanges_RestoresSnapshotAndManager()
    {
        var manager = new FakeTickersSettingsManager();
        var vm = new TickersSettingsViewModel(manager);
        vm.SelectedAutoPlayStopAtListEnd = false;
        vm.SelectedAutoPlayIntervalSeconds = 44;
        vm.SelectedColumnSelectionScope = TickerColumnSelectionScope.Shared;

        vm.RevertChanges();

        Assert.True(manager.AutoPlayStopAtListEnd);
        Assert.Equal(manager.AutoPlayDefaultIntervalSeconds, manager.AutoPlayIntervalSeconds);
        Assert.Equal(TickerColumnSelectionScope.PerList, manager.ColumnSelectionScope);
        Assert.True(vm.SelectedAutoPlayStopAtListEnd);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async System.Threading.Tasks.Task SaveChanges_PersistsAndResetsModifiedFlag()
    {
        var manager = new FakeTickersSettingsManager();
        var vm = new TickersSettingsViewModel(manager);
        vm.SelectedAutoPlayIntervalSeconds = 15;
        Assert.True(vm.IsModified);

        await vm.SaveChangesAsync();

        Assert.Equal(1, manager.SaveCount);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ResetToDefault_UsesTheManagerDefaults()
    {
        var manager = new FakeTickersSettingsManager();
        manager.SetAutoPlayStopAtListEnd(false);
        manager.SetAutoPlayStopOnListChange(false);
        manager.SetAutoPlayIntervalSeconds(99);
        manager.SetColumnSelectionScope(TickerColumnSelectionScope.Shared);
        var vm = new TickersSettingsViewModel(manager);

        vm.ResetToDefault();

        Assert.True(vm.SelectedAutoPlayStopAtListEnd);
        Assert.True(vm.SelectedAutoPlayStopOnListChange);
        Assert.Equal(manager.AutoPlayDefaultIntervalSeconds, vm.SelectedAutoPlayIntervalSeconds);
        Assert.Equal(TickerColumnSelectionScope.PerList, vm.SelectedColumnSelectionScope);
    }

    [Fact]
    public void TitleAndIconKeys_AreTheTickersKeys()
    {
        var vm = new TickersSettingsViewModel(new FakeTickersSettingsManager());

        Assert.Equal("Settings_Tickers", vm.TitleKey);
        Assert.Equal("SettingsTickersIcon", vm.IconKey);
    }

    [Fact]
    public void Category_IsListedImmediatelyAboveNotes()
    {
        var keys = SettingsConstants.Categories.Select(c => c.Key).ToList();

        int tickers = keys.IndexOf(SettingsConstants.Keys.Tickers);
        int notes = keys.IndexOf(SettingsConstants.Keys.Notes);

        Assert.True(tickers >= 0);
        Assert.Equal(notes - 1, tickers);
    }

    [Fact]
    public void SettingsViewModel_NavigatesToTheTickersPage()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IThemeManager>(new ThemeManager());
        services.AddTransient<ThemeSettingsViewModel>();
        services.AddSingleton<ITickersSettingsManager>(new FakeTickersSettingsManager());
        services.AddTransient<TickersSettingsViewModel>();
        var shell = new SettingsViewModel(services.BuildServiceProvider());

        shell.SelectedCategory = SettingsConstants.Categories.Single(c => c.Key == SettingsConstants.Keys.Tickers);

        Assert.IsType<TickersSettingsViewModel>(shell.CurrentPage);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void EnumConverterKeys_ExistInEveryShippedLocale(string locale)
    {
        // Converter-resolved keys are not covered by the XAML localization scan (SA_ARCHITECTURE_RULES).
        var path = System.IO.Path.Combine(TestSolution.Root, "StockAnalyzer.Avalonia", "Resources", "Locales", locale + ".json");
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));

        foreach (var value in Enum.GetValues<TickerColumnSelectionScope>())
        {
            Assert.True(doc.RootElement.TryGetProperty($"{nameof(TickerColumnSelectionScope)}_{value}", out _),
                $"{locale}.json is missing {nameof(TickerColumnSelectionScope)}_{value}");
        }
    }

    [Fact]
    public void SettingsView_UsesOnlyGlobalTextStylesAndBoundNumericLimits()
    {
        var path = System.IO.Path.Combine(TestSolution.Root, "StockAnalyzer.Avalonia", "Views", "Dialogs", "TickersSettingsView.axaml");
        var xaml = System.IO.File.ReadAllText(path);

        Assert.DoesNotMatch(@"FontSize=""\d", xaml);
        Assert.DoesNotMatch(@"Foreground=""#", xaml);
        Assert.DoesNotMatch(@"(Minimum|Maximum)=""-?\d", xaml);
        Assert.Contains("DynamicResource BaseFontSize", xaml);
        Assert.Contains("DynamicResource DetailFontSize", xaml);
        Assert.Contains("DynamicResource HelperFontSize", xaml);
        Assert.Contains("Minimum=\"{Binding AutoPlayMinIntervalSeconds}\"", xaml);
        Assert.Contains("Maximum=\"{Binding AutoPlayMaxIntervalSeconds}\"", xaml);
    }
}
