using System;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>The pre-filled start and end date of the RL window (fake stored-period advisor, manual clock for the debounce).</summary>
public sealed partial class RlUiTests
{
    private static readonly DateOnly PeriodLatest = new(2026, 10, 8), PeriodStart = new(2006, 3, 1);
    private static int StandardBars => RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.Standard);
    private static int ShortBars => RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory);

    private static global::StockAnalyzer.Core.Tests.Services.ManualTimeProvider NewClock()
        => new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

    private static FakeStoredPeriodAdvisor Advisor(DateOnly? start = null, DateOnly? latest = null)
        => new() { Rl = (_, required, _) => Task.FromResult(FakeStoredPeriodAdvisor.Found(start ?? PeriodStart, latest ?? PeriodLatest, required)) };

    private async Task<RlTrainingDialogViewModel> PeriodViewModel(ChartViewModel chart, FakeStoredPeriodAdvisor? advisor, TimeProvider clock, string symbol = "SPY")
    {
        LocalizationManager.Instance.Initialize("en");
        chart.Symbol = symbol;
        var vm = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, new LocalizationService(),
            periodAdvisor: advisor, clock: clock);
        await vm.InitializeAsync();
        return vm;
    }

    /// <summary>Lets the debounce of the last input change elapse and waits until the advisor has been asked <paramref name="calls"/> times.</summary>
    private static async Task ElapseDebounce(global::StockAnalyzer.Core.Tests.Services.ManualTimeProvider clock, FakeStoredPeriodAdvisor advisor, int calls)
    {
        await clock.TimerArmed.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(StoredPeriodTiming.Debounce);
        await Until(() => advisor.RlCalls.Count >= calls);
        await Task.Delay(20);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "Timed out waiting for the condition.");
    }

    [Fact]
    public async Task Period_OpensWithBothBoxesFilled_TheEndIsTheDayAfterTheLatestBar()
    {
        using var chart = Chart(new ImmediateData());
        var advisor = Advisor();
        using var vm = await PeriodViewModel(chart, advisor, NewClock());
        Assert.Equal(("2006-03-01", "2026-10-09"), (vm.StartDate, vm.EndDateExclusive));
        var call = Assert.Single(advisor.RlCalls);
        Assert.Equal(("SPY", StandardBars, (DateOnly?)null), (call.Symbol, call.Required, call.Bound));   // the requirement is read from the catalog rule, not typed here
        Assert.False(vm.HasPeriodStatus);
    }

    [Fact]
    public async Task Period_WithoutTheAdvisorTheBoxesStayEmpty()
    {
        using var chart = Chart(new ImmediateData());
        using var vm = await PeriodViewModel(chart, null, NewClock());
        Assert.Equal(("", ""), (vm.StartDate, vm.EndDateExclusive));
        Assert.Equal(RlStartBlocker.StartDateMissing, vm.CurrentStartBlocker());
    }

    [Fact]
    public async Task Period_ATypedStartSurvivesASymbolChangeWhileTheEndFollows()
    {
        using var chart = Chart(new ImmediateData());
        var advisor = Advisor(); var clock = NewClock();
        using var vm = await PeriodViewModel(chart, advisor, clock);
        vm.StartDate = "2015-05-05";                                          // typed
        advisor.Rl = (_, required, _) => Task.FromResult(FakeStoredPeriodAdvisor.Found(new DateOnly(1999, 1, 4), new DateOnly(2026, 9, 30), required));
        vm.Symbol = "QQQ";
        await ElapseDebounce(clock, advisor, 2);
        Assert.Equal(("2015-05-05", "2026-10-01"), (vm.StartDate, vm.EndDateExclusive));
    }

    [Fact]
    public async Task Period_ATypedEndFixesTheEndAndTheStartFollowsFromTheBeforeDay()
    {
        using var chart = Chart(new ImmediateData());
        var advisor = Advisor(); var clock = NewClock();
        using var vm = await PeriodViewModel(chart, advisor, clock);
        advisor.Rl = (_, required, bound) => Task.FromResult(FakeStoredPeriodAdvisor.Found(new DateOnly(1990, 1, 2), bound ?? PeriodLatest, required));
        vm.EndDateExclusive = "2020-01-01";                                   // typed
        await ElapseDebounce(clock, advisor, 2);
        Assert.Equal(new DateOnly(2019, 12, 31), advisor.RlCalls[^1].Bound);   // the end is exclusive: the bound is the day before
        Assert.Equal(("1990-01-02", "2020-01-01"), (vm.StartDate, vm.EndDateExclusive));
    }

    [Fact]
    public async Task Period_ClearingABoxByTypingKeepsItTyped()
    {
        using var chart = Chart(new ImmediateData());
        var advisor = Advisor(); var clock = NewClock();
        using var vm = await PeriodViewModel(chart, advisor, clock);
        vm.StartDate = "";                                                    // the user emptied it
        vm.Symbol = "QQQ";
        await ElapseDebounce(clock, advisor, 2);
        Assert.Equal(("", "2026-10-09"), (vm.StartDate, vm.EndDateExclusive));
        Assert.Equal(RlStartBlocker.StartDateMissing, vm.CurrentStartBlocker());
    }

    [Fact]
    public async Task Period_ALateOlderProposalNeverOverwritesANewerOne()
    {
        using var chart = Chart(new ImmediateData());
        var advisor = Advisor(); var clock = NewClock();
        using var vm = await PeriodViewModel(chart, advisor, clock);
        var first = new TaskCompletionSource<StoredPeriodProposal>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<StoredPeriodProposal>(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        advisor.Rl = (_, _, _) => ++call == 1 ? first.Task : second.Task;
        vm.Symbol = "AAA";
        await clock.TimerArmed.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(StoredPeriodTiming.Debounce);
        await Until(() => advisor.RlCalls.Count >= 2);
        vm.Symbol = "BBB";
        await clock.TimerArmed.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(StoredPeriodTiming.Debounce);
        await Until(() => advisor.RlCalls.Count >= 3);
        second.SetResult(FakeStoredPeriodAdvisor.Found(new DateOnly(2001, 1, 1), new DateOnly(2002, 1, 1), StandardBars));
        await Until(() => vm.StartDate == "2001-01-01");
        first.SetResult(FakeStoredPeriodAdvisor.Found(new DateOnly(1980, 1, 1), new DateOnly(1981, 1, 1), StandardBars));
        await Task.Delay(100);
        Assert.Equal(("2001-01-01", "2002-01-02"), (vm.StartDate, vm.EndDateExclusive));
    }

    [Fact]
    public async Task Period_NoDataEmptiesTheAutomaticBoxes_AndNoProposalChangesNothing()
    {
        using var chart = Chart(new ImmediateData());
        var advisor = Advisor(); var clock = NewClock();
        using var vm = await PeriodViewModel(chart, advisor, clock);
        advisor.Rl = (_, required, _) => Task.FromResult(StoredPeriodProposal.None(StoredPeriodStatus.NoProposal, required));
        vm.SmokeOnly = true;
        await ElapseDebounce(clock, advisor, 2);
        Assert.Equal(("2006-03-01", "2026-10-09"), (vm.StartDate, vm.EndDateExclusive));      // unchanged
        advisor.Rl = (_, required, _) => Task.FromResult(StoredPeriodProposal.None(StoredPeriodStatus.NoData, required));
        vm.Symbol = "NONE";
        await ElapseDebounce(clock, advisor, 3);
        Assert.Equal(("", ""), (vm.StartDate, vm.EndDateExclusive));
    }

    [Fact]
    public async Task Period_TheOptionsChooseTheRequiredBarsFromTheCatalogRule()
    {
        using var chart = Chart(new ImmediateData());
        var advisor = Advisor(); var clock = NewClock();
        using var vm = await PeriodViewModel(chart, advisor, clock);
        Assert.NotEqual(StandardBars, ShortBars);
        Assert.Equal(StandardBars, vm.RequiredBars());
        vm.UseShortHistory = true; Assert.Equal(ShortBars, vm.RequiredBars());
        vm.UseShortHistory = false; vm.SmokeOnly = true; Assert.Equal(ShortBars, vm.RequiredBars());
        vm.SmokeOnly = false; vm.UseContinuousPolicy = true; Assert.Equal(StandardBars, vm.RequiredBars());   // short history is not offered there
        vm.UseShortHistory = true; Assert.Equal(StandardBars, vm.RequiredBars());
        vm.UseAllCompleteBlocks = true; Assert.Equal(ShortBars, vm.RequiredBars());
        await ElapseDebounce(clock, advisor, 2);
        Assert.Equal(ShortBars, advisor.RlCalls[^1].Required);
    }

    [Fact]
    public async Task Period_ABusyWindowWritesNothing()
    {
        using var chart = Chart(new ImmediateData());
        var advisor = Advisor(); var clock = NewClock();
        using var vm = await PeriodViewModel(chart, advisor, clock);
        vm.IsBusy = true;
        vm.Symbol = "QQQ";
        await Task.Delay(50);
        Assert.Equal(0, clock.TimerArmed.CurrentCount);                           // no recompute was even scheduled
        Assert.Single(advisor.RlCalls);
    }

    [Fact]
    public async Task Period_TheMessageAppearsOnlyForAnInsufficientHistoryAndOnlyWhileTheStartIsAutomatic()
    {
        using var chart = Chart(new ImmediateData());
        var advisor = new FakeStoredPeriodAdvisor { Rl = (_, required, _) => Task.FromResult(FakeStoredPeriodAdvisor.Short(PeriodStart, PeriodLatest, required, 1500, "SPY")) };
        using var vm = await PeriodViewModel(chart, advisor, NewClock());
        Assert.True(vm.HasPeriodStatus);
        Assert.Equal($"{StandardBars} bars are needed, 1500 are available.", vm.PeriodStatusDisplay);
        vm.StartDate = "2010-01-01";                                              // a typed start makes the claim irrelevant
        Assert.False(vm.HasPeriodStatus);
        using var chart2 = Chart(new ImmediateData());
        using var found = await PeriodViewModel(chart2, Advisor(), NewClock());
        Assert.False(found.HasPeriodStatus);
    }

    [Fact]
    public void Period_TheStatusKeyExistsInBothLanguagesWithTheSamePlaceholders()
    {
        LocalizationManager.Instance.Initialize("ja");
        string ja = new LocalizationService().GetString("Rl.Ui.Period.Insufficient");
        LocalizationManager.Instance.Initialize("en");
        string en = new LocalizationService().GetString("Rl.Ui.Period.Insufficient");
        foreach (string text in new[] { ja, en }) { Assert.Contains("{0}", text); Assert.Contains("{1}", text); }
        Assert.NotEqual(en, ja);
    }
}
