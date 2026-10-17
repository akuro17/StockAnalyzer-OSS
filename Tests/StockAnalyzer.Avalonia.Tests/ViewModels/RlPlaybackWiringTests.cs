using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>Window wiring, Settings tree and editor validation of RL Playback (a Tools window since the model-execution separation, T1).</summary>
public sealed partial class RlUiTests
{
    [Fact]
    public void Playback_IsNoLongerASettingsCategoryAndBacktestStaysAboveTrainingWizard()
    {
        var keys = SettingsConstants.Categories.Select(c => c.Key).ToList();
        Assert.DoesNotContain(SettingsConstants.Categories, c => c.TitleKey == "Settings_RlPlayback");
        Assert.Contains(SettingsConstants.Keys.AIPredictions, keys);
        Assert.Equal(keys.IndexOf(SettingsConstants.Keys.TrainingWizard) - 1, keys.IndexOf(SettingsConstants.Keys.Backtest));
    }

    [AvaloniaFact]
    public async Task Playback_WindowStaysOpenAfterASuccessfulReplay_AndHasNoCloseAfterShowOption()
    {
        var f = await Playback(); var vm = f.Vm;
        var window = new StockAnalyzer.Avalonia.Views.RlPlaybackWindow { DataContext = vm };
        window.Show();
        try
        {
            Assert.True(window.IsVisible);
            Assert.Empty(window.GetVisualDescendants().OfType<CheckBox>());   // the footer holds only Reset and Close
            await vm.ShowOnChartCommand.ExecuteAsync(null);
            Assert.True(window.IsVisible);
            Assert.NotEqual(RlDialogOperation.Disposed, vm.Operation);
            Assert.Single(f.Target.Applied);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Playback_WindowDisposesTheViewModelWhenItIsClosedByTheUser()
    {
        var f = await Playback(); var vm = f.Vm;
        var window = new StockAnalyzer.Avalonia.Views.RlPlaybackWindow { DataContext = vm };
        window.Show();
        window.Close();
        Assert.Equal(RlDialogOperation.Disposed, vm.Operation);
        Assert.Empty(f.Target.Applied);
    }

    [AvaloniaFact]
    public async Task Playback_ViewLocatorBuildsThePlaybackView()
    {
        var f = await Playback(); using var vm = f.Vm;
        var view = new ViewLocator().Build(vm);
        Assert.IsType<StockAnalyzer.Avalonia.Views.Dialogs.RlPlaybackView>(view);
    }

    [AvaloniaFact]
    public async Task Playback_DecisionEditorReportsBlankFractionalAndAcceptsIntegersUpToTheHorizon()
    {
        var f = await Playback(); using var vm = f.Vm;
        var number = new NumericUpDown { Value = 0, DataContext = vm };
        RlInputValidation.SetField(number, nameof(vm.Decision));
        var window = new Window { Content = number }; window.Show();
        try
        {
            number.Value = 1.5m; number.Text = "1.5"; Assert.False(vm.DecisionEditorValid); Assert.False(vm.CanShowOnChart);
            number.Text = ""; Assert.False(vm.DecisionEditorValid);
            number.Value = 0; number.Text = "0"; Assert.True(vm.DecisionEditorValid); Assert.True(vm.CanShowOnChart);
            string last = (RlContract.EpisodeHorizon - 1).ToString(CultureInfo.CurrentCulture);
            number.Value = RlContract.EpisodeHorizon - 1; number.Text = last; Assert.True(vm.DecisionEditorValid);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Playback_ViewShowsTheFreeReplayCardBoundToTheChildViewModelAndItsDecisionEditorReports()
    {
        var registry = new EmptyRegistry { Generations = new[] { PlaybackGeneration("a") } };
        var data = new PlaybackData(registry); var target = new RecordingReplayTarget();
        var free = new RlFreeReplayViewModel(data, target, new StockAnalyzer.Avalonia.Tests.TestHelpers.FakeMarketDataProvider(new[] { "SPY", "QQQ" }),
            new StockAnalyzer.Avalonia.Tests.TestHelpers.FakeTickerStateStore() is var store ? new TickerStateRlListSource(store) : null!,
            NullLocalizationService.Instance, new SynchronousDispatcherService());
        using var page = new RlPlaybackViewModel(registry, data, target, NullLocalizationService.Instance, new SynchronousDispatcherService(), free);
        await page.RefreshCommand.ExecuteAsync(null);
        await PlaybackUntil(() => page.BlocksState == RlDialogLoadState.Loaded && free.HasRangeText);
        var view = new StockAnalyzer.Avalonia.Views.Dialogs.RlPlaybackView { DataContext = page };
        var window = new Window { Content = view, Width = 900, Height = 1000 }; window.Show();
        try
        {
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            T Find<T>(string id) where T : Control => view.GetVisualDescendants().OfType<T>()
                .Single(c => global::Avalonia.Automation.AutomationProperties.GetAutomationId(c) == id);
            var card = Find<Border>(RlUiConstants.PlaybackFreeCard);
            Assert.True(card.IsVisible);
            Assert.Equal("SPY", Find<AutoCompleteBox>(RlUiConstants.PlaybackFreeTicker).Text);
            Assert.Equal(free.AnchorDate, Find<CalendarDatePicker>(RlUiConstants.PlaybackFreeDate).SelectedDate);
            Assert.Equal(2, Find<ComboBox>(RlUiConstants.PlaybackFreeList).ItemCount + 1);
            Assert.Equal(5, Find<ComboBox>(RlUiConstants.PlaybackFreePolicies).ItemCount);
            Assert.True(Find<Button>(RlUiConstants.PlaybackFreeShow).IsEffectivelyEnabled);
            var number = Find<NumericUpDown>(RlUiConstants.PlaybackFreeDecision);
            number.Value = 1.5m; number.Text = "1.5";
            Assert.False(free.DecisionEditorValid); global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(Find<Button>(RlUiConstants.PlaybackFreeShow).IsEffectivelyEnabled);
            number.Value = 3; number.Text = "3";
            Assert.True(free.DecisionEditorValid); Assert.Equal(3, free.Decision);
            free.TickerText = "NOPE";
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(Find<Button>(RlUiConstants.PlaybackFreeShow).IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Playback_EditorValidationIgnoresAnUnrelatedDataContext()
    {
        var number = new NumericUpDown { Value = 1, DataContext = new object() };
        var exception = Record.Exception(() => RlInputValidation.SetField(number, "Decision"));
        Assert.Null(exception);
    }
}
