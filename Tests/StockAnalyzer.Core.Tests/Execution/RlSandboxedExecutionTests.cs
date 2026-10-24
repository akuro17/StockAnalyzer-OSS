#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Execution;

/// <summary>
/// MP-3 at the gateway level: a reinforcement-learning smoke job through the shared job table. The RL side is replaced by a scripted runner (no Python),
/// so the period rules, the state machine, the folders and the clean-up are tested exactly; the real worker is covered by the opt-in tests
/// (<see cref="StockAnalyzer.Core.Tests.Services.RlSmokeSandboxRealTests"/> and the MCP end-to-end test).
/// </summary>
public sealed class RlSandboxedExecutionTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly DateOnly Latest = new(2024, 3, 28);
    private static readonly DateOnly Earliest = new(2019, 1, 2);

    private readonly string _root = Path.Combine(EphemeralSandbox.Root, "rlgwtest-" + Guid.NewGuid().ToString("N"));
    private readonly List<SandboxedExecutionGateway> _gateways = new();
    private readonly List<string> _extraRoots = new();

    public void Dispose()
    {
        foreach (SandboxedExecutionGateway gateway in _gateways)
        {
            gateway.Dispose();
        }

        try { Directory.Delete(_root, true); } catch (IOException) { }
        foreach (string extraRoot in _extraRoots)
        {
            try { Directory.Delete(extraRoot, true); } catch (IOException) { }
        }
    }

    private static string GoodModel => Path.Combine(AppContext.BaseDirectory, "Assets", "trend_predictor_goodmeta.onnx");

    private SandboxedExecutionGateway Create(ScriptedRunner? runner, ITrainingOrchestrator? orchestrator = null, SandboxedExecutionSettings? settings = null, string? root = null, TimeSpan? grace = null)
    {
        var gateway = new SandboxedExecutionGateway(
            orchestrator ?? new NeverEndingOrchestrator(), new FixedPython(), settings ?? new SandboxedExecutionSettings(), null, root ?? _root, grace ?? TimeSpan.FromMilliseconds(300), runner);
        _gateways.Add(gateway);
        return gateway;
    }

    private static EphemeralRlRequest Request(DateOnly? start = null, DateOnly? end = null, string symbol = "ABC")
        => new() { Symbol = symbol, StartDate = start, EndDateExclusive = end };

    // ---- the period (as the RL window pre-fills it) ----------------------------------------------------------------------------------

    [Fact]
    public async Task OmittedDatesAreProposedFromTheStoredDataWithTheShortHistoryRequirement()
    {
        var runner = new ScriptedRunner { Proposal = Found() };
        SandboxedExecutionGateway gateway = Create(runner);

        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

        Assert.Equal(TrainingJobState.Running, ticket.State);
        Assert.Equal(RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory), runner.ProposedBars);
        Assert.Null(runner.ProposedBound);
        RlTrainingJob job = Assert.Single(runner.Jobs);
        Assert.True(job.SmokeOnly);
        Assert.Equal("ABC", job.Symbol);
        Assert.Equal(Earliest, job.StartDate);
        Assert.Equal(Latest.AddDays(1), job.EndDateExclusive);
        runner.Release();
    }

    [Fact]
    public async Task ATypedEndBoundsTheProposalAndATypedStartIsKept()
    {
        var runner = new ScriptedRunner { Proposal = Found() };
        SandboxedExecutionGateway gateway = Create(runner);
        DateOnly end = new(2023, 6, 1);

        await gateway.StartRlTrainingAsync(Request(end: end), CancellationToken.None);
        Assert.Equal(end.AddDays(-1), runner.ProposedBound);
        Assert.Equal(Earliest, runner.Jobs[0].StartDate);
        Assert.Equal(end, runner.Jobs[0].EndDateExclusive);
        runner.Release();
    }

    [Fact]
    public async Task AnExplicitPeriodIsNotSecondGuessedAndAsksNoProposal()
    {
        var runner = new ScriptedRunner { Proposal = StoredPeriodProposal.None(StoredPeriodStatus.NoData) };
        SandboxedExecutionGateway gateway = Create(runner);

        await gateway.StartRlTrainingAsync(Request(new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1)), CancellationToken.None);

        Assert.Null(runner.ProposedBars);
        Assert.Equal(new DateOnly(2020, 1, 1), runner.Jobs[0].StartDate);
        runner.Release();
    }

    [Fact]
    public async Task ATypedStartSurvivesAnInsufficientProposalButAnAutomaticStartDoesNot()
    {
        var runner = new ScriptedRunner { Proposal = new StoredPeriodProposal(StoredPeriodStatus.InsufficientHistory, Earliest, Latest, 1_278, 900, Array.Empty<string>()) };
        SandboxedExecutionGateway gateway = Create(runner, settings: new SandboxedExecutionSettings { MaxConcurrentJobs = 2, KeepLatestRuns = 2 });

        var refused = await Assert.ThrowsAsync<InsufficientStoredDataException>(() => gateway.StartRlTrainingAsync(Request(), CancellationToken.None));
        Assert.Equal(1_278, refused.RequiredBars);
        Assert.Equal(900, refused.AvailableBars);
        Assert.Empty(runner.Jobs);

        await gateway.StartRlTrainingAsync(Request(start: new DateOnly(2020, 1, 1)), CancellationToken.None);
        Assert.Equal(Latest.AddDays(1), runner.Jobs[0].EndDateExclusive);
        runner.Release();
    }

    [Theory]
    [InlineData(StoredPeriodStatus.NoData)]
    [InlineData(StoredPeriodStatus.NoProposal)]
    [InlineData(StoredPeriodStatus.TooMany)]
    public async Task NoUsableStoredBarsIsInsufficientData(StoredPeriodStatus status)
    {
        SandboxedExecutionGateway gateway = Create(new ScriptedRunner { Proposal = StoredPeriodProposal.None(status) });

        var refused = await Assert.ThrowsAsync<InsufficientStoredDataException>(() => gateway.StartRlTrainingAsync(Request(), CancellationToken.None));

        Assert.Equal(0, refused.AvailableBars);
        Assert.False(Directory.Exists(_root), "a refused request must not touch the sandbox");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("A/B")]
    public async Task ARefusedSymbolCreatesNoJob(string symbol)
    {
        var runner = new ScriptedRunner { Proposal = Found() };
        SandboxedExecutionGateway gateway = Create(runner);

        await Assert.ThrowsAsync<ExecutionRequestInvalidException>(() => gateway.StartRlTrainingAsync(
            Request(new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1), symbol), CancellationToken.None));

        Assert.Empty(runner.Jobs);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task AnEmptyOrReversedPeriodIsRefused()
    {
        SandboxedExecutionGateway gateway = Create(new ScriptedRunner { Proposal = Found() });

        var error = await Assert.ThrowsAsync<ExecutionRequestInvalidException>(() => gateway.StartRlTrainingAsync(
            Request(new DateOnly(2021, 1, 1), new DateOnly(2021, 1, 1)), CancellationToken.None));

        Assert.Equal(nameof(EphemeralRlRequest.StartDate), error.ParameterName);
    }

    // ---- refusals before a job exists --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMissingManagedEnvironmentCreatesNoJobAndNoFolder()
    {
        var runner = new ScriptedRunner { Proposal = Found(), EnvironmentAvailable = false };
        SandboxedExecutionGateway gateway = Create(runner);

        await Assert.ThrowsAsync<RlEnvironmentMissingException>(() => gateway.StartRlTrainingAsync(Request(), CancellationToken.None));

        Assert.Empty(runner.Jobs);
        Assert.False(Directory.Exists(_root));
    }

    // ---- decision 3-A: the path budget of a reinforcement-learning job ------------------------------------------------------------------

    [Fact]
    public async Task APathBudget_AJobFolderOfTheMeasuredLimitStarts()
    {
        string root = JobFolderRoots.WithJobFolderLength(JobFolderRoots.LongestJobFolderThatFits(ExecutionLimits.RlLongestRelativePathChars));
        _extraRoots.Add(root);
        var runner = new ScriptedRunner { Proposal = Found() };
        SandboxedExecutionGateway gateway = Create(runner, root: root);

        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

        Assert.Equal(TrainingJobState.Running, ticket.State);
        Assert.Equal(JobFolderRoots.LongestJobFolderThatFits(ExecutionLimits.RlLongestRelativePathChars), Assert.Single(runner.Folders).Length);
        runner.Release();
    }

    [Fact]
    public async Task APathBudget_OneCharacterMoreIsRefusedBeforeAnythingIsCreated()
    {
        string root = JobFolderRoots.WithJobFolderLength(JobFolderRoots.LongestJobFolderThatFits(ExecutionLimits.RlLongestRelativePathChars) + 1);
        _extraRoots.Add(root);
        var runner = new ScriptedRunner { Proposal = Found() };
        SandboxedExecutionGateway gateway = Create(runner, root: root);

        var refused = await Assert.ThrowsAsync<ExecutionPathTooLongException>(() => gateway.StartRlTrainingAsync(Request(), CancellationToken.None));

        Assert.Equal(ExecutionLimits.PathCharsLimit, refused.RequiredChars);
        Assert.Empty(runner.Jobs);
        Assert.False(Directory.Exists(root), "a refused start must not create the sandbox");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("gymnasium")]
    public async Task AnEnvironmentThatCannotImportItsPackagesCarriesTheModuleNameAndCreatesNoJobAndNoFolder(string? module)
    {
        var runner = new ScriptedRunner { Proposal = Found(), EnvironmentAvailable = false, MissingModule = module };
        SandboxedExecutionGateway gateway = Create(runner);

        var refused = await Assert.ThrowsAsync<RlEnvironmentMissingException>(() => gateway.StartRlTrainingAsync(Request(), CancellationToken.None));

        Assert.Equal(module, refused.MissingModule);
        Assert.Empty(runner.Jobs);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task AGatewayWithoutAnRlRunnerDoesNotOfferReinforcementLearning()
    {
        SandboxedExecutionGateway gateway = Create(runner: null);

        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.StartRlTrainingAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task TheLimitOnRunningJobsIsSharedWithTheSupervisedJobs()
    {
        var runner = new ScriptedRunner { Proposal = Found() };
        SandboxedExecutionGateway gateway = Create(runner);

        await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);
        await Assert.ThrowsAsync<ExecutionBusyException>(() => gateway.StartRlTrainingAsync(Request(), CancellationToken.None));
        await Assert.ThrowsAsync<ExecutionBusyException>(() => gateway.StartTrainingAsync(SupervisedRequest(), CancellationToken.None));
        Assert.Single(runner.Jobs);
        Assert.Single(JobFolders()); // the refused starts left no folder behind
        runner.Release();
    }

    [Fact]
    public async Task ARunningSupervisedJobMakesAnRlStartBusyToo()
    {
        var runner = new ScriptedRunner { Proposal = Found() };
        SandboxedExecutionGateway gateway = Create(runner);

        await gateway.StartTrainingAsync(SupervisedRequest(), CancellationToken.None);
        await Assert.ThrowsAsync<ExecutionBusyException>(() => gateway.StartRlTrainingAsync(Request(), CancellationToken.None));

        Assert.Empty(runner.Jobs);
    }

    // ---- the run ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASucceededRunReportsItsResultAndLeavesNoFilesAndNoArtifact()
    {
        var runner = new ScriptedRunner { Proposal = Found(), Behavior = ScriptedRunner.Success };
        SandboxedExecutionGateway gateway = Create(runner);

        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);
        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Succeeded);

        Assert.Equal(TrainingJobKind.ReinforcementLearning, status.Kind);
        Assert.Null(status.Request);
        Assert.Equal(new EphemeralRlRequest { Symbol = "ABC", StartDate = Earliest, EndDateExclusive = Latest.AddDays(1) }, status.RlRequest);
        Assert.Null(status.Artifact);
        Assert.Null(status.Metrics);
        EphemeralRlResult result = status.RlResult!;
        Assert.Equal("0.0123", result.ValidationMeanReturn);
        Assert.Equal(42, result.CandidateSeed);
        Assert.Equal(512, result.CheckpointStep);
        Assert.Equal(RlContract.SmokeTrainingTransitions, result.TrainingTransitions);
        Assert.Equal(new FileInfo(GoodModel).Length, result.ModelSizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(GoodModel))).ToLowerInvariant(), result.ModelSha256);
        Assert.Equal("source-revision", result.SourceRevision);
        Assert.Null(gateway.FindArtifact(ticket.JobId));
        Assert.Null(gateway.FindArtifact("latest"));
        Assert.Throws<ArtifactNotFoundException>(() => gateway.LeaseArtifact(ticket.JobId));
        Assert.Empty(JobFolders());
    }

    [Fact]
    public async Task TheRunnerWorksInsideTheJobFolderOfTheSandbox()
    {
        var runner = new ScriptedRunner { Proposal = Found() };
        SandboxedExecutionGateway gateway = Create(runner);

        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

        string folder = Assert.Single(runner.Folders);
        Assert.Equal(ticket.JobId, Path.GetFileName(folder));
        Assert.True(Directory.Exists(folder));
        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
        runner.Release();
    }

    [Fact]
    public async Task ProgressMapsTheStageAndThePercentOfTheSmokeTransitions()
    {
        var runner = new ScriptedRunner { Proposal = Found() };
        SandboxedExecutionGateway gateway = Create(runner);
        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

        runner.Report(new RlTrainingProgress(Guid.Empty, 1, "Baselines", "m", 0));
        runner.Report(new RlTrainingProgress(Guid.Empty, 2, "Training", "m", RlContract.SmokeTrainingTransitions / 2));
        runner.Report(new RlTrainingProgress(Guid.Empty, 3, "Training", "m", -1)); // an invalid count keeps the last percent

        TrainingJobStatus status = gateway.GetStatus(ticket.JobId);
        Assert.Equal("Training", status.Stage);
        Assert.Equal(50, status.ProgressPercent);
        Assert.Contains("Stage: Baselines", status.Messages);
        Assert.Contains("Stage: Training", status.Messages);
        runner.Report(new RlTrainingProgress(Guid.Empty, 4, "Training", "m", int.MaxValue));
        Assert.Equal(100, gateway.GetStatus(ticket.JobId).ProgressPercent);
        runner.Release();
    }

    [Fact]
    public async Task ProgressNeverGoesBackWhenALaterPhaseCountsFromZeroAgain()
    {
        var runner = new ScriptedRunner { Proposal = Found() };
        SandboxedExecutionGateway gateway = Create(runner);
        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

        runner.Report(new RlTrainingProgress(Guid.Empty, 1, "Training", "m", RlContract.SmokeTrainingTransitions / 2));
        runner.Report(new RlTrainingProgress(Guid.Empty, 2, "Evaluation", "m", 0));
        Assert.Equal(50, gateway.GetStatus(ticket.JobId).ProgressPercent);
        Assert.Equal("Evaluation", gateway.GetStatus(ticket.JobId).Stage); // the stage still follows the report
        runner.Report(new RlTrainingProgress(Guid.Empty, 3, "Evaluation", "m", RlContract.SmokeTrainingTransitions / 4));
        Assert.Equal(50, gateway.GetStatus(ticket.JobId).ProgressPercent);
        runner.Report(new RlTrainingProgress(Guid.Empty, 4, "Evaluation", "m", RlContract.SmokeTrainingTransitions * 3 / 4));
        Assert.Equal(75, gateway.GetStatus(ticket.JobId).ProgressPercent);
        runner.Release();
    }

    [Fact]
    public async Task ARunThatSucceedsWhileTheCancellationIsPendingIsACancelledJobWithNoResultAndNoFolder()
    {
        var runner = new ScriptedRunner { Proposal = Found() };
        var finish = new TaskCompletionSource();
        runner.Behavior = async (folder, token) =>
        {
            await finish.Task; // the worker ignores the cancellation and ends successfully
            return await ScriptedRunner.Success(folder, token);
        };
        SandboxedExecutionGateway gateway = Create(runner, grace: TimeSpan.FromMinutes(5));
        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);
        await WaitUntilAsync(() => runner.Jobs.Count == 1);

        Assert.Equal(TrainingJobState.Cancelling, gateway.Cancel(ticket.JobId));
        finish.SetResult();

        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Cancelled);
        Assert.Null(status.RlResult);
        await WaitUntilAsync(() => JobFolders().Length == 0);
    }

    [Fact]
    public async Task ACancelStopsTheRunAndRemovesTheFolder()
    {
        var runner = new ScriptedRunner { Proposal = Found(), Behavior = ScriptedRunner.WaitForCancellation };
        SandboxedExecutionGateway gateway = Create(runner);
        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

        TrainingJobState state = gateway.Cancel(ticket.JobId);
        Assert.Equal(TrainingJobState.Cancelling, state);

        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Cancelled);
        Assert.Null(status.RlResult);
        await WaitUntilAsync(() => JobFolders().Length == 0);
    }

    [Fact]
    public async Task ARunThatIsCancelledByItsOwnDeadlineIsATimeout()
    {
        var runner = new ScriptedRunner { Proposal = Found(), Behavior = (_, _) => throw new OperationCanceledException() };
        SandboxedExecutionGateway gateway = Create(runner);
        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

        TrainingJobStatus status = await WaitForFinalAsync(gateway, ticket.JobId);

        Assert.Equal(TrainingJobState.Failed, status.State);
        Assert.Equal(TrainingFailureReason.Timeout, status.FailureReason);
        Assert.Empty(JobFolders());
    }

    [Fact]
    public async Task AFailedRunSaysWhyFromTheWorkersLastErrorLineAndRemovesItsFolder()
    {
        var runner = new ScriptedRunner
        {
            Proposal = Found(),
            Behavior = (folder, _) =>
            {
                string run = Path.Combine(folder, RlFiles.TrainingFolder, RlFiles.RlFolder, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(run);
                File.WriteAllText(Path.Combine(run, "train_rl.stderr.log"), "Traceback (most recent call last):\nRuntimeError: Pinned dependency mismatch: stable-baselines3\n\n");
                throw new InvalidOperationException("RL worker exited 1.");
            },
        };
        SandboxedExecutionGateway gateway = Create(runner);
        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

        TrainingJobStatus status = await WaitForFinalAsync(gateway, ticket.JobId);

        Assert.Equal(TrainingJobState.Failed, status.State);
        Assert.Equal(TrainingFailureReason.TrainingFailed, status.FailureReason);
        Assert.Contains(status.Messages, m => m.Contains("RL worker exited 1.") && m.Contains("Pinned dependency mismatch: stable-baselines3"));
        Assert.Empty(JobFolders());
    }

    [Fact]
    public async Task AFailureWithoutAWorkerLogKeepsTheErrorText()
    {
        var runner = new ScriptedRunner { Proposal = Found(), Behavior = (_, _) => throw new InvalidDataException("Unavailable: no existing local Data/Daily Parquet.") };
        SandboxedExecutionGateway gateway = Create(runner);
        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

        TrainingJobStatus status = await WaitForFinalAsync(gateway, ticket.JobId);

        Assert.Equal(TrainingJobState.Failed, status.State);
        Assert.Contains(status.Messages, m => m.Contains("Unavailable: no existing local Data/Daily Parquet."));
    }

    [Fact]
    public async Task AModelThatDoesNotLoadIsInvalidOnnx()
    {
        var runner = new ScriptedRunner
        {
            Proposal = Found(),
            Behavior = (folder, _) =>
            {
                ScriptedRunner.WriteResult(folder, out RlTrainingResult result);
                File.WriteAllText(result.ModelPath, "not an onnx file");
                return Task.FromResult(result);
            },
        };
        SandboxedExecutionGateway gateway = Create(runner);
        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

        TrainingJobStatus status = await WaitForFinalAsync(gateway, ticket.JobId);

        Assert.Equal(TrainingJobState.Failed, status.State);
        Assert.Equal(TrainingFailureReason.InvalidOnnx, status.FailureReason);
        Assert.Empty(JobFolders());
    }

    [Fact]
    public async Task AnEvaluationReportWithoutTheExpectedFieldsIsInvalid()
    {
        var runner = new ScriptedRunner
        {
            Proposal = Found(),
            Behavior = (folder, _) =>
            {
                ScriptedRunner.WriteResult(folder, out RlTrainingResult result);
                File.WriteAllText(result.EvaluationPath, "{\"candidate_seed\":42}");
                return Task.FromResult(result);
            },
        };
        SandboxedExecutionGateway gateway = Create(runner);
        TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

        TrainingJobStatus status = await WaitForFinalAsync(gateway, ticket.JobId);

        Assert.Equal(TrainingFailureReason.InvalidOnnx, status.FailureReason);
    }

    [Fact]
    public async Task AResultOutsideTheJobFolderIsRefused()
    {
        string outside = Path.Combine(Path.GetTempPath(), "sa_rl_outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var runner = new ScriptedRunner
            {
                Proposal = Found(),
                Behavior = (_, _) =>
                {
                    ScriptedRunner.WriteResult(outside, out RlTrainingResult result);
                    return Task.FromResult(result);
                },
            };
            SandboxedExecutionGateway gateway = Create(runner);
            TrainingJobTicket ticket = await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

            TrainingJobStatus status = await WaitForFinalAsync(gateway, ticket.JobId);

            Assert.Equal(TrainingJobState.Failed, status.State);
            Assert.Equal(TrainingFailureReason.TrainingFailed, status.FailureReason);
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task DisposingTheGatewayStopsARunningRlJobAndRemovesTheSession()
    {
        var runner = new ScriptedRunner { Proposal = Found(), Behavior = ScriptedRunner.WaitForCancellation };
        SandboxedExecutionGateway gateway = Create(runner);
        await gateway.StartRlTrainingAsync(Request(), CancellationToken.None);

        gateway.Dispose();

        Assert.True(runner.CancelledByToken);
        Assert.Empty(Directory.EnumerateDirectories(_root));
    }

    // ---- the shared table keeps both kinds apart --------------------------------------------------------------------------------

    [Fact]
    public async Task ASupervisedJobStatusStillCarriesItsRequestAndNoRlParts()
    {
        SandboxedExecutionGateway gateway = Create(new ScriptedRunner { Proposal = Found() });
        EphemeralTrainingRequest request = SupervisedRequest();
        TrainingJobTicket ticket = await gateway.StartTrainingAsync(request, CancellationToken.None);

        TrainingJobStatus status = gateway.GetStatus(ticket.JobId);

        Assert.Equal(TrainingJobKind.Supervised, status.Kind);
        Assert.Same(request, status.Request);
        Assert.Null(status.RlRequest);
        Assert.Null(status.RlResult);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------------

    private static EphemeralTrainingRequest SupervisedRequest() => new()
    {
        Symbols = new[] { "SYM" },
        Framework = TrainingFramework.LightGBM,
        Architecture = TrainingEnsembleContract.LightGbmArchitecture,
        WindowSize = 10,
        Horizon = 1,
    };

    private static StoredPeriodProposal Found()
        => new(StoredPeriodStatus.Found, Earliest, Latest, RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory), 2_000, Array.Empty<string>());

    private string[] JobFolders()
        => Directory.Exists(_root)
            ? Directory.EnumerateDirectories(_root).SelectMany(session => Directory.EnumerateDirectories(session)).Select(Path.GetFileName).OfType<string>().ToArray()
            : Array.Empty<string>();

    private static async Task<TrainingJobStatus> WaitForAsync(SandboxedExecutionGateway gateway, string jobId, TrainingJobState state)
    {
        TrainingJobStatus status = await WaitForFinalAsync(gateway, jobId, state);
        Assert.Equal(state, status.State);
        return status;
    }

    private static async Task<TrainingJobStatus> WaitForFinalAsync(SandboxedExecutionGateway gateway, string jobId, TrainingJobState? stopAt = null)
    {
        DateTime limit = DateTime.UtcNow + Patience;
        TrainingJobStatus status;
        while (!((status = gateway.GetStatus(jobId)).State is TrainingJobState.Succeeded or TrainingJobState.Failed or TrainingJobState.Cancelled) && status.State != stopAt)
        {
            Assert.True(DateTime.UtcNow < limit, $"job {jobId} is still {status.State}");
            await Task.Delay(10);
        }

        return status;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "condition not reached in time");
            await Task.Delay(10);
        }
    }

    /// <summary>The RL side with a scripted behaviour; it records what the gateway asks of it.</summary>
    private sealed class ScriptedRunner : IEphemeralRlRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<RlTrainingProgress>? _progress;

        public StoredPeriodProposal Proposal { get; set; } = StoredPeriodProposal.None(StoredPeriodStatus.NoData);

        public bool EnvironmentAvailable { get; set; } = true;

        public int? ProposedBars { get; private set; }

        public DateOnly? ProposedBound { get; private set; }

        public List<RlTrainingJob> Jobs { get; } = new();

        public List<string> Folders { get; } = new();

        public bool CancelledByToken { get; private set; }

        /// <summary>What a run does; by default it waits until <see cref="Release"/> is called.</summary>
        public Func<string, CancellationToken, Task<RlTrainingResult>>? Behavior { get; set; }

        public static readonly Func<string, CancellationToken, Task<RlTrainingResult>> Success = (folder, _) =>
        {
            WriteResult(folder, out RlTrainingResult result);
            return Task.FromResult(result);
        };

        public static Func<string, CancellationToken, Task<RlTrainingResult>> WaitForCancellation => async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        };

        public void Release() => _release.TrySetResult();

        public void Report(RlTrainingProgress progress) => _progress!.Report(progress);

        public string? MissingModule { get; set; }

        public Task<RlEnvironmentStatus> CheckEnvironmentAsync(CancellationToken cancellationToken)
            => Task.FromResult(EnvironmentAvailable ? RlEnvironmentStatus.Usable : RlEnvironmentStatus.Unusable(MissingModule));

        public Task<StoredPeriodProposal> ProposePeriodAsync(string symbol, int requiredBars, DateOnly? endBoundInclusive, CancellationToken cancellationToken)
        {
            ProposedBars = requiredBars;
            ProposedBound = endBoundInclusive;
            return Task.FromResult(Proposal);
        }

        public async Task<RlTrainingResult> RunAsync(RlTrainingJob job, string jobDirectory, IProgress<RlTrainingProgress> progress, CancellationToken cancellationToken)
        {
            Jobs.Add(job);
            Folders.Add(jobDirectory);
            _progress = progress;
            await Task.Yield();
            try
            {
                if (Behavior is { } behavior)
                {
                    return await behavior(jobDirectory, cancellationToken).ConfigureAwait(false);
                }

                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new OperationCanceledException(); // released: the default run just ends
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancelledByToken = true;
                throw;
            }
        }

        /// <summary>Writes a model and its evaluation report the way a finished smoke run leaves them (below the run folder of <paramref name="folder"/>).</summary>
        public static void WriteResult(string folder, out RlTrainingResult result)
        {
            string run = Path.Combine(folder, RlFiles.TrainingFolder, RlFiles.RlFolder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(run);
            string model = Path.Combine(run, RlFiles.Model);
            File.Copy(GoodModel, model);
            string evaluation = model + RlFiles.EvaluationSuffix;
            File.WriteAllText(evaluation,
                $"{{\"validation_mean_return\":\"0.0123\",\"candidate_seed\":42,\"checkpoint_step\":512,\"training_transitions\":{{\"42\":{RlContract.SmokeTrainingTransitions}}}}}");
            result = new RlTrainingResult(Guid.NewGuid(), run, model, evaluation, null, true, "source-revision");
        }
    }

    private sealed class NeverEndingOrchestrator : ITrainingOrchestrator
    {
        public Task<TrainingRunResult> StartTrainingAsync(TrainingJobConfig config, IProgress<TrainingProgress>? progress = null, CancellationToken ct = default)
            => Task.Delay(Timeout.Infinite, ct).ContinueWith<TrainingRunResult>(_ => throw new OperationCanceledException(ct), TaskScheduler.Default);
    }

    private sealed class FixedPython : IPythonExecutableResolver
    {
        public bool TryResolve(out string executablePath)
        {
            executablePath = "python.exe";
            return true;
        }
    }
}
