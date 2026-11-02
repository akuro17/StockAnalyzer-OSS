using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.Tests.Backtest;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public sealed class ModelMetadataAnnotationUiIntegrationTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _annotationPath;

    public ModelMetadataAnnotationUiIntegrationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AnnotationUiTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _annotationPath = Path.Combine(_testDir, "model_annotations.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    public class InputDialogProxy : DispatchProxy
    {
        public string? NextInputResult { get; set; }
        public int InputCalls { get; private set; }
        public string? LastInputDefault { get; private set; }

        public static (IDialogService Service, InputDialogProxy Controller) Create(string? nextInputResult = null)
        {
            object proxy = Create<IDialogService, InputDialogProxy>();
            var p = (InputDialogProxy)proxy;
            p.NextInputResult = nextInputResult;
            return ((IDialogService)proxy, p);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowInputAsync))
            {
                InputCalls++;
                LastInputDefault = args?.Length > 2 ? (string?)args[2] : null;
                return Task.FromResult(NextInputResult);
            }

            if (targetMethod?.Name == nameof(IDialogService.ShowConfirmationAsync))
            {
                return Task.FromResult(true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class SimpleLocalization : ILocalizationService
    {
        public static readonly SimpleLocalization Instance = new();
        public string GetString(string key) => key switch
        {
            "Rl.Ui.Target.Direction" => "Direction",
            "Rl.Ui.Target.Continuous" => "Continuous",
            "Rl.Ui.Field.Identifier" => "ID",
            "Rl.Ui.Field.ValidationMeanReturn" => "Val",
            "Rl.Ui.Field.FinalMeanReturn" => "Final",
            "Rl.Ui.NoWorkFolder" => "NoWorkFolder",
            "Prl.Transfer.Period" => "{0} to {1}",
            "Prl.Transfer.PeriodUnknown" => "Unknown",
            "Prl.Transfer.PeriodAmbiguous" => "Ambiguous",
            "Prl.Transfer.BaseRow" => "{0} · {1} · {2}",
            "Prl.Compare.Row" => "{0} · {1} · {2} · {3}",
            "Prl.Compare.Kind.FineTune" => "FineTune",
            "Prl.Compare.Kind.ZeroShot" => "ZeroShot",
            _ => key
        };
    }

    [Fact]
    public void RlUiDisplay_ModelLabel_WithAndWithoutCustomDisplayName()
    {
        var gen = new RlGeneration("mod1", "SPY", "src", "hash", "eval", 0.1m, 0.2m, new(), DateTimeOffset.UtcNow)
        {
            Target = RlFiles.Target
        };

        // Without custom name
        string normal = RlUiDisplay.ModelLabel(gen, withoutWorkFolder: false, SimpleLocalization.Instance);
        Assert.Equal("Direction · SPY · mod1", normal);

        // With custom name
        string withCustom = RlUiDisplay.ModelLabel(gen, withoutWorkFolder: false, SimpleLocalization.Instance, "My Alpha Model");
        Assert.Equal("[My Alpha Model] Direction · SPY · mod1", withCustom);

        // With whitespace/empty custom name
        string withEmpty = RlUiDisplay.ModelLabel(gen, withoutWorkFolder: false, SimpleLocalization.Instance, "   ");
        Assert.Equal("Direction · SPY · mod1", withEmpty);
    }

    [Fact]
    public void RlUiDisplay_Generation_WithAndWithoutCustomDisplayName()
    {
        var gen = new RlGeneration("mod2", "AAPL", "src", "hash", "eval", 0.15m, 0.25m, new(), DateTimeOffset.UtcNow)
        {
            Target = RlFiles.Target
        };

        // Without custom name
        string normal = RlUiDisplay.Generation(gen, SimpleLocalization.Instance);
        Assert.StartsWith("Direction · AAPL", normal);

        // With custom name
        string withCustom = RlUiDisplay.Generation(gen, SimpleLocalization.Instance, "Custom Agent");
        Assert.StartsWith("[Custom Agent] Direction · AAPL", withCustom);
    }

    [Fact]
    public void PortfolioRlPresentation_BaseRun_WithAndWithoutCustomDisplayName()
    {
        var info = new RlPortfolioBaseRunInfo(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            "shaU", "shaC",
            new RlBasePeriod(RlBasePeriodStatus.Known, new DateOnly(2020, 1, 1), new DateOnly(2025, 1, 1)));

        string normal = PortfolioRlPresentation.BaseRun(info, SimpleLocalization.Instance);
        Assert.StartsWith("2026-01-01 12:00 · 11111111", normal);

        string withCustom = PortfolioRlPresentation.BaseRun(info, SimpleLocalization.Instance, "Portfolio Champion");
        Assert.StartsWith("[Portfolio Champion] 2026-01-01 12:00 · 11111111", withCustom);
    }

    [Fact]
    public void PortfolioRlPresentation_ComparableRun_WithAndWithoutCustomDisplayName()
    {
        var run = new RlPortfolioComparableRun(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero),
            RlPortfolioTransferContract.FineTuneKind,
            "1234567890abcdef", "abcdef1234567890");

        string normal = PortfolioRlPresentation.ComparableRun(run, SimpleLocalization.Instance);
        Assert.StartsWith("2026-02-01 12:00 · FineTune · aaaaaaaa", normal);

        string withCustom = PortfolioRlPresentation.ComparableRun(run, SimpleLocalization.Instance, "Transfer Run V1");
        Assert.StartsWith("[Transfer Run V1] 2026-02-01 12:00 · FineTune · aaaaaaaa", withCustom);
    }

    private sealed class UnavailableQ : IQValueService
    {
        public Task<RlQSnapshot> ReplayAsync(RlReplayRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No generation supplied.");
    }

    private sealed class FakeDefaultSettingsManager : IBacktestDefaultSettingsManager
    {
        public Task<BacktestDefaultSettingsLoadResult> LoadAsync()
            => Task.FromResult(new BacktestDefaultSettingsLoadResult(BacktestDefaultSettings.BuiltIn, BacktestDefaultSettingsLoadStatus.Loaded));
        public Task SaveAsync(BacktestDefaultSettings settings) => Task.CompletedTask;
    }

    private sealed class TestRlRegistry : IRlModelGenerationRegistry
    {
        public List<RlGeneration> Generations { get; set; } = new();
        public IReadOnlyList<RlGeneration> ListRl() => Generations;
        public IReadOnlySet<string> RlModelIdsWithoutWorkFolder() => ImmutableHashSet<string>.Empty;
        public RlRegistryCapabilities Capabilities => new(Storage: true, Recovery: false, Checkpoints: false);
        public string? RlActiveId { get; set; }
        public string? RlContinuousActiveId { get; set; }
        public Task<RlGenerationRemoval> RemoveRlGenerationAsync(string modelId, CancellationToken ct = default) => Task.FromResult(new RlGenerationRemoval(true, ImmutableArray<string>.Empty));
        public void SetRlActive(string modelId) => RlActiveId = modelId;
        public void SetRlContinuousActive(string modelId) => RlContinuousActiveId = modelId;
        public RlGeneration? GetRlGeneration(string modelId) => Generations.FirstOrDefault(g => g.ModelId == modelId);
        public Task<RlGeneration> RegisterRlAsync(string artifactDirectory, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> ActivateRlAsync(string modelId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public ModelGenerationLease? AcquireRl(string modelId) => null;
        public ModelGenerationLease? AcquireRlActive() => null;
    }

    private sealed class TestReplayTarget : IRlReplayTarget
    {
        public Task ApplyReplayAsync(RlQSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Clear() { }
    }

    [Fact]
    public async Task RlPlaybackViewModel_RenameCommand_FullCycle()
    {
        var annotationService = new ModelMetadataAnnotationService(_annotationPath);
        await annotationService.InitializeAsync();

        var (dialogService, dialogController) = InputDialogProxy.Create();
        var registry = new TestRlRegistry
        {
            Generations = new()
            {
                new RlGeneration("rl-m1", "SPY", "src", "hash", "eval", 0.1m, 0.2m, new(), DateTimeOffset.UtcNow) { Target = RlFiles.Target }
            }
        };

        var dataService = new RlDialogDataService(registry, new UnavailableQ());
        using var vm = new RlPlaybackViewModel(
            registry, dataService, new TestReplayTarget(),
            SimpleLocalization.Instance, new SynchronousDispatcherService(),
            dialogService: dialogService, annotationService: annotationService);

        // Wait for RefreshAsync
        for (int i = 0; i < 50 && vm.ModelsState != RlDialogLoadState.Loaded; i++)
        {
            await Task.Delay(10);
        }

        // Initially no custom display name
        Assert.Single(vm.ModelOptions);
        Assert.Equal("Direction · SPY · rl-m1", vm.ModelOptions[0].Name);
        Assert.True(vm.CanRenameSelectedModel);

        // 1. User cancels input (null) -> no state change
        dialogController.NextInputResult = null;
        await vm.RenameSelectedModelCommand.ExecuteAsync(null);
        Assert.Equal("Direction · SPY · rl-m1", vm.ModelOptions[0].Name);

        // 2. User inputs valid custom name "Trained Agent X"
        dialogController.NextInputResult = "Trained Agent X";
        await vm.RenameSelectedModelCommand.ExecuteAsync(null);
        Assert.Equal("[Trained Agent X] Direction · SPY · rl-m1", vm.ModelOptions[0].Name);
        Assert.Contains("[Trained Agent X]", vm.SelectedModelDisplay);

        // Verify annotation store persistence
        string resolved = annotationService.ResolveDisplayName(new ModelMetadataKey(ModelArtifactKind.ReinforcementLearning, "rl-m1"), "fallback");
        Assert.Equal("Trained Agent X", resolved);

        // 3. User clears custom name with empty string
        dialogController.NextInputResult = "   ";
        await vm.RenameSelectedModelCommand.ExecuteAsync(null);
        Assert.Equal("Direction · SPY · rl-m1", vm.ModelOptions[0].Name);

        resolved = annotationService.ResolveDisplayName(new ModelMetadataKey(ModelArtifactKind.ReinforcementLearning, "rl-m1"), "fallback");
        Assert.Equal("fallback", resolved);
    }

    private sealed class FakeTransferService : IRlPortfolioTransferService
    {
        public List<RlPortfolioBaseRunInfo> BaseRuns { get; set; } = new();
        public List<RlPortfolioComparableRun> ComparableRuns { get; set; } = new();

        public ImmutableArray<RlPortfolioBaseRunInfo> ListBaseRuns() => BaseRuns.ToImmutableArray();
        public ImmutableArray<RlPortfolioComparableRun> ListComparableRuns() => ComparableRuns.ToImmutableArray();
        public RlPortfolioBaseRun Admit(Guid baseRunId, DateOnly targetFirstValidationDate) => throw new NotImplementedException();
        public Task<RlPortfolioZeroShotResult> EvaluateStoredModelAsync(Guid baseRunId, RlPortfolioRunRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public RlPortfolioTransferComparisonResult CompareRuns(Guid runA, Guid runB) => throw new NotImplementedException();
    }

    private sealed class FakePortfolioService : IRlPortfolioService
    {
        public RlPortfolioSettings Settings => new();
        public Task<PanelPreflight> PreflightAsync(IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested, RlPanelEvaluationMode mode, CancellationToken cancellationToken)
            => Task.FromResult(new PanelPreflight(start, endExclusive, mode, ImmutableArray<RlPanelSymbolPreflight>.Empty, 0, 0, ImmutableArray<RlPanelCellCount>.Empty, true, false));
        public Task<RlPortfolioTrainingResult> RunAsync(RlPortfolioRunRequest request, IProgress<RlTrainingProgress>? progress, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class FakeTickerSourceResolver : ITickerSourceResolver
    {
        public Task<RlSourceResolution> ResolveAsync(IReadOnlyList<RlSourceDescriptor> sources, IReadOnlyCollection<string>? excluded = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new RlSourceResolution(ImmutableArray<string>.Empty, ImmutableArray<RlSourceDiagnostic>.Empty));
    }

    private sealed class FakeTickerStateStore : ITickerStateStore
    {
        public List<TickerGroupNode> Groups { get; } = new();
        IEnumerable<TickerGroupNode> ITickerStateStore.Groups => Groups;
        public IEnumerable<IFilterableSymbol> DisplayItems => Array.Empty<IFilterableSymbol>();
        public IReadOnlyList<string> GetTickersForNode(TickerGroupNode? node) => Array.Empty<string>();
    }

    [Fact]
    public async Task PortfolioRlDialogViewModel_RenameBaseRun_FullCycle()
    {
        var annotationService = new ModelMetadataAnnotationService(_annotationPath);
        await annotationService.InitializeAsync();

        var (dialogService, dialogController) = InputDialogProxy.Create();
        var runId = Guid.Parse("99999999-8888-7777-6666-555555555555");
        var transferService = new FakeTransferService
        {
            BaseRuns = new()
            {
                new RlPortfolioBaseRunInfo(runId, DateTimeOffset.UtcNow, "1234567890abcdef", "1234567890abcdef", new RlBasePeriod(RlBasePeriodStatus.Ambiguous, null, null))
            },
            ComparableRuns = new()
            {
                new RlPortfolioComparableRun(runId, DateTimeOffset.UtcNow, RlPortfolioTransferContract.FineTuneKind, "1234567890abcdef", "1234567890abcdef")
            }
        };

        using var vm = new PortfolioRlDialogViewModel(
            new FakePortfolioService(),
            new FakeTickerSourceResolver(),
            new FakeTickerStateStore(),
            new FakeDefaultSettingsManager(),
            SimpleLocalization.Instance,
            transfer: transferService,
            dialogService: dialogService,
            annotationService: annotationService);

        // Switch to FineTune run to activate transfer controls
        vm.RunKind = PortfolioRunKind.FineTune;
        await vm.InitializeAsync();

        // Wait for base runs and comparable runs to load
        for (int i = 0; i < 50 && (vm.BaseRuns.Count == 0 || vm.ComparableRuns.Count == 0); i++)
        {
            await Task.Delay(10);
        }

        Assert.Single(vm.BaseRuns);
        Assert.Single(vm.ComparableRuns);
        vm.SelectedBaseRun = vm.BaseRuns[0];
        Assert.NotNull(vm.SelectedBaseRun);
        Assert.True(vm.CanRenameBaseRun);

        // 1. Cancel input -> no state change
        dialogController.NextInputResult = null;
        await vm.RenameSelectedBaseRunCommand.ExecuteAsync(null);
        Assert.DoesNotContain("[Portfolio Champion]", vm.SelectedBaseRun.Text);

        // 2. Set custom name
        dialogController.NextInputResult = "Portfolio Champion";
        await vm.RenameSelectedBaseRunCommand.ExecuteAsync(null);
        Assert.StartsWith("[Portfolio Champion]", vm.SelectedBaseRun.Text);
        Assert.StartsWith("[Portfolio Champion]", vm.ComparableRuns.Single(r => r.RunId == runId).Text);

        // 3. Clear custom name
        dialogController.NextInputResult = "";
        await vm.RenameSelectedBaseRunCommand.ExecuteAsync(null);
        Assert.DoesNotContain("[Portfolio Champion]", vm.SelectedBaseRun.Text);

        // 4. Over 128 characters -> length error message
        dialogController.NextInputResult = new string('X', 129);
        await vm.RenameSelectedBaseRunCommand.ExecuteAsync(null);
        Assert.Equal("Model_Rename_LengthError", vm.Message);

        // 5. Control char -> invalid char error message
        dialogController.NextInputResult = "Portfolio\u0000Run";
        await vm.RenameSelectedBaseRunCommand.ExecuteAsync(null);
        Assert.Equal("Model_Rename_InvalidCharError", vm.Message);
    }
}
