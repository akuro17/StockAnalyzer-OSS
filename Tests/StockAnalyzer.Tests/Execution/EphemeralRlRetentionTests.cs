#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.Tests.Backtest;
using Xunit;

namespace StockAnalyzer.Tests.Execution;

public sealed class EphemeralRlRetentionTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private readonly string _root = Path.Combine(EphemeralSandbox.Root, "retention-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<SandboxedExecutionGateway> _gateways = new();

    public void Dispose()
    {
        foreach (SandboxedExecutionGateway gateway in _gateways)
        {
            gateway.Dispose();
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string GoodModelPath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "StockAnalyzer.Core.Tests", "Assets", "trend_predictor_goodmeta.onnx"));

    private SandboxedExecutionGateway CreateGateway(
        IEphemeralRlRunner? rlRunner = null,
        IEphemeralPortfolioRlRunner? portfolioRunner = null)
    {
        var gateway = new SandboxedExecutionGateway(
            new DummyTrainingOrchestrator(),
            new DummyPythonResolver(),
            new SandboxedExecutionSettings { MaxConcurrentJobs = 1, KeepLatestRuns = 1 },
            NullLogger<SandboxedExecutionGateway>.Instance,
            _root,
            TimeSpan.FromSeconds(2),
            rlRunner,
            portfolioRunner: portfolioRunner);

        _gateways.Add(gateway);
        return gateway;
    }

    private static async Task<TrainingJobStatus> WaitForTerminalAsync(SandboxedExecutionGateway gateway, string jobId)
    {
        using var cts = new CancellationTokenSource(Patience);
        while (!cts.Token.IsCancellationRequested)
        {
            TrainingJobStatus status = gateway.GetStatus(jobId);
            if (status.State is TrainingJobState.Succeeded or TrainingJobState.Failed or TrainingJobState.Cancelled)
            {
                return status;
            }

            await Task.Delay(50, cts.Token);
        }

        throw new TimeoutException($"Job {jobId} did not finish within {Patience.TotalSeconds} seconds.");
    }

    [Fact]
    public async Task SandboxedExecutionGateway_WhenRetainArtifactIsFalse_DeletesJobFolderAndArtifactIsNull()
    {
        var runner = new ScriptedRlRunner(GoodModelPath);
        SandboxedExecutionGateway gateway = CreateGateway(rlRunner: runner);

        var request = new EphemeralRlRequest
        {
            Symbol = "SPY",
            StartDate = new DateOnly(2023, 1, 1),
            EndDateExclusive = new DateOnly(2024, 1, 1),
            RetainArtifact = false,
        };

        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(request, CancellationToken.None);
        Assert.Equal(TrainingJobState.Running, ticket.State);

        TrainingJobStatus status = await WaitForTerminalAsync(gateway, ticket.JobId);
        Assert.Equal(TrainingJobState.Succeeded, status.State);
        Assert.Null(status.Artifact);
        Assert.NotNull(status.RlResult);

        // Job directory should be deleted under the sandbox root
        string[] matchingDirs = Directory.GetDirectories(_root, ticket.JobId, SearchOption.AllDirectories);
        Assert.Empty(matchingDirs);
    }

    [Fact]
    public async Task SandboxedExecutionGateway_WhenRetainArtifactIsTrue_RetainsJobFolderAndPopulatesArtifact()
    {
        var runner = new ScriptedRlRunner(GoodModelPath);
        SandboxedExecutionGateway gateway = CreateGateway(rlRunner: runner);

        var request = new EphemeralRlRequest
        {
            Symbol = "SPY",
            StartDate = new DateOnly(2023, 1, 1),
            EndDateExclusive = new DateOnly(2024, 1, 1),
            RetainArtifact = true,
        };

        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(request, CancellationToken.None);
        Assert.Equal(TrainingJobState.Running, ticket.State);

        TrainingJobStatus status = await WaitForTerminalAsync(gateway, ticket.JobId);
        Assert.Equal(TrainingJobState.Succeeded, status.State);
        Assert.NotNull(status.Artifact);
        Assert.Equal(ticket.JobId, status.Artifact.ArtifactId);
        Assert.EndsWith("policy.onnx", status.Artifact.OnnxFileName);
        Assert.True(status.Artifact.Loads);

        // Job directory should still exist
        string[] matchingDirs = Directory.GetDirectories(_root, ticket.JobId, SearchOption.AllDirectories);
        Assert.Single(matchingDirs);
        Assert.True(Directory.Exists(matchingDirs[0]));

        // Artifact should be leasable
        using ArtifactLease lease = gateway.LeaseArtifact(ticket.JobId);
        Assert.NotNull(lease);
        Assert.True(File.Exists(lease.OnnxPath));
    }

    [Fact]
    public async Task SandboxedExecutionGateway_PortfolioRl_WhenRetainArtifactIsTrue_RetainsJobFolderAndPopulatesArtifact()
    {
        var runner = new ScriptedPortfolioRunner(GoodModelPath);
        SandboxedExecutionGateway gateway = CreateGateway(portfolioRunner: runner);

        var request = new EphemeralPortfolioRlRequest
        {
            Symbols = new[] { "AAPL", "MSFT", "GOOG" },
            StartDate = new DateOnly(2023, 1, 1),
            EndDateExclusive = new DateOnly(2024, 1, 1),
            RetainArtifact = true,
        };

        TrainingJobTicket ticket = await gateway.StartPortfolioRlTrainingAsync(request, CancellationToken.None);
        Assert.Equal(TrainingJobState.Running, ticket.State);

        TrainingJobStatus status = await WaitForTerminalAsync(gateway, ticket.JobId);
        Assert.Equal(TrainingJobState.Succeeded, status.State);
        Assert.NotNull(status.Artifact);
        Assert.Equal(ticket.JobId, status.Artifact.ArtifactId);
        Assert.True(status.Artifact.Loads);

        string[] matchingDirs = Directory.GetDirectories(_root, ticket.JobId, SearchOption.AllDirectories);
        Assert.Single(matchingDirs);
        Assert.True(Directory.Exists(matchingDirs[0]));

        using ArtifactLease lease = gateway.LeaseArtifact(ticket.JobId);
        Assert.NotNull(lease);
        Assert.True(File.Exists(lease.OnnxPath));
    }

    [Fact]
    public async Task SandboxedExecutionGateway_PortfolioRl_StartGateUsesTheRunnersConfiguredSymbolMaximum()
    {
        string[] symbols = { "AAPL", "MSFT", "GOOG" };
        var runner = new ScriptedPortfolioRunner(GoodModelPath, new RlPortfolioSettings { MaxSymbols = symbols.Length - 1 });
        SandboxedExecutionGateway gateway = CreateGateway(portfolioRunner: runner);

        var request = new EphemeralPortfolioRlRequest
        {
            Symbols = symbols,
            StartDate = new DateOnly(2023, 1, 1),
            EndDateExclusive = new DateOnly(2024, 1, 1),
        };

        var refusal = await Assert.ThrowsAsync<ExecutionRequestInvalidException>(() => gateway.StartPortfolioRlTrainingAsync(request, CancellationToken.None));

        Assert.Contains(nameof(RlPanelStartBlocker.TooManySymbols), refusal.Message);
    }

    [Fact]
    public async Task ReplayEphemeralModelAsync_ValidatesInputBoundaries()
    {
        var fakeReplay = new FakeQValueService();
        var fakeRegistry = new FakeModelRegistry();
        var service = new RlDialogDataService(fakeRegistry, fakeReplay);

        // 1. Path is null or empty
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReplayEphemeralModelAsync("", "AAPL", new DateOnly(2024, 1, 1), 0));

        // 2. Path is outside EphemeralSandbox.Root
        string outsidePath = Path.Combine(Path.GetTempPath(), "outside-test", "model.onnx");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReplayEphemeralModelAsync(outsidePath, "AAPL", new DateOnly(2024, 1, 1), 0));

        // 3. Path is inside EphemeralSandbox.Root but does not exist
        string nonExistentInside = Path.Combine(EphemeralSandbox.Root, "non-existent-" + Guid.NewGuid().ToString("N"), "model.onnx");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ReplayEphemeralModelAsync(nonExistentInside, "AAPL", new DateOnly(2024, 1, 1), 0));

        // Create a real file inside EphemeralSandbox.Root for further checks
        string validFolder = Path.Combine(EphemeralSandbox.Root, "test-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(validFolder);
        try
        {
            string validPath = Path.Combine(validFolder, "model.onnx");
            File.WriteAllText(validPath, "placeholder");

            // 4. DecisionPosition < 0
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.ReplayEphemeralModelAsync(validPath, "AAPL", new DateOnly(2024, 1, 1), -1));

            // 5. DecisionPosition >= 128 (RlContract.EpisodeHorizon)
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                service.ReplayEphemeralModelAsync(validPath, "AAPL", new DateOnly(2024, 1, 1), 128));

            // 6. Symbol is null or whitespace
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ReplayEphemeralModelAsync(validPath, "   ", new DateOnly(2024, 1, 1), 0));

            // 7. Normal success case
            var result = await service.ReplayEphemeralModelAsync(validPath, "MSFT", new DateOnly(2024, 2, 1), 42, "policy-123");
            Assert.NotNull(result);
            Assert.Equal(validPath, fakeReplay.LastModelPath);
            Assert.Equal("MSFT", fakeReplay.LastSymbol);
            Assert.Equal(new DateOnly(2024, 2, 1), fakeReplay.LastDate);
            Assert.Equal(42, fakeReplay.LastPosition);
            Assert.Equal("policy-123", fakeReplay.LastPolicyId);
        }
        finally
        {
            try { Directory.Delete(validFolder, true); } catch { }
        }
    }

    [Fact]
    public void RlPlaybackViewModel_TestTempModelFromFileCommand_CanExecuteRespectsDialogService()
    {
        var fakeReplay = new FakeQValueService();
        var fakeRegistry = new FakeModelRegistry();
        var data = new RlDialogDataService(fakeRegistry, fakeReplay);
        var target = new DummyReplayTarget();
        var dispatcher = new SynchronousDispatcherService();

        // Without dialog service -> CanTestTempModel is false
        var vmWithoutDialog = new RlPlaybackViewModel(
            fakeRegistry, data, target, NullLocalizationService.Instance, dispatcher, dialogService: null);
        Assert.False(vmWithoutDialog.TestTempModelFromFileCommand.CanExecute(null));

        // With dialog service -> CanTestTempModel is true
        var fakeDialog = new FakeDialogService();
        var vmWithDialog = new RlPlaybackViewModel(
            fakeRegistry, data, target, NullLocalizationService.Instance, dispatcher, dialogService: fakeDialog);
        Assert.True(vmWithDialog.TestTempModelFromFileCommand.CanExecute(null));
    }

    // --- Helper fakes and runners ---

    private sealed class ScriptedRlRunner(string goodModel) : IEphemeralRlRunner
    {
        public Task<RlEnvironmentStatus> CheckEnvironmentAsync(CancellationToken cancellationToken)
            => Task.FromResult(RlEnvironmentStatus.Usable);

        public Task<StoredPeriodProposal> ProposePeriodAsync(string symbol, int requiredBars, DateOnly? endBoundInclusive, CancellationToken cancellationToken)
            => Task.FromResult(new StoredPeriodProposal(StoredPeriodStatus.Found, new DateOnly(2023, 1, 1), new DateOnly(2024, 1, 1), requiredBars, requiredBars, Array.Empty<string>()));

        public Task<RlTrainingResult> RunAsync(RlTrainingJob job, string jobDirectory, IProgress<RlTrainingProgress> progress, CancellationToken cancellationToken)
        {
            string run = Path.Combine(jobDirectory, RlFiles.TrainingFolder, RlFiles.RlFolder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(run);
            string model = Path.Combine(run, RlFiles.Model);
            File.Copy(goodModel, model);
            string evaluation = model + RlFiles.EvaluationSuffix;
            File.WriteAllText(evaluation,
                $"{{\"validation_mean_return\":\"0.0123\",\"candidate_seed\":42,\"checkpoint_step\":512,\"training_transitions\":{{\"42\":{RlContract.SmokeTrainingTransitions}}}}}");

            var result = new RlTrainingResult(Guid.NewGuid(), run, model, evaluation, null, true, "source-rev");
            return Task.FromResult(result);
        }
    }

    private sealed class ScriptedPortfolioRunner(string goodModel, RlPortfolioSettings? settings = null) : IEphemeralPortfolioRlRunner
    {
        public RlPortfolioSettings Settings { get; } = settings ?? new RlPortfolioSettings();

        public Task<RlEnvironmentStatus> CheckEnvironmentAsync(CancellationToken cancellationToken)
            => Task.FromResult(RlEnvironmentStatus.Usable);

        public Task<PanelPreflight> PreflightAsync(IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested, RlPanelEvaluationMode mode, CancellationToken cancellationToken)
        {
            var symbolPreflights = symbols.Select(s => new RlPanelSymbolPreflight(s, RlPanelSymbolStatus.Ready, 500, 100, "ready")).ToImmutableArray();
            return Task.FromResult(new PanelPreflight(start, endExclusive, mode, symbolPreflights, 500, 100, ImmutableArray<RlPanelCellCount>.Empty, true, false));
        }

        public Task<RlStartSuggestion?> SuggestStartAsync(IReadOnlyList<string> symbols, DateOnly endExclusive, RlPanelEvaluationMode mode, CancellationToken cancellationToken)
            => Task.FromResult<RlStartSuggestion?>(new RlStartSuggestion(RlStartSuggestionStatus.Found, new DateOnly(2023, 1, 1), 500, 500));

        public Task<RlPortfolioTrainingResult> RunAsync(RlPortfolioRunRequest request, string jobDirectory, IProgress<RlTrainingProgress>? progress, CancellationToken cancellationToken)
        {
            string run = Path.Combine(jobDirectory, "portfolio_run_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(run);
            string model = Path.Combine(run, "policy.onnx");
            File.Copy(goodModel, model);
            string report = Path.Combine(run, "report.json");
            File.WriteAllText(report, "{}");

            var evaluation = new RlPortfolioEvaluationReport(
                "spec", 1, "hash1", "hash2", "evalspec",
                "sha1", "sha2", "sha3",
                ImmutableArray.Create("AAPL", "MSFT", "GOOG"),
                ImmutableArray.Create("AAPL", "MSFT", "GOOG"),
                ImmutableArray<string>.Empty,
                false,
                new BacktestConfiguration(),
                50,
                true,
                ImmutableArray<RlPortfolioCellReport>.Empty,
                "pit", "eff", "stmt",
                ImmutableArray<string>.Empty);

            var result = new RlPortfolioTrainingResult(
                Guid.NewGuid(), run, model, Path.Combine(run, "parity.json"), 42, 512, "sha-universe", "sha-calendar", report, evaluation, run);
            return Task.FromResult(result);
        }
    }

    private sealed class DummyTrainingOrchestrator : ITrainingOrchestrator
    {
        public Task<TrainingRunResult> StartTrainingAsync(TrainingJobConfig config, IProgress<TrainingProgress>? progress = null, CancellationToken ct = default)
            => Task.FromException<TrainingRunResult>(new NotSupportedException());
    }

    private sealed class DummyPythonResolver : IPythonExecutableResolver
    {
        public bool TryResolve(out string executablePath)
        {
            executablePath = "python.exe";
            return true;
        }
    }

    private sealed class FakeQValueService : IQValueService
    {
        public string? LastModelPath { get; private set; }
        public string? LastSymbol { get; private set; }
        public DateOnly LastDate { get; private set; }
        public int LastPosition { get; private set; }
        public string? LastPolicyId { get; private set; }

        public Task<RlQSnapshot> ReplayAsync(RlReplayRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<RlExternalReplayResult> ReplayEphemeralAsync(
            string onnxModelPath,
            string symbol,
            DateOnly asOfDate,
            int decisionPosition,
            string? policyId = null,
            CancellationToken cancellationToken = default)
        {
            LastModelPath = onnxModelPath;
            LastSymbol = symbol;
            LastDate = asOfDate;
            LastPosition = decisionPosition;
            LastPolicyId = policyId;

            var now = DateTime.UtcNow;
            var eq = new EquityPoint(0, now, 1000m, 1000m, 0m, 0m);
            var account = new RlSessionSnapshot(
                0, now, null, 0, 10, eq, 0m, 0m, 0m, 1000m, 0m, 0m, 0, false, false, false,
                RlTerminalReason.None, RunStatus.Completed, ImmutableArray<decimal>.Empty, ImmutableArray<CandleData>.Empty);

            var snapshot = new RlQSnapshot(
                "req-key", "model-1", symbol, now,
                ImmutableArray.Create(0.5f, 0.2f),
                RlAction.Hold,
                account,
                "disclosure",
                policyId ?? "policy-default");

            return Task.FromResult(new RlExternalReplayResult(snapshot, asOfDate, asOfDate));
        }
    }

    private sealed class FakeModelRegistry : IRlModelGenerationRegistry
    {
        public string? RlActiveId => null;
        public IReadOnlyList<RlGeneration> ListRl() => Array.Empty<RlGeneration>();
        public ModelGenerationLease? AcquireRl(string modelId) => null;
        public ModelGenerationLease? AcquireRlActive() => null;
        public RlGeneration? GetRlGeneration(string modelId) => null;
        public Task<bool> ActivateRlAsync(string modelId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<RlGeneration> RegisterRlAsync(string artifactDirectory, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class DummyReplayTarget : IRlReplayTarget
    {
        public Task ApplyReplayAsync(RlQSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
        public void Clear() { }
    }
}
