using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

[Collection("MessengerSharedState")]
public sealed class PortfolioRlTempModeTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly List<PortfolioRlDialogViewModel> _viewModels = new();

    public PortfolioRlTempModeTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    public void Dispose()
    {
        foreach (var vm in _viewModels) vm.Dispose();
        CultureInfo.CurrentCulture = _culture;
    }

    private sealed class JsonLocalization : ILocalizationService
    {
        private readonly Dictionary<string, string> _values;
        public JsonLocalization(string language) => _values = ReadFlat(language);
        public string GetString(string key) => _values.TryGetValue(key, out var value) ? value : "[" + key + "]";
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
    }

    private sealed class FakeResolver : ITickerSourceResolver
    {
        public ImmutableArray<string> Symbols { get; set; } = ImmutableArray.Create("AAA", "BBB", "CCC", "DDD");
        public Task<RlSourceResolution> ResolveAsync(IReadOnlyList<RlSourceDescriptor> sources, IReadOnlyCollection<string>? excluded = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new RlSourceResolution(Symbols, ImmutableArray<RlSourceDiagnostic>.Empty));
    }

    private sealed class FakeService : IRlPortfolioService
    {
        public RlPortfolioSettings Settings { get; set; } = new();
        public List<RlPortfolioRunRequest> Runs { get; } = new();
        public Func<RlPortfolioRunRequest, CancellationToken, Task<RlPortfolioTrainingResult>>? Run { get; set; }

        public Task<PanelPreflight> PreflightAsync(IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested,
            RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan, int? holdoutPercent, CancellationToken cancellationToken)
            => Task.FromResult(new PanelPreflight(start, endExclusive, mode,
                symbols.Select(s => new RlPanelSymbolPreflight(s, RlPanelSymbolStatus.Ready, 1400, RlContract.SupportLength, "")).ToImmutableArray(),
                1400, 1278, ImmutableArray.Create(new RlPanelCellCount("train", RlPanelSymbolGroup.Train, 4, 1)), true, false));

        public Task<RlStartSuggestion?> SuggestStartAsync(IReadOnlyList<string> symbols, DateOnly endExclusive, RlPanelEvaluationMode mode,
            RlPanelPartitionPlan? plan, CancellationToken cancellationToken)
            => Task.FromResult<RlStartSuggestion?>(null);

        public Task<PanelPreflight> PreflightAsync(IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested,
            RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan, CancellationToken cancellationToken)
            => PreflightAsync(symbols, start, endExclusive, holdoutRequested, mode, plan, null, cancellationToken);

        public Task<RlStartSuggestion?> SuggestStartAsync(IReadOnlyList<string> symbols, DateOnly endExclusive, RlPanelEvaluationMode mode, CancellationToken cancellationToken)
            => Task.FromResult<RlStartSuggestion?>(null);

        public Task<PanelPreflight> PreflightAsync(IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested,
            RlPanelEvaluationMode mode, CancellationToken cancellationToken)
            => PreflightAsync(symbols, start, endExclusive, holdoutRequested, mode, null, null, cancellationToken);

        public Task<RlPortfolioTrainingResult> RunAsync(RlPortfolioRunRequest request, IProgress<RlTrainingProgress>? progress, CancellationToken cancellationToken)
        {
            Runs.Add(request);
            return Run != null ? Run(request, cancellationToken) : Task.FromResult(CreateResult());
        }
    }

    private static RlPortfolioTrainingResult CreateResult()
    {
        var one = new RlReturnDistribution(2, 0.05m, 0.05m, 0.01m, 0.09m, 1m);
        var delta = new RlReturnDistribution(2, 0.02m, 0.02m, -0.01m, 0.05m, 0.5m);
        var cells = ImmutableArray.Create(new RlPortfolioCellReport("validation", RlPanelSymbolGroup.Train, ImmutableArray.Create("S0", "S1"), 2, 1, true,
            ImmutableArray<RlPortfolioBlockResult>.Empty, one, RlPortfolioEvaluationContract.Baselines.Select(id => new RlPortfolioBaselineSummary(id, one, delta)).ToImmutableArray()));
        var report = new RlPortfolioEvaluationReport(RlPortfolioContract.SpecificationId, 1, RlPortfolioEvaluationContract.ContractHash, RlPortfolioContract.ContractHash,
            RlShortHistoryEvaluationContract.SpecificationId, new string('a', 64), new string('b', 64), new string('c', 64), ImmutableArray.Create("S0", "S1"),
            ImmutableArray.Create("S0", "S1"), ImmutableArray<string>.Empty, false, new BacktestConfiguration { InitialCapital = 10_000m }, 100, false, cells,
            RlSourceContract.Unverified, RlConstraintContract.InvestmentEfficacy, RlPortfolioEvaluationContract.Statement, ImmutableArray<string>.Empty);
        return new RlPortfolioTrainingResult(Guid.NewGuid(), "run-dir", "model", "parity", 42, 512, new string('a', 64), new string('b', 64), "report", report, "panel-dir");
    }

    private sealed class FakeTransfer : IRlPortfolioTransferService
    {
        public int ListCalls;
        public int PanelListCalls;
        public int RunListCalls;

        public ImmutableArray<RlPortfolioBaseRunInfo> ListBaseRuns()
        {
            Interlocked.Increment(ref ListCalls);
            return ImmutableArray<RlPortfolioBaseRunInfo>.Empty;
        }

        public ImmutableArray<RlPortfolioStoredPanelInfo> ListStoredPanels()
        {
            Interlocked.Increment(ref PanelListCalls);
            return ImmutableArray<RlPortfolioStoredPanelInfo>.Empty;
        }

        public ImmutableArray<RlPortfolioComparableRun> ListComparableRuns()
        {
            Interlocked.Increment(ref RunListCalls);
            return ImmutableArray<RlPortfolioComparableRun>.Empty;
        }

        public RlPortfolioBaseRun Admit(Guid baseRunId, DateOnly targetFirstValidationDate) => throw new NotSupportedException();
        public Task<RlPortfolioZeroShotResult> EvaluateStoredModelAsync(Guid baseRunId, RlPortfolioRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public DateOnly StoredPanelFirstValidationDate(Guid panelId, RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan) => throw new NotSupportedException();
        public RlPortfolioTransferComparisonResult CompareRuns(Guid runA, Guid runB) => throw new NotSupportedException();
        public RlPortfolioSymbolOverlap? SymbolOverlap(Guid baseRunId, string panelDirectory) => null;
    }

    private (PortfolioRlDialogViewModel Vm, FakeService Service, FakeTransfer Transfer, FakeTickerStateStore Store) Create(FakeTransfer? transfer = null)
    {
        var service = new FakeService();
        var resolver = new FakeResolver();
        var store = new FakeTickerStateStore();
        transfer ??= new FakeTransfer();
        var vm = new PortfolioRlDialogViewModel(service, resolver, store, new FakeBacktestDefaultSettingsManager(), new JsonLocalization("en"), null, transfer);
        _viewModels.Add(vm);
        return (vm, service, transfer, store);
    }

    private static async Task<PortfolioRlDialogViewModel> ReadyVm(PortfolioRlDialogViewModel vm, FakeTickerStateStore store)
    {
        store.Groups.Add(new AllTickersNode("All"));
        await vm.InitializeAsync();
        vm.StartDate = "2000-01-03";
        vm.EndDateExclusive = "2006-01-03";
        vm.Sources.Single(s => s.Descriptor.Kind == RlSourceKind.AllTickers).IsSelected = true;
        for (int i = 0; i < 250 && vm.PreflightState != RlDialogLoadState.Loaded; i++)
            await Task.Delay(20);
        Assert.Equal(RlDialogLoadState.Loaded, vm.PreflightState);
        return vm;
    }

    [Fact]
    public void StorageMode_DefaultsToArtifacts()
    {
        var (vm, _, _, _) = Create();
        Assert.Equal(OnnxArtifactStorageMode.Artifacts, vm.SelectedStorageMode);
        Assert.True(vm.IsArtifactsStorageMode);
        Assert.False(vm.IsTempStorageMode);
        Assert.Contains(OnnxArtifactStorageMode.Artifacts, vm.AvailableStorageModes);
        Assert.Contains(OnnxArtifactStorageMode.Temp, vm.AvailableStorageModes);
    }

    [Fact]
    public void StorageMode_TwoWayBinding_TogglesModeCorrectly()
    {
        var (vm, _, _, _) = Create();

        vm.IsTempStorageMode = true;
        Assert.Equal(OnnxArtifactStorageMode.Temp, vm.SelectedStorageMode);
        Assert.False(vm.IsArtifactsStorageMode);
        Assert.True(vm.IsTempStorageMode);

        vm.IsArtifactsStorageMode = true;
        Assert.Equal(OnnxArtifactStorageMode.Artifacts, vm.SelectedStorageMode);
        Assert.True(vm.IsArtifactsStorageMode);
        Assert.False(vm.IsTempStorageMode);

        vm.SelectedStorageMode = OnnxArtifactStorageMode.Temp;
        Assert.False(vm.IsArtifactsStorageMode);
        Assert.True(vm.IsTempStorageMode);
    }

    [Fact]
    public async Task StartCommand_InArtifactsMode_PassesArtifactsStorageModeToService()
    {
        var (vm, service, _, store) = Create();
        await ReadyVm(vm, store);

        vm.SelectedStorageMode = OnnxArtifactStorageMode.Artifacts;
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Single(service.Runs);
        Assert.Equal(OnnxArtifactStorageMode.Artifacts, service.Runs[0].StorageMode);
    }

    [Fact]
    public async Task StartCommand_InTempMode_PassesTempStorageModeToService_AndBypassesBaseRunReload()
    {
        var transfer = new FakeTransfer();
        var (vm, service, _, store) = Create(transfer);
        await ReadyVm(vm, store);

        // Reset transfer call counts after initial load
        int initialListCalls = transfer.ListCalls;
        int initialPanelCalls = transfer.PanelListCalls;
        int initialRunCalls = transfer.RunListCalls;

        vm.SelectedStorageMode = OnnxArtifactStorageMode.Temp;
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Single(service.Runs);
        Assert.Equal(OnnxArtifactStorageMode.Temp, service.Runs[0].StorageMode);

        // In Temp mode, LoadBaseRunsAsync, LoadStoredPanelsAsync, LoadComparableRunsAsync must not be triggered after training
        Assert.Equal(initialListCalls, transfer.ListCalls);
        Assert.Equal(initialPanelCalls, transfer.PanelListCalls);
        Assert.Equal(initialRunCalls, transfer.RunListCalls);
    }

    [Fact]
    public void RlPortfolioRunRequest_StorageMode_DefaultsToArtifacts()
    {
        var request = new RlPortfolioRunRequest(
            ImmutableArray.Create("AAA"),
            new DateOnly(2020, 1, 1),
            new DateOnly(2021, 1, 1),
            true,
            RlPanelEvaluationMode.ShortHistory,
            new BacktestConfiguration { InitialCapital = 10_000m },
            true);

        Assert.Equal(OnnxArtifactStorageMode.Artifacts, request.StorageMode);
    }

    [Fact]
    public async Task RlPortfolioRunService_InvalidStorageMode_ThrowsArgumentOutOfRangeException()
    {
        var service = new RlPortfolioService(new RlPortfolioSettings());

        var request = new RlPortfolioRunRequest(
            ImmutableArray.Create("AAA"),
            new DateOnly(2020, 1, 1),
            new DateOnly(2021, 1, 1),
            true,
            RlPanelEvaluationMode.ShortHistory,
            new BacktestConfiguration { InitialCapital = 10_000m },
            true,
            StorageMode: (OnnxArtifactStorageMode)999);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RunAsync(request, null, CancellationToken.None));
    }

    [Fact]
    public async Task RlPortfolioTransferService_InvalidStorageMode_ThrowsArgumentOutOfRangeException()
    {
        var transfer = new RlPortfolioTransferService(new RlPortfolioSettings());

        var request = new RlPortfolioRunRequest(
            ImmutableArray.Create("AAA"),
            new DateOnly(2020, 1, 1),
            new DateOnly(2021, 1, 1),
            true,
            RlPanelEvaluationMode.ShortHistory,
            new BacktestConfiguration { InitialCapital = 10_000m },
            true,
            StorageMode: (OnnxArtifactStorageMode)999);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transfer.EvaluateStoredModelAsync(Guid.NewGuid(), request, CancellationToken.None));
    }
}
