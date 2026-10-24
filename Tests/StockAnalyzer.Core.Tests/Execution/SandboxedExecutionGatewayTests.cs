#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Services;
using Xunit;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Execution;

/// <summary>
/// WP-2: the sandboxed execution gateway. The orchestrator is replaced by a scripted implementation of its interface (no Python), so the state
/// machine, the folders, the retention and the clean-up are tested exactly; one real run (embedded Python) closes the file.
/// </summary>
[Collection("ChildProcess")]
public sealed class SandboxedExecutionGatewayTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortGrace = TimeSpan.FromMilliseconds(300);

    private readonly string _root = Path.Combine(EphemeralSandbox.Root, "gwtest-" + Guid.NewGuid().ToString("N"));
    private readonly List<SandboxedExecutionGateway> _gateways = new();
    private readonly List<string> _extraRoots = new();

    public void Dispose()
    {
        foreach (SandboxedExecutionGateway gateway in _gateways)
        {
            gateway.Dispose();
        }

        TryDelete(_root);
        foreach (string extraRoot in _extraRoots)
        {
            TryDelete(extraRoot);
        }
    }

    // ---- AT-3.1 the transition table ----------------------------------------------------------------------------------------

    private static readonly Dictionary<(TrainingJobState, JobEvent), TrainingJobState> Expected = new()
    {
        [(TrainingJobState.Running, JobEvent.ProcessSucceeded)] = TrainingJobState.Validating,
        [(TrainingJobState.Running, JobEvent.ProcessFailed)] = TrainingJobState.Failed,
        [(TrainingJobState.Running, JobEvent.TimedOut)] = TrainingJobState.Failed,
        [(TrainingJobState.Running, JobEvent.CancelRequested)] = TrainingJobState.Cancelling,
        [(TrainingJobState.Cancelling, JobEvent.Stopped)] = TrainingJobState.Cancelled,
        [(TrainingJobState.Validating, JobEvent.OnnxLoaded)] = TrainingJobState.Succeeded,
        [(TrainingJobState.Validating, JobEvent.OnnxInvalid)] = TrainingJobState.Failed,
        [(TrainingJobState.Validating, JobEvent.CancelRequested)] = TrainingJobState.Cancelled,
        [(TrainingJobState.Succeeded, JobEvent.CancelRequested)] = TrainingJobState.Succeeded,
        [(TrainingJobState.Failed, JobEvent.CancelRequested)] = TrainingJobState.Failed,
        [(TrainingJobState.Cancelled, JobEvent.CancelRequested)] = TrainingJobState.Cancelled,
    };

    [Fact]
    public void Transitions_EveryPairIsEitherInTheTableOrABug()
    {
        foreach (TrainingJobState state in Enum.GetValues<TrainingJobState>())
        {
            foreach (JobEvent jobEvent in Enum.GetValues<JobEvent>())
            {
                if (Expected.TryGetValue((state, jobEvent), out TrainingJobState next))
                {
                    Assert.Equal(next, TrainingJobTransitions.Next(state, jobEvent));
                }
                else
                {
                    Assert.Throws<InvalidOperationException>(() => TrainingJobTransitions.Next(state, jobEvent));
                }
            }
        }
    }

    [Fact]
    public void Registry_AppliesTheTableUnderItsLockAndNeverExceedsTheConcurrencyLimit()
    {
        var registry = new TrainingJobRegistry();
        var first = new TrainingJobRecord("20260101-000000000-aaaaaaa1", Request(), DateTimeOffset.UtcNow);
        var second = new TrainingJobRecord("20260101-000000000-aaaaaaa2", Request(), DateTimeOffset.UtcNow);

        Assert.True(registry.TryAdd(first, maxConcurrent: 1));
        Assert.False(registry.TryAdd(second, maxConcurrent: 1));
        Assert.Null(registry.Find(second.JobId));
        Assert.Equal(TrainingJobState.Failed, registry.Apply(first, JobEvent.ProcessFailed, DateTimeOffset.UtcNow));
        Assert.Equal(TrainingFailureReason.TrainingFailed, first.FailureReason);
        Assert.NotNull(first.CompletedUtc);
        Assert.True(registry.TryAdd(second, maxConcurrent: 1));
    }

    [Fact]
    public void Registry_DropsTheOldestFinishedJobsBeyondTheTableSizeButNeverARunningOne()
    {
        var registry = new TrainingJobRegistry();
        var running = new TrainingJobRecord("20260101-000000000-00000000", Request(), DateTimeOffset.UtcNow.AddHours(-1));
        Assert.True(registry.TryAdd(running, maxConcurrent: 2));
        for (int i = 1; i <= ExecutionLimits.MaxTrackedJobs + 5; i++)
        {
            var finished = new TrainingJobRecord($"20260101-000000000-{i:x8}", Request(), DateTimeOffset.UtcNow.AddMinutes(i));
            Assert.True(registry.TryAdd(finished, maxConcurrent: 2));
            registry.Apply(finished, JobEvent.ProcessFailed, DateTimeOffset.UtcNow);
            if (i == 1)
            {
                registry.Apply(running, JobEvent.CancelRequested, DateTimeOffset.UtcNow); // still not final: Cancelling
            }
        }

        Assert.Equal(ExecutionLimits.MaxTrackedJobs, registry.Snapshot().Count);
        Assert.NotNull(registry.Find(running.JobId));
        Assert.Null(registry.Find("20260101-000000000-00000001"));
    }

    [Fact]
    public void Settings_AValueOutsideItsBandIsRefusedAtStartUp()
    {
        Assert.Throws<ExecutionLimitsException>(() => ExecutionLimits.Validate(new SandboxedExecutionSettings { KeepLatestRuns = 0 }));
        Assert.Throws<ExecutionLimitsException>(() => ExecutionLimits.Validate(new SandboxedExecutionSettings { KeepLatestRuns = 9 }));
        Assert.Throws<ExecutionLimitsException>(() => ExecutionLimits.Validate(new SandboxedExecutionSettings { OrphanSessionMaxAgeHours = 0 }));
        Assert.Throws<ExecutionLimitsException>(() => ExecutionLimits.Validate(new SandboxedExecutionSettings { OrphanSessionMaxAgeHours = 169 }));
        Assert.Throws<ExecutionLimitsException>(() => ExecutionLimits.Validate(new SandboxedExecutionSettings { MaxConcurrentJobs = 3 }));
        Assert.Throws<ExecutionLimitsException>(() => ExecutionLimits.Validate(new SandboxedExecutionSettings { StatusTailMessages = 101 }));
        ExecutionLimits.Validate(new SandboxedExecutionSettings());
        ExecutionLimits.Validate(new SandboxedExecutionSettings { KeepLatestRuns = 8, OrphanSessionMaxAgeHours = 168, MaxConcurrentJobs = 2, StatusTailMessages = 0 });
        Assert.Throws<ExecutionLimitsException>(() => new SandboxedExecutionGateway(new ScriptedOrchestrator(), new FixedPython(true), new SandboxedExecutionSettings { MaxConcurrentJobs = 0 }));
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)] // two jobs at once, one kept model: the first to finish would be deleted before anything could use it
    [InlineData(2, 2, true)]
    [InlineData(2, 1, true)]
    public void Settings_AtLeastAsManyModelsAreKeptAsJobsRunAtOnce(int keep, int concurrent, bool valid)
    {
        var settings = new SandboxedExecutionSettings { KeepLatestRuns = keep, MaxConcurrentJobs = concurrent };

        if (valid)
        {
            ExecutionLimits.Validate(settings);
            return;
        }

        var refused = Assert.Throws<ExecutionLimitsException>(() => ExecutionLimits.Validate(settings));
        Assert.Contains(nameof(SandboxedExecutionSettings.KeepLatestRuns), refused.Message);
        Assert.Contains(nameof(SandboxedExecutionSettings.MaxConcurrentJobs), refused.Message);
        Assert.Throws<ExecutionLimitsException>(() => new SandboxedExecutionGateway(new ScriptedOrchestrator(), new FixedPython(true), settings));
    }

    [Fact]
    public void Settings_TheDefaultsKeepTheRuleAndTheBandsAreUnchanged()
    {
        var defaults = new SandboxedExecutionSettings();

        Assert.True(defaults.KeepLatestRuns >= defaults.MaxConcurrentJobs);
        Assert.True(ExecutionLimits.MaxKeepLatestRuns >= ExecutionLimits.MaxConcurrentJobsAllowed); // the rule can always be met inside the bands
    }

    // ---- AT-3.2 behaviour ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_TheJobRunsWithTheGatewaysFixedValuesTheCallersRunIdAndAFolderInsideTheSandbox()
    {
        var orchestrator = new ScriptedOrchestrator { Run = (config, _, _) => Success(config) };
        SandboxedExecutionGateway gateway = Create(orchestrator);

        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Succeeded);

        TrainingJobConfig config = Assert.Single(orchestrator.Configs);
        Assert.Equal(TrainingJobState.Running, ticket.State);
        Assert.True(TrainingRunId.IsValid(ticket.JobId));
        Assert.Equal(ticket.JobId, config.RunId);
        Assert.Equal(Path.Combine(_root, SessionOf(config), ticket.JobId), config.ArtifactsDirectory);
        Assert.True(EphemeralSandbox.IsStrictlyInside(config.ArtifactsDirectory!));
        Assert.Equal(TrainingTimeframe.Daily, config.Timeframe);
        Assert.Equal(PredictionFeatureMode.OhlcvMinMax, config.FeatureMode);
        Assert.Equal(TrainingInitializationMode.Fresh, config.InitializationMode);
        Assert.Equal(TrainingEnsembleMethod.None, config.EnsembleMethod);
        Assert.False(config.FixedZScore);
        Assert.False(config.Calibration);
        Assert.False(config.Cpcv);
        Assert.Equal(new[] { "SYM" }, config.Symbols);
        config.Validate();
    }

    [Fact]
    public async Task Success_TheArtifactDescribesTheLoadedModelAndTheStatusCarriesTheRequestAndMetrics()
    {
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator { Run = (config, progress, _) => Success(config, progress) });
        EphemeralTrainingRequest request = Request();

        TrainingJobTicket ticket = await gateway.StartTrainingAsync(request, CancellationToken.None);
        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Succeeded);

        EphemeralArtifact artifact = Assert.IsType<EphemeralArtifact>(status.Artifact);
        Assert.Equal(ticket.JobId, artifact.ArtifactId);
        Assert.True(artifact.Loads);
        Assert.EndsWith(".onnx", artifact.OnnxFileName, StringComparison.Ordinal);
        Assert.Equal(artifact.OnnxFileName + ".metrics.json", artifact.MetricsFileName);
        Assert.True(artifact.SizeBytes > 0);
        Assert.Matches("^[0-9a-f]{64}$", artifact.Sha256);
        Assert.NotEmpty(artifact.InputShape);
        Assert.NotEmpty(artifact.OutputShape);
        Assert.Equal(100, status.ProgressPercent);
        Assert.Equal(0.75, status.Metrics!["accuracy"]);
        Assert.Same(request, status.Request);
        Assert.Null(status.FailureReason);
        Assert.Contains("Stage: train", status.Messages);
        Assert.Same(artifact, gateway.FindArtifact(ticket.JobId));
        Assert.Same(artifact, gateway.FindArtifact("latest"));
        Assert.True(File.Exists(Path.Combine(_root, SessionOfJobFolder(ticket.JobId), ticket.JobId, artifact.OnnxFileName)));
    }

    [Fact]
    public async Task Busy_ASecondStartIsRefusedAndCreatesNoJobOrFolder()
    {
        var release = new TaskCompletionSource<TrainingRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator { Run = (_, _, _) => release.Task });
        TrainingJobTicket first = await gateway.StartTrainingAsync(Request(), CancellationToken.None);

        await Assert.ThrowsAsync<ExecutionBusyException>(() => gateway.StartTrainingAsync(Request(), CancellationToken.None));

        Assert.Equal(new[] { first.JobId }, JobFolders());
        gateway.Cancel(first.JobId);
        release.TrySetCanceled();
        await WaitForAsync(gateway, first.JobId, TrainingJobState.Cancelled);
        TrainingJobTicket second = await gateway.StartTrainingAsync(Request(), CancellationToken.None); // the slot is free again
        Assert.NotEqual(first.JobId, second.JobId);
        gateway.Cancel(second.JobId);
    }

    [Fact]
    public async Task MaxConcurrentJobs_TwoRunAtOnceAndAThirdIsBusy()
    {
        var release = new TaskCompletionSource<TrainingRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator { Run = (_, _, _) => release.Task }, new SandboxedExecutionSettings { MaxConcurrentJobs = 2, KeepLatestRuns = 2 });

        await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        await gateway.StartTrainingAsync(Request(), CancellationToken.None);

        await Assert.ThrowsAsync<ExecutionBusyException>(() => gateway.StartTrainingAsync(Request(), CancellationToken.None));
        release.TrySetCanceled();
    }

    [Fact]
    public async Task Cancel_StopsTheProcessMovesThroughCancellingToCancelledAndRemovesTheFolder()
    {
        var started = new TaskCompletionSource();
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator
        {
            Run = async (config, _, token) =>
            {
                File.WriteAllText(Path.Combine(config.ArtifactsDirectory!, "partial.bin"), "x");
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token); // the trainer process dies when the token fires
                throw new InvalidOperationException("unreachable");
            },
        });
        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        await started.Task.WaitAsync(Patience);

        TrainingJobState returned = gateway.Cancel(ticket.JobId);
        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Cancelled);

        Assert.Equal(TrainingJobState.Cancelling, returned);
        Assert.Null(status.Artifact);
        await WaitUntilAsync(() => JobFolders().Length == 0);
        Assert.Equal(TrainingJobState.Cancelled, gateway.Cancel(ticket.JobId)); // a finished job stays as it is
    }

    [Fact]
    public async Task Cancel_ARunThatSucceedsWhileTheCancellationIsPendingIsACancelledJobWithNoModelAndNoFolder()
    {
        var finish = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator
        {
            Run = async (config, progress, _) =>
            {
                started.SetResult();
                await finish.Task; // the trainer ignores the cancellation and ends successfully
                return await Success(config, progress);
            },
        }, new SandboxedExecutionSettings(), root: null, grace: TimeSpan.FromMinutes(5));
        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        await started.Task.WaitAsync(Patience);

        Assert.Equal(TrainingJobState.Cancelling, gateway.Cancel(ticket.JobId));
        finish.SetResult();

        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Cancelled);
        Assert.Null(status.Artifact);
        Assert.Null(gateway.FindArtifact("latest"));
        await WaitUntilAsync(() => JobFolders().Length == 0);
    }

    [Fact]
    public async Task Cancel_AProcessThatDoesNotStopIsReportedCancelledAfterTheGracePeriod()
    {
        var never = new TaskCompletionSource<TrainingRunResult>();
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator { Run = (_, _, _) => never.Task });
        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);

        Assert.Equal(TrainingJobState.Cancelling, gateway.Cancel(ticket.JobId));

        await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Cancelled);
        never.TrySetCanceled();
    }

    [Fact]
    public async Task Failure_ATimeoutIsFailedWithTimeoutAndTheFolderIsGone()
    {
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator { Run = (_, _, _) => Task.FromException<TrainingRunResult>(new TimeoutException("Training exceeded 7200 seconds.")) });

        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Failed);

        Assert.Equal(TrainingFailureReason.Timeout, status.FailureReason);
        Assert.Contains("Training exceeded 7200 seconds.", status.Messages);
        await WaitUntilAsync(() => JobFolders().Length == 0);
    }

    [Fact]
    public async Task Failure_ATrainerThatFailsOrWritesNoModelIsTrainingFailedAndTheFolderIsGone()
    {
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator
        {
            Run = (config, _, _) => Task.FromResult(new TrainingRunResult { RunId = config.RunId!, Success = false, ExitCode = 3, Message = "Trainer exited with code 3." }),
        });

        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Failed);

        Assert.Equal(TrainingFailureReason.TrainingFailed, status.FailureReason);
        Assert.Contains("Trainer exited with code 3.", status.Messages);
        await WaitUntilAsync(() => JobFolders().Length == 0);
    }

    [Fact]
    public async Task Failure_AnErrorOfTheOrchestratorAfterItStartedIsTrainingFailedWithTheMessageCut()
    {
        string longMessage = new('x', 500);
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator { Run = async (_, _, _) => { await Task.Yield(); throw new IOException(longMessage); } });

        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Failed);

        Assert.Equal(TrainingFailureReason.TrainingFailed, status.FailureReason);
        Assert.All(status.Messages, message => Assert.True(message.Length <= ExecutionLimits.MaxStatusMessageChars));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("nometrics")]
    public async Task Failure_AFileThatIsNotALoadableModelOrHasNoMetricsIsInvalidOnnxAndTheFolderIsGone(string kind)
    {
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator
        {
            Run = (config, _, _) =>
            {
                string onnx = Path.Combine(config.ArtifactsDirectory!, "model.onnx");
                if (kind == "garbage")
                {
                    File.WriteAllText(onnx, "this is not onnx");
                    File.WriteAllText(onnx + ".metrics.json", "{}");
                }
                else
                {
                    File.Copy(GoodModel, onnx);
                }

                return Task.FromResult(new TrainingRunResult { RunId = config.RunId!, Success = true, OnnxArtifactPath = onnx });
            },
        });

        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Failed);

        Assert.Equal(TrainingFailureReason.InvalidOnnx, status.FailureReason);
        Assert.Null(status.Artifact);
        Assert.Null(gateway.FindArtifact(ticket.JobId));
        await WaitUntilAsync(() => JobFolders().Length == 0);
    }

    [Fact]
    public async Task Failure_AModelPathOutsideTheJobFolderIsNotTrusted()
    {
        string stray = Path.Combine(_root, "stray.onnx");
        Directory.CreateDirectory(_root);
        File.Copy(GoodModel, stray, overwrite: true);
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator
        {
            Run = (config, _, _) => Task.FromResult(new TrainingRunResult { RunId = config.RunId!, Success = true, OnnxArtifactPath = stray }),
        });

        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Failed);

        Assert.Equal(TrainingFailureReason.TrainingFailed, status.FailureReason);
    }

    // ---- refusals create no job ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Refusal_ARequestTheApplicationsRulesRefuseCreatesNoJobAndNamesTheMember()
    {
        var orchestrator = new ScriptedOrchestrator { Run = (config, _, _) => Success(config) };
        SandboxedExecutionGateway gateway = Create(orchestrator);

        var window = await Assert.ThrowsAsync<ExecutionRequestInvalidException>(() => gateway.StartTrainingAsync(Request() with { WindowSize = 0 }, CancellationToken.None));
        var splits = await Assert.ThrowsAsync<ExecutionRequestInvalidException>(() => gateway.StartTrainingAsync(Request() with { NSplits = 1 }, CancellationToken.None));
        var none = await Assert.ThrowsAsync<ExecutionRequestInvalidException>(() => gateway.StartTrainingAsync(Request() with { Symbols = Array.Empty<string>() }, CancellationToken.None));
        var twice = await Assert.ThrowsAsync<ExecutionRequestInvalidException>(() => gateway.StartTrainingAsync(Request() with { Symbols = new[] { "A", "a" } }, CancellationToken.None));
        var architecture = await Assert.ThrowsAsync<ExecutionRequestInvalidException>(() => gateway.StartTrainingAsync(Request() with { Architecture = "gru" }, CancellationToken.None));

        // Both kinds of failure the application's rules raise reach the caller as one exception type.
        Assert.Null(window.ParameterName); // TrainingJobConfig.Validate raises an InvalidOperationException for this member
        Assert.IsType<InvalidOperationException>(window.InnerException);
        Assert.IsType<InvalidOperationException>(splits.InnerException);
        Assert.Equal(nameof(EphemeralTrainingRequest.Symbols), none.ParameterName);
        Assert.Equal(nameof(EphemeralTrainingRequest.Symbols), twice.ParameterName);
        Assert.Equal(nameof(EphemeralTrainingRequest.Architecture), architecture.ParameterName);
        Assert.Empty(orchestrator.Configs);
        Assert.Empty(JobFolders());
    }

    [Fact]
    public async Task Refusal_ANullOrCancelledStartIsRefusedBeforeAnythingIsCreated()
    {
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator { Run = (config, _, _) => Success(config) });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<ArgumentNullException>(() => gateway.StartTrainingAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<OperationCanceledException>(() => gateway.StartTrainingAsync(Request(), cancelled.Token));
        Assert.Empty(JobFolders());
    }

    [Fact]
    public async Task Refusal_NoInterpreterIsPythonEnvironmentMissingAndCreatesNoJob()
    {
        var orchestrator = new ScriptedOrchestrator { Run = (config, _, _) => Success(config) };
        SandboxedExecutionGateway gateway = Create(orchestrator, python: new FixedPython(false));

        await Assert.ThrowsAsync<PythonEnvironmentMissingException>(() => gateway.StartTrainingAsync(Request(), CancellationToken.None));

        Assert.Empty(orchestrator.Configs);
        Assert.Empty(JobFolders());
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("argument")]
    [InlineData("operation")]
    [InlineData("missing")]
    public async Task Refusal_AnOrchestratorThatRefusesBeforeItStartsCreatesNoJobAndTheCodeMatches(string kind)
    {
        Exception refusal = kind switch
        {
            "busy" => new TrainingJobBusyException(),
            "argument" => new ArgumentException("bad", "WindowSize"),
            "operation" => new InvalidOperationException("bad"),
            _ => new FileNotFoundException("no python"),
        };
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator { Run = (_, _, _) => Task.FromException<TrainingRunResult>(refusal) });

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => gateway.StartTrainingAsync(Request(), CancellationToken.None));

        switch (kind)
        {
            case "busy": Assert.IsType<ExecutionBusyException>(thrown); break;
            case "missing": Assert.IsType<PythonEnvironmentMissingException>(thrown); break;
            case "argument": Assert.Equal("WindowSize", Assert.IsType<ExecutionRequestInvalidException>(thrown).ParameterName); break;
            default: Assert.Null(Assert.IsType<ExecutionRequestInvalidException>(thrown).ParameterName); break;
        }

        Assert.Empty(JobFolders());
        // the slot is not consumed by the refused attempt
        await Assert.ThrowsAnyAsync<Exception>(() => gateway.StartTrainingAsync(Request(), CancellationToken.None));
    }

    // ---- decision 3-A: the path budget is checked before anything is created -----------------------------------------------------

    [Fact]
    public void PathBudget_TheMeasuredValuesArePinned()
    {
        Assert.Equal(260, ExecutionLimits.PathCharsLimit);
        Assert.Equal(119, ExecutionLimits.SupervisedLongestRelativePathChars);
        Assert.Equal(82, ExecutionLimits.RlLongestRelativePathChars);
        Assert.Equal(139, JobFolderRoots.LongestJobFolderThatFits(ExecutionLimits.SupervisedLongestRelativePathChars)); // measured: 139 succeeded, 140 failed
        Assert.Equal(176, JobFolderRoots.LongestJobFolderThatFits(ExecutionLimits.RlLongestRelativePathChars)); // measured: 176 succeeded, 177 failed
    }

    [Fact]
    public async Task PathBudget_AJobFolderOfTheMeasuredLimitStarts()
    {
        string root = JobFolderRoots.WithJobFolderLength(JobFolderRoots.LongestJobFolderThatFits(ExecutionLimits.SupervisedLongestRelativePathChars));
        _extraRoots.Add(root);
        var orchestrator = new ScriptedOrchestrator { Run = (config, progress, _) => Success(config, progress) };
        SandboxedExecutionGateway gateway = Create(orchestrator, root: root);

        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Succeeded);

        TrainingJobConfig config = Assert.Single(orchestrator.Configs);
        Assert.Equal(JobFolderRoots.LongestJobFolderThatFits(ExecutionLimits.SupervisedLongestRelativePathChars), config.ArtifactsDirectory!.Length);
    }

    [Fact]
    public async Task PathBudget_OneCharacterMoreIsRefusedBeforeAnythingIsCreated()
    {
        string root = JobFolderRoots.WithJobFolderLength(JobFolderRoots.LongestJobFolderThatFits(ExecutionLimits.SupervisedLongestRelativePathChars) + 1);
        _extraRoots.Add(root);
        var orchestrator = new ScriptedOrchestrator { Run = (config, progress, _) => Success(config, progress) };
        SandboxedExecutionGateway gateway = Create(orchestrator, root: root);

        var refused = await Assert.ThrowsAsync<ExecutionPathTooLongException>(() => gateway.StartTrainingAsync(Request(), CancellationToken.None));

        Assert.Equal(ExecutionLimits.PathCharsLimit, refused.RequiredChars);
        Assert.Empty(orchestrator.Configs);
        Assert.False(Directory.Exists(root), "a refused start must not create the sandbox");
        // the refusal consumes no job slot
        Assert.Throws<JobNotFoundException>(() => gateway.GetStatus("20260101-000000000-00000000"));
    }

    [Fact]
    public async Task PathBudget_AnInvalidRequestIsStillReportedAsInvalidWhateverTheFolderLength()
    {
        string root = JobFolderRoots.WithJobFolderLength(JobFolderRoots.LongestJobFolderThatFits(ExecutionLimits.SupervisedLongestRelativePathChars) + 30);
        _extraRoots.Add(root);
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator(), root: root);

        await Assert.ThrowsAsync<ExecutionRequestInvalidException>(() => gateway.StartTrainingAsync(Request() with { Symbols = Array.Empty<string>() }, CancellationToken.None));
    }

    [Fact]
    public async Task Lookup_AnUnknownJobOrArtifactIsReportedWithoutCreatingAnything()
    {
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator());

        Assert.Throws<JobNotFoundException>(() => gateway.GetStatus("20260101-000000000-00000000"));
        Assert.Throws<JobNotFoundException>(() => gateway.Cancel("20260101-000000000-00000000"));
        Assert.Null(gateway.FindArtifact("20260101-000000000-00000000"));
        Assert.Null(gateway.FindArtifact("latest"));
        Assert.Throws<ArtifactNotFoundException>(() => gateway.LeaseArtifact("20260101-000000000-00000000"));
        Assert.Throws<ArtifactNotFoundException>(() => gateway.LeaseArtifact("latest"));
        await Task.CompletedTask;
        Assert.False(Directory.Exists(_root), "an idle gateway must not create the sandbox");
    }

    [Fact]
    public async Task Status_KeepsOnlyTheLatestMessagesEachCutToTheLimit()
    {
        var settings = new SandboxedExecutionSettings { StatusTailMessages = 3 };
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator
        {
            Run = (config, progress, _) =>
            {
                for (int i = 0; i < 10; i++)
                {
                    progress!.Report(new TrainingProgress { Stage = "stage-" + i + new string('y', 100), Percent = i * 10 });
                }

                return Success(config, progress);
            },
        }, settings);

        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Succeeded);

        Assert.Equal(3, status.Messages.Count);
        Assert.All(status.Messages, message => Assert.True(message.Length <= ExecutionLimits.MaxStatusMessageChars));
        Assert.True(status.Stage!.Length <= ExecutionLimits.MaxStageChars);
    }

    [Fact]
    public async Task Status_ZeroTailMessagesKeepsNone()
    {
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator { Run = (config, progress, _) => Success(config, progress) }, new SandboxedExecutionSettings { StatusTailMessages = 0 });

        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Succeeded);

        Assert.Empty(status.Messages);
    }

    // ---- AT-3.3 retention and leases -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retention_OnlyTheNewestSuccessfulJobsKeepTheirFiles()
    {
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator { Run = (config, progress, _) => Success(config, progress) }, new SandboxedExecutionSettings { KeepLatestRuns = 2 });
        var ids = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
            await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Succeeded);
            ids.Add(ticket.JobId);
            await Task.Delay(5); // distinct completion times
        }

        await WaitUntilAsync(() => JobFolders().Length == 2);
        Assert.Equal(ids.Skip(2).OrderBy(id => id, StringComparer.Ordinal), JobFolders().OrderBy(id => id, StringComparer.Ordinal));
        Assert.Null(gateway.FindArtifact(ids[0]));
        Assert.Null(gateway.FindArtifact(ids[1]));
        Assert.NotNull(gateway.FindArtifact(ids[2]));
        Assert.Equal(ids[3], gateway.FindArtifact("latest")!.ArtifactId);
        Assert.Equal(TrainingJobState.Succeeded, gateway.GetStatus(ids[0]).State);
        Assert.Null(gateway.GetStatus(ids[0]).Artifact);
    }

    [Fact]
    public async Task Retention_ALeasedArtifactSurvivesUntilTheLeaseIsReleased()
    {
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator { Run = (config, progress, _) => Success(config, progress) }, new SandboxedExecutionSettings { KeepLatestRuns = 1 });
        TrainingJobTicket first = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        await WaitForAsync(gateway, first.JobId, TrainingJobState.Succeeded);
        ArtifactLease lease = gateway.LeaseArtifact(first.JobId);
        Assert.True(File.Exists(lease.OnnxPath));
        await Task.Delay(5);

        TrainingJobTicket second = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        await WaitForAsync(gateway, second.JobId, TrainingJobState.Succeeded);

        Assert.Contains(first.JobId, JobFolders());
        Assert.True(File.Exists(lease.OnnxPath));
        lease.Dispose();
        await WaitUntilAsync(() => !JobFolders().Contains(first.JobId));
        Assert.Null(gateway.FindArtifact(first.JobId));
        lease.Dispose(); // twice is harmless
    }

    [Fact]
    public async Task Retention_ARunningJobsFolderIsNeverDeleted()
    {
        var release = new TaskCompletionSource<TrainingRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator
        {
            Run = (config, progress, _) => Interlocked.Increment(ref calls) == 1 ? Success(config, progress) : release.Task,
        }, new SandboxedExecutionSettings { KeepLatestRuns = 2, MaxConcurrentJobs = 2 });
        TrainingJobTicket done = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        await WaitForAsync(gateway, done.JobId, TrainingJobState.Succeeded);
        TrainingJobTicket running = await gateway.StartTrainingAsync(Request(), CancellationToken.None);

        Assert.Contains(running.JobId, JobFolders());
        Assert.Contains(done.JobId, JobFolders());
        gateway.Cancel(running.JobId);
        release.TrySetCanceled();
    }

    // ---- AT-3.4 / AT-3.5 clean-up ---------------------------------------------------------------------------------------------------

    [Fact]
    public void StartUpCleanUp_DeletesOnlyOldUnheldSessionsAndNeverAHeldOne()
    {
        string oldUnheld = MakeSession("old-unheld", hoursOld: 25, withLock: true, hold: false);
        string oldNoLock = MakeSession("old-nolock", hoursOld: 25, withLock: false, hold: false);
        string recent = MakeSession("recent", hoursOld: 1, withLock: true, hold: false);
        string oldHeld = MakeSession("old-held", hoursOld: 25, withLock: true, hold: true, out FileStream? heldLock);
        using (heldLock)
        {
            Create(new ScriptedOrchestrator());

            Assert.False(Directory.Exists(oldUnheld));
            Assert.False(Directory.Exists(oldNoLock));
            Assert.True(Directory.Exists(recent));
            Assert.True(Directory.Exists(oldHeld));
        }
    }

    [Fact]
    public void StartUpCleanUp_TheAgeLimitIsTheConfiguredNumberOfHours()
    {
        string session = MakeSession("middle-aged", hoursOld: 5, withLock: true, hold: false);

        Create(new ScriptedOrchestrator(), new SandboxedExecutionSettings { OrphanSessionMaxAgeHours = 6 });
        Assert.True(Directory.Exists(session));

        Create(new ScriptedOrchestrator(), new SandboxedExecutionSettings { OrphanSessionMaxAgeHours = 4 });
        Assert.False(Directory.Exists(session));
    }

    [Fact]
    public async Task Dispose_StopsRunningJobsAndDeletesTheSessionFolder()
    {
        var started = new TaskCompletionSource();
        SandboxedExecutionGateway gateway = Create(new ScriptedOrchestrator
        {
            Run = async (_, _, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
        });
        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        await started.Task.WaitAsync(Patience);
        string session = SessionOfJobFolder(ticket.JobId);
        Assert.True(Directory.Exists(Path.Combine(_root, session)));

        gateway.Dispose();

        Assert.False(Directory.Exists(Path.Combine(_root, session)));
        Assert.Throws<ObjectDisposedException>(() => gateway.StartTrainingAsync(Request(), CancellationToken.None).GetAwaiter().GetResult());
        gateway.Dispose(); // twice is harmless
    }

    // ---- AT-3.6 only the sandbox is written -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Writes_EveryPathHandedToTheOrchestratorIsInsideTheSandboxAndTheConfigIsEphemeral()
    {
        var orchestrator = new ScriptedOrchestrator { Run = (config, progress, _) => Success(config, progress) };
        SandboxedExecutionGateway gateway = Create(orchestrator);

        TrainingJobTicket ticket = await gateway.StartTrainingAsync(Request(), CancellationToken.None);
        await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Succeeded);

        TrainingJobConfig config = Assert.Single(orchestrator.Configs);
        Assert.True(EphemeralSandbox.IsStrictlyInside(config.ArtifactsDirectory!));
        Assert.Equal(EphemeralTrainingRequest.OutputStem, config.OutputName);
        Assert.Null(config.CheckpointPath);
        Assert.Null(config.SourceManifestPath);
        Assert.Null(config.PreparedInputDir);
        Assert.StartsWith(_root, config.ArtifactsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    // ---- DI -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Registration_AMisconfiguredSettingIsRefusedAtRegistrationAndNothingIsCreatedUntilAskedFor()
    {
        var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AiIntegration:Execution:KeepLatestRuns"] = "99" }).Build();
        var good = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AiIntegration:Execution:KeepLatestRuns"] = "3" }).Build();

        Assert.Throws<ExecutionLimitsException>(() => { new ServiceCollection().AddStockAnalyzerSandboxedExecution(bad); });

        IServiceCollection services = new ServiceCollection().AddStockAnalyzerSandboxedExecution(good);
        Assert.Contains(services, d => d.ServiceType == typeof(ISandboxedExecutionGateway));
        var settings = Assert.IsType<SandboxedExecutionSettings>(services.Single(d => d.ServiceType == typeof(SandboxedExecutionSettings)).ImplementationInstance);
        Assert.Equal(3, settings.KeepLatestRuns);
        Assert.All(services.Where(d => d.ServiceType == typeof(ISandboxedExecutionGateway)), d => Assert.Null(d.ImplementationInstance)); // a factory: nothing is created until asked for
    }

    // ---- AT-3.8 one real run ---------------------------------------------------------------------------------------------------------------

    [EmbeddedPythonFact]
    public async Task RealRun_ALightGbmJobSucceedsLoadsAndLeavesTheApplicationsDataFoldersUntouched()
    {
        string python = PythonEmbeddedInstaller.ExecutablePath(PythonEmbeddedDistribution.PythonExecutableName);
        if (await ModuleInstallProbe.RunAsync(python, "-c \"import lightgbm, polars, numpy\"", TimeSpan.FromMinutes(2)) != ModuleInstallState.Installed)
        {
            throw SkipException.ForSkip("The embedded Python cannot import lightgbm, polars and numpy.");
        }

        string? symbol = FirstStoredDailySymbol();
        if (symbol is null)
        {
            throw SkipException.ForSkip("The application's Data/Daily folder holds no daily file to train on.");
        }

        string[] watched = { "TrainingArtifacts", "Experiments", "Models" };
        string dataRoot = PathDiscovery.ResolveDataPathWithoutCreating(null, "Data");
        IReadOnlyList<string> before = ListFiles(dataRoot, watched);
        var orchestrator = new TrainingOrchestrator(
            UnavailablePythonService.Instance,
            new IndicatorChannelExporter(new ReadOnlyMetadataMarketDataProvider(new EmptyReadOnlyMarketDataSource()), StockAnalyzer.Core.Models.Indicators.IndicatorFactory.Default),
            executableResolver: new EmbeddedPythonExecutableResolver());
        SandboxedExecutionGateway gateway = Create(orchestrator, python: new EmbeddedPythonExecutableResolver());
        string[] hyperparameters = TrainingHyperparameterCatalog.For(TrainingFramework.LightGBM, TrainingEnsembleContract.LightGbmArchitecture)
            .Where(d => d.Domain is { Kind: HyperparameterKind.Integer, Minimum: not null } && d.Key.Contains("estimators", StringComparison.Ordinal))
            .Select(d => d.Key).ToArray();

        TrainingJobTicket ticket = await gateway.StartTrainingAsync(
            new EphemeralTrainingRequest
            {
                Symbols = new[] { symbol },
                Framework = TrainingFramework.LightGBM,
                Architecture = TrainingArchitectureCatalog.DefaultFor(TrainingFramework.LightGBM),
                WindowSize = 10,
                Horizon = 1,
                NSplits = 2,
                Hyperparameters = hyperparameters.ToDictionary(key => key, _ => "5"),
            },
            CancellationToken.None);
        TrainingJobStatus status = await WaitForAsync(gateway, ticket.JobId, TrainingJobState.Succeeded, TimeSpan.FromMinutes(6));

        Assert.True(status.Artifact!.Loads);
        Assert.Equal(EphemeralTrainingRequest.OutputStem + ".onnx", status.Artifact.OnnxFileName);
        Assert.NotEmpty(status.Artifact.InputShape);
        Assert.NotEmpty(status.Artifact.OutputShape);
        Assert.Equal(before, ListFiles(dataRoot, watched));

        // The path budget rests on the measured longest relative path (ExecutionLimits.SupervisedLongestRelativePathChars): no file of a real run may be longer.
        string jobFolder = Path.Combine(_root, SessionOfJobFolder(ticket.JobId), ticket.JobId);
        int longest = Directory.EnumerateFileSystemEntries(jobFolder, "*", SearchOption.AllDirectories).Max(entry => Path.GetRelativePath(jobFolder, entry).Length);
        Assert.InRange(longest, 1, ExecutionLimits.SupervisedLongestRelativePathChars);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------------------------

    private static string GoodModel => Path.Combine(AppContext.BaseDirectory, "Assets", "trend_predictor_goodmeta.onnx");

    private static EphemeralTrainingRequest Request() => new()
    {
        Symbols = new[] { "SYM" },
        Framework = TrainingFramework.LightGBM,
        Architecture = TrainingEnsembleContract.LightGbmArchitecture,
        WindowSize = 10,
        Horizon = 1,
    };

    private SandboxedExecutionGateway Create(ITrainingOrchestrator orchestrator, SandboxedExecutionSettings? settings = null, IPythonExecutableResolver? python = null, string? root = null, TimeSpan? grace = null)
    {
        var gateway = new SandboxedExecutionGateway(orchestrator, python ?? new FixedPython(true), settings ?? new SandboxedExecutionSettings(), null, root ?? _root, grace ?? ShortGrace);
        _gateways.Add(gateway);
        return gateway;
    }

    /// <summary>A model the way the trainer leaves it: the ONNX file and its metrics file in the job folder, progress reported on the way.</summary>
    private static Task<TrainingRunResult> Success(TrainingJobConfig config, IProgress<TrainingProgress>? progress = null)
    {
        progress?.Report(new TrainingProgress { Stage = "train", Percent = 50 });
        string onnx = Path.Combine(config.ArtifactsDirectory!, "model_" + config.RunId + ".onnx");
        File.Copy(GoodModel, onnx);
        File.WriteAllText(onnx + ".metrics.json", "{}");
        progress?.Report(new TrainingProgress { Stage = "done", Percent = 100, Metric = new Dictionary<string, double> { ["accuracy"] = 0.75 } });
        return Task.FromResult(new TrainingRunResult
        {
            RunId = config.RunId!,
            Success = true,
            OnnxArtifactPath = onnx,
            MetricsArtifactPath = onnx + ".metrics.json",
            Metrics = new Dictionary<string, double> { ["accuracy"] = 0.75 },
        });
    }

    private string[] JobFolders()
        => Directory.Exists(_root)
            ? Directory.EnumerateDirectories(_root).SelectMany(session => Directory.EnumerateDirectories(session)).Select(Path.GetFileName).OfType<string>().ToArray()
            : Array.Empty<string>();

    private string SessionOfJobFolder(string jobId)
        => Path.GetFileName(Directory.EnumerateDirectories(_root).Single(session => Directory.Exists(Path.Combine(session, jobId))))!;

    private string SessionOf(TrainingJobConfig config) => Path.GetFileName(Path.GetDirectoryName(config.ArtifactsDirectory!))!;

    private static async Task<TrainingJobStatus> WaitForAsync(SandboxedExecutionGateway gateway, string jobId, TrainingJobState state, TimeSpan? patience = null)
    {
        DateTime limit = DateTime.UtcNow + (patience ?? Patience);
        TrainingJobStatus status;
        while ((status = gateway.GetStatus(jobId)).State != state)
        {
            bool endedElsewhere = status.State is TrainingJobState.Succeeded or TrainingJobState.Failed or TrainingJobState.Cancelled;
            Assert.False(endedElsewhere, $"job {jobId} ended as {status.State} ({status.FailureReason}), expected {state}: {string.Join(" / ", status.Messages)}");
            Assert.True(DateTime.UtcNow < limit, $"job {jobId} is {status.State}, expected {state}");
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

    private string MakeSession(string name, int hoursOld, bool withLock, bool hold) => MakeSession(name, hoursOld, withLock, hold, out _);

    private string MakeSession(string name, int hoursOld, bool withLock, bool hold, out FileStream? heldLock)
    {
        string session = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(session, "20260101-000000000-00000000"));
        heldLock = null;
        if (withLock)
        {
            string lockPath = Path.Combine(session, EphemeralArtifactStore.SessionLockFileName);
            if (hold)
            {
                heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            else
            {
                File.WriteAllText(lockPath, string.Empty);
            }
        }

        Directory.SetLastWriteTimeUtc(session, DateTime.UtcNow.AddHours(-hoursOld));
        return session;
    }

    private static string? FirstStoredDailySymbol()
    {
        string daily = PathDiscovery.ResolveDataPathWithoutCreating(null, "Data");
        daily = Path.Combine(daily, "Daily");
        return Directory.Exists(daily)
            ? Directory.EnumerateFiles(daily, "*.parquet").OrderBy(f => f, StringComparer.Ordinal).Select(Path.GetFileNameWithoutExtension).FirstOrDefault()
            : null;
    }

    private static IReadOnlyList<string> ListFiles(string dataRoot, IEnumerable<string> folders)
    {
        var entries = new List<string>();
        foreach (string folder in folders)
        {
            string path = Path.Combine(dataRoot, folder);
            if (!Directory.Exists(path))
            {
                entries.Add($"<missing>|{folder}");
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                entries.Add($"{Path.GetRelativePath(dataRoot, file)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // best-effort: the OS temp cleaner removes the rest
        }
    }

    /// <summary>The orchestrator's interface with a scripted behaviour; it records the configurations it was given.</summary>
    private sealed class ScriptedOrchestrator : ITrainingOrchestrator
    {
        public List<TrainingJobConfig> Configs { get; } = new();

        public Func<TrainingJobConfig, IProgress<TrainingProgress>?, CancellationToken, Task<TrainingRunResult>> Run { get; set; }
            = (_, _, _) => Task.FromException<TrainingRunResult>(new InvalidOperationException("no script"));

        public Task<TrainingRunResult> StartTrainingAsync(TrainingJobConfig config, IProgress<TrainingProgress>? progress = null, CancellationToken ct = default)
        {
            lock (Configs)
            {
                Configs.Add(config);
            }

            return Run(config, progress, ct);
        }
    }

    private sealed class FixedPython(bool installed) : IPythonExecutableResolver
    {
        public bool TryResolve(out string executablePath)
        {
            executablePath = installed ? "python.exe" : string.Empty;
            return installed;
        }
    }

}
