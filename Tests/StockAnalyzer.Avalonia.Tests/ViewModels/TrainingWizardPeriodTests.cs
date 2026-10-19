using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>The pre-filled data period of the Training Wizard (fake stored-period advisor, manual clock for the debounce).</summary>
[Collection("LocalizationSharedState")]
public sealed class TrainingWizardPeriodTests : IDisposable
{
    private static readonly DateOnly Latest = new(2026, 10, 8), Start = new(2014, 11, 3);
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;

    public TrainingWizardPeriodTests() => LocalizationManager.Instance.Initialize("en");
    public void Dispose() => LocalizationManager.Instance.Initialize(_originalLanguage);

    private static int DefaultRequired => WalkForwardDataRequirement.MinimumRawBarsForTraining(PredictionSettingsManager.DefaultWindowSize,
        WalkForwardDataRequirement.DefaultHorizon, WalkForwardDataRequirement.DefaultSplitCount);

    private static global::StockAnalyzer.Core.Tests.Services.ManualTimeProvider NewClock()
        => new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

    private static FakeStoredPeriodAdvisor Advisor()
        => new() { Training = (_, _, required, _) => Task.FromResult(FakeStoredPeriodAdvisor.Found(Start, Latest, required)) };

    private static TrainingWizardViewModel Wizard(FakeStoredPeriodAdvisor? advisor, TimeProvider? clock, string[]? tickers = null, Action<TrainingJobConfig>? onStart = null)
    {
        var orchestrator = new Mock<ITrainingOrchestrator>();
        orchestrator.Setup(o => o.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((TrainingJobConfig config, IProgress<TrainingProgress> _, CancellationToken _) =>
            {
                onStart?.Invoke(config);
                return Task.FromResult(new TrainingRunResult { RunId = "run", Success = true });
            });
        var market = new Mock<IMarketDataProvider>();
        market.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync((tickers ?? new[] { "AAA" }).ToList());
        var store = StockAnalyzer.Avalonia.Tests.TestHelpers.FakeTickerStateStore.FromProfiles(Array.Empty<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        return new TrainingWizardViewModel(orchestrator: orchestrator.Object, marketDataProvider: market.Object, periodAdvisor: advisor, clock: clock, tickerStore: store);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "Timed out waiting for the condition.");
    }

    private static async Task ElapseDebounce(global::StockAnalyzer.Core.Tests.Services.ManualTimeProvider clock, FakeStoredPeriodAdvisor advisor, int calls)
    {
        await clock.TimerArmed.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(StoredPeriodTiming.Debounce);
        await Until(() => advisor.TrainingCalls.Count >= calls);
        await Task.Delay(20);
    }

    [Fact]
    public void ItOpensWithBothPickersFilled_AndTheEndIsTheLatestStoredDayItself()
    {
        var advisor = Advisor();
        using var wizard = Wizard(advisor, NewClock());
        Assert.Equal((Start.ToDateTime(TimeOnly.MinValue), Latest.ToDateTime(TimeOnly.MinValue)), (wizard.StartDate, wizard.EndDate));
        var call = Assert.Single(advisor.TrainingCalls);
        Assert.Equal(new[] { "AAA" }, call.Symbols.ToArray());
        Assert.Equal((TrainingTimeframe.Daily, (DateOnly?)null), (call.Timeframe, call.Bound));
        Assert.Equal(DefaultRequired, call.Required);                          // the requirement of the default settings, from the existing function
        Assert.Equal(2374, call.Required);                                     // window 60 (the default), horizon 5, 5 splits; window 75 needs 2929 (documented example)
        Assert.True(wizard.StartTrainingCommand.CanExecute(null));
        Assert.False(wizard.HasPeriodStatus);
    }

    [Fact]
    public void WithoutTheAdvisorBothPickersStayEmpty_AsBefore()
    {
        using var wizard = Wizard(null, NewClock());
        Assert.Equal((null, null), (wizard.StartDate, wizard.EndDate));
        Assert.True(wizard.StartTrainingCommand.CanExecute(null));
    }

    [Fact]
    public async Task ATypedStartSurvivesTheTimeframeChangeWhileTheEndFollows()
    {
        var advisor = Advisor(); var clock = NewClock();
        using var wizard = Wizard(advisor, clock);
        var typedStart = new DateTime(2018, 1, 2);
        wizard.StartDate = typedStart;                                         // typed
        wizard.SelectedTimeframe = TrainingTimeframe.Weekly;
        await ElapseDebounce(clock, advisor, 2);
        Assert.Equal((typedStart, Latest.ToDateTime(TimeOnly.MinValue)), (wizard.StartDate, wizard.EndDate));
        Assert.Equal(TrainingTimeframe.Weekly, advisor.TrainingCalls[^1].Timeframe);
    }

    [Fact]
    public async Task ATypedEndFixesTheEndAndTheStartFollowsFromIt_AndNothingIsAskedOnceBothAreTyped()
    {
        var advisor = new FakeStoredPeriodAdvisor { Training = (_, _, required, bound) => Task.FromResult(FakeStoredPeriodAdvisor.Found(new DateOnly(1999, 1, 4), bound ?? Latest, required)) };
        var clock = NewClock();
        using var wizard = Wizard(advisor, clock);
        var typedEnd = new DateTime(2025, 6, 30);
        wizard.EndDate = typedEnd;                                             // typed
        await ElapseDebounce(clock, advisor, 2);
        Assert.Equal(DateOnly.FromDateTime(typedEnd), advisor.TrainingCalls[^1].Bound);   // the end is inclusive: the bound is the typed day itself
        Assert.Equal((new DateTime(1999, 1, 4), typedEnd), (wizard.StartDate, wizard.EndDate));
        var typedStart = new DateTime(2018, 1, 2);
        wizard.StartDate = typedStart;                                         // both typed now
        wizard.WindowSize = 120;
        await Task.Delay(100);
        Assert.Equal(0, clock.TimerArmed.CurrentCount);                        // nothing could be written, so nothing is scheduled
        Assert.Equal(2, advisor.TrainingCalls.Count);
        Assert.Equal((typedStart, typedEnd), (wizard.StartDate, wizard.EndDate));
    }

    [Fact]
    public async Task ChangingTheWindowHorizonOrSplitsMovesTheAutomaticStartByTheNewRequirement()
    {
        var advisor = new FakeStoredPeriodAdvisor { Training = (_, _, required, _) => Task.FromResult(FakeStoredPeriodAdvisor.Found(Latest.AddDays(-required), Latest, required)) };
        var clock = NewClock();
        using var wizard = Wizard(advisor, clock);
        Assert.Equal(Latest.AddDays(-DefaultRequired).ToDateTime(TimeOnly.MinValue), wizard.StartDate);
        wizard.WindowSize = 150;
        await ElapseDebounce(clock, advisor, 2);
        int widened = WalkForwardDataRequirement.MinimumRawBarsForTraining(150, WalkForwardDataRequirement.DefaultHorizon, WalkForwardDataRequirement.DefaultSplitCount);
        Assert.NotEqual(DefaultRequired, widened);
        Assert.Equal(widened, advisor.TrainingCalls[^1].Required);
        Assert.Equal(Latest.AddDays(-widened).ToDateTime(TimeOnly.MinValue), wizard.StartDate);
        wizard.Horizon = 20; wizard.NSplits = 7;
        await ElapseDebounce(clock, advisor, 3);
        Assert.Equal(WalkForwardDataRequirement.MinimumRawBarsForTraining(150, 20, 7), advisor.TrainingCalls[^1].Required);
    }

    [Fact]
    public async Task ATransientInvalidWindowChangesNothing_AndNoRequestIsMade()
    {
        var advisor = Advisor(); var clock = NewClock();
        using var wizard = Wizard(advisor, clock);
        wizard.WindowSize = 0;
        await clock.TimerArmed.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(StoredPeriodTiming.Debounce);
        await Task.Delay(100);
        Assert.Single(advisor.TrainingCalls);                                   // only the opening request
        Assert.Equal((Start.ToDateTime(TimeOnly.MinValue), Latest.ToDateTime(TimeOnly.MinValue)), (wizard.StartDate, wizard.EndDate));
    }

    [Fact]
    public void TooManyOrNoDataLeavesTheAutomaticPickersEmpty()
    {
        var advisor = new FakeStoredPeriodAdvisor { Training = (_, _, required, _) => Task.FromResult(StoredPeriodProposal.None(StoredPeriodStatus.TooMany, required)) };
        using var wizard = Wizard(advisor, NewClock());
        Assert.Equal((null, null), (wizard.StartDate, wizard.EndDate));
        Assert.True(wizard.StartTrainingCommand.CanExecute(null));              // the empty pickers mean all stored data, as before
    }

    [Fact]
    public void AShortSymbolShowsOneLine_ThatDisappearsWhenTheStartIsTyped()
    {
        var advisor = new FakeStoredPeriodAdvisor { Training = (_, _, required, _) => Task.FromResult(FakeStoredPeriodAdvisor.Short(Start, Latest, required, 900, "ZZZ")) };
        using var wizard = Wizard(advisor, NewClock());
        Assert.True(wizard.HasPeriodStatus);
        Assert.Equal($"{DefaultRequired} bars are needed, but ZZZ has only 900.", wizard.PeriodStatusDisplay);
        LocalizationManager.Instance.Initialize("ja");
        Assert.Contains("ZZZ", wizard.PeriodStatusDisplay);
        Assert.NotEqual($"{DefaultRequired} bars are needed, but ZZZ has only 900.", wizard.PeriodStatusDisplay);
        wizard.StartDate = new DateTime(2020, 1, 1);
        Assert.False(wizard.HasPeriodStatus);
        Assert.True(wizard.StartTrainingCommand.CanExecute(null));              // the message never changes the start gate
    }

    [Fact]
    public async Task TheFilledDatesReachTheTrainingConfiguration()
    {
        TrainingJobConfig? config = null;
        using var wizard = Wizard(Advisor(), NewClock(), onStart: c => config = c);
        await wizard.StartTrainingCommand.ExecuteAsync(null);
        Assert.NotNull(config);
        Assert.Equal((Start, Latest), (config!.StartDate, config.EndDate));
    }

    [Fact]
    public async Task ABusyWizardWritesNothing()
    {
        var advisor = Advisor(); var clock = NewClock();
        using var wizard = Wizard(advisor, clock);
        wizard.IsTraining = true;
        wizard.SelectedTimeframe = TrainingTimeframe.Monthly;
        await Task.Delay(50);
        Assert.Equal(0, clock.TimerArmed.CurrentCount);
        Assert.Single(advisor.TrainingCalls);
    }

    [Fact]
    public void TheStatusKeyExistsInBothLanguagesWithTheSamePlaceholders()
    {
        string en = LocalizationManager.Instance["TrainingWizard_Period_Insufficient"];
        LocalizationManager.Instance.Initialize("ja");
        string ja = LocalizationManager.Instance["TrainingWizard_Period_Insufficient"];
        foreach (string text in new[] { en, ja }) { Assert.Contains("{0}", text); Assert.Contains("{1}", text); Assert.Contains("{2}", text); }
        Assert.NotEqual(en, ja);
    }
}
