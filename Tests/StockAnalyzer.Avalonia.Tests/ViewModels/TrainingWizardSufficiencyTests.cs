using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The two warnings under the data period of the Training Wizard: tickers with too few bars in the period, and a selection whose sample estimate is above the
/// cap. The warnings debounce on the system clock, so these tests wait for the real 400 ms.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class TrainingWizardSufficiencyTests : IDisposable
{
    private static readonly DateOnly Latest = new(2026, 10, 8), Start = new(2017, 5, 1);
    private const int Required = 2374;
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;

    public TrainingWizardSufficiencyTests() => LocalizationManager.Instance.Initialize("en");
    public void Dispose() => LocalizationManager.Instance.Initialize(_originalLanguage);

    private sealed class FakeResourceSettings(TrainingResourceOverrides snapshot, string? loadError = null) : ITrainingResourceSettings
    {
        public TrainingResourceOverrides Snapshot { get; } = snapshot;
        public TrainingResourceOverrides SavedSnapshot => Snapshot;
        public string? LoadError { get; } = loadError;
        public bool IsSaving => false;
        public bool TryAcquirePreview(object owner) => true;
        public void SetPreview(object owner, TrainingResourceOverrides value) { }
        public void ReleasePreview(object owner) { }
        public Task SaveAsync(object owner) => Task.CompletedTask;
    }

    private static SymbolBarInfo Info(string symbol, int inRange, int? upToEnd = null, bool reachable = true, int? fromMinimum = null)
        => new(symbol, inRange, upToEnd ?? inRange, reachable ? Start : null, reachable ? fromMinimum ?? inRange : null);

    private static IReadOnlyList<SymbolBarInfo> Tickers(int count, int bars = Required)
        => Enumerable.Range(0, count).Select(i => Info("T" + i.ToString("D2", CultureInfo.InvariantCulture), bars)).ToArray();

    private static FakeStoredPeriodAdvisor Advisor(Func<IReadOnlyList<SymbolBarInfo>> infos)
        => new()
        {
            Training = (_, _, required, _) => Task.FromResult(FakeStoredPeriodAdvisor.Found(Start, Latest, required)),
            SymbolBars = (_, _, _, _) => infos(),
        };

    private static TrainingWizardViewModel Wizard(FakeStoredPeriodAdvisor advisor, ITrainingResourceSettings? settings = null, string[]? tickers = null)
    {
        var market = new Mock<IMarketDataProvider>();
        market.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync((tickers ?? new[] { "AAA" }).ToList());
        var store = StockAnalyzer.Avalonia.Tests.TestHelpers.FakeTickerStateStore.FromProfiles(Array.Empty<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        return new TrainingWizardViewModel(orchestrator: Mock.Of<ITrainingOrchestrator>(), marketDataProvider: market.Object, periodAdvisor: advisor,
            clock: TimeProvider.System, resourceSettings: settings, tickerStore: store);
    }

    private static async Task Until(Func<bool> condition, string what = "the condition")
    {
        for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "Timed out waiting for " + what + ".");
    }

    private static string N0(long value) => value.ToString("N0", DisplayFormat.Culture);   // the single display culture

    [Fact]
    public async Task EnoughBarsThatFitTheCap_ShowNoWarningAtAll_AndStartStaysEnabled()
    {
        var advisor = Advisor(() => Tickers(4));
        using var wizard = Wizard(advisor);
        await Until(() => advisor.SymbolBarCalls.Count >= 1, "the first bar read");
        await Task.Delay(100);

        Assert.False(wizard.HasRequirementWarning);
        Assert.False(wizard.HasSampleLimitWarning);
        Assert.Equal("", wizard.RequirementWarningDisplay);
        Assert.Equal("", wizard.SampleLimitWarningDisplay);
        Assert.True(wizard.StartTrainingCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheReaderIsAskedForTheCurrentPeriodTimeframeAndMinimum()
    {
        var advisor = Advisor(() => Tickers(1));
        using var wizard = Wizard(advisor, tickers: new[] { "AAA", "BBB" });
        await Until(() => advisor.SymbolBarCalls.Any(c => c.Start == Start && c.End == Latest), "a read for the pre-filled period");

        var call = advisor.SymbolBarCalls.Last(c => c.Start == Start && c.End == Latest);
        Assert.Equal(new[] { "AAA", "BBB" }, call.Symbols.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal((TrainingTimeframe.Daily, Required), (call.Timeframe, call.Required));
    }

    [Fact]
    public async Task TickersWithTooFewBars_AreNamedFewestFirst_WithTheStartThatFixesThem_AndTheOnesNoStartCanFix()
    {
        var advisor = Advisor(() => new[]
        {
            Info("AAA", 600, upToEnd: 3000, fromMinimum: Required),
            Info("BBB", 100, upToEnd: 100, reachable: false),
            Info("CCC", Required + 10),
        });
        using var wizard = Wizard(advisor);
        await Until(() => wizard.RequirementWarningDisplay.Contains("up to 2026-10-08"), "the requirement warning for the pre-filled period");

        string text = wizard.RequirementWarningDisplay;
        Assert.StartsWith($"Each ticker needs {N0(Required)} bars (Window 60, Horizon 5, Splits 5, Gap 64). Too few in this period: BBB (100 bars), AAA (600 bars).", text);
        Assert.Contains($"Start 2017-05-01 or earlier gives every ticker with enough history {N0(Required)} bars up to 2026-10-08.", text);
        Assert.EndsWith("Not enough stored history for any period: BBB (100 bars). Remove them or lower Window, Horizon, Splits or Gap.", text);
        Assert.DoesNotContain("CCC", text);
        Assert.True(wizard.StartTrainingCommand.CanExecute(null));                 // a warning never disables Start
        Assert.Equal(Start.ToDateTime(TimeOnly.MinValue), wizard.StartDate);        // and never writes a field
    }

    [Fact]
    public async Task TheRequirementListNamesFiveTickersThenCountsTheRest()
    {
        var advisor = Advisor(() => Tickers(8, 100).Select(i => i with { RequiredStart = null, BarsFromMinimumStart = null }).ToArray());
        using var wizard = Wizard(advisor);
        await Until(() => wizard.HasRequirementWarning, "the requirement warning");

        string text = wizard.RequirementWarningDisplay;
        Assert.Contains("T00 (100 bars), T01 (100 bars), T02 (100 bars), T03 (100 bars), T04 (100 bars) +3 more.", text);
        Assert.DoesNotContain("T05", text);
        Assert.DoesNotContain("Start 2017", text);                                  // nothing can reach the minimum: no start to propose
    }

    [Fact]
    public async Task TheRequirementWarningYieldsToTheExistingPeriodLine_WhenThatLineShows()
    {
        var advisor = Advisor(() => new[] { Info("AAA", 600, upToEnd: 600, reachable: false) });
        advisor.Training = (_, _, required, _) => Task.FromResult(FakeStoredPeriodAdvisor.Short(Start, Latest, required, 600, "AAA"));
        using var wizard = Wizard(advisor);
        await Until(() => wizard.HasPeriodStatus, "the existing period line");
        await Until(() => advisor.SymbolBarCalls.Count >= 1, "the bar read");
        await Task.Delay(100);

        Assert.True(wizard.HasPeriodStatus);
        Assert.False(wizard.HasRequirementWarning);
    }

    [Fact]
    public async Task TooManyTickers_SayHowManyFitEvenAtTheShortestPeriod()
    {
        var advisor = Advisor(() => Tickers(12));                                   // the 12 tickers the trainer refused
        using var wizard = Wizard(advisor);
        await Until(() => wizard.HasSampleLimitWarning, "the sample warning");

        Assert.Equal($"Up to {N0(23_100)} training samples from 12 tickers; the limit is {N0(10_000)} (Settings > Training Wizard > Maximum training samples). "
                     + "Even the shortest period fits at most 5 tickers; remove 7.", wizard.SampleLimitWarningDisplay);
        Assert.False(wizard.HasRequirementWarning);
    }

    [Fact]
    public async Task ASelectionThatFitsAtTheShortestPeriodNamesThatStart()
    {
        var advisor = Advisor(() => Enumerable.Range(0, 4).Select(i => Info("T" + i, 4000, fromMinimum: Required)).ToArray());
        using var wizard = Wizard(advisor);
        await Until(() => wizard.HasSampleLimitWarning, "the sample warning");

        // 4 x 5 x floor((4000 - 64) / 6) = 13,120 now; from the minimum start 4 x 1,925 = 7,700
        Assert.Equal($"Up to {N0(13_120)} training samples from 4 tickers; the limit is {N0(10_000)} (Settings > Training Wizard > Maximum training samples). "
                     + $"Start 2017-05-01 (the shortest period) gives up to {N0(7_700)} samples.", wizard.SampleLimitWarningDisplay);
    }

    [Fact]
    public async Task ALoweredCapFromTheSavedLimits_IsTheOneComparedWith_AndOneTickerAloneCanBeAboveIt()
    {
        var advisor = Advisor(() => Tickers(1));
        using var wizard = Wizard(advisor, new FakeResourceSettings(new TrainingResourceOverrides(MaxSamples: 1_900)));
        await Until(() => wizard.HasSampleLimitWarning, "the sample warning");

        Assert.Equal($"Up to {N0(1_925)} training samples from 1 tickers; the limit is {N0(1_900)} (Settings > Training Wizard > Maximum training samples). "
                     + $"Even one ticker needs up to {N0(1_925)} samples, above the limit {N0(1_900)}. Raise Maximum training samples (at most {N0(10_000)}) or lower Window and Horizon.",
            wizard.SampleLimitWarningDisplay);
    }

    [Fact]
    public async Task WhenTheShortestPeriodWouldNotHelpAndNoCountIsKnown_TheAdviceIsToSelectFewerTickers()
    {
        var advisor = Advisor(() => Enumerable.Range(0, 2).Select(i => Info("T" + i, 6000)).ToArray());   // calendars that do not line up: 2 x 4,945 now and at the minimum
        using var wizard = Wizard(advisor, new FakeResourceSettings(new TrainingResourceOverrides(MaxSamples: 9_000)));
        await Until(() => wizard.HasSampleLimitWarning, "the sample warning");

        Assert.EndsWith("Select fewer tickers.", wizard.SampleLimitWarningDisplay);
    }

    [Fact]
    public async Task ASavedLimitThatCannotBeResolved_MakesNoSampleWarning_ButTheRequirementWarningStays()
    {
        var advisor = Advisor(() => new[] { Info("AAA", 100, reachable: false) }.Concat(Tickers(12)).ToArray());
        using var wizard = Wizard(advisor, new FakeResourceSettings(new TrainingResourceOverrides(), loadError: "unreadable"));
        await Until(() => wizard.HasRequirementWarning, "the requirement warning");
        await Task.Delay(100);

        Assert.False(wizard.HasSampleLimitWarning);
    }

    [Fact]
    public async Task ComposedFeaturesAndAFixedZScoreCountEveryWindow_SoFewerTickersFit()
    {
        var advisor = Advisor(() => Tickers(5));                                    // 5 x 1,925 = 9,625 fits; 5 x 2,310 = 11,550 does not
        using var wizard = Wizard(advisor);
        await Until(() => advisor.SymbolBarCalls.Count >= 1, "the first bar read");
        await Task.Delay(100);
        Assert.False(wizard.HasSampleLimitWarning);

        wizard.UseFixedZScore = true;
        await Until(() => wizard.HasSampleLimitWarning, "the sample warning after the fixed z-score");

        Assert.Contains($"Up to {N0(11_550)} training samples from 5 tickers", wizard.SampleLimitWarningDisplay);
        Assert.EndsWith("Even the shortest period fits at most 4 tickers; remove 1.", wizard.SampleLimitWarningDisplay);
    }

    [Fact]
    public async Task ChangingTheWindow_ReadsAgainWithTheNewMinimum()
    {
        var advisor = Advisor(() => Tickers(1));
        using var wizard = Wizard(advisor);
        await Until(() => advisor.SymbolBarCalls.Count >= 1, "the first bar read");

        wizard.WindowSize = 100;
        int expected = WalkForwardDataRequirement.MinimumRawBarsForTraining(100, WalkForwardDataRequirement.DefaultHorizon, WalkForwardDataRequirement.DefaultSplitCount);
        await Until(() => advisor.SymbolBarCalls.Any(c => c.Required == expected), "a read for window 100");
    }

    [Fact]
    public async Task WarningsAreEmptyDuringARun_AndComeBackAfterIt()
    {
        var advisor = Advisor(() => Tickers(12));
        using var wizard = Wizard(advisor);
        await Until(() => wizard.HasSampleLimitWarning, "the sample warning");

        wizard.IsTraining = true;
        Assert.False(wizard.HasSampleLimitWarning);
        Assert.Equal("", wizard.SampleLimitWarningDisplay);

        wizard.IsTraining = false;
        await Until(() => wizard.HasSampleLimitWarning, "the sample warning after the run");
    }

    [Fact]
    public async Task AFailingReaderMakesNoWarning_AndDoesNotBreakTheWizard()
    {
        var advisor = Advisor(() => throw new InvalidOperationException("unreadable"));
        using var wizard = Wizard(advisor);
        await Until(() => advisor.SymbolBarCalls.Count >= 1, "the bar read");
        await Task.Delay(100);

        Assert.False(wizard.HasRequirementWarning);
        Assert.False(wizard.HasSampleLimitWarning);
        Assert.True(wizard.StartTrainingCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFailureAnywhereInTheRefresh_ClearsTheWarnings_InsteadOfLeavingAnOldFigure()
    {
        // 12 tickers over the cap would warn; the resource limits then fail with an exception the refresh does not expect. The warning that was shown stays gone.
        var limits = new SwitchableResourceSettings();
        var advisor = Advisor(() => Tickers(12));
        using var wizard = Wizard(advisor, limits, tickers: Enumerable.Range(0, 12).Select(i => "T" + i.ToString("D2", CultureInfo.InvariantCulture)).ToArray());
        await Until(() => wizard.HasSampleLimitWarning, "the warning before the failure");

        limits.Fail = true;
        int readsBefore = advisor.SymbolBarCalls.Count;
        wizard.Horizon = 6;                                                  // any input of the warnings: starts a new refresh
        await Until(() => advisor.SymbolBarCalls.Count > readsBefore && limits.FailedReads >= 1, "the failing refresh");
        await Task.Delay(100);

        Assert.False(wizard.HasSampleLimitWarning);
        Assert.False(wizard.HasRequirementWarning);
        Assert.True(wizard.StartTrainingCommand.CanExecute(null));
    }

    private sealed class SwitchableResourceSettings : ITrainingResourceSettings
    {
        public volatile bool Fail;
        public int FailedReads;
        public TrainingResourceOverrides Snapshot
        {
            get
            {
                if (!Fail) return new TrainingResourceOverrides();
                Interlocked.Increment(ref FailedReads);
                throw new System.IO.InvalidDataException("unexpected");
            }
        }
        public TrainingResourceOverrides SavedSnapshot => new();
        public string? LoadError => null;
        public bool IsSaving => false;
        public bool TryAcquirePreview(object owner) => true;
        public void SetPreview(object owner, TrainingResourceOverrides value) { }
        public void ReleasePreview(object owner) { }
        public Task SaveAsync(object owner) => Task.CompletedTask;
    }

    [Fact]
    public async Task WithoutTheAdvisorTheWarningsStayEmpty()
    {
        using var wizard = new TrainingWizardViewModel(orchestrator: Mock.Of<ITrainingOrchestrator>());
        await Task.Delay(600);

        Assert.False(wizard.HasRequirementWarning);
        Assert.False(wizard.HasSampleLimitWarning);
    }

    [Fact]
    public async Task TheTextsExistInBothLanguages_WithTheSamePlaceholders()
    {
        string[] keys =
        {
            "TrainingWizard_Sufficiency_Short", "TrainingWizard_Sufficiency_StartHint", "TrainingWizard_Sufficiency_LatestBar", "TrainingWizard_Sufficiency_Unreachable",
            "TrainingWizard_Sufficiency_SymbolBars", "TrainingWizard_Sufficiency_More", "TrainingWizard_Sufficiency_Limit", "TrainingWizard_Sufficiency_LimitStart",
            "TrainingWizard_Sufficiency_LimitSingle", "TrainingWizard_Sufficiency_LimitTickers", "TrainingWizard_Sufficiency_LimitFewer",
        };
        var placeholders = new Dictionary<string, string>();
        foreach (var language in new[] { "en", "ja" })
        {
            LocalizationManager.Instance.Initialize(language);
            foreach (var key in keys)
            {
                string value = LocalizationManager.Instance[key];
                Assert.False(string.IsNullOrWhiteSpace(value) || value == $"[{key}]", $"{language}:{key}");
                string signature = string.Join(",", System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(v => v, StringComparer.Ordinal));
                if (language == "en") placeholders[key] = signature;
                else Assert.Equal(placeholders[key], signature);
            }
        }
        LocalizationManager.Instance.Initialize("en");
        await Task.CompletedTask;
    }
}
