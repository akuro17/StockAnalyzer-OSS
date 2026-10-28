using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Point = global::Avalonia.Point;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Core.Tests.Services;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The portfolio dialog without a window: real ticker-tree nodes, real Core records and the real Start gate; only the two Core seams
/// (<see cref="IRlPortfolioService"/> and the source resolver) are scripted so each check can return an exact preflight.
/// </summary>
[Collection("MessengerSharedState")]
public sealed class PortfolioRlDialogViewModelTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 1, 5);
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly List<PortfolioRlDialogViewModel> _viewModels = new();

    public PortfolioRlDialogViewModelTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    public void Dispose() { foreach (var vm in _viewModels) vm.Dispose(); CultureInfo.CurrentCulture = _culture; }

    // ----- doubles -----

    private sealed class JsonLocalization : ILocalizationService
    {
        private readonly Dictionary<string, string> _values;
        public JsonLocalization(string language) => _values = ReadFlat(language);
        public string GetString(string key) => _values.TryGetValue(key, out var value) ? value : "[" + key + "]";
        /// <summary>The string entries of a locale file (the file also holds a metadata object).</summary>
        public static Dictionary<string, string> ReadFlat(string language)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path(language)));
            return json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!);
        }
        public static string Path(string language)
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(System.IO.Path.Combine(repo.FullName, "StockAnalyzer.sln"))) repo = repo.Parent;
            return System.IO.Path.Combine(repo!.FullName, "StockAnalyzer.Avalonia", "Resources", "Locales", language + ".json");
        }
        public static string Root() => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path("en"))!, "..", ".."));
    }

    private sealed class FakeResolver : ITickerSourceResolver
    {
        public ImmutableArray<string> Symbols { get; set; } = ImmutableArray.Create("AAA", "BBB", "CCC", "DDD");
        public int Missing { get; set; }
        public List<IReadOnlyList<RlSourceDescriptor>> Calls { get; } = new();
        public Task<RlSourceResolution> ResolveAsync(IReadOnlyList<RlSourceDescriptor> sources, IReadOnlyCollection<string>? excluded = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(sources.ToArray());
            var diagnostics = Enumerable.Range(0, Missing).Select(_ => new RlSourceDiagnostic(RlSourceDiagnosticCode.SourceNotFound, RlSourceDescriptor.AllTickers())).ToImmutableArray();
            return Task.FromResult(new RlSourceResolution(Symbols, diagnostics));
        }
    }

    private sealed class FakeService : IRlPortfolioService
    {
        public RlPortfolioSettings Settings { get; set; } = new();
        public Func<IReadOnlyList<string>, DateOnly, DateOnly, bool, RlPanelEvaluationMode, CancellationToken, Task<PanelPreflight>> Preflight { get; set; } = null!;
        public List<(IReadOnlyList<string> Symbols, DateOnly Start, DateOnly End, bool Holdout, RlPanelEvaluationMode Mode)> PreflightCalls { get; } = new();
        public List<RlPortfolioRunRequest> Runs { get; } = new();
        public Func<RlPortfolioRunRequest, CancellationToken, Task<RlPortfolioTrainingResult>> Run { get; set; } = null!;
        /// <summary>Scripted start suggestion; null = a service without an advisor (the interface default).</summary>
        public Func<IReadOnlyList<string>, DateOnly, RlPanelEvaluationMode, CancellationToken, Task<RlStartSuggestion?>>? Suggest { get; set; }
        public List<(DateOnly End, RlPanelEvaluationMode Mode)> SuggestCalls { get; } = new();
        /// <summary>The partition plan each check and each suggestion was asked under (null = the settings decide).</summary>
        public List<RlPanelPartitionPlan?> PreflightPlans { get; } = new();
        public List<RlPanelPartitionPlan?> SuggestPlans { get; } = new();
        /// <summary>The holdout share each check was asked under (null = the configured share).</summary>
        public List<int?> PreflightPercents { get; } = new();

        public Task<PanelPreflight> PreflightAsync(IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested,
            RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan, int? holdoutPercent, CancellationToken cancellationToken)
        {
            PreflightPercents.Add(holdoutPercent);
            return PreflightAsync(symbols, start, endExclusive, holdoutRequested, mode, plan, cancellationToken);
        }

        public Task<RlStartSuggestion?> SuggestStartAsync(IReadOnlyList<string> symbols, DateOnly endExclusive, RlPanelEvaluationMode mode,
            RlPanelPartitionPlan? plan, CancellationToken cancellationToken)
        {
            SuggestPlans.Add(plan);
            return SuggestStartAsync(symbols, endExclusive, mode, cancellationToken);
        }

        public Task<PanelPreflight> PreflightAsync(IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested,
            RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan, CancellationToken cancellationToken)
        {
            PreflightPlans.Add(plan);
            return PreflightAsync(symbols, start, endExclusive, holdoutRequested, mode, cancellationToken);
        }

        public Task<RlStartSuggestion?> SuggestStartAsync(IReadOnlyList<string> symbols, DateOnly endExclusive, RlPanelEvaluationMode mode, CancellationToken cancellationToken)
        {
            SuggestCalls.Add((endExclusive, mode));
            return Suggest is null ? Task.FromResult<RlStartSuggestion?>(null) : Suggest(symbols, endExclusive, mode, cancellationToken);
        }

        public Task<PanelPreflight> PreflightAsync(IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested,
            RlPanelEvaluationMode mode, CancellationToken cancellationToken)
        {
            PreflightCalls.Add((symbols.ToArray(), start, endExclusive, holdoutRequested, mode));
            return Preflight(symbols, start, endExclusive, holdoutRequested, mode, cancellationToken);
        }

        public Task<RlPortfolioTrainingResult> RunAsync(RlPortfolioRunRequest request, IProgress<RlTrainingProgress>? progress, CancellationToken cancellationToken)
        {
            Runs.Add(request);
            return Run(request, cancellationToken);
        }
    }

    private sealed class FakeTransfer : IRlPortfolioTransferService
    {
        public ImmutableArray<RlPortfolioBaseRunInfo> Infos { get; set; } = ImmutableArray<RlPortfolioBaseRunInfo>.Empty;
        public int ListCalls;
        public Func<Guid, DateOnly, RlPortfolioBaseRun> AdmitFunc { get; set; } = (id, _) => BaseRunOf(id, Today.AddYears(-3));
        public System.Collections.Concurrent.ConcurrentQueue<(Guid Id, DateOnly Date)> AdmitCalls { get; } = new();
        public Func<Guid, RlPortfolioRunRequest, CancellationToken, Task<RlPortfolioZeroShotResult>> EvaluateFunc { get; set; } = null!;
        public List<(Guid Id, RlPortfolioRunRequest Request)> Evaluations { get; } = new();
        /// <summary>Scripted symbol-overlap report; null (the default) = a service that has nothing to report, like the interface default.</summary>
        public Func<Guid, string, RlPortfolioSymbolOverlap?>? OverlapFunc { get; set; }
        public System.Collections.Concurrent.ConcurrentQueue<(Guid Id, string Panel)> OverlapCalls { get; } = new();

        public RlPortfolioSymbolOverlap? SymbolOverlap(Guid baseRunId, string panelDirectory)
        {
            OverlapCalls.Enqueue((baseRunId, panelDirectory));
            return OverlapFunc?.Invoke(baseRunId, panelDirectory);
        }

        /// <summary>Stored panels, runs with a report and scripted answers for the panel check and the comparison; the defaults behave like a service that has none.</summary>
        public ImmutableArray<RlPortfolioStoredPanelInfo> Panels { get; set; } = ImmutableArray<RlPortfolioStoredPanelInfo>.Empty;
        public int PanelListCalls, RunListCalls;
        public Func<Guid, RlPanelEvaluationMode, RlPanelPartitionPlan?, DateOnly> PanelDateFunc { get; set; } = (_, _, _) => FirstValidation;
        public System.Collections.Concurrent.ConcurrentQueue<(Guid Id, RlPanelEvaluationMode Mode, RlPanelPartitionPlan? Plan)> PanelChecks { get; } = new();
        public ImmutableArray<RlPortfolioComparableRun> RunInfos { get; set; } = ImmutableArray<RlPortfolioComparableRun>.Empty;
        public Func<Guid, Guid, RlPortfolioTransferComparisonResult> CompareFunc { get; set; } = null!;
        public List<(Guid A, Guid B)> Compares { get; } = new();

        public ImmutableArray<RlPortfolioStoredPanelInfo> ListStoredPanels() { Interlocked.Increment(ref PanelListCalls); return Panels; }

        public DateOnly StoredPanelFirstValidationDate(Guid panelId, RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan)
        {
            PanelChecks.Enqueue((panelId, mode, plan));
            return PanelDateFunc(panelId, mode, plan);
        }

        public ImmutableArray<RlPortfolioComparableRun> ListComparableRuns() { Interlocked.Increment(ref RunListCalls); return RunInfos; }

        public RlPortfolioTransferComparisonResult CompareRuns(Guid runA, Guid runB) { Compares.Add((runA, runB)); return CompareFunc(runA, runB); }

        public ImmutableArray<RlPortfolioBaseRunInfo> ListBaseRuns() { Interlocked.Increment(ref ListCalls); return Infos; }

        public RlPortfolioBaseRun Admit(Guid baseRunId, DateOnly targetFirstValidationDate)
        {
            AdmitCalls.Enqueue((baseRunId, targetFirstValidationDate));
            return AdmitFunc(baseRunId, targetFirstValidationDate);
        }

        public Task<RlPortfolioZeroShotResult> EvaluateStoredModelAsync(Guid baseRunId, RlPortfolioRunRequest request, CancellationToken cancellationToken)
        {
            Evaluations.Add((baseRunId, request));
            return EvaluateFunc(baseRunId, request, cancellationToken);
        }
    }

    // ----- builders -----

    private static RlPortfolioBaseRun BaseRunOf(Guid id, DateOnly end)
        => new(new RlPortfolioArtifactEntry(id, "base-dir", RlPortfolioArtifactStatus.Verified, null, null), "model", "scaler", "native",
            new string('d', 64), new string('e', 64), new string('f', 64), 4, end.AddDays(-1_000), end);

    private static RlPortfolioBaseRunInfo InfoOf(Guid id, DateOnly end, RlBasePeriodStatus status = RlBasePeriodStatus.Known)
        => new(id, new DateTimeOffset(2026, 9, 1, 10, 30, 0, TimeSpan.Zero), new string('1', 64), new string('2', 64),
            status == RlBasePeriodStatus.Known ? new RlBasePeriod(status, end.AddDays(-1_000), end) : new RlBasePeriod(status, null, null));

    private static PanelPreflight Pre(bool feasible, DateOnly start, DateOnly end, params RlPanelSymbolStatus[] statuses)
        => new(start, end, RlPanelEvaluationMode.ShortHistory,
            statuses.Select((s, i) => new RlPanelSymbolPreflight("S" + i, s, 1400, RlContract.SupportLength, "")).ToImmutableArray(), 1400, 1278,
            ImmutableArray.Create(new RlPanelCellCount("train", RlPanelSymbolGroup.Train, 4, 1), new RlPanelCellCount("validation", RlPanelSymbolGroup.Train, feasible ? 1 : 0, 1),
                new RlPanelCellCount("oos", RlPanelSymbolGroup.Train, 1, 1)), feasible, false);

    private static RlPanelSymbolStatus[] Ready(int count) => Enumerable.Repeat(RlPanelSymbolStatus.Ready, count).ToArray();

    private static readonly DateOnly Today = new(2026, 10, 8);

    /// <summary>A clock fixed at noon UTC of a day, with UTC as the local zone, so "today" is exact.</summary>
    private static TimeProvider NoonUtc(DateOnly day) => new ManualTimeProvider(new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero), TimeZoneInfo.Utc);

    private (PortfolioRlDialogViewModel Vm, FakeService Service, FakeResolver Resolver, FakeTickerStateStore Store) Create(string language = "en", Func<DateOnly, DateOnly, PanelPreflight>? preflight = null, TimeProvider? clock = null,
        RlPortfolioSettings? settings = null, FakeTransfer? transfer = null, StockAnalyzer.Core.Services.StoredPeriod.IStoredPeriodAdvisor? periodAdvisor = null)
    {
        var service = new FakeService();
        if (settings is not null) service.Settings = settings;
        service.Preflight = (_, start, end, _, _, _) => Task.FromResult((preflight ?? ((s, e) => Pre(true, s, e, Ready(4))))(start, end));
        var resolver = new FakeResolver();
        var store = new FakeTickerStateStore();
        var vm = new PortfolioRlDialogViewModel(service, resolver, store, new FakeBacktestDefaultSettingsManager(), new JsonLocalization(language), clock, transfer, periodAdvisor);
        _viewModels.Add(vm);
        return (vm, service, resolver, store);
    }

    private static PortfolioSourceOption All(PortfolioRlDialogViewModel vm) => vm.Sources.Single(s => s.Descriptor.Kind == RlSourceKind.AllTickers);

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "Timed out waiting for " + what);
    }

    private static async Task<PortfolioRlDialogViewModel> Loaded(PortfolioRlDialogViewModel vm, FakeTickerStateStore store)
    {
        store.Groups.Add(new AllTickersNode("All"));
        await vm.InitializeAsync();
        vm.StartDate = "2000-01-03"; vm.EndDateExclusive = "2006-01-03";
        All(vm).IsSelected = true;
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the preflight");
        return vm;
    }

    // ----- sources and defaults -----

    [Fact]
    public async Task TheSourceListMirrorsTheTickerTreeAndTheCostsComeFromTheDefaults()
    {
        var (vm, _, _, store) = Create();
        var watchlist = new WatchlistNode(new WatchlistProfile(Guid.NewGuid(), "Core", IndicatorColor.Gray, false));
        var portfolio = new PortfolioNode(new WatchlistProfile(Guid.NewGuid(), "Held", IndicatorColor.Gray, true));
        var watchlists = new CategoryNode(TickerListViewModel.WatchlistsCategoryId, "Watchlists"); watchlists.Children!.Add(watchlist);
        var portfolios = new CategoryNode(TickerListViewModel.PortfoliosCategoryId, "Portfolios"); portfolios.Children!.Add(portfolio);
        var folder = new TickerListFolderNode(new TickerListFolderSettings { Name = "Tech" });
        var category = new CustomCategoryNode(new TickerParentCategorySettings { Name = "Mine" }); category.Children!.Add(folder);
        var filter = new FilterNode(new FilterSettings { Name = "Cheap" });
        store.Groups.AddRange(new TickerGroupNode[] { new AllTickersNode("All"), watchlists, portfolios, category, filter,
            new ActionNode(Guid.NewGuid(), "Create", new CommunityToolkit.Mvvm.Input.RelayCommand(() => { })) });
        await vm.InitializeAsync();
        Assert.Equal(new[]
        {
            RlSourceDescriptor.AllTickers(), RlSourceDescriptor.Watchlist(), RlSourceDescriptor.Watchlist(watchlist.Id), RlSourceDescriptor.Portfolio(),
            RlSourceDescriptor.Portfolio(portfolio.Id), RlSourceDescriptor.CustomCategory(category.Id), RlSourceDescriptor.ListFolder(folder.Id),
            RlSourceDescriptor.SavedFilter(filter.Id),
        }, vm.Sources.Select(s => s.Descriptor).ToArray());
        Assert.Equal("Mine / Tech", vm.Sources.Single(s => s.Descriptor.Kind == RlSourceKind.ListFolder).Name);
        Assert.All(vm.Sources, s => Assert.False(s.IsSelected));
        var defaults = BacktestDefaultSettings.BuiltIn;
        Assert.Equal((defaults.InitialCapital.ToString(CultureInfo.CurrentCulture), defaults.SlippageRatio.ToString(CultureInfo.CurrentCulture)),
            (vm.InitialCapitalText, vm.SlippageRatioText));
        Assert.True(vm.CostsValid);
        Assert.False(vm.CanStart);          // nothing selected yet
    }

    [Fact]
    public async Task SelectingASourceChecksTheSelectionOnce()
    {
        var (vm, _, resolver, store) = Create();
        store.Groups.Add(new AllTickersNode("All"));
        await vm.InitializeAsync();
        vm.StartDate = "2000-01-03"; vm.EndDateExclusive = "2006-01-03";
        All(vm).IsSelected = true;
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the preflight");
        Assert.Equal(RlSourceDescriptor.AllTickers(), Assert.Single(Assert.Single(resolver.Calls)));
        All(vm).IsSelected = true;                                   // no change, no new check
        await Task.Delay(50);
        Assert.Single(resolver.Calls);
        Assert.DoesNotContain(vm.Sources, s => s.Descriptor.Kind == RlSourceKind.Tag);       // the list is the only way to choose sources
    }

    [Fact]
    public async Task OnlyAnUnreadableSymbolShowsTheReasonOfThePreflight()
    {
        const string reason = "The selected stored bars include the current or a future exchange date.";
        var (vm, _, _, store) = Create(preflight: (s, e) =>
        {
            var p = Pre(true, s, e, RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.ReadError, RlPanelSymbolStatus.CurrencyMismatch);
            return p with { Symbols = p.Symbols.SetItem(1, p.Symbols[1] with { Detail = reason }).SetItem(2, p.Symbols[2] with { Detail = "EUR" }) };
        });
        await Loaded(vm, store);
        Assert.Equal(new[] { "", reason, "" }, vm.Symbols.Select(r => r.Detail).ToArray());
        Assert.Equal(new[] { false, true, false }, vm.Symbols.Select(r => r.HasDetail).ToArray());
        Assert.Equal("The data could not be read", vm.Symbols[1].StatusText);        // the wording stays; the reason is added below it
    }

    // ----- preflight table, gate, period -----

    [Fact]
    public async Task TheStatusTableShowsEverySymbolAndTheExclusionGateOpensStartWithoutANewCheck()
    {
        var statuses = new[] { RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.MissingFile };
        var (vm, service, _, store) = Create(preflight: (s, e) => Pre(true, s, e, statuses));
        await Loaded(vm, store);
        Assert.Equal(new[] { "S0", "S1", "S2", "S3" }, vm.Symbols.Select(r => r.Symbol).ToArray());
        Assert.Equal("Usable", vm.Symbols[0].StatusText);
        Assert.Equal("Local price or metadata file is missing", vm.Symbols[3].StatusText);
        Assert.Equal((1400, RlContract.SupportLength), (vm.Symbols[0].BarsInPeriod, vm.Symbols[0].RequiredBarsPerBlock));
        Assert.Contains("Calendar: 1400 bars (at least 1278 needed); usable symbols: 3 of 4", vm.PreflightText);
        Assert.Contains("The data is sufficient", vm.PreflightText);
        Assert.Equal(3, vm.Cells.Count);
        Assert.Contains("Training: 4 of 1 segments", vm.Cells[0]);
        Assert.True(vm.HasUnavailableSymbols);
        Assert.False(vm.CanStart);
        Assert.Equal("Some symbols cannot be used. Tick the box to continue without them.", vm.BlockerText);
        int calls = service.PreflightCalls.Count;
        vm.ExcludeUnavailable = true;
        Assert.True(vm.CanStart);
        Assert.Equal("", vm.BlockerText);
        Assert.Equal(calls, service.PreflightCalls.Count);        // an exclusion tick is a rule change, not a new check
    }

    [Fact]
    public async Task AOneDayPeriodIsAcceptedAsInputAndTheInfeasiblePreflightDisablesStartWithoutCreatingAJob()
    {
        var (vm, service, _, store) = Create(preflight: (s, e) => Pre(false, s, e, Ready(4)));
        store.Groups.Add(new AllTickersNode("All"));
        await vm.InitializeAsync();
        vm.StartDate = "2026-01-05"; vm.EndDateExclusive = "2026-01-06";
        All(vm).IsSelected = true;
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the preflight");
        Assert.True(vm.PeriodValid);
        Assert.Equal("", vm.PeriodMessage);
        var call = Assert.Single(service.PreflightCalls);
        Assert.Equal((Day, Day.AddDays(1)), (call.Start, call.End));
        Assert.False(vm.CanStart);
        Assert.Equal("The period holds too little data for a run (see needed / actual above). Move the start date earlier, or press \"Recalculate start date\" to set the latest start that has enough bars.", vm.BlockerText);
        Assert.Contains("The data is not sufficient: Validation: 0 of 1 segments", vm.PreflightText);
        Assert.False(vm.StartCommand.CanExecute(null));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Empty(service.Runs);                               // the engine is never reached
    }

    [Fact]
    public async Task APeriodThatIsNotAnIntervalIsReportedAndNeverChecked()
    {
        var (vm, service, _, store) = Create();
        store.Groups.Add(new AllTickersNode("All"));
        await vm.InitializeAsync();
        All(vm).IsSelected = true;
        Assert.Equal("", vm.PeriodMessage);                       // still empty: nothing entered
        vm.EndDateExclusive = "2026-01-06"; vm.StartDate = "2026-01-06";      // the end date is filled at open, so it is entered first: a valid end must not start a check with this start
        Assert.False(vm.PeriodValid);
        Assert.Equal("Enter a start date that is before the end date.", vm.PeriodMessage);
        vm.EndDateExclusive = "2026-1-xx";
        Assert.False(vm.PeriodValid);
        await Task.Delay(100);
        Assert.Empty(service.PreflightCalls);
        Assert.False(vm.CanStart);
        Assert.Null(vm.EndDateExclusiveAsDateTime);
    }

    [Fact]
    public async Task OneResolvedSymbolShowsTheSingleSymbolMessageAndNeverSwitchesMode()
    {
        var (vm, service, resolver, store) = Create(preflight: (s, e) => Pre(true, s, e, Ready(1)));
        resolver.Symbols = ImmutableArray.Create("AAA");
        await Loaded(vm, store);
        Assert.False(vm.CanStart);
        Assert.Equal("Only one symbol is available. Use the single-symbol reinforcement learning dialog instead.", vm.BlockerText);
        Assert.Empty(service.Runs);
    }

    [Fact]
    public async Task TheSymbolHoldoutNoticeAndTheSplitRiskNoticeAreShown()
    {
        var (vm, _, _, store) = Create(preflight: (s, e) => Pre(true, s, e, Ready(3)));
        await Loaded(vm, store);
        Assert.True(vm.CanStart);
        Assert.Contains("The symbol holdout needs at least 4 usable symbols", Assert.Single(vm.Notices));

        var (risky, _, resolver, riskyStore) = Create(preflight: (s, e) => Pre(true, s, e, Ready(4)) with { HoldoutSplitRisk = true });
        resolver.Missing = 2;
        await Loaded(risky, riskyStore);
        Assert.Contains(risky.Notices, n => n.Contains("2 selected source(s) no longer exist"));
        Assert.Contains(risky.Notices, n => n.StartsWith("The symbol split is decided when the data is captured"));
        risky.SymbolHoldout = false;                              // no holdout: the risk notice goes away after the new check
        await Until(() => risky.PreflightState == RlDialogLoadState.Loaded && risky.Notices.All(n => !n.StartsWith("The symbol split")) , "the notice to clear");
    }

    [Fact]
    public async Task AStaleCheckNeverOverwritesANewerSelection()
    {
        var (vm, service, resolver, store) = Create();
        var firstGate = new TaskCompletionSource<PanelPreflight>();
        int call = 0;
        service.Preflight = (_, s, e, _, _, _) => ++call == 1 ? firstGate.Task : Task.FromResult(Pre(true, s, e, Ready(4)));
        store.Groups.Add(new AllTickersNode("All")); store.Groups.Add(new CategoryNode(TickerListViewModel.WatchlistsCategoryId, "Watchlists"));
        await vm.InitializeAsync();
        vm.StartDate = "2000-01-03"; vm.EndDateExclusive = "2006-01-03";
        vm.Sources[0].IsSelected = true;                         // check 1 starts and waits
        await Until(() => service.PreflightCalls.Count == 1, "the first check to start");
        vm.Sources[1].IsSelected = true;                         // check 2 supersedes it
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the second check");
        Assert.Equal(4, vm.Symbols.Count);
        firstGate.SetResult(Pre(true, Day, Day.AddDays(2), RlPanelSymbolStatus.MissingFile));      // the late, stale answer
        await Task.Delay(150);
        Assert.Equal(4, vm.Symbols.Count);
        Assert.All(vm.Symbols, r => Assert.Equal(RlPanelSymbolStatus.Ready, r.Status));
        Assert.Equal(2, service.PreflightCalls.Count);
    }

    [Fact]
    public async Task AFailedCheckIsShownAndLeavesStartDisabled()
    {
        var (vm, service, _, store) = Create();
        service.Preflight = (_, _, _, _, _, _) => Task.FromException<PanelPreflight>(new IOException("disk gone"));
        store.Groups.Add(new AllTickersNode("All"));
        await vm.InitializeAsync();
        vm.StartDate = "2000-01-03"; vm.EndDateExclusive = "2006-01-03";
        All(vm).IsSelected = true;
        await Until(() => vm.PreflightState == RlDialogLoadState.Failed, "the failure");
        Assert.Equal("The check failed: disk gone", vm.PreflightText);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public async Task InvalidCostsDisableStartAndSayWhy()
    {
        var (vm, _, _, store) = Create();
        await Loaded(vm, store);
        Assert.True(vm.CanStart);
        vm.InitialCapitalText = "0";
        Assert.False(vm.CostsValid);
        Assert.False(vm.CanStart);
        Assert.Equal("Enter valid costs: initial equity above zero, the other values zero or more; slippage and liquidation penalty below 1.", vm.CostsMessage);
        vm.InitialCapitalText = "abc";
        Assert.False(vm.CostsValid);
        vm.InitialCapitalText = "10000"; vm.SlippageRatioText = "-0.1";
        Assert.False(vm.CostsValid);
        vm.SlippageRatioText = "0.001";
        Assert.True(vm.CanStart);
    }

    // ----- dates: the end default, the future limit and the automatic start -----

    private static RlStartSuggestion Found(DateOnly start, int available = 1278) => new(RlStartSuggestionStatus.Found, start, 1278, available);

    private async Task<(PortfolioRlDialogViewModel Vm, FakeService Service, FakeResolver Resolver, FakeTickerStateStore Store)> Dated(Func<IReadOnlyList<string>, DateOnly, RlPanelEvaluationMode, CancellationToken, Task<RlStartSuggestion?>>? suggest)
    {
        var created = Create(clock: NoonUtc(Today));
        created.Service.Suggest = suggest;
        created.Store.Groups.Add(new AllTickersNode("All"));
        await created.Vm.InitializeAsync();
        return created;
    }

    [Fact]
    public async Task TheEndDateIsFilledWithTheLastConfirmedDayAndTheStartIsLeftToTheSuggestion()
    {
        var (vm, service, resolver, _) = await Dated(null);
        Assert.Equal(("2026-10-07", ""), (vm.EndDateExclusive, vm.StartDate));
        Assert.Equal(new DateTime(2026, 10, 8), vm.EndDateLatest);
        Assert.True(vm.StartIsAuto);
        Assert.Equal("", vm.PeriodMessage);                         // nothing is wrong yet: the start follows the ticked lists
        Assert.Equal("", vm.StartMessage);
        Assert.False(vm.CanRecalculateStart);
        Assert.Empty(service.PreflightCalls); Assert.Empty(service.SuggestCalls); Assert.Empty(resolver.Calls);
    }

    private async Task<(PortfolioRlDialogViewModel Vm, FakeService Service, FakeStoredPeriodAdvisor Advisor)> DatedWithAdvisor(DateOnly? latest)
    {
        var advisor = new FakeStoredPeriodAdvisor { Latest = _ => Task.FromResult(latest) };
        var created = Create(clock: NoonUtc(Today), periodAdvisor: advisor);
        created.Service.Suggest = (_, _, mode, _) => Task.FromResult<RlStartSuggestion?>(Found(new DateOnly(2006, 8, 1), mode == RlPanelEvaluationMode.Standard ? 5118 : 1278));
        created.Store.Groups.Add(new AllTickersNode("All"));
        await created.Vm.InitializeAsync();
        return (created.Vm, created.Service, advisor);
    }

    [Fact]
    public async Task ThePortfolioEndDateFollowsTheLatestStoredDayOfTheTickedLists_AndTheStartIsSuggestedFromTheNewEnd()
    {
        var (vm, service, advisor) = await DatedWithAdvisor(new DateOnly(2026, 10, 5));
        Assert.Equal("2026-10-07", vm.EndDateExclusive);                          // the fallback until a list is ticked
        Assert.Empty(advisor.LatestCalls);
        All(vm).IsSelected = true;
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the check");
        Assert.Equal("2026-10-06", vm.EndDateExclusive);                          // the exclusive end: the day after the latest stored bar
        Assert.Equal(new DateOnly(2026, 10, 6), Assert.Single(service.SuggestCalls).End);   // the start is suggested from the NEW end
        Assert.Equal(new DateOnly(2026, 10, 6), Assert.Single(service.PreflightCalls).End);
        Assert.Single(advisor.LatestCalls);
    }

    [Fact]
    public async Task ThePortfolioEndDateIsNeverLaterThanToday_AndWithoutStoredDataTheLastConfirmedDayStays()
    {
        var (vm, service, _) = await DatedWithAdvisor(new DateOnly(2026, 10, 8));        // the latest bar is today's: the end would be tomorrow
        All(vm).IsSelected = true;
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the check");
        Assert.Equal("2026-10-08", vm.EndDateExclusive);                          // the picker allows today at most
        var (none, _, _) = await DatedWithAdvisor(null);
        All(none).IsSelected = true;
        await Task.Delay(100);
        Assert.Equal("2026-10-07", none.EndDateExclusive);                        // no stored data: the existing fallback
    }

    [Fact]
    public async Task ATypedPortfolioEndSurvives_AndATypedStartSurvivesWhileTheEndFollows()
    {
        var (typedEnd, _, advisor) = await DatedWithAdvisor(new DateOnly(2026, 10, 5));
        typedEnd.EndDateExclusive = "2026-10-01";
        All(typedEnd).IsSelected = true;
        await Until(() => typedEnd.PreflightState == RlDialogLoadState.Loaded, "the check");
        Assert.Equal("2026-10-01", typedEnd.EndDateExclusive);
        Assert.Empty(advisor.LatestCalls);                                        // a typed end is not asked about at all

        var (typedStart, service, _) = await DatedWithAdvisor(new DateOnly(2026, 10, 5));
        typedStart.StartDate = "2020-01-01";
        All(typedStart).IsSelected = true;
        await Until(() => typedStart.PreflightState == RlDialogLoadState.Loaded, "the check");
        Assert.Equal(("2020-01-01", "2026-10-06"), (typedStart.StartDate, typedStart.EndDateExclusive));
        Assert.Empty(service.SuggestCalls);
    }

    [Fact]
    public async Task ThePortfolioEndReturnsToTheFallbackWhenTheNewSelectionHasNoStoredData()
    {
        var (vm, _, advisor) = await DatedWithAdvisor(new DateOnly(2026, 10, 5));
        All(vm).IsSelected = true;
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded && vm.EndDateExclusive == "2026-10-06", "the first end");
        advisor.Latest = _ => Task.FromResult<DateOnly?>(null);
        All(vm).IsSelected = false; All(vm).IsSelected = true;
        await Until(() => vm.EndDateExclusive == "2026-10-07", "the fallback end");
        Assert.True(vm.PeriodValid || vm.StartDate.Length == 0);
    }

    [Fact]
    public async Task ALateOlderLatestDateNeverOverwritesANewerSelection()
    {
        var (vm, _, advisor) = await DatedWithAdvisor(null);
        var first = new TaskCompletionSource<DateOnly?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        advisor.Latest = _ => ++call == 1 ? first.Task : Task.FromResult<DateOnly?>(new DateOnly(2026, 10, 2));
        All(vm).IsSelected = true;
        await Until(() => advisor.LatestCalls.Count == 1, "the first request");
        All(vm).IsSelected = false; All(vm).IsSelected = true;                    // a newer selection supersedes it
        await Until(() => vm.EndDateExclusive == "2026-10-03", "the newer end");
        first.SetResult(new DateOnly(2026, 9, 1));
        await Task.Delay(100);
        Assert.Equal("2026-10-03", vm.EndDateExclusive);
    }

    [Fact]
    public async Task AFutureEndDateIsRejectedBeforeAnyServiceCallAndTodayIsAccepted()
    {
        var (vm, service, resolver, _) = await Dated((_, _, _, _) => Task.FromResult<RlStartSuggestion?>(Found(new DateOnly(2023, 4, 14))));
        vm.EndDateExclusive = "2026-10-09";
        All(vm).IsSelected = true;
        await Task.Delay(100);
        Assert.False(vm.PeriodValid);
        Assert.Equal("The end date cannot be later than today.", vm.PeriodMessage);
        Assert.False(vm.CanStart);
        Assert.Empty(service.SuggestCalls); Assert.Empty(service.PreflightCalls); Assert.Empty(resolver.Calls);
        vm.EndDateExclusive = "2026-10-08";                           // the end date is exclusive: today is the latest allowed value
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the check");
        Assert.Equal("", vm.PeriodMessage);
        Assert.Equal(new DateOnly(2026, 10, 8), Assert.Single(service.SuggestCalls).End);
        // A typed start with a future end names the end, not the order of the dates.
        vm.StartDate = "2026-10-09"; vm.EndDateExclusive = "2026-10-10";
        Assert.Equal("The end date cannot be later than today.", vm.PeriodMessage);
        vm.EndDateExclusive = "2026-10-08";
        Assert.Equal("Enter a start date that is before the end date.", vm.PeriodMessage);
    }

    [Fact]
    public async Task TheStartDateFollowsTheTickedListsAndTheEvaluationMode()
    {
        var starts = new Dictionary<RlPanelEvaluationMode, DateOnly> { [RlPanelEvaluationMode.Standard] = new(2006, 8, 1), [RlPanelEvaluationMode.ShortHistory] = new(2023, 4, 14) };
        var (vm, service, resolver, _) = await Dated((_, _, mode, _) => Task.FromResult<RlStartSuggestion?>(Found(starts[mode], mode == RlPanelEvaluationMode.Standard ? 5118 : 1278)));
        All(vm).IsSelected = true;
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the check");
        Assert.Equal("2006-08-01", vm.StartDate);
        Assert.True(vm.StartIsAuto);
        Assert.Equal("Start date set automatically: 1278 bars are needed before 2026-10-07.", vm.StartMessage);
        Assert.Equal((new DateOnly(2026, 10, 7), RlPanelEvaluationMode.Standard), Assert.Single(service.SuggestCalls));
        var call = Assert.Single(service.PreflightCalls);
        Assert.Equal((new DateOnly(2006, 8, 1), new DateOnly(2026, 10, 7)), (call.Start, call.End));
        Assert.Equal(1, resolver.Calls.Count);
        vm.UseShortHistory = true;                                    // the minimum changes with the mode, so the start is computed again
        await Until(() => service.PreflightCalls.Count == 2 && vm.PreflightState == RlDialogLoadState.Loaded, "the second check");
        Assert.Equal("2023-04-14", vm.StartDate);
        Assert.Equal(RlPanelEvaluationMode.ShortHistory, service.SuggestCalls[1].Mode);
        Assert.Equal(new DateOnly(2023, 4, 14), service.PreflightCalls[1].Start);
        vm.EndDateExclusive = "2026-10-01";                           // a new end moves the start too
        await Until(() => service.PreflightCalls.Count == 3 && vm.PreflightState == RlDialogLoadState.Loaded, "the third check");
        Assert.Equal(new DateOnly(2026, 10, 1), service.SuggestCalls[2].End);
        Assert.Equal("Start date set automatically: 1278 bars are needed before 2026-10-01.", vm.StartMessage);
        Assert.True(vm.StartIsAuto);
    }

    [Fact]
    public async Task AShortStoredHistoryIsSaidSoAndStillChecked()
    {
        var (vm, service, _, _) = await Dated((_, _, _, _) => Task.FromResult<RlStartSuggestion?>(new RlStartSuggestion(RlStartSuggestionStatus.InsufficientHistory, new DateOnly(2021, 7, 9), 5118, 1218)));
        All(vm).IsSelected = true;
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the check");
        Assert.Equal("2021-07-09", vm.StartDate);
        Assert.Equal("The stored history has 1218 bars but 5118 are needed; the start date is the earliest usable stored date.", vm.StartMessage);
        Assert.Single(service.PreflightCalls);                        // the preflight stays the authority and shows the shortfall
    }

    [Fact]
    public async Task NoStoredDataLeavesTheStartEmptyAndTheDialogIdle()
    {
        var (vm, service, _, _) = await Dated((_, _, _, _) => Task.FromResult<RlStartSuggestion?>(new RlStartSuggestion(RlStartSuggestionStatus.NoUsableData, null, 5118, 0)));
        vm.StartIsAuto = true;
        All(vm).IsSelected = true;
        await Until(() => vm.StartMessage.Length > 0, "the message");
        await Task.Delay(50);
        Assert.Equal(("", RlDialogLoadState.Unloaded), (vm.StartDate, vm.PreflightState));
        Assert.Equal("No stored price data was found for the ticked lists, so no start date was suggested.", vm.StartMessage);
        Assert.Equal("Select a source and enter a period.", vm.PreflightText);
        Assert.Empty(service.PreflightCalls);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public async Task ATypedStartStopsTheAutomaticStartUntilItIsRecalculated()
    {
        int n = 0;
        var (vm, service, _, _) = await Dated((_, _, _, _) => Task.FromResult<RlStartSuggestion?>(Found(++n == 1 ? new DateOnly(2023, 4, 14) : new DateOnly(2023, 5, 2))));
        All(vm).IsSelected = true;
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the check");
        Assert.False(vm.CanRecalculateStart);
        vm.StartDate = "2024-01-02";
        Assert.False(vm.StartIsAuto);
        Assert.Equal("", vm.StartMessage);
        Assert.True(vm.CanRecalculateStart);
        Assert.True(vm.RecalculateStartCommand.CanExecute(null));
        await Until(() => service.PreflightCalls.Count == 2 && vm.PreflightState == RlDialogLoadState.Loaded, "the check with the typed start");
        Assert.Equal(new DateOnly(2024, 1, 2), service.PreflightCalls[1].Start);
        vm.UseShortHistory = true;                                    // a mode change must not overwrite the typed start
        await Until(() => service.PreflightCalls.Count == 3 && vm.PreflightState == RlDialogLoadState.Loaded, "the third check");
        Assert.Equal(("2024-01-02", 1), (vm.StartDate, service.SuggestCalls.Count));
        vm.RecalculateStartCommand.Execute(null);                     // the explicit request overwrites it
        await Until(() => service.PreflightCalls.Count == 4 && vm.PreflightState == RlDialogLoadState.Loaded, "the recalculated check");
        Assert.Equal(("2023-05-02", true, 2), (vm.StartDate, vm.StartIsAuto, service.SuggestCalls.Count));
        Assert.False(vm.CanRecalculateStart);
        Assert.Contains("1278 bars", vm.StartMessage);
    }

    [Fact]
    public async Task AStaleSuggestionNeverOverwritesANewerSelection()
    {
        var gate = new TaskCompletionSource<RlStartSuggestion?>();
        int n = 0;
        var created = Create(clock: NoonUtc(Today));
        created.Service.Suggest = (_, _, _, _) => ++n == 1 ? gate.Task : Task.FromResult<RlStartSuggestion?>(Found(new DateOnly(2023, 4, 14)));
        created.Store.Groups.Add(new AllTickersNode("All")); created.Store.Groups.Add(new CategoryNode(TickerListViewModel.WatchlistsCategoryId, "Watchlists"));
        var vm = created.Vm;
        await vm.InitializeAsync();
        vm.Sources[0].IsSelected = true;                              // suggestion 1 starts and waits
        await Until(() => created.Service.SuggestCalls.Count == 1, "the first suggestion");
        vm.Sources[1].IsSelected = true;                              // check 2 supersedes it
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the second check");
        Assert.Equal("2023-04-14", vm.StartDate);
        gate.SetResult(Found(new DateOnly(1999, 1, 1)));              // the late, stale answer
        await Task.Delay(150);
        Assert.Equal("2023-04-14", vm.StartDate);
        Assert.Single(created.Service.PreflightCalls);
    }

    [Fact]
    public async Task AServiceWithoutAnAdvisorLeavesTheStartToTheUser()
    {
        var (vm, service, _, _) = await Dated(null);
        All(vm).IsSelected = true;
        await Until(() => service.SuggestCalls.Count == 1, "the suggestion call");
        await Task.Delay(50);
        Assert.Equal(("", ""), (vm.StartDate, vm.StartMessage));
        Assert.Empty(service.PreflightCalls);                          // no start, no check
        vm.StartDate = "2020-01-02";                                   // typing one works as before
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the check");
        Assert.Equal(new DateOnly(2020, 1, 2), Assert.Single(service.PreflightCalls).Start);
    }

    // ----- the ticker-list tree -----

    [Fact]
    public async Task TheTreeMirrorsTheTickerHierarchyAndSharesOneOptionPerSource()
    {
        var (vm, _, _, store) = Create();
        var profile = new WatchlistProfile(Guid.NewGuid(), "Core", IndicatorColor.Gray, false);
        var first = new WatchlistNode(profile); var second = new WatchlistNode(profile);     // two nodes, one source
        var watchlists = new CategoryNode(TickerListViewModel.WatchlistsCategoryId, "Watchlists"); watchlists.Children!.Add(first); watchlists.Children.Add(second);
        var portfolio = new PortfolioNode(new WatchlistProfile(Guid.NewGuid(), "Held", IndicatorColor.Gray, true));
        var filter = new FilterNode(new FilterSettings { Name = "Cheap" });
        var folder = new TickerListFolderNode(new TickerListFolderSettings { Name = "Tech" });
        var category = new CustomCategoryNode(new TickerParentCategorySettings { Name = "Mine" }); category.Children!.Add(folder);
        var other = new CategoryNode(Guid.NewGuid(), "Other");
        store.Groups.AddRange(new TickerGroupNode[] { new AllTickersNode("All"), watchlists, portfolio, category, filter, other,
            new ActionNode(Guid.NewGuid(), "Create", new CommunityToolkit.Mvvm.Input.RelayCommand(() => { })) });
        await vm.InitializeAsync();
        Assert.Equal(new[] { "All", "Watchlists", "Held", "Mine", "Cheap", "Other" }, vm.SourceTree.Select(i => i.Name).ToArray());     // the action node is not a row
        var tree = vm.SourceTree;
        Assert.Equal(new[] { "Core", "Core" }, tree[1].Children.Select(i => i.Name).ToArray());
        Assert.Same(tree[1].Children[0].Option, tree[1].Children[1].Option);                      // one descriptor, one option
        Assert.Equal(1, vm.Sources.Count(s => s.Descriptor == RlSourceDescriptor.Watchlist(profile.Id)));
        Assert.Equal("Tech", Assert.Single(tree[3].Children).Name);                                  // the node's own name, not the "Mine / Tech" path of the flat list
        Assert.Equal("Mine / Tech", vm.Sources.Single(s => s.Descriptor.Kind == RlSourceKind.ListFolder).Name);
        Assert.Equal(new[] { true, true, true, true, true, false }, tree.Select(i => i.IsSelectable).ToArray());
        Assert.Equal(new[] { false, false, true, false, true, false }, tree.Select(i => i.HasIcon).ToArray());     // watchlist/portfolio/filter nodes carry the ticker tab's icons
        Assert.Equal(TickerGroupIconGeometries.Portfolio, tree[2].IconData);
        Assert.Equal(TickerGroupIconGeometries.Filter, tree[4].IconData);
        Assert.Equal(TickerGroupIconGeometries.Watchlist, tree[1].Children[0].IconData);
    }

    [Fact]
    public async Task TheSelectionOfARowAndOfItsSourceStayInStep()
    {
        var (vm, _, resolver, store) = Create();
        await Loaded(vm, store);
        All(vm).IsSelected = false;
        var row = Assert.Single(vm.SourceTree);
        Assert.False(row.IsSelected);
        row.IsSelected = true;                                       // the checkbox
        Assert.True(All(vm).IsSelected);
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the check");
        All(vm).IsSelected = false;                                  // the option (for example a test or a command)
        Assert.False(row.IsSelected);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        All(vm).IsSelected = true;
        Assert.Contains(nameof(PortfolioSourceTreeItem.IsSelected), changed);
        Assert.True(resolver.Calls.Count >= 1);
    }

    // ----- run -----

    private static RlPortfolioTrainingResult Result(bool holdoutCell = false, bool allEvaluableCells = false)
    {
        var one = new RlReturnDistribution(2, 0.05m, 0.05m, 0.01m, 0.09m, 1m);
        var delta = new RlReturnDistribution(2, 0.02m, 0.02m, -0.01m, 0.05m, 0.5m);
        var cells = ImmutableArray.CreateBuilder<RlPortfolioCellReport>();
        cells.Add(new RlPortfolioCellReport("validation", RlPanelSymbolGroup.Train, ImmutableArray.Create("S0", "S1"), 2, 1, true,
            ImmutableArray<RlPortfolioBlockResult>.Empty, one, RlPortfolioEvaluationContract.Baselines.Select(id => new RlPortfolioBaselineSummary(id, one, delta)).ToImmutableArray()));
        if (allEvaluableCells)
            foreach (var (partition, group) in new[] { ("validation", RlPanelSymbolGroup.Holdout), ("oos", RlPanelSymbolGroup.Train), ("oos", RlPanelSymbolGroup.Holdout) })
                cells.Add(new RlPortfolioCellReport(partition, group, ImmutableArray.Create("S0", "S1"), 2, 1, true,
                    ImmutableArray<RlPortfolioBlockResult>.Empty, one, ImmutableArray<RlPortfolioBaselineSummary>.Empty));
        if (holdoutCell)
            cells.Add(new RlPortfolioCellReport("oos", RlPanelSymbolGroup.Holdout, ImmutableArray.Create("S2", "S3"), 0, 1, false,
                ImmutableArray<RlPortfolioBlockResult>.Empty, null, ImmutableArray<RlPortfolioBaselineSummary>.Empty));
        var report = new RlPortfolioEvaluationReport(RlPortfolioContract.SpecificationId, 1, RlPortfolioEvaluationContract.ContractHash, RlPortfolioContract.ContractHash,
            RlShortHistoryEvaluationContract.SpecificationId, new string('a', 64), new string('b', 64), new string('c', 64), ImmutableArray.Create("S0", "S1", "S2", "S3"),
            ImmutableArray.Create("S0", "S1"), ImmutableArray.Create("S2", "S3"), true, BacktestConfigurationFor(), 100, false, cells.ToImmutable(),
            RlSourceContract.Unverified, RlConstraintContract.InvestmentEfficacy, RlPortfolioEvaluationContract.Statement, ImmutableArray<string>.Empty);
        return new RlPortfolioTrainingResult(Guid.NewGuid(), "run-dir", "model", "parity", 42, 512, new string('a', 64), new string('b', 64), "report", report, "panel-dir");
    }

    private static BacktestConfiguration BacktestConfigurationFor() => new() { InitialCapital = 10_000m };

    [Fact]
    public async Task StartFreezesTheRunFromTheGateAndShowsTheDescriptiveResult()
    {
        var statuses = new[] { RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.NotEquity };
        var (vm, service, _, store) = Create(preflight: (s, e) => Pre(true, s, e, statuses));
        var gate = new TaskCompletionSource<RlPortfolioTrainingResult>();
        service.Run = (_, _) => gate.Task;
        await Loaded(vm, store);
        vm.ExcludeUnavailable = true; vm.SmokeOnly = true; vm.UseShortHistory = false;
        await Until(() => vm.PreflightState == RlDialogLoadState.Loaded && vm.CanStart, "the gate");
        var run = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanStart);
        var request = Assert.Single(service.Runs);
        vm.UseShortHistory = true; vm.SmokeOnly = false; vm.InitialCapitalText = "1";      // late edits cannot change the frozen run
        Assert.Equal(new[] { "S0", "S1", "S2", "S3" }, request.Symbols.ToArray());           // the unusable symbol is excluded, never silently kept
        Assert.Equal((new DateOnly(2000, 1, 3), new DateOnly(2006, 1, 3), true, RlPanelEvaluationMode.Standard, true),
            (request.Start, request.EndExclusive, request.SymbolHoldout, request.Mode, request.SmokeOnly));
        Assert.Equal(BacktestDefaultSettings.BuiltIn.InitialCapital, request.Costs.InitialCapital);
        gate.SetResult(Result(holdoutCell: true));
        await run;
        Assert.Equal(("Succeeded", false), (vm.Status, vm.IsBusy));
        Assert.Equal("Completed", vm.StatusDisplay);
        Assert.Equal("Evidence saved in run-dir", vm.Message);
        Assert.True(vm.HasResult);
        Assert.Equal("Validation / Training symbols: 2 segments, mean return 5.00 % (this cell chose the checkpoint, so the figure is optimistic)", vm.ResultLines[0]);
        Assert.Equal("  versus Cash: mean difference 2.00 %; the model was ahead in 50.00 % of the segments", vm.ResultLines[1]);
        Assert.Equal("  versus Equal-weight buy and hold: mean difference 2.00 %; the model was ahead in 50.00 % of the segments", vm.ResultLines[2]);
        Assert.Equal("Final evaluation / Held-out symbols: not evaluable (0 of 1 segments)", vm.ResultLines[^3]);
        Assert.Equal(new[] { "Stored prices are not verified as point-in-time data.", RlPortfolioEvaluationContract.Statement }, vm.ResultLines.TakeLast(2).ToArray());   // the caveats close the result
    }

    // ---- holdout share, weight cap and block-count entry of one run ----

    [Fact]
    public async Task TheHoldoutShareAndTheWeightCapStartAtTheConfiguredValuesAndAreSentOnlyWhenChanged()
    {
        var (vm, service, _, store) = Create(preflight: (s, e) => Pre(true, s, e, Ready(5)));
        service.Settings = new RlPortfolioSettings { HoldoutPercent = 25, MaxWeightPercent = 100 };
        service.Run = (_, _) => Task.FromResult(Result());
        await Loaded(vm, store);
        Assert.Equal(("25", "100", true), (vm.HoldoutPercentText, vm.MaxWeightPercentText, vm.PercentsValid));
        await Settled(() => vm.PreflightState == RlDialogLoadState.Loaded && vm.CanStart, "the gate");
        Assert.All(service.PreflightPercents, p => Assert.Null(p));                                  // a default run asks the question it always asked
        await vm.StartCommand.ExecuteAsync(null);
        var plain = Assert.Single(service.Runs);
        Assert.Equal((null, null), (plain.HoldoutPercent, plain.MaxWeightPercent));

        vm.HoldoutPercentText = "40"; vm.MaxWeightPercentText = "35";
        await Settled(() => service.PreflightPercents.Contains(40) && vm.PreflightState == RlDialogLoadState.Loaded && vm.CanStart, "the check under the new share");
        await vm.StartCommand.ExecuteAsync(null);
        var changed = service.Runs[^1];
        Assert.Equal((40, 35), (changed.HoldoutPercent, changed.MaxWeightPercent));
    }

    [Theory]
    [InlineData("0", "100")]
    [InlineData("51", "100")]
    [InlineData("abc", "100")]
    [InlineData("25", "0")]
    [InlineData("25", "101")]
    [InlineData("25", "")]
    [InlineData("-5", "100")]
    public async Task AnInvalidPercentBlocksStartWithAMessageAndStartsNoCheck(string holdout, string weight)
    {
        var (vm, service, _, store) = Create(preflight: (s, e) => Pre(true, s, e, Ready(5)));
        await Loaded(vm, store);
        await Settled(() => vm.PreflightState == RlDialogLoadState.Loaded && vm.CanStart, "the gate");
        int calls = service.PreflightCalls.Count;
        vm.HoldoutPercentText = holdout; vm.MaxWeightPercentText = weight;
        await Task.Delay(150);
        Assert.False(vm.PercentsValid);
        Assert.False(vm.CanStart);
        Assert.Contains("whole numbers from 1 to 50", vm.PercentMessage);
        Assert.Contains("from 1 to 100", vm.PercentMessage);
        Assert.Equal(calls, service.PreflightCalls.Count);                                                  // an invalid input starts no new check
        vm.HoldoutPercentText = "25"; vm.MaxWeightPercentText = "100";
        Assert.True(vm.PercentsValid);
        Assert.Equal("", vm.PercentMessage);
    }

    [Fact]
    public async Task TheHoldoutShareIsEditableOnlyWhileTheSymbolHoldoutIsOnAndNoStoredPanelDecidesIt()
    {
        var (vm, _, _, store) = Create(preflight: (s, e) => Pre(true, s, e, Ready(5)));
        await Loaded(vm, store);
        Assert.True(vm.HoldoutPercentEditable);
        vm.SymbolHoldout = false;
        Assert.False(vm.HoldoutPercentEditable);
        vm.SymbolHoldout = true;
        vm.IsBusy = true;
        Assert.False(vm.HoldoutPercentEditable);
    }

    [Fact]
    public async Task TheBlockInputsAreTheBarInputsInWholeBlocks()
    {
        var (vm, _, _, store) = Create(preflight: (s, e) => Pre(true, s, e, Ready(5)));
        await Loaded(vm, store);
        vm.UseShortHistory = true; vm.IsBlockCountPartition = true;
        Assert.Equal(("1", "1", "1"), (vm.TrainBlocksText, vm.ValidationBlocksText, vm.TestBlocksText));           // 192 bars each
        vm.TrainBlocksText = "3"; vm.ValidationBlocksText = "10"; vm.TestBlocksText = "10";
        Assert.Equal(("576", "1920", "1920"), (vm.MinimumTrainBarsText, vm.MinimumValidationBarsText, vm.MinimumTestBarsText));
        Assert.Equal(("3", "10", "10"), (vm.TrainBlocksText, vm.ValidationBlocksText, vm.TestBlocksText));
        // Bars that are not whole blocks show the rounded-up block count, as the effective-bars line does.
        vm.MinimumTrainBarsText = "250";
        Assert.Equal("2", vm.TrainBlocksText);
        // Anything that is not a whole number of blocks inside the band leaves the bars alone.
        foreach (string bad in new[] { "0", "-1", "abc", "", "3.5", "81" })
        {
            vm.ValidationBlocksText = bad;
            Assert.Equal("1920", vm.MinimumValidationBarsText);
        }
        vm.ValidationBlocksText = "80";                                                                              // 15,360 bars: the top of the band
        Assert.Equal("15360", vm.MinimumValidationBarsText);
        vm.MinimumTestBarsText = "abc";
        Assert.Equal("", vm.TestBlocksText);
        Assert.Equal("250", vm.MinimumTrainBarsText);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void OnlyTheTrainingSymbolValidationCellIsMarkedAsTheSelectionSet(string language)
    {
        var localization = new JsonLocalization(language);
        var lines = PortfolioRlPresentation.Result(Result(allEvaluableCells: true).Evaluation, localization);
        string validation = localization.GetString("Rl.Ui.Partition.validation"), oos = localization.GetString("Rl.Ui.Partition.oos");
        string train = localization.GetString("Prl.Group.Train"), holdout = localization.GetString("Prl.Group.Holdout");
        string marked = localization.GetString("Prl.Result.CellSelection"), plain = localization.GetString("Prl.Result.Cell");
        Assert.Equal(new[]
        {
            string.Format(marked, validation, train, 2, "5.00 %"),      // the cell the checkpoint was chosen on
            string.Format(plain, validation, holdout, 2, "5.00 %"),
            string.Format(plain, oos, train, 2, "5.00 %"),
            string.Format(plain, oos, holdout, 2, "5.00 %"),
        }, lines.Where(l => !l.StartsWith(' ')).Take(4).ToArray());   // indented lines are the baseline comparisons
        Assert.NotEqual(marked, plain);
        Assert.Equal(new[] { localization.GetString("Prl.Result.PricePit"), RlPortfolioEvaluationContract.Statement }, lines.TakeLast(2).ToArray());   // the caveats close the result
    }

    [Fact]
    public async Task CancelStopsTheRunAndReleasesTheDialog()
    {
        var (vm, service, _, store) = Create();
        service.Run = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null!; };
        await Loaded(vm, store);
        var run = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await run;
        Assert.Equal(("Cancelled", false), (vm.Status, vm.IsBusy));
        Assert.False(vm.HasResult);
    }

    [Theory]
    [InlineData(true, "InsufficientData")]
    [InlineData(false, "Failed")]
    public async Task AFailedRunShowsTheStateAndKeepsTheTechnicalText(bool insufficient, string state)
    {
        var (vm, service, _, store) = Create();
        service.Run = (_, _) => Task.FromException<RlPortfolioTrainingResult>(insufficient
            ? new InvalidDataException(RlSourceContract.InsufficientData("short"))
            : new InvalidOperationException("worker exited"));
        await Loaded(vm, store);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal((state, false), (vm.Status, vm.IsBusy));
        Assert.Contains(insufficient ? "short" : "worker exited", vm.TechnicalDetails);
        Assert.True(vm.CanStart);                                  // the dialog is usable again
    }

    [Fact]
    public async Task TheJapaneseWordingComesFromTheSameDecision()
    {
        var statuses = new[] { RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.CurrencyMismatch };
        var (vm, _, _, store) = Create("ja", (s, e) => Pre(true, s, e, statuses));
        await Loaded(vm, store);
        // The Japanese expectations below are deliberate literals: they pin the wording a Japanese user reads (the one exception to English-only code).
        Assert.Equal("他の銘柄と通貨が異なります", vm.Symbols[1].StatusText);
        Assert.Equal("使用できない銘柄があります。除いて続行するにはチェックを入れてください。", vm.BlockerText);
        Assert.DoesNotContain("[", vm.PreflightText);
    }

    // ----- partition of the short-history evaluation -----

    private static RlPanelPartitionPlan Block(int train = 1, int validation = 1, int test = 1)
        => new(RlPortfolioPartitionMode.BlockCount, new RlPanelBlockRequirement(train, validation, test), new RlPanelMinimumBars(train * 192, validation * 192, test * 192));

    private static async Task Settled(Func<bool> condition, string what) { await Until(condition, what); await Task.Delay(60); }

    [Fact]
    public void TheDialogStartsAtTheConfiguredPartitionWithoutAnyCheck()
    {
        var settings = new RlPortfolioSettings { PartitionMode = RlPortfolioPartitionMode.BlockCount, MinimumTrainBars = 250, MinimumValidationBars = 192, MinimumTestBars = 384 };
        var (vm, service, _, _) = Create(settings: settings);
        Assert.Equal(RlPortfolioPartitionMode.BlockCount, vm.PartitionMode);
        Assert.True(vm.IsBlockCountPartition); Assert.False(vm.IsRatioPartition);
        Assert.Equal(("250", "192", "384"), (vm.MinimumTrainBarsText, vm.MinimumValidationBarsText, vm.MinimumTestBarsText));
        vm.UseShortHistory = true;
        Assert.Equal(("250 bars = 2 block(s) = 384 bars", "192 bars = 1 block(s) = 192 bars", "384 bars = 2 block(s) = 384 bars"),
            (vm.TrainBarsEffective, vm.ValidationBarsEffective, vm.TestBarsEffective));
        Assert.Empty(service.PreflightCalls);
    }

    [Fact]
    public async Task DefaultSettingsPassNoPlanToTheServiceAndToTheRun()
    {
        var (vm, service, _, store) = Create();
        await Loaded(vm, store);
        vm.UseShortHistory = true;
        await Settled(() => service.PreflightCalls.Count >= 2 && vm.PreflightState == RlDialogLoadState.Loaded, "the short-history check");
        Assert.All(service.PreflightPlans, plan => Assert.Null(plan));
        Assert.All(service.SuggestPlans, plan => Assert.Null(plan));
        Assert.True(vm.PartitionEditable);
        service.Run = (_, _) => Task.FromResult(Result());
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(service.Runs).Plan);
    }

    [Fact]
    public async Task ChoosingTheBlockCountMethodChecksAgainUnderThePlanAndStartFreezesIt()
    {
        var (vm, service, _, store) = Create();
        await Loaded(vm, store);
        vm.UseShortHistory = true;
        await Settled(() => vm.PreflightState == RlDialogLoadState.Loaded, "the short-history check");
        int before = service.PreflightCalls.Count;
        vm.IsBlockCountPartition = true;
        await Settled(() => service.PreflightCalls.Count > before && vm.PreflightState == RlDialogLoadState.Loaded, "the check under the plan");
        Assert.False(vm.IsRatioPartition);
        Assert.Equal(Block(), service.PreflightPlans[^1]);
        service.SuggestPlans.Clear();
        vm.RecalculateStartCommand.Execute(null);                                 // an automatic start is suggested under the plan too
        await Until(() => service.SuggestPlans.Count > 0, "the suggestion");
        Assert.Equal(Block(), service.SuggestPlans[^1]);
        await Settled(() => vm.PreflightState == RlDialogLoadState.Loaded && vm.CanStart, "the check after the suggestion");
        var gate = new TaskCompletionSource<RlPortfolioTrainingResult>();
        service.Run = (_, _) => gate.Task;
        var run = vm.StartCommand.ExecuteAsync(null);
        var request = Assert.Single(service.Runs);
        vm.MinimumTrainBarsText = "500"; vm.PartitionMode = RlPortfolioPartitionMode.Ratio;       // late edits cannot change the frozen run
        Assert.Equal(Block(), request.Plan);
        gate.SetResult(Result());
        await run;
    }

    [Fact]
    public async Task ARatioPlanThatDiffersFromTheSettingsIsPassedExplicitly()
    {
        var settings = new RlPortfolioSettings { PartitionMode = RlPortfolioPartitionMode.BlockCount };
        var (vm, service, _, store) = Create(settings: settings);
        await Loaded(vm, store);
        vm.UseShortHistory = true;
        await Settled(() => vm.PreflightState == RlDialogLoadState.Loaded, "the check");
        Assert.Null(service.PreflightPlans[^1]);                                  // the dialog shows exactly the settings: the service resolves them itself
        int before = service.PreflightCalls.Count;
        vm.IsRatioPartition = true;
        await Settled(() => service.PreflightCalls.Count > before && vm.PreflightState == RlDialogLoadState.Loaded, "the ratio check");
        var plan = service.PreflightPlans[^1]!;
        Assert.True(plan.IsDefault);                                              // an explicit default plan wins over the BlockCount settings
    }

    [Theory]
    [InlineData("191")]
    [InlineData("15361")]
    [InlineData("0")]
    [InlineData("-192")]
    [InlineData("250.5")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task AMinimumThatIsNotAWholeNumberInTheHardBandIsReportedAndNeverChecked(string text)
    {
        var (vm, service, _, store) = Create();
        await Loaded(vm, store);
        vm.UseShortHistory = true;
        await Settled(() => vm.PreflightState == RlDialogLoadState.Loaded, "the check");
        int calls = service.PreflightCalls.Count;
        vm.MinimumTestBarsText = text;
        await Task.Delay(150);
        Assert.False(vm.MinimumBarsValid);
        Assert.Equal("Enter whole numbers from 192 to 15360 bars for all three minimums.", vm.MinimumBarsMessage);
        Assert.False(vm.CanStart);
        Assert.Equal(RlDialogLoadState.Unloaded, vm.PreflightState);
        Assert.Equal(calls, service.PreflightCalls.Count);
        Assert.Equal("", vm.TestBarsEffective);
        vm.MinimumTestBarsText = "192";                                          // a valid value resumes the check
        await Settled(() => vm.PreflightState == RlDialogLoadState.Loaded && vm.CanStart, "the resumed check");
        Assert.Equal("", vm.MinimumBarsMessage);
        Assert.True(service.PreflightCalls.Count > calls);
    }

    [Theory]
    [InlineData("192", 1)]
    [InlineData("193", 2)]
    [InlineData("250", 2)]
    [InlineData("384", 2)]
    [InlineData("15360", 80)]
    public void AMinimumIsShownAsBarsBlocksAndTheRoundedBars(string text, int blocks)
    {
        var (vm, _, _, _) = Create();
        vm.UseShortHistory = true;
        vm.MinimumTrainBarsText = text;
        Assert.Equal($"{text} bars = {blocks} block(s) = {blocks * 192} bars", vm.TrainBarsEffective);
    }

    [Fact]
    public async Task TheStandardEvaluationDisablesThePartitionControlsAndSaysWhy()
    {
        var (vm, service, _, store) = Create();
        await Loaded(vm, store);
        Assert.False(vm.UseShortHistory);
        Assert.False(vm.PartitionEditable);
        Assert.True(vm.ShowPartitionFixed);
        Assert.StartsWith("The standard evaluation uses the fixed minimum of 1 / 5 / 5 blocks (train / validation / test).", vm.PartitionFixedText);
        // Nothing typed in a disabled field blocks a standard run, and no plan is passed.
        vm.MinimumTrainBarsText = "abc";
        await Settled(() => vm.PreflightState == RlDialogLoadState.Loaded && vm.CanStart, "the standard check");
        Assert.True(vm.MinimumBarsValid);
        Assert.Equal("", vm.TrainBarsEffective);
        Assert.Empty(vm.PartitionNotices);
        Assert.All(service.PreflightPlans, plan => Assert.Null(plan));
        vm.UseShortHistory = true;
        Assert.True(vm.PartitionEditable);
        Assert.False(vm.ShowPartitionFixed);
        Assert.False(vm.MinimumBarsValid);                                       // the typed text now counts
    }

    [Fact]
    public void TheReplayNoticeFollowsTheTrainBlocksOfTheBlockCountMethodOnly()
    {
        var (vm, _, _, _) = Create();
        vm.UseShortHistory = true;
        Assert.Empty(vm.PartitionNotices);                                        // the ratio method has many train blocks
        vm.IsBlockCountPartition = true;
        Assert.Equal(new[] { "Each training block is replayed about 80 times per seed (the same market path again, not new data).",
            "With one training block the policy practises on a single episode; consider raising the train minimum." }, vm.PartitionNotices.ToArray());
        vm.MinimumTrainBarsText = "500";                                          // 3 blocks: 10240 / (3 * 128) = 26, and no hint
        Assert.Equal(new[] { "Each training block is replayed about 26 times per seed (the same market path again, not new data)." }, vm.PartitionNotices.ToArray());
        vm.MinimumTrainBarsText = "15360";                                        // 80 blocks: replayed once
        Assert.Equal(new[] { "Each training block is replayed about 1 times per seed (the same market path again, not new data)." }, vm.PartitionNotices.ToArray());
        vm.UseShortHistory = false;
        Assert.Empty(vm.PartitionNotices);
    }

    [Fact]
    public async Task TheSymbolTableShowsTheRoleOfEveryReadySymbolAndTheLegend()
    {
        var roles = new[] { RlPanelSymbolRole.Trains, RlPanelSymbolRole.EvaluatedOnly, RlPanelSymbolRole.Trains, RlPanelSymbolRole.None };
        var (vm, _, _, store) = Create(preflight: (s, e) =>
        {
            var pre = Pre(true, s, e, RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.NotEquity);
            return pre with { Symbols = pre.Symbols.Select((sym, i) => sym with { Role = roles[i] }).ToImmutableArray() };
        });
        await Loaded(vm, store);
        Assert.Equal(new[] { "Trains", "Evaluated only", "Trains", "" }, vm.Symbols.Select(r => r.RoleText).ToArray());      // a symbol that is not ready has no role
        Assert.Equal(new[] { RlPanelSymbolRole.Trains, RlPanelSymbolRole.EvaluatedOnly, RlPanelSymbolRole.Trains, RlPanelSymbolRole.None }, vm.Symbols.Select(r => r.Role).ToArray());
        var ja = new JsonLocalization("ja");
        Assert.Equal(("学習する", "評価のみ", "完全なブロックなし"),
            (PortfolioRlPresentation.Role(RlPanelSymbolRole.Trains, ja), PortfolioRlPresentation.Role(RlPanelSymbolRole.EvaluatedOnly, ja), PortfolioRlPresentation.Role(RlPanelSymbolRole.None, ja)));
    }

    [Fact]
    public async Task ALateCheckUnderTheOldPlanNeverOverwritesTheNewOne()
    {
        var (vm, service, _, store) = Create();
        var firstGate = new TaskCompletionSource<PanelPreflight>();
        int call = 0;
        service.Preflight = (_, s, e, _, _, _) => ++call == 2 ? firstGate.Task : Task.FromResult(Pre(true, s, e, Ready(4)));
        await Loaded(vm, store);                                                  // call 1: standard
        vm.UseShortHistory = true;                                                // call 2 waits
        await Until(() => service.PreflightCalls.Count == 2, "the second check to start");
        vm.IsBlockCountPartition = true;                                          // call 3 supersedes it
        await Until(() => service.PreflightCalls.Count == 3 && vm.PreflightState == RlDialogLoadState.Loaded, "the check under the plan");
        Assert.Equal(4, vm.Symbols.Count);
        firstGate.SetResult(Pre(true, Day, Day.AddDays(2), RlPanelSymbolStatus.MissingFile));
        await Task.Delay(150);
        Assert.All(vm.Symbols, r => Assert.Equal(RlPanelSymbolStatus.Ready, r.Status));
        Assert.Equal(Block(), service.PreflightPlans[^1]);
    }

    [Fact]
    public void TheJapanesePartitionWordingComesFromTheSameState()
    {
        var (vm, _, _, _) = Create("ja");
        vm.UseShortHistory = true; vm.IsBlockCountPartition = true; vm.MinimumTrainBarsText = "250";
        // Deliberate Japanese literals: they pin the wording a Japanese user reads.
        Assert.Equal("250 本 = 2 ブロック = 384 本", vm.TrainBarsEffective);
        Assert.Equal("学習ブロックは 1 シードあたり約 40 回繰り返し使われます（同じ相場経路の繰り返しで、新しいデータではありません）。", vm.PartitionNotices[0]);
        Assert.DoesNotContain("[Prl.", string.Join("", vm.PartitionNotices));
    }

    // ----- stored base model (transfer) -----

    private static readonly DateOnly FirstValidation = new(2004, 6, 1);
    private static readonly Guid BaseA = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"), BaseB = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

    private (PortfolioRlDialogViewModel Vm, FakeService Service, FakeTransfer Transfer, FakeTickerStateStore Store) CreateTransfer(string language = "en", DateOnly? validation = null)
    {
        var transfer = new FakeTransfer { Infos = ImmutableArray.Create(InfoOf(BaseA, new DateOnly(2003, 1, 1)), InfoOf(BaseB, new DateOnly(2002, 1, 1), RlBasePeriodStatus.Unknown)) };
        var date = validation ?? FirstValidation;
        var (vm, service, _, store) = Create(language, (s, e) => Pre(true, s, e, Ready(4)) with { FirstValidationDate = date }, transfer: transfer);
        return (vm, service, transfer, store);
    }

    private static PortfolioBaseRunRow Row(PortfolioRlDialogViewModel vm, Guid id) => vm.BaseRuns.Single(r => r.RunId == id);

    [Fact]
    public async Task WithoutAStoredModelServiceTheTransferControlsStayOutAndEveryRunTrainsFromScratch()
    {
        var (vm, service, _, store) = Create();
        await Loaded(vm, store);
        Assert.False(vm.TransferAvailable);
        Assert.Equal((PortfolioRunKind.Scratch, false, false, ""), (vm.RunKind, vm.ShowBaseSelection, vm.ShowFreeze, vm.AdmissionMessage));
        vm.RunKind = PortfolioRunKind.FineTune;                       // a kind without a service cannot gate or change the run
        Assert.True(vm.CanStart);
        service.Run = (_, _) => Task.FromResult(Result());
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(service.Runs).Transfer);
    }

    [Fact]
    public async Task ChoosingAStoredModelKindListsTheBaseRunsAndNeedsABaseBeforeStart()
    {
        var (vm, _, transfer, store) = CreateTransfer();
        await Loaded(vm, store);
        Assert.Equal((0, false, true), (transfer.ListCalls, vm.ShowBaseSelection, vm.CanStart));      // scratch: nothing listed, nothing gated
        vm.RunKind = PortfolioRunKind.FineTune;
        await Until(() => vm.BaseRuns.Count == 2, "the base runs");
        Assert.Equal(1, transfer.ListCalls);
        Assert.Equal((true, true, false, false), (vm.ShowBaseSelection, vm.ShowFreeze, vm.CanStart, vm.NoBaseRuns));
        Assert.Equal("Choose a base run.", vm.AdmissionMessage);
        Assert.Equal(PortfolioAdmissionState.None, vm.AdmissionState);
        Assert.Equal("2026-09-01 10:30 UTC | run aaaaaaaa | data 2000-04-06 to 2003-01-01 (end exclusive)", Row(vm, BaseA).Text);
        Assert.Equal("2026-09-01 10:30 UTC | run bbbbbbbb | data period unknown (its panel is not stored)", Row(vm, BaseB).Text);
        vm.RunKind = PortfolioRunKind.EvaluateBase;
        Assert.Equal((true, false), (vm.ShowBaseSelection, vm.ShowFreeze));                          // an evaluation has no freeze
        vm.RunKind = PortfolioRunKind.Scratch;
        Assert.Equal((false, true, ""), (vm.ShowBaseSelection, vm.CanStart, vm.AdmissionMessage));
    }

    [Fact]
    public async Task AnEmptyStoreSaysSoAndKeepsStartDisabled()
    {
        var (vm, _, transfer, store) = CreateTransfer();
        transfer.Infos = ImmutableArray<RlPortfolioBaseRunInfo>.Empty;
        await Loaded(vm, store);
        vm.RunKind = PortfolioRunKind.EvaluateBase;
        await Until(() => transfer.ListCalls == 1, "the listing");
        await Task.Delay(60);
        Assert.Equal((true, false), (vm.NoBaseRuns, vm.CanStart));
    }

    [Fact]
    public async Task TheBaseIsAdmittedAgainstTheFirstValidationDateOfTheCurrentCheckAndStartOpensOnlyThen()
    {
        var (vm, _, transfer, store) = CreateTransfer();
        var gate = new TaskCompletionSource();
        transfer.AdmitFunc = (id, _) => { gate.Task.Wait(); return BaseRunOf(id, new DateOnly(2003, 1, 1)); };
        await Loaded(vm, store);
        vm.RunKind = PortfolioRunKind.FineTune;
        await Until(() => vm.BaseRuns.Count == 2, "the base runs");
        vm.SelectedBaseRun = Row(vm, BaseA);
        await Until(() => !transfer.AdmitCalls.IsEmpty, "the admission call");
        Assert.Equal((PortfolioAdmissionState.Pending, false), (vm.AdmissionState, vm.CanStart));
        Assert.Equal("Checking the base run...", vm.AdmissionMessage);
        gate.SetResult();
        await Until(() => vm.AdmissionState == PortfolioAdmissionState.Admitted, "the admission");
        Assert.Equal((BaseA, FirstValidation), Assert.Single(transfer.AdmitCalls));
        Assert.Equal("Base run accepted: its data ends 2003-01-01 (exclusive); the validation of this run starts 2004-06-01.", vm.AdmissionMessage);
        Assert.True(vm.CanStart);
    }

    [Fact]
    public async Task ARejectedBaseShowsTheExactReasonAndKeepsStartClosedUntilAnInputChanges()
    {
        string reason = "PortfolioTransfer: the base data reaches 2005-01-01, the target validation starts 2004-06-01.";
        var (vm, _, transfer, store) = CreateTransfer();
        transfer.AdmitFunc = (id, date) => date == FirstValidation ? throw new InvalidDataException(reason) : BaseRunOf(id, new DateOnly(2003, 1, 1));
        await Loaded(vm, store);
        vm.RunKind = PortfolioRunKind.EvaluateBase;
        await Until(() => vm.BaseRuns.Count == 2, "the base runs");
        vm.SelectedBaseRun = Row(vm, BaseA);
        await Until(() => vm.AdmissionState == PortfolioAdmissionState.Rejected, "the rejection");
        Assert.Equal((reason, true, false), (vm.AdmissionMessage, vm.AdmissionRejected, vm.CanStart));
        await Task.Delay(120);
        Assert.Equal(PortfolioAdmissionState.Rejected, vm.AdmissionState);                           // it stays until an input changes
        Assert.Single(transfer.AdmitCalls);
    }

    [Fact]
    public async Task EveryInputChangeEntersThePendingStateAgainAndAdmitsOnTheNewCheck()
    {
        var later = new DateOnly(2004, 9, 1);
        var (vm, service, transfer, store) = CreateTransfer();
        await Loaded(vm, store);
        vm.RunKind = PortfolioRunKind.FineTune;
        await Until(() => vm.BaseRuns.Count == 2, "the base runs");
        vm.SelectedBaseRun = Row(vm, BaseA);
        await Until(() => vm.AdmissionState == PortfolioAdmissionState.Admitted, "the first admission");
        var check = new TaskCompletionSource<PanelPreflight>();
        service.Preflight = (_, _, _, _, _, _) => check.Task;       // the next data check stays in flight
        vm.SymbolHoldout = !vm.SymbolHoldout;                       // an input of the data check changed
        Assert.Equal((PortfolioAdmissionState.Pending, false, "The base run is checked as soon as the data check has run."), (vm.AdmissionState, vm.CanStart, vm.AdmissionMessage));
        Assert.Single(transfer.AdmitCalls);                         // nothing is admitted against an outdated check
        check.SetResult(Pre(true, new DateOnly(2000, 1, 3), new DateOnly(2006, 1, 3), Ready(4)) with { FirstValidationDate = later });
        await Until(() => transfer.AdmitCalls.Count == 2 && vm.AdmissionState == PortfolioAdmissionState.Admitted, "the second admission");
        Assert.Equal(later, transfer.AdmitCalls.Last().Date);
        var slow = new TaskCompletionSource();
        transfer.AdmitFunc = (id, _) => { slow.Task.Wait(); return BaseRunOf(id, new DateOnly(2002, 1, 1)); };
        vm.SelectedBaseRun = Row(vm, BaseB);                        // another base
        Assert.Equal(PortfolioAdmissionState.Pending, vm.AdmissionState);
        slow.SetResult();
        await Until(() => transfer.AdmitCalls.Count == 3 && vm.AdmissionState == PortfolioAdmissionState.Admitted, "the third admission");
        Assert.Equal(BaseB, transfer.AdmitCalls.Last().Id);
    }

    [Fact]
    public async Task ALateAdmissionNeverOverwritesTheAdmissionOfANewerChoice()
    {
        var (vm, _, transfer, store) = CreateTransfer();
        var slow = new TaskCompletionSource();
        transfer.AdmitFunc = (id, _) =>
        {
            if (id == BaseA) { slow.Task.Wait(); throw new InvalidDataException("PortfolioTransfer: stale"); }
            return BaseRunOf(id, new DateOnly(2002, 1, 1));
        };
        await Loaded(vm, store);
        vm.RunKind = PortfolioRunKind.FineTune;
        await Until(() => vm.BaseRuns.Count == 2, "the base runs");
        vm.SelectedBaseRun = Row(vm, BaseA);
        await Until(() => !transfer.AdmitCalls.IsEmpty, "the first call");
        vm.SelectedBaseRun = Row(vm, BaseB);
        await Until(() => vm.AdmissionState == PortfolioAdmissionState.Admitted, "the newer admission");
        slow.SetResult();
        await Task.Delay(120);
        Assert.Equal((PortfolioAdmissionState.Admitted, true), (vm.AdmissionState, vm.CanStart));
        Assert.DoesNotContain("stale", vm.AdmissionMessage);
    }

    [Fact]
    public async Task APreflightWithoutAValidationDateLeavesTheAdmissionWaitingAndNeverCallsTheStore()
    {
        var (vm, service, transfer, store) = CreateTransfer();
        service.Preflight = (_, s, e, _, _, _) => Task.FromResult(Pre(true, s, e, Ready(4)));
        await Loaded(vm, store);
        vm.RunKind = PortfolioRunKind.FineTune;
        await Until(() => vm.BaseRuns.Count == 2, "the base runs");
        vm.SelectedBaseRun = Row(vm, BaseA);
        await Task.Delay(120);
        Assert.Equal((PortfolioAdmissionState.Pending, false, true), (vm.AdmissionState, vm.CanStart, transfer.AdmitCalls.IsEmpty));
    }

    private const string TrainCellLimit = "The training cell of a transfer lies in the period the base model has seen.";

    [Fact]
    public async Task AScratchRunNeverTouchesTheStoreOrTheAdmission()
    {
        var (vm, service, transfer, store) = CreateTransfer();
        service.Run = (_, _) => Task.FromResult(Result());
        await Loaded(vm, store);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(service.Runs).Transfer);
        Assert.Equal((0, true), (transfer.ListCalls, transfer.AdmitCalls.IsEmpty));
        Assert.DoesNotContain(vm.ResultLines, l => l.Contains("base", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(TrainCellLimit, vm.ResultLines);                    // a scratch run has no base model: no transfer limit line
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFineTuneStartsFromTheAdmittedBaseWithTheChosenFreezeAndShowsItsLineage(bool freeze)
    {
        var (vm, service, transfer, store) = CreateTransfer();
        var gate = new TaskCompletionSource<RlPortfolioTrainingResult>();
        service.Run = (_, _) => gate.Task;
        transfer.AdmitFunc = (id, _) => BaseRunOf(id, new DateOnly(2003, 1, 1));
        await Loaded(vm, store);
        vm.RunKind = PortfolioRunKind.FineTune; vm.FreezeEncoder = freeze;
        await Until(() => vm.BaseRuns.Count == 2, "the base runs");
        vm.SelectedBaseRun = Row(vm, BaseA);
        await Until(() => vm.CanStart, "the admission");
        int listed = transfer.ListCalls;
        var run = vm.StartCommand.ExecuteAsync(null);
        var request = Assert.Single(service.Runs);
        vm.FreezeEncoder = !freeze; vm.SelectedBaseRun = Row(vm, BaseB);       // late edits cannot change the frozen run
        Assert.Equal(new RlPortfolioTransferInit(BaseA, freeze ? RlPortfolioFreeze.EncoderFrozen : RlPortfolioFreeze.None), request.Transfer);
        gate.SetResult(Result());
        await run;
        Assert.Equal("Succeeded", vm.Status);
        var lines = vm.ResultLines.ToArray();
        int at = Array.IndexOf(lines, "Fine-tuned from a stored base model.");
        Assert.True(at > 0, "the lineage follows the result lines");
        Assert.Equal("Base run " + BaseA.ToString("N") + ", model dddddddddddd", lines[at + 1]);
        Assert.Equal(freeze ? "The shared encoder stayed frozen at the base weights." : "Nothing was frozen.", lines[at + 2]);
        Assert.Equal("Base data ends 2003-01-01 (exclusive); the validation of this run starts 2004-06-01.", lines[at + 3]);
        Assert.Equal(TrainCellLimit, lines[at + 4]);                              // before the contract statement, for a fine-tune
        Assert.Equal(RlPortfolioTransferContract.Statement, lines[^1]);
        await Until(() => transfer.ListCalls > listed, "the base list reload after the new run");       // the fine-tuned run is a base candidate now
    }

    [Fact]
    public async Task EvaluatingABaseTrainsNothingAndShowsTheLineageWithoutAFreeze()
    {
        var (vm, service, transfer, store) = CreateTransfer();
        transfer.AdmitFunc = (id, _) => BaseRunOf(id, new DateOnly(2003, 1, 1));
        transfer.EvaluateFunc = (id, _, _) => Task.FromResult(new RlPortfolioZeroShotResult(Guid.NewGuid(), "zero-dir", "report", Result().Evaluation, "panel-dir", BaseRunOf(id, new DateOnly(2003, 1, 1))));
        await Loaded(vm, store);
        vm.RunKind = PortfolioRunKind.EvaluateBase; vm.SmokeOnly = true;
        await Until(() => vm.BaseRuns.Count == 2, "the base runs");
        vm.SelectedBaseRun = Row(vm, BaseA);
        await Until(() => vm.CanStart, "the admission");
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Empty(service.Runs);
        var (id, request) = Assert.Single(transfer.Evaluations);
        Assert.Equal((BaseA, true, null), (id, request.SmokeOnly, request.Transfer));
        Assert.Equal(("Succeeded", "Evidence saved in zero-dir"), (vm.Status, vm.Message));
        var lines = vm.ResultLines.ToArray();
        Assert.Contains("A stored base model evaluated on this panel without any training.", lines);
        Assert.DoesNotContain(lines, l => l.Contains("frozen", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(TrainCellLimit, lines[^2]);                                  // and for the evaluation of a stored model
        Assert.Equal(RlPortfolioTransferContract.Statement, lines[^1]);
    }

    // ----- the panel choice (reuse a stored panel) -----

    private static readonly Guid PanelOne = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000001"), PanelTwo = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000002");

    private static RlPortfolioStoredPanelInfo PanelInfoOf(Guid id, int holdout = 1)
        => new(id, new DateOnly(2026, 9, 1), new DateOnly(2000, 4, 6), new DateOnly(2006, 1, 3), 4, holdout, new string('1', 64), new string('2', 64));

    private (PortfolioRlDialogViewModel Vm, FakeService Service, FakeTransfer Transfer) CreateWithPanels(string language = "en")
    {
        var (vm, service, transfer, store) = CreateTransfer(language);
        transfer.Panels = ImmutableArray.Create(PanelInfoOf(PanelOne), PanelInfoOf(PanelTwo, holdout: 0));
        service.Run = (_, _) => Task.FromResult(Result());
        store.Groups.Add(new AllTickersNode("All"));
        return (vm, service, transfer);
    }

    private static PortfolioStoredPanelRow PanelRow(PortfolioRlDialogViewModel vm, Guid id) => vm.StoredPanels.Single(r => r.PanelId == id);

    [Fact]
    public async Task ThePanelChoiceStartsAtANewCaptureAndListsTheStoredPanelsAfterIt()
    {
        var (vm, service, transfer) = CreateWithPanels();
        await vm.InitializeAsync();
        await Until(() => vm.StoredPanels.Count == 3, "the stored panels");
        Assert.Equal(new Guid?[] { null, PanelOne, PanelTwo }, vm.StoredPanels.Select(r => r.PanelId).ToArray());
        Assert.Equal("Capture a new panel (default)", vm.StoredPanels[0].Text);
        Assert.Equal("11111111 | 4 symbols, 1 held out | data 2000-04-06 to 2006-01-03 (end exclusive)", PanelRow(vm, PanelOne).Text);
        Assert.Same(vm.StoredPanels[0], vm.SelectedStoredPanel);                                       // the default is the chosen row
        Assert.False(vm.UseStoredPanel);
        Assert.Equal(("", PortfolioStoredPanelState.None), (vm.StoredPanelMessage, vm.StoredPanelState));
        Assert.True(transfer.PanelChecks.IsEmpty);                                                       // nothing is checked for the default
        Assert.False(vm.CanStart);                                                                       // the data check of sources and period still gates a new capture
        Assert.True(service.Runs.Count == 0);
    }

    [Fact]
    public async Task AStoredPanelReplacesTheDataCheckAndTheRunReusesItWithoutTheSymbolsOfTheForm()
    {
        var (vm, service, transfer) = CreateWithPanels();
        await vm.InitializeAsync();
        await Until(() => vm.StoredPanels.Count == 3, "the stored panels");
        var gate = new TaskCompletionSource<DateOnly>();
        transfer.PanelDateFunc = (_, _, _) => gate.Task.GetAwaiter().GetResult();
        vm.SelectedStoredPanel = PanelRow(vm, PanelOne);
        Assert.Equal((true, PortfolioStoredPanelState.Checking, false), (vm.UseStoredPanel, vm.StoredPanelState, vm.CanStart));
        Assert.Equal("Checking the stored panel ...", vm.StoredPanelMessage);
        gate.SetResult(new DateOnly(2005, 3, 1));
        await Until(() => vm.StoredPanelState == PortfolioStoredPanelState.Ready, "the stored panel check");
        Assert.Equal("The stored panel decides the symbols, the period and the holdout; the data check above is not used. The validation of this run starts 2005-03-01.", vm.StoredPanelMessage);
        Assert.True(vm.CanStart);                                                                        // no source was ticked and no data check ran

        await vm.StartCommand.ExecuteAsync(null);
        var request = Assert.Single(service.Runs);
        Assert.Equal((PanelOne, true, new DateOnly(2000, 4, 6), new DateOnly(2006, 1, 3), 0), (request.PanelId, request.SymbolHoldout, request.Start, request.EndExclusive, request.Symbols.Length));
        Assert.Null(request.Transfer);
        var (id, mode, plan) = Assert.Single(transfer.PanelChecks);
        Assert.Equal((PanelOne, RlPanelEvaluationMode.Standard, null), (id, mode, plan));
        Assert.Equal("Succeeded", vm.Status);
        await Until(() => transfer.PanelListCalls >= 2 && transfer.RunListCalls >= 2, "the lists reloaded after the run");    // a new capture is a stored panel, the run has a report

        vm.SelectedStoredPanel = vm.StoredPanels[0];                                                   // back to a new capture: the data check gates again
        Assert.Equal((false, false, ""), (vm.UseStoredPanel, vm.CanStart, vm.StoredPanelMessage));
    }

    [Fact]
    public async Task AFineTuneOnAStoredPanelIsAdmittedAgainstThePanelsOwnFirstValidationDate()
    {
        var (vm, service, transfer) = CreateWithPanels();
        transfer.AdmitFunc = (id, _) => BaseRunOf(id, new DateOnly(2003, 1, 1));
        transfer.PanelDateFunc = (_, _, _) => new DateOnly(2005, 3, 1);
        await vm.InitializeAsync();
        await Until(() => vm.StoredPanels.Count == 3, "the stored panels");
        vm.UseShortHistory = true;
        vm.SelectedStoredPanel = PanelRow(vm, PanelTwo);
        vm.RunKind = PortfolioRunKind.FineTune;
        await Until(() => vm.BaseRuns.Count == 2, "the base runs");
        vm.SelectedBaseRun = Row(vm, BaseA);
        await Until(() => vm.AdmissionState == PortfolioAdmissionState.Admitted, "the admission");
        Assert.Equal(new DateOnly(2005, 3, 1), transfer.AdmitCalls.Last().Date);                       // the date of the stored panel, not of a data check
        Assert.True(vm.CanStart);
        Assert.Equal(RlPanelEvaluationMode.ShortHistory, transfer.PanelChecks.Last().Mode);
        await vm.StartCommand.ExecuteAsync(null);
        var request = Assert.Single(service.Runs);
        Assert.Equal((PanelTwo, new RlPortfolioTransferInit(BaseA, RlPortfolioFreeze.EncoderFrozen), false), (request.PanelId, request.Transfer, request.SymbolHoldout));
    }

    [Fact]
    public async Task AStoredPanelThatCannotBeUsedShowsTheExactReasonAndBlocksStartAndAChangedPlanChecksItAgain()
    {
        var (vm, service, transfer) = CreateWithPanels();
        const string reason = "PortfolioUniverse: the panel is too short for this plan.";
        transfer.PanelDateFunc = (_, _, _) => throw new InvalidDataException(reason);
        await vm.InitializeAsync();
        await Until(() => vm.StoredPanels.Count == 3, "the stored panels");
        vm.SelectedStoredPanel = PanelRow(vm, PanelOne);
        await Until(() => vm.StoredPanelState == PortfolioStoredPanelState.Failed, "the failed check");
        Assert.Equal((reason, true, false), (vm.StoredPanelMessage, vm.StoredPanelFailed, vm.CanStart));
        await vm.StartCommand.ExecuteAsync(null);                                                       // a closed gate starts nothing
        Assert.Empty(service.Runs);

        transfer.PanelDateFunc = (_, _, _) => new DateOnly(2005, 3, 1);
        vm.UseShortHistory = true;                                                                      // the plan changed: the panel is loaded again under it
        await Until(() => vm.StoredPanelState == PortfolioStoredPanelState.Ready, "the new check");
        Assert.True(vm.CanStart);
        Assert.Equal(RlPanelEvaluationMode.ShortHistory, transfer.PanelChecks.Last().Mode);
        vm.MinimumTrainBarsText = "abc";                                                                // an invalid minimum has its own message and ends the check
        Assert.Equal((PortfolioStoredPanelState.None, false), (vm.StoredPanelState, vm.CanStart));
    }

    [Fact]
    public async Task ALateAnswerOfAnOlderPanelCheckIsDropped()
    {
        var (vm, _, transfer) = CreateWithPanels();
        await vm.InitializeAsync();
        await Until(() => vm.StoredPanels.Count == 3, "the stored panels");
        var slow = new TaskCompletionSource<DateOnly>();
        transfer.PanelDateFunc = (id, _, _) => id == PanelOne ? slow.Task.GetAwaiter().GetResult() : new DateOnly(2007, 7, 7);
        vm.SelectedStoredPanel = PanelRow(vm, PanelOne);                                               // slow
        vm.SelectedStoredPanel = PanelRow(vm, PanelTwo);                                               // newer, immediate
        await Until(() => vm.StoredPanelState == PortfolioStoredPanelState.Ready, "the newer check");
        slow.SetResult(new DateOnly(2001, 1, 1));
        await Task.Delay(150);
        Assert.Contains("2007-07-07", vm.StoredPanelMessage);                                          // the older answer did not overwrite it
    }

    // ----- the comparison entry -----

    private static readonly Guid RunFine = Guid.Parse("aaaa0001-0000-0000-0000-000000000001"), RunScratch = Guid.Parse("aaaa0002-0000-0000-0000-000000000002"),
        RunOther = Guid.Parse("aaaa0003-0000-0000-0000-000000000003");

    private static RlPortfolioComparableRun ComparableOf(Guid id, string kind, char universe, char calendar)
        => new(id, new DateTimeOffset(2026, 9, 1, 10, 30, 0, TimeSpan.Zero), kind, new string(universe, 64), new string(calendar, 64));

    private static RlPortfolioTransferComparisonResult HandComparison()
    {
        var delta = new RlReturnDistribution(5, 0.011m, 0.01m, -0.005m, 0.03m, 0.6m);       // deltas 0.01, 0.02, -0.005, 0.03, 0.00
        return new RlPortfolioTransferComparisonResult(ImmutableArray.Create(
            new RlPortfolioCellComparison("validation", RlPanelSymbolGroup.Train, true, 5, delta, 3, 1, 1, 4, 0.3125, 0.125, RlPortfolioComparisonInference.Unavailable, true),
            new RlPortfolioCellComparison("oos", RlPanelSymbolGroup.Train, false, 0, null)), RlPortfolioTransferContract.Statement);
    }

    private (PortfolioRlDialogViewModel Vm, FakeTransfer Transfer) CreateWithRuns(string language = "en")
    {
        var (vm, _, transfer, _) = CreateTransfer(language);
        transfer.RunInfos = ImmutableArray.Create(ComparableOf(RunFine, RlPortfolioTransferContract.FineTuneKind, '1', '2'),
            ComparableOf(RunScratch, RlPortfolioComparableRun.ScratchKind, '1', '2'), ComparableOf(RunOther, RlPortfolioTransferContract.ZeroShotKind, '3', '4'));
        transfer.CompareFunc = (_, _) => HandComparison();
        return (vm, transfer);
    }

    [Fact]
    public async Task TheReferenceRunsAreTheOnesMadeOnTheSamePanelAsRunA()
    {
        var (vm, transfer) = CreateWithRuns();
        await vm.InitializeAsync();
        await Until(() => vm.ComparableRuns.Count == 3, "the runs with a report");
        Assert.False(vm.NoComparableRuns);
        Assert.Equal("2026-09-01 10:30 UTC | fine-tuned from a stored model | run aaaa0001 | panel 11111111", vm.ComparableRuns.Single(r => r.RunId == RunFine).Text);
        Assert.Contains(vm.ComparableRuns, r => r.RunId == RunScratch && r.Text.Contains("trained from scratch"));
        Assert.Contains(vm.ComparableRuns, r => r.RunId == RunOther && r.Text.Contains("stored model evaluated"));
        Assert.False(vm.CanCompare);

        vm.SelectedRunA = vm.ComparableRuns.Single(r => r.RunId == RunFine);
        Assert.Equal(new[] { RunScratch }, vm.ReferenceRuns.Select(r => r.RunId).ToArray());            // equal universe and calendar hashes, and not A itself
        Assert.False(vm.CanCompare);
        vm.SelectedRunB = vm.ReferenceRuns[0];
        Assert.True(vm.CanCompare);
        await vm.CompareCommand.ExecuteAsync(null);
        Assert.Equal((RunFine, RunScratch), Assert.Single(transfer.Compares));
        Assert.True(vm.HasComparison);

        vm.SelectedRunA = vm.ComparableRuns.Single(r => r.RunId == RunOther);                          // no other run on that panel
        Assert.True(vm.NoReferenceRuns);
        Assert.Equal((null, false, false), (vm.SelectedRunB, vm.CanCompare, vm.HasComparison));          // the old comparison is cleared with the choice
    }

    [Fact]
    public async Task TheComparisonShowsTheCountsTheSignTestAsAReferenceAndTheStatementAndAMismatchShowsItsReason()
    {
        var (vm, transfer) = CreateWithRuns();
        await vm.InitializeAsync();
        await Until(() => vm.ComparableRuns.Count == 3, "the runs with a report");
        vm.SelectedRunA = vm.ComparableRuns.Single(r => r.RunId == RunFine);
        vm.SelectedRunB = vm.ReferenceRuns[0];
        await vm.CompareCommand.ExecuteAsync(null);
        Assert.Equal(new[]
        {
            "Validation / Training symbols: 5 paired segments, mean difference A minus B 1.10 %; A ahead in 3, tied in 1, behind in 1 (this cell chose the checkpoint of a trained run, so a difference here is optimistic)",
            "  Sign test, a reference value only (the segments are not independent): one-sided p = 0.3125 over 4 effective pairs; the smallest possible two-sided p is 0.125",
            "  Fewer than 6 effective pairs: no inference is possible, whatever the data are.",
            "Final evaluation / Training symbols: not evaluated in both runs, so not compared",
            RlPortfolioTransferContract.Statement,
        }, vm.ComparisonLines.ToArray());
        Assert.Equal("", vm.ComparisonMessage);

        transfer.CompareFunc = (_, _) => throw new InvalidDataException("PortfolioTransfer: the reports differ in the calendar hash.");
        await vm.CompareCommand.ExecuteAsync(null);
        Assert.Equal(("PortfolioTransfer: the reports differ in the calendar hash.", true, false), (vm.ComparisonMessage, vm.HasComparisonMessage, vm.HasComparison));
    }

    [Fact]
    public void TheComparisonWordingHasTheNoPairsAndThePlentyOfPairsCasesAndComesFromTheSameCountsInJapanese()
    {
        var plenty = new RlPortfolioTransferComparisonResult(ImmutableArray.Create(
            new RlPortfolioCellComparison("oos", RlPanelSymbolGroup.Holdout, true, 8, new RlReturnDistribution(8, -0.02m, -0.02m, -0.04m, 0m, 0m), 0, 0, 8, 8, 1d, 0.0078125, RlPortfolioComparisonInference.ReferenceOnly),
            new RlPortfolioCellComparison("validation", RlPanelSymbolGroup.Holdout, true, 2, new RlReturnDistribution(2, 0m, 0m, 0m, 0m, 0m), 0, 2, 0, 0)), "statement");
        var en = PortfolioRlPresentation.Comparison(plenty, new JsonLocalization("en"));
        Assert.Equal("Final evaluation / Held-out symbols: 8 paired segments, mean difference A minus B -2.00 %; A ahead in 0, tied in 0, behind in 8", en[0]);
        Assert.Equal("  Sign test, a reference value only (the segments are not independent): one-sided p = 1 over 8 effective pairs; the smallest possible two-sided p is 0.007813", en[1]);
        Assert.DoesNotContain(en, l => l.Contains("no inference is possible", StringComparison.Ordinal));      // eight effective pairs can reach the level
        Assert.Equal("  No effective pair: every difference is a tie.", en[3]);
        Assert.Equal("statement", en[^1]);
        var ja = PortfolioRlPresentation.Comparison(HandComparison(), new JsonLocalization("ja"));
        Assert.All(ja, line => Assert.DoesNotContain("[Prl.", line));
        Assert.Contains("符号検定", ja[1]);
        Assert.Contains("0.3125", ja[1]);
    }

    private const string OverlapLine = "Held-out symbols the base model was trained on: 1 of 2. A held-out symbol the base has seen is not unseen for this run. "
        + "The count is an upper bound (symbols the base only evaluated are included).";

    private static RlPortfolioSymbolOverlap OneOfTwo()
        => new(ImmutableArray.Create("A", "B", "C"), ImmutableArray.Create("C", "D"), ImmutableArray.Create("C"), ImmutableArray.Create("A", "C"));

    [Fact]
    public async Task AFineTuneShowsHowManyHeldOutSymbolsTheBaseMayHaveSeenBeforeTheLimitsAndTheStatement()
    {
        var (vm, service, transfer, store) = CreateTransfer();
        service.Run = (_, _) => Task.FromResult(Result());
        transfer.AdmitFunc = (id, _) => BaseRunOf(id, new DateOnly(2003, 1, 1));
        transfer.OverlapFunc = (_, _) => OneOfTwo();
        await Loaded(vm, store);
        vm.RunKind = PortfolioRunKind.FineTune;
        await Until(() => vm.BaseRuns.Count == 2, "the base runs");
        vm.SelectedBaseRun = Row(vm, BaseA);
        await Until(() => vm.CanStart, "the admission");
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("Succeeded", vm.Status);
        Assert.Equal((BaseA, "panel-dir"), Assert.Single(transfer.OverlapCalls));                         // the base of the run and the panel the run captured
        var lines = vm.ResultLines.ToArray();
        Assert.Equal(OverlapLine, lines[^3]);                                                            // never a gate: it only adds a line
        Assert.Equal(TrainCellLimit, lines[^2]);
        Assert.Equal(RlPortfolioTransferContract.Statement, lines[^1]);
    }

    [Fact]
    public async Task EvaluatingAStoredModelShowsTheSameOverlapLineAndANullReportShowsNone()
    {
        foreach (bool reported in new[] { true, false })
        {
            var (vm, _, transfer, store) = CreateTransfer();
            transfer.AdmitFunc = (id, _) => BaseRunOf(id, new DateOnly(2003, 1, 1));
            transfer.EvaluateFunc = (id, _, _) => Task.FromResult(new RlPortfolioZeroShotResult(Guid.NewGuid(), "zero-dir", "report", Result().Evaluation, "zero-panel", BaseRunOf(id, new DateOnly(2003, 1, 1))));
            transfer.OverlapFunc = reported ? (_, _) => OneOfTwo() : null;                                // null report: the base panel is not stored
            await Loaded(vm, store);
            vm.RunKind = PortfolioRunKind.EvaluateBase;
            await Until(() => vm.BaseRuns.Count == 2, "the base runs");
            vm.SelectedBaseRun = Row(vm, BaseA);
            await Until(() => vm.CanStart, "the admission");
            await vm.StartCommand.ExecuteAsync(null);
            Assert.Equal("Succeeded", vm.Status);
            Assert.Equal((BaseA, "zero-panel"), Assert.Single(transfer.OverlapCalls));
            Assert.Equal(reported, vm.ResultLines.Contains(OverlapLine));
            Assert.Equal(RlPortfolioTransferContract.Statement, vm.ResultLines[^1]);
        }
    }

    [Fact]
    public async Task AScratchRunNeverAsksForAnOverlapAndTheJapaneseLineComesFromTheSameCounts()
    {
        var (vm, service, transfer, store) = CreateTransfer();
        service.Run = (_, _) => Task.FromResult(Result());
        transfer.OverlapFunc = (_, _) => OneOfTwo();
        await Loaded(vm, store);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(transfer.OverlapCalls.IsEmpty);
        Assert.DoesNotContain(vm.ResultLines, l => l.Contains("Held-out symbols the base", StringComparison.Ordinal));

        var lines = PortfolioRlPresentation.Lineage(RlPortfolioTransferContract.FineTuneKind, BaseRunOf(BaseA, new DateOnly(2003, 1, 1)), FirstValidation,
            RlPortfolioTransferContract.EncoderFrozen, new JsonLocalization("ja"), OneOfTwo());
        Assert.All(lines, line => Assert.DoesNotContain("[Prl.", line));
        Assert.Contains("基底モデルの学習に含まれていた除外銘柄: 2 件中 1 件。", lines[^3]);
    }

    [Fact]
    public async Task AFailedTransferRunShowsTheReasonAndLeavesTheDialogUsable()
    {
        var (vm, service, _, store) = CreateTransfer();
        service.Run = (_, _) => Task.FromException<RlPortfolioTrainingResult>(new InvalidDataException("PortfolioTransfer: the base data reaches 2005-01-01, the target validation starts 2004-05-30."));
        await Loaded(vm, store);
        vm.RunKind = PortfolioRunKind.FineTune;
        await Until(() => vm.BaseRuns.Count == 2, "the base runs");
        vm.SelectedBaseRun = Row(vm, BaseA);
        await Until(() => vm.CanStart, "the admission");
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(("Failed", false), (vm.Status, vm.IsBusy));
        Assert.Contains("PortfolioTransfer: the base data reaches", vm.TechnicalDetails);
        Assert.False(vm.HasResult);
    }

    [Fact]
    public async Task TheJapaneseTransferWordingComesFromTheSameState()
    {
        var (vm, _, transfer, store) = CreateTransfer("ja");
        transfer.AdmitFunc = (id, _) => BaseRunOf(id, new DateOnly(2003, 1, 1));
        await Loaded(vm, store);
        vm.RunKind = PortfolioRunKind.FineTune;
        await Until(() => vm.BaseRuns.Count == 2, "the base runs");
        Assert.Equal("基底の実行を選んでください。", vm.AdmissionMessage);
        vm.SelectedBaseRun = Row(vm, BaseA);
        await Until(() => vm.AdmissionState == PortfolioAdmissionState.Admitted, "the admission");
        Assert.Equal("基底の実行を受け入れました。そのデータは 2003-01-01（終端は含まない）までで、この実行の検証は 2004-06-01 から始まります。", vm.AdmissionMessage);
        Assert.Equal("2026-09-01 10:30 UTC | 実行 aaaaaaaa | データ 2000-04-06 ～ 2003-01-01（終端は含まない）", Row(vm, BaseA).Text);
        Assert.Equal("2026-09-01 10:30 UTC | 実行 bbbbbbbb | データ期間は不明（そのパネルが保存されていません）", Row(vm, BaseB).Text);
        var lines = PortfolioRlPresentation.Lineage(RlPortfolioTransferContract.FineTuneKind, BaseRunOf(BaseA, new DateOnly(2003, 1, 1)), FirstValidation,
            RlPortfolioTransferContract.EncoderFrozen, new JsonLocalization("ja"));
        Assert.All(lines, line => Assert.DoesNotContain("[Prl.", line));
        Assert.Equal("保存済みの基底モデルから微調整しました。", lines[0]);
        Assert.Equal("転移の学習セルは、基底モデルがすでに見た期間にあります。", lines[^2]);
    }

    // ----- the real window -----

    private static T Find<T>(Window window, string automationId) where T : Control
        => window.GetVisualDescendants().OfType<T>().First(c => global::Avalonia.Automation.AutomationProperties.GetAutomationId(c) == automationId);

    private static void Pump()
    {
        for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    [AvaloniaFact]
    public async Task TheWindowShowsTheStatusTableAndTheStartGateAndNoLimitationsBlock()
    {
        var statuses = new[] { RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.MissingFile };
        var (vm, _, _, store) = Create(preflight: (s, e) => Pre(true, s, e, statuses));
        var window = new PortfolioRlWindow { DataContext = vm };
        try
        {
            window.Show();
            Pump();
            Assert.DoesNotContain("Limitations", Texts(window));                                       // the limitations block is not part of the window
            Assert.False(Find<Button>(window, PrlUiConstants.Start).IsEffectivelyEnabled);
            await Loaded(vm, store);
            Pump();
            var rows = Find<ItemsControl>(window, PrlUiConstants.SymbolTable).GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("S2", rows);
            Assert.Contains("Local price or metadata file is missing", rows);
            Assert.Contains("Role", Texts(window));                                                      // the role column belongs to the table; its legend text was removed (S5)
            Assert.DoesNotContain(Texts(window), t => t.StartsWith("An evaluated-only symbol is traded by the policy"));
            var exclude = Find<CheckBox>(window, PrlUiConstants.Exclude);
            Assert.True(exclude.IsVisible);
            Assert.False(Find<Button>(window, PrlUiConstants.Start).IsEffectivelyEnabled);      // one symbol is not usable and the exclusion is not confirmed
            exclude.IsChecked = true;
            Pump();
            Assert.True(Find<Button>(window, PrlUiConstants.Start).IsEffectivelyEnabled);
            Assert.Contains("usable symbols: 2 of 3", Find<TextBlock>(window, PrlUiConstants.PreflightSummary).Text);
        }
        finally { window.Close(); }
    }

    // ----- the redesigned window: two columns, the list tree, the standard, the icon -----

    private static async Task<(PortfolioRlWindow Window, PortfolioRlDialogViewModel Vm, FakeService Service, FakeTickerStateStore Store)> Shown(Func<FakeTickerStateStore, Task>? fill = null, string language = "en",
        FakeTransfer? transfer = null)
    {
        var service = new FakeService();
        service.Preflight = (_, start, end, _, _, _) => Task.FromResult(Pre(true, start, end, Ready(4)) with { FirstValidationDate = FirstValidation });
        service.Run = (_, _) => Task.FromResult(Result());
        var store = new FakeTickerStateStore();
        var vm = new PortfolioRlDialogViewModel(service, new FakeResolver(), store, new FakeBacktestDefaultSettingsManager(), new JsonLocalization(language), NoonUtc(Today), transfer);
        var window = new PortfolioRlWindow { DataContext = vm };
        window.Show();
        if (fill is not null) await fill(store);
        await vm.InitializeAsync();
        Pump(); Pump();
        return (window, vm, service, store);
    }

    private static int ColumnOf(Grid grid, Control control)
        => Grid.GetColumn(control.GetVisualAncestors().OfType<Control>().First(a => ReferenceEquals(a.GetVisualParent(), grid)));

    /// <summary>The pixel tolerance of the layout assertions (rounding of the arranged bounds).</summary>
    private const double LayoutTolerance = 0.5;

    private static Point Origin(Visual visual, Visual relativeTo) => visual.TranslatePoint(new Point(0, 0), relativeTo)!.Value;

    private static double Top(Visual visual, Visual relativeTo) => Origin(visual, relativeTo).Y;

    [AvaloniaFact]
    public async Task TheRunStatusStripIsAboveTheFooterOutsideTheScrollAndCarriesTheMessageTheDetailsAndTheBusyBar()
    {
        var (window, vm, service, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; });
        var gate = new TaskCompletionSource<RlPortfolioTrainingResult>();
        service.Run = (_, _) => gate.Task;
        try
        {
            var strip = window.GetVisualDescendants().OfType<StockAnalyzer.Avalonia.Views.Controls.RunStatusStrip>().Single();
            var start = Find<Button>(window, PrlUiConstants.Start);
            StockAnalyzer.Avalonia.Tests.Views.RunStatusStripTests.AssertPlacedBetweenBodyAndFooter(window, strip, start);   // also while collapsed
            Assert.Equal(0d, strip.Bounds.Height);
            Assert.DoesNotContain("Progress", Texts(window));                                       // the former "Run" section heading is gone

            vm.TechnicalDetails = "the stored list is unavailable";                                // a failure detail with no run: not lost
            Pump();
            Assert.Contains("the stored list is unavailable", Texts(window));
            vm.TechnicalDetails = "";

            vm.StartDate = "2000-01-03"; vm.EndDateExclusive = "2006-01-03";
            All(vm).IsSelected = true;
            await Until(() => vm.PreflightState == RlDialogLoadState.Loaded && vm.CanStart, "the gate");
            var run = vm.StartCommand.ExecuteAsync(null);
            Pump();
            var bar = strip.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert.True(bar.IsEffectivelyVisible);
            Assert.True(bar.IsIndeterminate, "no percentage is reported");
            vm.Message = "Preparing the panel";
            Pump();
            var message = Find<TextBlock>(window, PrlUiConstants.Message);
            Assert.Equal("Preparing the panel", message.Text);
            Assert.Same(strip, message.GetVisualAncestors().OfType<StockAnalyzer.Avalonia.Views.Controls.RunStatusStrip>().Single());
            StockAnalyzer.Avalonia.Tests.Views.RunStatusStripTests.AssertPlacedBetweenBodyAndFooter(window, strip, start);
            gate.SetResult(Result());
            await run;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheFooterKeepsTheFourStateLabelsInsideTheWindowLeftOfTheButtons_AndTheBlockingReasonStaysVisible_AtEveryWidth()
    {
        foreach (string language in new[] { "en", "ja" })
        foreach (double width in new[] { StockAnalyzer.Avalonia.Common.RlUiLayout.WindowWidth, StockAnalyzer.Avalonia.Common.RlUiLayout.MinimumWidth })
        {
            var (window, vm, _, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; }, language: language, transfer: new FakeTransfer());
            try
            {
                window.Width = width;
                vm.RunKind = PortfolioRunKind.FineTune; vm.SmokeOnly = true; vm.UseShortHistory = true; vm.SymbolHoldout = false;      // the most labels at once
                Pump(); Pump();
                string where = $"{language}, width {width}";
                Assert.Equal(4, vm.ModeBadges.Count);
                Assert.True(vm.HasStartBlocker, where + ": a blocking reason is shown together with the labels");
                var badges = Find<ItemsControl>(window, PrlUiConstants.ModeBadges);
                var reason = Find<TextBlock>(window, PrlUiConstants.StartBlocker);
                var start = Find<Button>(window, PrlUiConstants.Start);
                Assert.True(badges.IsEffectivelyVisible && reason.IsEffectivelyVisible);
                double right = Origin(badges, window).X + badges.Bounds.Width;
                Assert.True(right <= window.Bounds.Width + LayoutTolerance, $"{where}: the labels end at {right}, inside the window");
                Assert.True(right <= Origin(start, window).X + LayoutTolerance, $"{where}: the labels end at {right}, left of the Start button at {Origin(start, window).X}");
                Assert.True(Origin(start, window).X + start.Bounds.Width <= window.Bounds.Width + LayoutTolerance, $"{where}: the buttons stay inside the window");
                FooterLayoutAssert.ReasonIsShownInFull(reason, where);
                var footer = Find<Control>(window, PrlUiConstants.Start).GetVisualAncestors().OfType<Border>().First(x => Grid.GetRow(x) == 3);
                foreach (var label in badges.GetVisualDescendants().OfType<Border>().Where(x => x.Child is TextBlock))
                {
                    var origin = Origin(label, window);
                    Assert.True(origin.X + label.Bounds.Width <= right + LayoutTolerance, $"{where}: every label is fully inside the label area");
                    Assert.True(Origin(label, footer).Y + label.Bounds.Height <= footer.Bounds.Height + LayoutTolerance, $"{where}: every label is inside the footer height");
                }
                Assert.True(Origin(reason, footer).Y + reason.Bounds.Height <= footer.Bounds.Height + LayoutTolerance, $"{where}: the reason is inside the footer height");
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task TheSettingsAreInBasicAdvancedAndResultsGroups_DocumentOrderEqualsVisualOrder()
    {
        // with a stored-model service, so the comparison card (the last item of the Results group) is on screen
        var (window, vm, _, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; }, transfer: new FakeTransfer());
        try
        {
            Pump();
            StockAnalyzer.Avalonia.Tests.Views.TrainingGroupHeaderTests.AssertSharedHeaderValues(window, "Basic Settings", "Advanced Settings", "Results and management");
            string[] ids =
            {
                PrlUiConstants.Sources, PrlUiConstants.StartDate, PrlUiConstants.EndDate,
                PrlUiConstants.Capital, PrlUiConstants.FlatCommission, PrlUiConstants.UnitCommission, PrlUiConstants.Slippage, PrlUiConstants.Liquidation,
                PrlUiConstants.PreflightSummary,
                PrlUiConstants.Smoke, PrlUiConstants.ShortHistory, PrlUiConstants.SymbolHoldout, PrlUiConstants.HoldoutPercent, PrlUiConstants.MaxWeight,
                PrlUiConstants.MinimumTrain, PrlUiConstants.Start,
            };
            var all = window.GetVisualDescendants().OfType<Control>().ToList();
            var controls = ids.Select(id => Find<Control>(window, id)).ToArray();
            Assert.Equal(controls.Select(c => all.IndexOf(c)).OrderBy(i => i), controls.Select(c => all.IndexOf(c)));                 // document order
            double Bottom(Control c) => c.TranslatePoint(new Point(0, c.Bounds.Height), window)!.Value.Y;
            double Left(Control c) => c.TranslatePoint(new Point(0, 0), window)!.Value.X;
            for (int i = 2; i < controls.Length - 1; i++)          // from the end date to the partition (the list and the period are two columns; the footer is outside the body)
            {
                bool nextRow = Bottom(controls[i]) > Bottom(controls[i - 1]) + LayoutTolerance;
                bool sameRowToTheRight = Math.Abs(Bottom(controls[i]) - Bottom(controls[i - 1])) <= LayoutTolerance && Left(controls[i]) > Left(controls[i - 1]);
                Assert.True(nextRow || sameRowToTheRight, $"{ids[i]} must follow {ids[i - 1]} visually");
            }
            // the group headers sit where the group changes
            var headers = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("groupHeader")).ToArray();
            Assert.True(Top(headers[0], window) < Top(controls[0], window));
            Assert.True(Bottom(controls[8]) <= Top(headers[1], window) + LayoutTolerance && Top(headers[1], window) < Top(controls[9], window));   // the data check closes Basic
            Assert.True(Top(controls[14], window) < Top(headers[2], window));                                                                     // the partition is above the Results group
            Assert.True(Top(headers[2], window) < Top(Find<Button>(window, PrlUiConstants.Compare), window));                                    // the comparison is a Results item
            Assert.Equal(vm.LiquidationPenaltyText, Find<TextBox>(window, PrlUiConstants.Liquidation).Text);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task LiquidationPenaltyIsLoadedFromTheSavedDefaultShownAndCarriedByTheRunRequest()
    {
        var defaults = new FakeBacktestDefaultSettingsManager();
        var builtIn = BacktestDefaultSettings.BuiltIn;
        await defaults.SaveAsync(new BacktestDefaultSettings
        {
            SchemaVersion = builtIn.SchemaVersion, DefaultTimeFrame = builtIn.DefaultTimeFrame, EndDateMode = builtIn.EndDateMode, FixedEndDateUtc = builtIn.FixedEndDateUtc,
            StartDateMode = builtIn.StartDateMode, StartDateRelativeYears = builtIn.StartDateRelativeYears, FixedStartDateUtc = builtIn.FixedStartDateUtc,
            InitialCapital = builtIn.InitialCapital, CommissionFlat = builtIn.CommissionFlat, CommissionPerUnit = builtIn.CommissionPerUnit, SlippageRatio = builtIn.SlippageRatio,
            SizingModel = builtIn.SizingModel, SizingParameter = builtIn.SizingParameter, InitialMarginRatio = builtIn.InitialMarginRatio,
            MaintenanceMarginRatio = builtIn.MaintenanceMarginRatio, LiquidationPenaltyRatio = 0.02m, AnnualRiskFreeRate = builtIn.AnnualRiskFreeRate,
            AnnualMAR = builtIn.AnnualMAR, AnnualPeriodsByFrame = builtIn.AnnualPeriodsByFrame, BootstrapSeed = builtIn.BootstrapSeed, BootstrapIterations = builtIn.BootstrapIterations,
        });
        var service = new FakeService { Run = (_, _) => Task.FromResult(Result()) };
        service.Preflight = (_, start, end, _, _, _) => Task.FromResult(Pre(true, start, end, Ready(4)));
        var store = new FakeTickerStateStore();
        var vm = new PortfolioRlDialogViewModel(service, new FakeResolver(), store, defaults, new JsonLocalization("en"), null, null);
        _viewModels.Add(vm);
        await Loaded(vm, store);
        Assert.Equal(0.02m.ToString(CultureInfo.CurrentCulture), vm.LiquidationPenaltyText);        // the saved default fills the field
        await Until(() => vm.CanStart, "the gate");
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(0.02m, Assert.Single(service.Runs).Costs.LiquidationPenaltyRatio);              // unedited: the saved default, as before
        vm.LiquidationPenaltyText = 0.01m.ToString(CultureInfo.CurrentCulture);
        await Until(() => vm.CanStart, "the gate again");
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(0.01m, service.Runs[1].Costs.LiquidationPenaltyRatio);                          // the edited value is what the run uses
        Assert.Equal(0.02m, defaults.Settings.LiquidationPenaltyRatio);                              // and nothing is written back to the global defaults
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("0.005", true)]
    [InlineData("0.999", true)]
    [InlineData("1", false)]
    [InlineData("-0.1", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public async Task LiquidationPenaltyFollowsTheRlBounds_ZeroInclusiveToOneExclusive_AndTheCostsMessageNamesAnInvalidValue(string text, bool valid)
    {
        var (vm, _, _, store) = Create();
        await Loaded(vm, store);
        await Until(() => vm.CanStart, "the gate");
        Assert.Equal(BacktestDefaults.LiquidationPenaltyRatio.ToString(CultureInfo.CurrentCulture), vm.LiquidationPenaltyText);     // the built-in default is valid and loaded
        vm.LiquidationPenaltyText = text;
        Assert.Equal(valid, vm.CostsValid);
        Assert.Equal(valid, vm.CanStart);
        Assert.Equal(valid ? "" : "Enter valid costs: initial equity above zero, the other values zero or more; slippage and liquidation penalty below 1.", vm.CostsMessage);
    }

    [AvaloniaFact]
    public async Task TheTabOrderAndTheStartGateIncludeTheLiquidationField()
    {
        var (window, vm, _, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; });
        try
        {
            vm.StartDate = "2000-01-03"; vm.EndDateExclusive = "2006-01-03";
            All(vm).IsSelected = true;
            await Until(() => vm.PreflightState == RlDialogLoadState.Loaded && vm.CanStart, "the gate");
            var liquidation = Find<TextBox>(window, PrlUiConstants.Liquidation);
            Assert.Equal(vm.LiquidationPenaltyText, liquidation.Text);
            liquidation.Text = "2";
            Pump();
            Assert.False(Find<Button>(window, PrlUiConstants.Start).IsEffectivelyEnabled);
            Assert.Contains(Texts(window), t => t.StartsWith("Enter valid costs"));
            liquidation.Text = "0.01";
            Pump();
            Assert.True(Find<Button>(window, PrlUiConstants.Start).IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task StartBlockerDisplay_NamesOneExistingReasonAtATime_AndIsEmptyWhenAStartIsPossibleOrARunIsActive()
    {
        var (vm, service, _, store) = Create();
        await Loaded(vm, store);
        await Until(() => vm.CanStart, "the gate");
        Assert.Equal("", vm.StartBlockerDisplay); Assert.False(vm.HasStartBlocker);

        All(vm).IsSelected = false;                                                               // nothing ticked: the existing check text
        await Until(() => !vm.CanStart, "the cleared check");
        Assert.Equal("Select a source and enter a period.", vm.StartBlockerDisplay);
        Assert.True(vm.HasStartBlocker);
        All(vm).IsSelected = true;
        await Until(() => vm.CanStart, "the gate again, ticked");

        vm.CommissionFlatText = "x";                                                              // costs: the existing message
        Assert.Equal(vm.CostsMessage, vm.StartBlockerDisplay); Assert.NotEqual("", vm.StartBlockerDisplay);
        vm.CommissionFlatText = "0";
        Assert.Equal("", vm.StartBlockerDisplay);

        vm.LiquidationPenaltyText = "1";                                                          // the new field is a costs reason too
        Assert.Equal(vm.CostsMessage, vm.StartBlockerDisplay);
        vm.LiquidationPenaltyText = "0.005";

        vm.HoldoutPercentText = "99";                                                             // percent: the existing message
        Assert.Equal(vm.PercentMessage, vm.StartBlockerDisplay); Assert.NotEqual("", vm.StartBlockerDisplay);
        vm.HoldoutPercentText = "25";
        await Until(() => vm.CanStart, "the gate again");

        vm.EndDateExclusive = "2999-01-01";                                                       // the end date lies after today: the period message
        Assert.Equal(vm.PeriodMessage, vm.StartBlockerDisplay); Assert.NotEqual("", vm.StartBlockerDisplay);
        vm.EndDateExclusive = "2006-01-03";
        await Until(() => vm.CanStart, "the gate once more");
        Assert.Equal("", vm.StartBlockerDisplay);

        var gate = new TaskCompletionSource<RlPortfolioTrainingResult>();
        service.Run = (_, _) => gate.Task;
        var run = vm.StartCommand.ExecuteAsync(null);
        vm.CommissionFlatText = "x";
        Assert.True(vm.IsBusy);
        Assert.Equal("", vm.StartBlockerDisplay);                                                 // the run-status strip speaks while a run is active
        gate.SetResult(Result());
        await run;
    }

    [Fact]
    public async Task StartBlockerDisplay_ShowsTheDataCheckReasonOfTheStartGate()
    {
        var statuses = new[] { RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.MissingFile };
        var (vm, _, _, store) = Create(preflight: (s, e) => Pre(true, s, e, statuses));
        await Loaded(vm, store);
        await Until(() => vm.BlockerText.Length > 0, "the gate decision");
        Assert.False(vm.CanStart);
        Assert.Equal(vm.BlockerText, vm.StartBlockerDisplay);                                     // the data-check card keeps its text; the footer repeats it
        Assert.Equal("Some symbols cannot be used. Tick the box to continue without them.", vm.StartBlockerDisplay);
        vm.ExcludeUnavailable = true;
        await Until(() => vm.CanStart, "the exclusion");
        Assert.Equal("", vm.StartBlockerDisplay);
    }

    [Fact]
    public async Task StartBlockerDisplay_NamesTheMissingBaseRun_AndTheModeBadgesFollowTheOptions()
    {
        var (vm, _, _, store) = Create(transfer: new FakeTransfer());
        await Loaded(vm, store);
        await Until(() => vm.CanStart, "the gate");
        Assert.Empty(vm.ModeBadges); Assert.False(vm.HasModeBadges);                              // default state: nothing
        vm.RunKind = PortfolioRunKind.FineTune;
        Assert.Equal(new[] { "Fine-tune" }, vm.ModeBadges);
        Assert.Equal("Choose a base run.", vm.StartBlockerDisplay);                               // the existing admission wording
        vm.RunKind = PortfolioRunKind.EvaluateBase;
        Assert.Equal(new[] { "Base evaluation" }, vm.ModeBadges);
        vm.RunKind = PortfolioRunKind.Scratch;
        vm.SmokeOnly = true; vm.UseShortHistory = true;
        Assert.Equal(new[] { "Trial run", "Short history" }, vm.ModeBadges);
        vm.SmokeOnly = false; vm.UseShortHistory = false;
        Assert.Empty(vm.ModeBadges);
        Assert.True(vm.SymbolHoldout);                                                            // an option that is ON by default gets no label
    }

    [Fact]
    public async Task DefaultValueInventory_EveryOptionStartsAtItsInventoryDefault()
    {
        var (vm, service, _, store) = Create(transfer: new FakeTransfer());
        store.Groups.Add(new AllTickersNode("All"));
        await vm.InitializeAsync();
        Assert.True(vm.SymbolHoldout);                                                            // ON by default, so it is never labelled
        Assert.Equal(("25", "100"), (vm.HoldoutPercentText, vm.MaxWeightPercentText));            // 25 % of the symbols held out, 100 % = no weight cap
        Assert.Equal((25, 100), (service.Settings.HoldoutPercent, service.Settings.MaxWeightPercent));
        Assert.False(vm.UseShortHistory); Assert.False(vm.SmokeOnly);
        Assert.Equal(PortfolioRunKind.Scratch, vm.RunKind);
        Assert.True(vm.FreezeEncoder);
        Assert.False(vm.UseStoredPanel);                                                          // capture a new panel
        Assert.Equal((1_000_000m.ToString(CultureInfo.CurrentCulture), "0", "0", "0", 0.005m.ToString(CultureInfo.CurrentCulture)),
            (vm.InitialCapitalText, vm.CommissionFlatText, vm.CommissionPerUnitText, vm.SlippageRatioText, vm.LiquidationPenaltyText));
        Assert.Empty(vm.ModeBadges);
    }

    [Fact]
    public async Task ModeBadges_ShowTheSymbolHoldoutOnlyWhenItIsSwitchedOff()
    {
        var (vm, _, _, store) = Create();
        await Loaded(vm, store);
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        Assert.Empty(vm.ModeBadges);                                                              // ON is the default: no label
        vm.SymbolHoldout = false;
        Assert.Equal(new[] { "No symbol holdout" }, vm.ModeBadges);
        Assert.True(vm.HasModeBadges);
        Assert.Contains(nameof(vm.ModeBadges), seen);
        vm.SmokeOnly = true; vm.UseShortHistory = true;
        Assert.Equal(new[] { "Trial run", "Short history", "No symbol holdout" }, vm.ModeBadges);
        vm.SmokeOnly = false; vm.UseShortHistory = false; vm.SymbolHoldout = true;
        Assert.Empty(vm.ModeBadges); Assert.False(vm.HasModeBadges);
    }

    [AvaloniaFact]
    public async Task TheFooterHoldsTheReasonAndTheModeLabelsNextToTheStatus()
    {
        var (window, vm, _, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; });
        try
        {
            Pump();
            var reason = Find<TextBlock>(window, PrlUiConstants.StartBlocker);
            var status = Find<TextBlock>(window, PrlUiConstants.Status);
            var start = Find<Button>(window, PrlUiConstants.Start);
            var badges = Find<ItemsControl>(window, PrlUiConstants.ModeBadges);
            Assert.True(reason.IsEffectivelyVisible);
            Assert.Equal("Select a source and enter a period.", reason.Text);
            var footer = reason.GetVisualAncestors().OfType<Border>().First(b => Grid.GetRow(b) == 3);
            Assert.Contains(start, footer.GetVisualDescendants());
            Assert.True(Origin(status, window).X < Origin(reason, window).X && Origin(reason, window).X < Origin(start, window).X);
            Assert.False(badges.IsEffectivelyVisible);
            Find<CheckBox>(window, PrlUiConstants.Smoke).IsChecked = true;
            Pump();
            Assert.True(badges.IsEffectivelyVisible);
            Assert.Equal(new[] { "Trial run" }, badges.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            vm.StartDate = "2000-01-03"; vm.EndDateExclusive = "2006-01-03";
            All(vm).IsSelected = true;
            await Until(() => vm.CanStart, "the gate");
            Pump();
            Assert.False(reason.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheFooterFollowsTheSubWindowStandard_StartIsPrimaryAndLeftOfCancelWhileBusy()
    {
        var (window, vm, service, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; });
        var gate = new TaskCompletionSource<RlPortfolioTrainingResult>();
        service.Run = (_, _) => gate.Task;
        try
        {
            vm.StartDate = "2000-01-03"; vm.EndDateExclusive = "2006-01-03";
            All(vm).IsSelected = true;
            await Until(() => vm.PreflightState == RlDialogLoadState.Loaded && vm.CanStart, "the gate");
            var run = vm.StartCommand.ExecuteAsync(null);
            Pump();
            Assert.True(vm.IsBusy);
            StockAnalyzer.Avalonia.Tests.Views.FooterOrderStandardTests.AssertPrimaryFirst(window, Find<Button>(window, PrlUiConstants.Start), Find<Button>(window, PrlUiConstants.Cancel));
            gate.SetResult(Result());
            await run;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheTransferCardIsThereOnlyWithAStoredModelServiceAndFollowsTheRunKindAndTheAdmission()
    {
        var (plain, _, _, _) = await Shown();
        try
        {
            Assert.DoesNotContain("Base model (transfer)", Texts(plain));
            Assert.False(Find<RadioButton>(plain, PrlUiConstants.RunKindFineTune).IsEffectivelyVisible);
        }
        finally { plain.Close(); }

        string reason = "PortfolioTransfer: the base data reaches 2005-01-01, the target validation starts 2004-06-01.";
        bool reject = false;
        var transfer = new FakeTransfer { Infos = ImmutableArray.Create(InfoOf(BaseA, new DateOnly(2003, 1, 1))) };
        transfer.AdmitFunc = (id, _) => reject ? throw new InvalidDataException(reason) : BaseRunOf(id, new DateOnly(2003, 1, 1));
        var (window, vm, _, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; }, transfer: transfer);
        try
        {
            vm.StartDate = "2000-01-03"; vm.EndDateExclusive = "2006-01-03";
            All(vm).IsSelected = true;
            await Until(() => vm.PreflightState == RlDialogLoadState.Loaded, "the preflight");
            Pump();
            Assert.Contains("Base model (transfer)", Texts(window));
            var fineTune = Find<RadioButton>(window, PrlUiConstants.RunKindFineTune);
            var combo = Find<ComboBox>(window, PrlUiConstants.BaseRun);
            var freeze = Find<CheckBox>(window, PrlUiConstants.FreezeEncoder);
            var admission = Find<TextBlock>(window, PrlUiConstants.Admission);
            var start = Find<Button>(window, PrlUiConstants.Start);
            Assert.True(Find<RadioButton>(window, PrlUiConstants.RunKindScratch).IsChecked);
            Assert.False(combo.IsEffectivelyVisible);
            Assert.True(start.IsEffectivelyEnabled);                                     // a scratch run is not gated

            fineTune.IsChecked = true;
            await Until(() => vm.BaseRuns.Count == 1, "the base runs");
            Pump();
            Assert.True(combo.IsEffectivelyVisible && freeze.IsEffectivelyVisible);
            Assert.True(freeze.IsChecked);                                               // the encoder is frozen by default
            Assert.Equal("Choose a base run.", admission.Text);
            Assert.False(start.IsEffectivelyEnabled);
            combo.SelectedIndex = 0;
            await Until(() => vm.AdmissionState == PortfolioAdmissionState.Admitted, "the admission");
            Pump();
            Assert.StartsWith("Base run accepted:", admission.Text);
            Assert.False(admission.Classes.Contains("warning"));
            Assert.True(start.IsEffectivelyEnabled);

            reject = true;
            Find<RadioButton>(window, PrlUiConstants.RunKindEvaluateBase).IsChecked = true;       // another kind admits again
            await Until(() => vm.AdmissionState == PortfolioAdmissionState.Rejected, "the rejection");
            Pump();
            Assert.False(freeze.IsEffectivelyVisible);                                   // an evaluation has no freeze
            Assert.Equal(reason, admission.Text);
            Assert.True(admission.Classes.Contains("warning"));
            Assert.False(start.IsEffectivelyEnabled);
            Find<RadioButton>(window, PrlUiConstants.RunKindScratch).IsChecked = true;
            Pump();
            Assert.True(start.IsEffectivelyEnabled && !combo.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheWindowOffersTheStoredPanelsAndTheComparisonOnlyWithAStoredModelService()
    {
        var (plain, _, _, _) = await Shown();
        try
        {
            Assert.DoesNotContain("Compare two runs", Texts(plain));
            Assert.False(Find<ComboBox>(plain, PrlUiConstants.StoredPanel).IsEffectivelyVisible);
            Assert.False(Find<Button>(plain, PrlUiConstants.Compare).IsEffectivelyVisible);
        }
        finally { plain.Close(); }

        var transfer = new FakeTransfer { Panels = ImmutableArray.Create(PanelInfoOf(PanelOne), PanelInfoOf(PanelTwo, holdout: 0)) };
        transfer.RunInfos = ImmutableArray.Create(ComparableOf(RunFine, RlPortfolioTransferContract.FineTuneKind, '1', '2'),
            ComparableOf(RunScratch, RlPortfolioComparableRun.ScratchKind, '1', '2'), ComparableOf(RunOther, RlPortfolioTransferContract.ZeroShotKind, '3', '4'));
        transfer.CompareFunc = (_, _) => HandComparison();
        transfer.PanelDateFunc = (_, _, _) => new DateOnly(2005, 3, 1);
        var (window, vm, _, _) = await Shown(transfer: transfer);
        try
        {
            await Until(() => vm.StoredPanels.Count == 3 && vm.ComparableRuns.Count == 3, "the panels and the runs");
            Pump();
            Assert.Contains("Compare two runs", Texts(window));
            var panels = Find<ComboBox>(window, PrlUiConstants.StoredPanel);
            var message = Find<TextBlock>(window, PrlUiConstants.StoredPanelMessage);
            var start = Find<Button>(window, PrlUiConstants.Start);
            Assert.Equal((3, 0), (panels.ItemCount, panels.SelectedIndex));                    // the default (a new capture) is chosen
            Assert.False(message.IsEffectivelyVisible);
            panels.SelectedIndex = 1;
            await Until(() => vm.StoredPanelState == PortfolioStoredPanelState.Ready, "the stored panel check");
            Pump();
            Assert.True(message.IsEffectivelyVisible);
            Assert.StartsWith("The stored panel decides the symbols", message.Text);
            Assert.True(start.IsEffectivelyEnabled);                                           // no source ticked, but the stored panel replaces the data check

            var runA = Find<ComboBox>(window, PrlUiConstants.CompareRunA);
            var runB = Find<ComboBox>(window, PrlUiConstants.CompareRunB);
            var compare = Find<Button>(window, PrlUiConstants.Compare);
            Assert.False(compare.IsEffectivelyEnabled);
            runA.SelectedIndex = 0;                                                            // the fine-tune; the one other run on its panel becomes the reference list
            Pump();
            Assert.Equal(1, runB.ItemCount);
            runB.SelectedIndex = 0;
            Pump();
            Assert.True(compare.IsEffectivelyEnabled);
            compare.Command!.Execute(null);
            await Until(() => vm.HasComparison, "the comparison");
            Pump();
            var lines = Find<ItemsControl>(window, PrlUiConstants.Comparison);
            Assert.True(lines.IsEffectivelyVisible);
            Assert.Contains(Texts(window), t => t.StartsWith("Validation / Training symbols: 5 paired segments", StringComparison.Ordinal));
            Assert.Contains(Texts(window), t => t.Contains("no inference is possible", StringComparison.Ordinal));
            Assert.False(Find<TextBlock>(window, PrlUiConstants.ComparisonMessage).IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheListsAreOnTheLeftAndThePeriodOnTheRight_TheEvaluationIsAnAdvancedCardBelow()
    {
        var (window, _, _, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; });
        try
        {
            var tree = Find<TreeView>(window, PrlUiConstants.Sources);
            var grid = tree.GetVisualAncestors().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 2);
            Assert.All(grid.ColumnDefinitions, c => Assert.True(c.Width.IsStar && c.Width.Value == 1));
            var start = Find<CalendarDatePicker>(window, PrlUiConstants.StartDate); var end = Find<CalendarDatePicker>(window, PrlUiConstants.EndDate);
            var shortHistory = Find<CheckBox>(window, PrlUiConstants.ShortHistory); var smoke = Find<CheckBox>(window, PrlUiConstants.Smoke);
            Assert.Equal(0, ColumnOf(grid, tree));
            Assert.All(new Control[] { start, end }, c => Assert.Equal(1, ColumnOf(grid, c)));
            Assert.DoesNotContain(shortHistory, grid.GetVisualDescendants());                          // the evaluation options left the period column (S3b)
            Assert.DoesNotContain(smoke, grid.GetVisualDescendants());
            Assert.True(Top(start, window) < Top(end, window));
            double gridBottom = grid.TranslatePoint(new Point(0, grid.Bounds.Height), window)!.Value.Y;
            Assert.True(Top(smoke, window) >= gridBottom - LayoutTolerance && Top(shortHistory, window) >= gridBottom - LayoutTolerance);
            var right = grid.Children.Single(c => Grid.GetColumn(c) == 1);
            var headings = right.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("heading")).Select(t => t.Text).ToArray();
            Assert.Equal(new[] { "Period" }, headings);
            // The partition card is outside the two columns: below the grid and below the evaluation card, above the stored-base-model card.
            var partitionHeading = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("heading") && t.Text == "Partition of the short-history evaluation");
            Assert.DoesNotContain(partitionHeading, grid.GetVisualDescendants());
            Assert.True(Top(partitionHeading, window) >= gridBottom - LayoutTolerance);
            Assert.True(Top(partitionHeading, window) > Top(Find<TextBox>(window, PrlUiConstants.MaxWeight), window));
            Assert.Equal("Ticker lists", grid.Children.Single(c => Grid.GetColumn(c) == 0).GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("heading")).Text);
            Assert.Equal("2026-10-07", end.SelectedDate!.Value.ToString("yyyy-MM-dd"));      // the last confirmed day
            Assert.Equal(new DateTime(2026, 10, 8), end.DisplayDateEnd);
        }
        finally { window.Close(); }
    }

    private static string[] Texts(Window window) => window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToArray();

    [AvaloniaFact]
    public async Task TheHoldoutShareTheWeightCapAndTheBlockInputsAreOnScreen()
    {
        var (window, vm, _, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; });
        try
        {
            var holdout = Find<TextBox>(window, PrlUiConstants.HoldoutPercent); var weight = Find<TextBox>(window, PrlUiConstants.MaxWeight);
            var train = Find<TextBox>(window, PrlUiConstants.TrainBlocks); var validation = Find<TextBox>(window, PrlUiConstants.ValidationBlocks);
            Pump();
            Assert.Equal(("25", "100"), (holdout.Text, weight.Text));
            Assert.True(holdout.IsEffectivelyEnabled);
            Assert.True(train.IsEffectivelyVisible && validation.IsEffectivelyVisible);   // always on screen, so they can be found
            Assert.False(train.IsEffectivelyEnabled);                                       // but, like the whole partition card, off until the short-history evaluation is chosen
            Assert.Contains(Texts(window), t => t.StartsWith("Symbol holdout: hold out some symbols"));
            Find<CheckBox>(window, PrlUiConstants.ShortHistory).IsChecked = true;
            Pump();
            Assert.True(train.IsEffectivelyEnabled);                                        // usable in the ratio method too: they are the same minimums in whole blocks
            Find<RadioButton>(window, PrlUiConstants.PartitionBlockCount).IsChecked = true;
            Pump();
            train.Text = "3"; validation.Text = "10";
            Pump();
            Assert.Equal(("576", "1920"), (vm.MinimumTrainBarsText, vm.MinimumValidationBarsText));
            holdout.Text = "40"; weight.Text = "abc";
            Pump();
            Assert.Equal("40", vm.HoldoutPercentText);
            Assert.False(Find<Button>(window, PrlUiConstants.Start).IsEffectivelyEnabled);
            Assert.Contains(Texts(window), t => t.StartsWith("Enter whole numbers from 1 to 50 for the holdout share"));
            Find<CheckBox>(window, PrlUiConstants.SymbolHoldout).IsChecked = false;
            Pump();
            Assert.False(holdout.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheEvaluationCardIsTrialRunAndShortHistoryThenHoldoutAndShareThenMaxWeight_AndTheBarsAndBlocksOfOneItemShareARow()
    {
        var (window, _, _, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; });
        try
        {
            Pump();
            var holdout = Find<CheckBox>(window, PrlUiConstants.SymbolHoldout); var trial = Find<CheckBox>(window, PrlUiConstants.Smoke);
            var shortHistory = Find<CheckBox>(window, PrlUiConstants.ShortHistory);
            var share = Find<TextBox>(window, PrlUiConstants.HoldoutPercent); var weight = Find<TextBox>(window, PrlUiConstants.MaxWeight);
            // row 0: Trial run | Short history (the order of the RL window)
            Assert.Equal(Origin(trial, window).Y, Origin(shortHistory, window).Y, LayoutTolerance);
            Assert.True(Origin(trial, window).X < Origin(shortHistory, window).X);
            // row 1: Symbol holdout | Holdout share, bottom aligned; row 2: Max weight under the left column
            Assert.True(Origin(holdout, window).X < Origin(share, window).X);
            Assert.Equal(holdout.TranslatePoint(new Point(0, holdout.Bounds.Height), window)!.Value.Y, share.TranslatePoint(new Point(0, share.Bounds.Height), window)!.Value.Y, LayoutTolerance);
            Assert.True(Origin(share, window).Y > Origin(trial, window).Y);
            Assert.True(Origin(weight, window).Y > Origin(share, window).Y + share.Bounds.Height - LayoutTolerance);
            Assert.True(Origin(weight, window).X < Origin(share, window).X);                                            // under the left column
            // The partition card lies outside the two columns: left of the period column, below the whole evaluation card.
            var partition = Origin(Find<TextBox>(window, PrlUiConstants.MinimumTrain), window);
            Assert.True(partition.X < Origin(Find<CalendarDatePicker>(window, PrlUiConstants.StartDate), window).X);
            Assert.True(partition.Y > Origin(weight, window).Y);
            foreach (var (bars, blocks) in new[] { (PrlUiConstants.MinimumTrain, PrlUiConstants.TrainBlocks), (PrlUiConstants.MinimumValidation, PrlUiConstants.ValidationBlocks),
                                                   (PrlUiConstants.MinimumTest, PrlUiConstants.TestBlocks) })
            {
                var barsBox = Origin(Find<TextBox>(window, bars), window); var blocksBox = Origin(Find<TextBox>(window, blocks), window);
                Assert.Equal(barsBox.Y, blocksBox.Y, LayoutTolerance);                                                  // one row
                Assert.True(barsBox.X < blocksBox.X);                                                       // bars on the left, blocks on the right
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ThePartitionCardFollowsTheShortHistoryChoiceAndShowsTheRoundedMinimums()
    {
        var (window, vm, _, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; });
        try
        {
            var ratio = Find<RadioButton>(window, PrlUiConstants.PartitionRatio); var block = Find<RadioButton>(window, PrlUiConstants.PartitionBlockCount);
            var train = Find<TextBox>(window, PrlUiConstants.MinimumTrain);
            Pump();
            // The standard evaluation is the initial one: the controls are disabled and the fixed minimum is explained.
            Assert.All(new Control[] { ratio, block, train, Find<TextBox>(window, PrlUiConstants.MinimumValidation), Find<TextBox>(window, PrlUiConstants.MinimumTest) },
                c => Assert.False(c.IsEffectivelyEnabled));
            Assert.Contains(Texts(window), t => t.StartsWith("The standard evaluation uses the fixed minimum of 1 / 5 / 5 blocks"));
            Find<CheckBox>(window, PrlUiConstants.ShortHistory).IsChecked = true;
            Pump();
            Assert.All(new Control[] { ratio, block, train }, c => Assert.True(c.IsEffectivelyEnabled));
            Assert.DoesNotContain(Texts(window), t => t.StartsWith("The standard evaluation uses the fixed minimum"));
            Assert.Equal((true, false, "192"), (ratio.IsChecked, block.IsChecked, train.Text));
            block.IsChecked = true;
            Pump();
            Assert.Equal((RlPortfolioPartitionMode.BlockCount, false), (vm.PartitionMode, ratio.IsChecked));
            train.Text = "250";
            Pump();
            Assert.Equal("250", vm.MinimumTrainBarsText);
            Assert.Contains("250 bars = 2 block(s) = 384 bars", Texts(window));
            Assert.Contains("Each training block is replayed about 40 times per seed (the same market path again, not new data).", Find<ItemsControl>(window, PrlUiConstants.PartitionNotices)
                .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            train.Text = "abc";
            Pump();
            Assert.Contains("Enter whole numbers from 192 to 15360 bars for all three minimums.", Texts(window));
            Assert.False(Find<Button>(window, PrlUiConstants.Start).IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheTabOrderIsTheVisualOrderAndTheDatePickersShowIsoDates()
    {
        var (window, _, _, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; });
        try
        {
            // No explicit TabIndex: the focus order is the document order, which must read lists, period, evaluation options (Trial run, then Short history), then the footer actions.
            string[] ids = { PrlUiConstants.Sources, PrlUiConstants.StartDate, PrlUiConstants.EndDate, PrlUiConstants.Smoke, PrlUiConstants.ShortHistory, PrlUiConstants.Start };
            var order = window.GetVisualDescendants().OfType<Control>().ToList();
            var positions = ids.Select(id => order.IndexOf(Find<Control>(window, id))).ToArray();
            Assert.All(positions, position => Assert.True(position >= 0));
            Assert.Equal(positions.OrderBy(position => position), positions);
            Assert.All(ids.Select(id => Find<Control>(window, id)), c => Assert.Equal(int.MaxValue, c.GetValue(KeyboardNavigation.TabIndexProperty)));
            foreach (var picker in new[] { Find<CalendarDatePicker>(window, PrlUiConstants.StartDate), Find<CalendarDatePicker>(window, PrlUiConstants.EndDate) })
            {
                Assert.Equal(CalendarDatePickerFormat.Custom, picker.SelectedDateFormat);
                Assert.Equal(IsoDatePickerFormat.Format, picker.CustomDateFormatString);
                Assert.Equal(IsoDatePickerFormat.Hint, picker.Watermark);
            }
        }
        finally { window.Close(); }
    }

    private static async Task<(PortfolioRlWindow Window, TreeView Tree)> ShownTree(int watchlists)
    {
        var shown = await Shown(store =>
        {
            var category = new CategoryNode(TickerListViewModel.WatchlistsCategoryId, "Watchlists");
            for (int i = 0; i < watchlists; i++) category.Children!.Add(new WatchlistNode(new WatchlistProfile(Guid.NewGuid(), "W" + i, IndicatorColor.Gray, false)));
            store.Groups.Add(category);
            return Task.CompletedTask;
        });
        Pump();
        return (shown.Window, Find<TreeView>(shown.Window, PrlUiConstants.Sources));
    }

    [AvaloniaFact]
    public async Task TheTreeShowsACheckboxPerListAndAScrollbarOnlyBeyondTheVisibleRows()
    {
        var (many, manyTree) = await ShownTree(14);                    // 15 rows: more than the 10 visible ones
        try
        {
            var scroll = manyTree.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.False(scroll.AllowAutoHide);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.True(manyTree.Bounds.Height <= PrlUiLayout.SourceListMaxHeight + LayoutTolerance);
            Assert.Equal(PrlUiLayout.SourceListVisibleRows, (int)Math.Round(PrlUiLayout.SourceListMaxHeight / PrlUiLayout.SourceRowHeight));
            Assert.True(scroll.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Orientation.Vertical).IsVisible);
            var boxes = manyTree.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsVisible).ToArray();
            Assert.True(boxes.Length >= 10);
            var item = Assert.IsType<PortfolioSourceTreeItem>(boxes[1].DataContext);                // the first list below the "Watchlists" row
            boxes[1].IsChecked = true;
            Assert.True(item.Option!.IsSelected);
            var icons = manyTree.GetVisualDescendants().OfType<PathIcon>().Where(i => i.IsVisible).ToArray();
            Assert.NotEmpty(icons);
            Assert.All(icons, i => Assert.NotNull(i.Data));                                          // the icon path data from the ticker tab's SSoT is bound
        }
        finally { many.Close(); }
        var (few, fewTree) = await ShownTree(3);                       // 4 rows: no scrollbar needed
        try
        {
            var scroll = fewTree.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + LayoutTolerance);
            Assert.False(scroll.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Orientation.Vertical).IsVisible);
        }
        finally { few.Close(); }
    }

    [AvaloniaFact]
    public async Task TheCalendarPickersDrawAnIconInsteadOfADayNumber()
    {
        var (window, _, _, _) = await Shown();
        try
        {
            foreach (string id in new[] { PrlUiConstants.StartDate, PrlUiConstants.EndDate })
            {
                var picker = Find<CalendarDatePicker>(window, id);
                var button = picker.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Button");
                var icon = Assert.Single(button.GetVisualDescendants().OfType<PathIcon>());
                Assert.NotNull(icon.Data);
                Assert.Equal((CalendarDatePickerIconLayout.Size, CalendarDatePickerIconLayout.Size), (icon.Width, icon.Height));
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheWindowFollowsTheSubWindowStandardHeaderFooterAndButtonOrder()
    {
        var (window, vm, _, _) = await Shown();
        try
        {
            Assert.True(window.ExtendClientAreaToDecorationsHint);
            // header / body / run-status strip (S2, Auto: collapsed while idle) / footer
            var root = window.GetVisualDescendants().OfType<Grid>().First(g => g.RowDefinitions.Count == 4);
            Assert.Equal(PrlUiLayout.HeaderHeight, root.RowDefinitions[0].Height.Value);
            Assert.True(root.RowDefinitions[2].Height.IsAuto);
            Assert.Equal(PrlUiLayout.FooterHeight, root.RowDefinitions[3].Height.Value);
            var body = root.Children.OfType<ScrollViewer>().Single();
            Assert.False(body.AllowAutoHide);
            Assert.Equal(ScrollBarVisibility.Auto, body.VerticalScrollBarVisibility);
            Assert.Equal(PrlUiLayout.HeaderHeight, root.Children.OfType<Border>().First(b => Grid.GetRow(b) == 0).Bounds.Height);
            Assert.Equal(PrlUiLayout.FooterHeight, root.Children.OfType<Border>().First(b => Grid.GetRow(b) == 3).Bounds.Height);
            var start = Find<Button>(window, PrlUiConstants.Start); var cancel = Find<Button>(window, PrlUiConstants.Cancel);
            Assert.False(cancel.IsVisible);                                  // nothing runs
            vm.IsBusy = true; Pump();
            Assert.True(cancel.IsVisible);
            Assert.True(start.TranslatePoint(new Point(0, 0), window)!.Value.X < cancel.TranslatePoint(new Point(0, 0), window)!.Value.X);   // Start, then Cancel
            Assert.Equal(PrlUiLayout.ControlHeight, start.Bounds.Height);
            Assert.Equal(HorizontalAlignment.Center, start.HorizontalContentAlignment);
            Assert.Contains(window.KeyBindings, k => k.Gesture.Key == Key.Escape);
            Assert.Contains(window.KeyBindings, k => k.Gesture.Key == Key.Enter);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The key bindings are checked by what they run. A real key press cannot be used in the headless test: its render loop never ends while a ScrollViewer has
    /// AllowAutoHide=False (required by the sub-window standard, also in the single-symbol RL window), and a routed event does not reach key bindings.
    /// </summary>
    [AvaloniaFact]
    public async Task EscapeClosesAndEnterStarts()
    {
        var (window, vm, _, _) = await Shown();
        try
        {
            var escape = window.KeyBindings.Single(k => k.Gesture.Key == Key.Escape);
            var enter = window.KeyBindings.Single(k => k.Gesture.Key == Key.Enter);
            Assert.Same(vm.StartCommand, enter.Command);
            Assert.NotNull(escape.Command);
            escape.Command!.Execute(null);
            Pump();
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheListCardEndsWhereThePeriodCardEndsAndTheTagInputIsGone()
    {
        var (window, _, _, _) = await Shown(store => { store.Groups.Add(new AllTickersNode("All")); return Task.CompletedTask; });
        try
        {
            var tree = Find<TreeView>(window, PrlUiConstants.Sources);
            var grid = tree.GetVisualAncestors().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 2);
            var left = grid.Children.Single(c => Grid.GetColumn(c) == 0); var right = grid.Children.Single(c => Grid.GetColumn(c) == 1);
            // The period card is the only card of the right column (S3b): the list card ends where it ends, so both cards have the same height.
            Assert.Equal(right.TranslatePoint(new Point(0, right.Bounds.Height), window)!.Value.Y, left.TranslatePoint(new Point(0, left.Bounds.Height), window)!.Value.Y, LayoutTolerance);
            Assert.Equal(right.Bounds.Bottom, left.Bounds.Bottom, LayoutTolerance);
            Assert.Equal("Period", right.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("heading")).Text);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => global::Avalonia.Automation.AutomationProperties.GetAutomationId(t) == "Prl_TagInput");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Tick the lists to include. Symbols are combined; duplicates are removed.");
        }
        finally { window.Close(); }
    }

    [Fact]
    public void TheWindowSourceUsesOnlyThemeTokensDynamicFontsAndScopedButtonStyles()
    {
        string xaml = File.ReadAllText(Path.Combine(JsonLocalization.Root(), "Views", "PortfolioRlWindow.axaml"));
        Assert.DoesNotContain("Selector=\"Button\"", xaml);                       // a global Button selector would corrupt composite controls
        Assert.DoesNotMatch(@"FontSize=""\d", xaml);                               // font sizes come from the Settings -> Fonts resources
        Assert.DoesNotMatch(@"(Background|Foreground|BorderBrush|Fill)=""(#|[A-Za-z]+"")", xaml.Replace("=\"Transparent\"", "=\"\""));       // brushes come from theme tokens
        Assert.DoesNotContain("Topmost", xaml);
        Assert.True(Regex.Matches(xaml, "AllowAutoHide=\"False\"").Count >= 2);     // the body and the list
        Assert.Contains("PrimaryButtonStyle", xaml); Assert.Contains("SecondaryButtonStyle", xaml);
    }

    [Fact]
    public void TheMenuNamesUseTitleCaseInEnglishAndTheJapaneseNamesAreUnchanged()
    {
        var en = Locale("en"); var ja = Locale("ja");
        Assert.Equal(("Reinforcement Learning", "Portfolio Reinforcement Learning"), (en["Rl.Title"], en["Prl.Title"]));
        // Deliberate Japanese literals: the menu names must not change.
                Assert.Equal(("強化学習", "ポートフォリオ強化学習"), (ja["Rl.Title"], ja["Prl.Title"]));
    }

    [Fact]
    public async Task PortfolioWeightsVisualizer_PopulatesAndAdaptsToMaxWeightChange()
    {
        var (vm, _, _, store) = Create();
        await Loaded(vm, store);

        Assert.True(vm.HasPortfolioWeights);
        Assert.NotEmpty(vm.PortfolioWeights);
        Assert.NotEmpty(vm.PortfolioAllocationSummary);

        var first = vm.PortfolioWeights[0];
        Assert.True(first.TargetWeightPercent > 0);

        // When changing MaxWeightPercentText
        vm.MaxWeightPercentText = "10";
        Assert.All(vm.PortfolioWeights, w => Assert.True(w.TargetWeightPercent <= 10.0));
        Assert.Contains("10%", vm.PortfolioAllocationSummary);

        var capped = RlPortfolioWeights.EqualWeights(Enumerable.Repeat(true, vm.PortfolioWeights.Count).ToArray(), 10);
        Assert.Equal(capped.Assets.Select(a => (double)(a * 100m)), vm.PortfolioWeights.Select(w => w.TargetWeightPercent));
    }

    [Fact]
    public async Task PortfolioWeightsTable_EqualsTheCoreEqualWeightBaselineWithoutInventedColumns()
    {
        const int readyCount = 3;
        const int uncappedPercent = 100;
        var (vm, _, _, store) = Create(preflight: (s, e) => Pre(true, s, e, Ready(readyCount)));
        await Loaded(vm, store);
        vm.MaxWeightPercentText = uncappedPercent.ToString(CultureInfo.InvariantCulture);

        var expected = RlPortfolioWeights.EqualWeights(Enumerable.Repeat(true, readyCount).ToArray(), uncappedPercent);
        Assert.Equal(readyCount, vm.PortfolioWeights.Count);
        for (int i = 0; i < readyCount; i++)
        {
            Assert.Equal((double)(expected.Assets[i] * 100m), vm.PortfolioWeights[i].TargetWeightPercent);
            Assert.Equal("33.3%", vm.PortfolioWeights[i].WeightDisplay);
        }
        decimal totalPercent = expected.Assets.Sum() * 100m;      // the quantization residual stays in cash
        Assert.Contains(totalPercent.ToString("0.0", CultureInfo.CurrentCulture) + "%", vm.PortfolioAllocationSummary);

        Assert.DoesNotContain(typeof(PortfolioWeightVisualRow).GetProperties(), p => p.Name.Contains("Correlation"));
        string xaml = File.ReadAllText(Path.Combine(JsonLocalization.Root(), "Views", "PortfolioRlWindow.axaml"));
        Assert.DoesNotContain("CorrelationLabel", xaml);
        Assert.Contains("Prl.Allocation.BaselineNote", xaml);
    }

    // ----- locale parity -----

    private static Dictionary<string, string> Locale(string language) => JsonLocalization.ReadFlat(language);

    [Fact]
    public void EveryPortfolioKeyExistsInBothLanguagesWithTheSamePlaceholders()
    {
        var en = Locale("en"); var ja = Locale("ja");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string repo = JsonLocalization.Root();
        foreach (string file in new[] { "Views/PortfolioRlWindow.axaml", "ViewModels/PortfolioRlDialogViewModel.cs", "ViewModels/PortfolioRlPresentation.cs" })
            foreach (Match match in Regex.Matches(File.ReadAllText(Path.Combine(repo, file)), @"""?((?:Prl|Rl)\.[A-Za-z0-9_.]*[A-Za-z0-9_])""?"))
                keys.Add(match.Groups[1].Value);
        foreach (var status in Enum.GetValues<RlPanelSymbolStatus>()) keys.Add("Prl.SymbolStatus." + status);
        foreach (var blocker in Enum.GetValues<RlPanelStartBlocker>().Where(b => b != RlPanelStartBlocker.None)) keys.Add("Prl.Blocker." + blocker);
        foreach (var kind in Enum.GetValues<RlSourceKind>()) keys.Add("Prl.Source.Kind." + kind);
        foreach (var group in Enum.GetValues<RlPanelSymbolGroup>()) keys.Add("Prl.Group." + group);
        foreach (var role in Enum.GetValues<RlPanelSymbolRole>()) keys.Add("Prl.Role." + role);
        foreach (string baseline in RlPortfolioEvaluationContract.Baselines) keys.Add("Prl.Baseline." + baseline);
        foreach (string kind in new[] { RlPortfolioTransferContract.FineTuneKind, RlPortfolioTransferContract.ZeroShotKind }) keys.Add("Prl.Transfer.Lineage.Kind." + kind);
        foreach (string freeze in new[] { RlPortfolioTransferContract.EncoderFrozen, RlPortfolioTransferContract.NoFreeze }) keys.Add("Prl.Transfer.Lineage.Freeze." + freeze);
        foreach (string partition in new[] { RlPortfolioContract.TrainPartition, RlPortfolioContract.ValidationPartition, RlPortfolioContract.OosPartition }) keys.Add("Rl.Ui.Partition." + partition);
        foreach (string state in new[] { "Ready", "Preparing", "Training", "Evaluating", "Succeeded", "Failed", "Rejected", "Unavailable", "InsufficientData", "Cancelling", "Cancelled" }) keys.Add("Rl.State." + state);
        keys.RemoveWhere(k => k.EndsWith(".", StringComparison.Ordinal) || k is "Rl.State" or "Prl.SymbolStatus" or "Prl.Blocker" or "Prl.Source.Kind" or "Prl.Group" or "Prl.Role" or "Prl.Baseline" or "Rl.Ui.Partition"
            or "Prl.Transfer.Lineage.Kind" or "Prl.Transfer.Lineage.Freeze");
        Assert.Contains("Prl.Title", keys);
        foreach (string key in keys)
        {
            Assert.True(en.ContainsKey(key), "en.json is missing " + key);
            Assert.True(ja.ContainsKey(key), "ja.json is missing " + key);
            Assert.Equal(Placeholders(en[key]), Placeholders(ja[key]));
        }
        var portfolioKeys = en.Keys.Where(k => k.StartsWith("Prl.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(portfolioKeys.OrderBy(k => k, StringComparer.Ordinal), ja.Keys.Where(k => k.StartsWith("Prl.", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(portfolioKeys, k => Assert.Contains(k, keys));      // no unused key
    }

    private static string Placeholders(string text) => string.Join(",", Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal));
}
