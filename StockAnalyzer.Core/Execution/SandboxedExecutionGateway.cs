using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Core.Execution;

/// <summary>
/// The default <see cref="ISandboxedExecutionGateway"/>. A job is a normal training run of <see cref="ITrainingOrchestrator"/> whose configuration
/// names a folder of the ephemeral sandbox (<see cref="TrainingJobConfig.ArtifactsDirectory"/>) and whose run id is the job id. Nothing is registered
/// and no experiment log is written (those are called by the application's windows, never by the orchestrator). The ranges of a request are the
/// application's own rules (<see cref="TrainingJobConfig.Validate"/>, <see cref="TrainingResourceGuard"/>); this class adds none.
/// </summary>
public sealed class SandboxedExecutionGateway : ISandboxedExecutionGateway, IDisposable
{
    private const string MetricsFileSuffix = ".metrics.json";
    private const string LatestAlias = "latest";

    private readonly ITrainingOrchestrator _orchestrator;
    private readonly IPythonExecutableResolver _resolver;
    private readonly SandboxedExecutionSettings _settings;
    private readonly ILogger _logger;
    private readonly TimeSpan _cancelGrace;
    private readonly EphemeralArtifactStore _store;
    private readonly IEphemeralRlRunner? _rlRunner;
    private readonly EphemeralExecutionPolicy _policy;
    private readonly IModelGenerationRegistry _modelRegistry;
    private readonly IEphemeralPortfolioRlRunner? _portfolioRunner;
    private readonly TrainingJobRegistry _jobs = new();
    private bool _disposed;

    /// <param name="marketData">The configured market-data folders the reinforcement-learning runs read from; null leaves reinforcement learning out.</param>
    /// <param name="portfolioSettings">The already validated "RlPortfolio" settings the portfolio runs use; null keeps the defaults.</param>
    public SandboxedExecutionGateway(
        ITrainingOrchestrator orchestrator,
        IPythonExecutableResolver resolver,
        SandboxedExecutionSettings settings,
        ILogger<SandboxedExecutionGateway>? logger = null,
        MarketDataSettings? marketData = null,
        EphemeralExecutionPolicy? policy = null,
        IModelGenerationRegistry? modelRegistry = null,
        RlPortfolioSettings? portfolioSettings = null)
        : this(orchestrator, resolver, settings, logger, EphemeralSandbox.Root, TimeSpan.FromSeconds(ExecutionLimits.CancelGraceSeconds),
            SourceFoldersOf(marketData, out var folders) ? new EphemeralRlRunner(folders) : null, policy, modelRegistry,
            new EphemeralPortfolioRlRunner(portfolioSettings, folders))
    {
    }

    /// <summary>The market-data folders the RL runners read from, resolved once for both runners; none when no market-data settings are given.</summary>
    private static bool SourceFoldersOf(MarketDataSettings? marketData, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RlLocalSourceFolders? folders)
    {
        folders = marketData is null ? null : RlLocalSourceFolders.FromSettings(marketData);
        return folders is not null;
    }

    /// <param name="sandboxRoot">The sandbox root; only a test uses another folder.</param>
    /// <param name="cancelGrace">How long a cancelled job may take to stop before it is reported as cancelled anyway.</param>
    /// <param name="rlRunner">The reinforcement-learning side; null leaves reinforcement learning out.</param>
    internal SandboxedExecutionGateway(
        ITrainingOrchestrator orchestrator,
        IPythonExecutableResolver resolver,
        SandboxedExecutionSettings settings,
        ILogger<SandboxedExecutionGateway>? logger,
        string sandboxRoot,
        TimeSpan cancelGrace,
        IEphemeralRlRunner? rlRunner = null,
        EphemeralExecutionPolicy? policy = null,
        IModelGenerationRegistry? modelRegistry = null,
        IEphemeralPortfolioRlRunner? portfolioRunner = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(resolver);
        ExecutionLimits.Validate(settings);
        _orchestrator = orchestrator;
        _resolver = resolver;
        _settings = settings;
        _rlRunner = rlRunner;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _cancelGrace = cancelGrace;
        _store = new EphemeralArtifactStore(sandboxRoot, settings, _logger);
        _policy = policy ?? EphemeralExecutionPolicy.McpFixed;
        _modelRegistry = modelRegistry ?? new ReadOnlyModelGenerationRegistry();
        _portfolioRunner = portfolioRunner ?? new EphemeralPortfolioRlRunner();
        int cleaned = _store.CleanOrphanSessions(DateTime.UtcNow);
        _logger.LogInformation("Sandboxed execution enabled; {Cleaned} abandoned session(s) removed.", cleaned);
    }

    public Task<TrainingJobTicket> StartTrainingAsync(EphemeralTrainingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateRequest(request);
        string firstJobId = TrainingRunId.New(DateTimeOffset.UtcNow);
        ValidateConfig(request.ToJobConfig(firstJobId, _store.JobDirectory(firstJobId)));
        if (!_resolver.TryResolve(out _))
        {
            throw new PythonEnvironmentMissingException();
        }

        RequireJobPathsFit(firstJobId, ExecutionLimits.SupervisedLongestRelativePathChars);
        _store.EnsureSession();
        string jobId = firstJobId;
        for (int attempt = 1; !_store.TryCreateJobDirectory(jobId); attempt++)
        {
            if (attempt >= ExecutionLimits.JobIdAttempts)
            {
                throw new IOException("A free job folder could not be found.");
            }

            jobId = TrainingRunId.New(DateTimeOffset.UtcNow);
        }

        var job = new TrainingJobRecord(jobId, request, DateTimeOffset.UtcNow);
        if (!_jobs.TryAdd(job, _settings.MaxConcurrentJobs))
        {
            _store.TryDeleteJobDirectory(jobId);
            throw new ExecutionBusyException();
        }

        TrainingJobConfig config = request.ToJobConfig(jobId, _store.JobDirectory(jobId));
        Task<TrainingRunResult> training = _orchestrator.StartTrainingAsync(config, new InlineProgress(update => OnProgress(job, update)), job.Cancellation.Token);
        if (training.IsFaulted && training.Exception!.GetBaseException() is TrainingJobBusyException or ArgumentException or InvalidOperationException or FileNotFoundException)
        {
            // The orchestrator refused before it started anything (its own rules, the job gate, no interpreter): no job exists.
            Exception refusal = training.Exception!.GetBaseException();
            _jobs.Forget(job);
            _store.TryDeleteJobDirectory(jobId);
            throw Refusal(refusal);
        }

        _logger.LogInformation("Training job {JobId} started.", jobId);
        job.Run = RunAsync(job, training);
        return Task.FromResult(new TrainingJobTicket(jobId, TrainingJobState.Running));
    }

    public async Task<TrainingJobTicket> StartRlTrainingAsync(EphemeralRlRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        IEphemeralRlRunner runner = _rlRunner ?? throw new NotSupportedException("Reinforcement learning is not configured for this gateway.");

        RlTrainingJob rlJob = await BuildRlJobAsync(request, runner, cancellationToken).ConfigureAwait(false);
        RlEnvironmentStatus environment = await runner.CheckEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        if (!environment.Available)
        {
            throw new RlEnvironmentMissingException(environment.MissingModule);
        }

        string jobId = TrainingRunId.New(DateTimeOffset.UtcNow);
        RequireJobPathsFit(jobId, ExecutionLimits.RlLongestRelativePathChars);
        _store.EnsureSession();
        for (int attempt = 1; !_store.TryCreateJobDirectory(jobId); attempt++)
        {
            if (attempt >= ExecutionLimits.JobIdAttempts)
            {
                throw new IOException("A free job folder could not be found.");
            }

            jobId = TrainingRunId.New(DateTimeOffset.UtcNow);
        }

        var job = new TrainingJobRecord(
            jobId,
            new EphemeralRlRequest { Symbol = rlJob.Symbol, StartDate = rlJob.StartDate, EndDateExclusive = rlJob.EndDateExclusive, RetainArtifact = request.RetainArtifact },
            DateTimeOffset.UtcNow);
        if (!_jobs.TryAdd(job, _settings.MaxConcurrentJobs))
        {
            _store.TryDeleteJobDirectory(jobId);
            throw new ExecutionBusyException();
        }

        Task<RlTrainingResult> run;
        try
        {
            run = runner.RunAsync(rlJob, _store.JobDirectory(jobId), new InlineRlProgress(update => OnRlProgress(job, update)), job.Cancellation.Token);
        }
        catch (Exception error)
        {
            run = Task.FromException<RlTrainingResult>(error); // the job exists already: the failure is the job's, not the request's
        }

        _logger.LogInformation("Reinforcement-learning job {JobId} started.", jobId);
        job.Run = RunRlAsync(job, run);
        return new TrainingJobTicket(jobId, TrainingJobState.Running);
    }

    public async Task<TrainingJobTicket> StartPortfolioRlTrainingAsync(EphemeralPortfolioRlRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        IEphemeralPortfolioRlRunner runner = _portfolioRunner ?? throw new NotSupportedException("Reinforcement learning portfolio is not configured for this gateway.");

        if (request.Symbols is null || request.Symbols.Count == 0)
        {
            throw Invalid("At least one symbol is required.", nameof(request.Symbols));
        }

        if (request.Symbols.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Symbols.Count)
        {
            throw Invalid("A symbol is listed twice.", nameof(request.Symbols));
        }

        if (request.Symbols.Count < RlPortfolioContract.MinimumSymbols)
        {
            throw Invalid($"At least {RlPortfolioContract.MinimumSymbols} symbols are required.", nameof(request.Symbols));
        }

        if (request.Symbols.Count > ExecutionLimits.MaxPortfolioSymbols)
        {
            throw Invalid($"At most {ExecutionLimits.MaxPortfolioSymbols} symbols are supported.", nameof(request.Symbols));
        }

        if (request.StartDate is not null && request.EndDateExclusive is not null && request.StartDate >= request.EndDateExclusive)
        {
            throw Invalid("The start date must be before the end date.", nameof(request.StartDate));
        }

        if (request.MaxAssetWeightPercent is < 1 or > 100)
        {
            throw Invalid("The maximum asset weight percent must be between 1 and 100.", nameof(request.MaxAssetWeightPercent));
        }

        RlEnvironmentStatus environment = await runner.CheckEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        if (!environment.Available)
        {
            throw new RlEnvironmentMissingException(environment.MissingModule);
        }

        DateOnly? start = request.StartDate;
        DateOnly end = request.EndDateExclusive ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (start is null)
        {
            RlStartSuggestion? suggestion = await runner.SuggestStartAsync(request.Symbols, end, request.Mode, cancellationToken).ConfigureAwait(false);
            if (suggestion is null || suggestion.Status == RlStartSuggestionStatus.NoUsableData || suggestion.Start is null)
            {
                throw new InsufficientStoredDataException(suggestion?.RequiredBars ?? 0, 0);
            }

            if (suggestion.Status == RlStartSuggestionStatus.InsufficientHistory)
            {
                throw new InsufficientStoredDataException(suggestion.RequiredBars, suggestion.AvailableBars);
            }

            start = suggestion.Start;
        }

        PanelPreflight preflight = await runner.PreflightAsync(request.Symbols, start.Value, end, request.SymbolHoldout, request.Mode, cancellationToken).ConfigureAwait(false);
        // The configured "RlPortfolio" settings (symbol maximum, holdout share) gate the start as in the GUI; only the weight cap is this request's own.
        RlPortfolioSettings settings = runner.Settings.WithRunOverrides(null, request.MaxAssetWeightPercent);
        var decision = RlPanelStartGate.Decide(preflight, excludeUnavailable: true, request.SymbolHoldout, settings);
        if (!decision.CanStart)
        {
            throw Invalid($"Portfolio training cannot start: {decision.Blocker}", nameof(request.Symbols));
        }

        string jobId = TrainingRunId.New(DateTimeOffset.UtcNow);
        RequireJobPathsFit(jobId, ExecutionLimits.RlLongestRelativePathChars);
        _store.EnsureSession();
        for (int attempt = 1; !_store.TryCreateJobDirectory(jobId); attempt++)
        {
            if (attempt >= ExecutionLimits.JobIdAttempts)
            {
                throw new IOException("A free job folder could not be found.");
            }

            jobId = TrainingRunId.New(DateTimeOffset.UtcNow);
        }

        var actualRequest = request with
        {
            StartDate = start,
            EndDateExclusive = end,
            Symbols = decision.TrainingSymbols.ToList(),
        };

        var job = new TrainingJobRecord(jobId, actualRequest, DateTimeOffset.UtcNow);
        if (!_jobs.TryAdd(job, _settings.MaxConcurrentJobs))
        {
            _store.TryDeleteJobDirectory(jobId);
            throw new ExecutionBusyException();
        }

        var runRequest = new RlPortfolioRunRequest(
            decision.TrainingSymbols,
            start.Value,
            end,
            decision.SymbolHoldout,
            request.Mode,
            FactoryCosts(),
            SmokeOnly: true,
            MaxWeightPercent: request.MaxAssetWeightPercent);

        Task<RlPortfolioTrainingResult> run;
        try
        {
            run = runner.RunAsync(runRequest, _store.JobDirectory(jobId), new InlineRlProgress(update => OnRlProgress(job, update)), job.Cancellation.Token);
        }
        catch (Exception error)
        {
            run = Task.FromException<RlPortfolioTrainingResult>(error);
        }

        _logger.LogInformation("Reinforcement-learning portfolio job {JobId} started.", jobId);
        job.Run = RunPortfolioRlAsync(job, run);
        return new TrainingJobTicket(jobId, TrainingJobState.Running);
    }

    public TrainingJobStatus GetStatus(string jobId)
    {
        TrainingJobRecord job = _jobs.Find(jobId) ?? throw new JobNotFoundException(jobId);
        lock (_jobs.Gate)
        {
            DateTimeOffset end = job.CompletedUtc ?? DateTimeOffset.UtcNow;
            return new TrainingJobStatus(
                job.JobId,
                job.State,
                job.Stage,
                job.ProgressPercent,
                end - job.StartedUtc,
                job.Metrics,
                job.Messages.ToArray(),
                job.Artifact,
                job.FailureReason,
                job.Request,
                job.Kind,
                job.RlRequest,
                job.RlResult,
                job.PortfolioRlRequest,
                job.PortfolioRlResult);
        }
    }

    public TrainingJobState Cancel(string jobId)
    {
        TrainingJobRecord job = _jobs.Find(jobId) ?? throw new JobNotFoundException(jobId);
        TrainingJobState before;
        TrainingJobState after;
        lock (_jobs.Gate)
        {
            before = job.State;
            // A second cancel of a job that is already stopping changes nothing (the table has no event for it; asking twice is not a bug).
            after = before == TrainingJobState.Cancelling ? before : _jobs.Apply(job, JobEvent.CancelRequested, DateTimeOffset.UtcNow);
        }

        if (after != before)
        {
            _logger.LogInformation("Training job {JobId} cancel requested; state {State}.", jobId, after);
            job.Cancellation.Cancel();
            if (after == TrainingJobState.Cancelling)
            {
                _ = ForceCancelledAfterGraceAsync(job);
            }
        }

        return after;
    }

    public EphemeralArtifact? FindArtifact(string artifactIdOrLatest)
    {
        ArgumentNullException.ThrowIfNull(artifactIdOrLatest);
        lock (_jobs.Gate)
        {
            if (string.Equals(artifactIdOrLatest, LatestAlias, StringComparison.Ordinal))
            {
                return NewestSucceeded(TrainingJobKind.Supervised)?.Artifact;
            }

            TrainingJobRecord? job = _jobs.Find(artifactIdOrLatest);
            return job is { State: TrainingJobState.Succeeded } ? job.Artifact : null;
        }
    }

    public ArtifactLease LeaseArtifact(string artifactId)
    {
        EphemeralArtifact artifact;
        IDisposable lease;
        lock (_jobs.Gate)
        {
            artifact = FindArtifact(artifactId) ?? throw new ArtifactNotFoundException(artifactId);
            lease = _store.Lease(artifact.ArtifactId, ApplyRetention);
        }

        string path = Path.Combine(_store.JobDirectory(artifact.ArtifactId), artifact.OnnxFileName);
        if (!File.Exists(path))
        {
            lease.Dispose();
            throw new ArtifactNotFoundException(artifactId);
        }

        return new ArtifactLease(artifact, path, lease.Dispose);
    }

    public ArtifactLease LeaseRegisteredModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ModelGenerationLease? lease;
        if (string.Equals(modelId, "active", StringComparison.OrdinalIgnoreCase))
        {
            lease = _modelRegistry.AcquireActive();
            if (lease is null)
            {
                throw new ExecutionRequestInvalidException("No model is currently active in the registry.", nameof(modelId), new InvalidOperationException("Active model not found."));
            }
        }
        else
        {
            lease = _modelRegistry.Acquire(modelId);
            if (lease is null)
            {
                throw new ArtifactNotFoundException(modelId);
            }
        }

        if (!File.Exists(lease.ModelPath))
        {
            lease.Dispose();
            throw new ArtifactNotFoundException(modelId);
        }

        return ArtifactLease.FromPath(lease.ModelPath, lease.Dispose, lease.ModelId);
    }

    /// <summary>Stops every running job (the whole process tree), then deletes the session folder.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IReadOnlyList<TrainingJobRecord> all = _jobs.Snapshot();
        foreach (TrainingJobRecord job in all)
        {
            lock (_jobs.Gate)
            {
                if (job.State is TrainingJobState.Running or TrainingJobState.Validating)
                {
                    _jobs.Apply(job, JobEvent.CancelRequested, DateTimeOffset.UtcNow);
                }
            }

            job.Cancellation.Cancel();
        }

        Task[] runs = all.Select(job => job.Run).OfType<Task>().ToArray();
        try
        {
            Task.WaitAll(runs, _cancelGrace);
        }
        catch (AggregateException)
        {
            // each run reports its own failure; shutting down must not throw
        }

        _store.Dispose();
        _logger.LogInformation("Sandboxed execution stopped; the session folder was removed.");
    }

    // ---- request checks (only what the application's own rules do not already say) -----------------------------------

    /// <summary>
    /// Refuses, before anything is created, a job whose longest file would need a path of <see cref="ExecutionLimits.PathCharsLimit"/> characters or more. A job
    /// id and a session id have a fixed length, so the job folder of the id asked about is as long as every folder this session will make.
    /// </summary>
    private void RequireJobPathsFit(string jobId, int longestRelativePathChars)
    {
        int required = Path.GetFullPath(_store.JobDirectory(jobId)).Length + 1 + longestRelativePathChars;
        if (required >= ExecutionLimits.PathCharsLimit)
        {
            throw new ExecutionPathTooLongException(required);
        }
    }

    private void ValidateRequest(EphemeralTrainingRequest request)
    {
        if (request.Symbols is null || request.Symbols.Count == 0)
        {
            throw Invalid("At least one symbol is required.", nameof(request.Symbols));
        }

        if (request.Symbols.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Symbols.Count)
        {
            throw Invalid("A symbol is listed twice.", nameof(request.Symbols));
        }

        if (!TrainingArchitectureCatalog.For(request.Framework).Contains(request.Architecture, StringComparer.Ordinal))
        {
            throw Invalid($"Architecture '{request.Architecture}' is not offered for {request.Framework}.", nameof(request.Architecture));
        }

        if (_policy.EnforceMcpFixedValues)
        {
            if (_policy.RequiredTimeframe is { } reqTimeframe && request.Timeframe != reqTimeframe)
            {
                throw Invalid($"Training timeframe {request.Timeframe} is not permitted by execution policy (required: {reqTimeframe}).", nameof(request.Timeframe));
            }

            if (_policy.RequiredFeatureMode is { } reqFeatureMode && request.FeatureMode != reqFeatureMode)
            {
                throw Invalid($"Feature mode {request.FeatureMode} is not permitted by execution policy (required: {reqFeatureMode}).", nameof(request.FeatureMode));
            }

            if (!_policy.AllowFixedScaler && request.FixedZScore)
            {
                throw Invalid("Fixed scaler is not permitted by execution policy.", nameof(request.FixedZScore));
            }

            if (!_policy.AllowCalibration && request.Calibration)
            {
                throw Invalid("Model calibration is not permitted by execution policy.", nameof(request.Calibration));
            }

            if (!_policy.AllowCpcv && request.Cpcv)
            {
                throw Invalid("CPCV is not permitted by execution policy.", nameof(request.Cpcv));
            }

            if (!_policy.AllowEnsemble && request.EnsembleMethod != TrainingEnsembleMethod.None)
            {
                throw Invalid("Ensemble training is not permitted by execution policy.", nameof(request.EnsembleMethod));
            }

            if (!_policy.AllowNonFreshInitialization && request.InitializationMode != TrainingInitializationMode.Fresh)
            {
                throw Invalid("Non-fresh initialization is not permitted by execution policy.", nameof(request.InitializationMode));
            }
        }

        if (request.ClipSigma is { } sigma)
        {
            if (!double.IsFinite(sigma) || sigma <= 0.0)
            {
                throw Invalid("clipSigma must be a finite positive number.", nameof(request.ClipSigma));
            }

            if (!request.FixedZScore)
            {
                throw Invalid("clipSigma requires fixedZScore to be enabled.", nameof(request.ClipSigma));
            }
        }

        if (request.Calibration && request.TargetType != TargetType.Classification)
        {
            throw Invalid("calibration is only supported for classification targets.", nameof(request.Calibration));
        }
    }

    // The application's own rules; both kinds of failure they raise become one exception type (the caller maps it to INVALID_ARGUMENT).
    private static void ValidateConfig(TrainingJobConfig config)
    {
        try
        {
            TrainingResourceGuard.Validate(config, new TrainingResourceOverrides());
        }
        catch (ArgumentException error)
        {
            throw new ExecutionRequestInvalidException(error.Message, error.ParamName, error);
        }
        catch (InvalidOperationException error)
        {
            throw new ExecutionRequestInvalidException(error.Message, null, error);
        }
    }

    private static ExecutionRequestInvalidException Invalid(string message, string parameterName)
        => new(message, parameterName, new ArgumentException(message, parameterName));

    private static Exception Refusal(Exception error) => error switch
    {
        TrainingJobBusyException => new ExecutionBusyException(),
        FileNotFoundException => new PythonEnvironmentMissingException(),
        ArgumentException argument => new ExecutionRequestInvalidException(argument.Message, argument.ParamName, argument),
        InvalidOperationException operation => new ExecutionRequestInvalidException(operation.Message, null, operation),
        _ => error,
    };

    /// <summary>
    /// The smoke job of the request: the costs of the Backtest window's factory defaults, and any omitted date proposed from the stored data the way the RL
    /// window pre-fills it (short-history bar requirement because the smoke run is checked like the short history; end = the day after the latest final bar).
    /// An explicit start is never replaced, and an explicit period is not second-guessed here (the run itself refuses a period it cannot use).
    /// </summary>
    private static async Task<RlTrainingJob> BuildRlJobAsync(EphemeralRlRequest request, IEphemeralRlRunner runner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            throw Invalid("A symbol is required.", nameof(request.Symbol));
        }

        if (request.StartDate is not null && request.EndDateExclusive is not null && request.StartDate >= request.EndDateExclusive)
        {
            throw Invalid("The start date must be before the end date.", nameof(request.StartDate));
        }

        DateOnly? start = request.StartDate;
        DateOnly? end = request.EndDateExclusive;
        if (start is null || end is null)
        {
            int required = RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory);
            StoredPeriodProposal proposal = await runner.ProposePeriodAsync(request.Symbol.Trim(), required, end?.AddDays(-1), cancellationToken).ConfigureAwait(false);
            if (proposal.Status is not (StoredPeriodStatus.Found or StoredPeriodStatus.InsufficientHistory))
            {
                throw new InsufficientStoredDataException(required, 0);
            }

            if (start is null && proposal.Status == StoredPeriodStatus.InsufficientHistory)
            {
                throw new InsufficientStoredDataException(proposal.RequiredBars, proposal.AvailableBars);
            }

            start ??= proposal.Start;
            end ??= proposal.Latest?.AddDays(1);
        }

        try
        {
            return RlTrainingValidation.Freeze(new RlTrainingJob(request.Symbol, start!.Value, end!.Value, FactoryCosts(), SmokeOnly: true));
        }
        catch (ArgumentException error)
        {
            throw new ExecutionRequestInvalidException(error.Message, null, error);
        }
    }

    /// <summary>The cost model the Backtest window starts from (the factory defaults), shaped for RL the way the RL window shapes its own costs.</summary>
    private static BacktestConfiguration FactoryCosts() => RlContract.CreateConfiguration(new BacktestConfiguration
    {
        SizingModel = BacktestDefaults.SizingModel,
        SizingParameter = BacktestDefaults.SizingParameter,
        InitialCapital = BacktestDefaults.InitialCapital,
        CommissionFlat = BacktestDefaults.CommissionFlat,
        CommissionPerUnit = BacktestDefaults.CommissionPerUnit,
        SlippageRatio = BacktestDefaults.SlippageRatio,
        LiquidationPenaltyRatio = BacktestDefaults.LiquidationPenaltyRatio,
    });

    // ---- the background run ----------------------------------------------------------------------------------------------

    private async Task RunRlAsync(TrainingJobRecord job, Task<RlTrainingResult> run)
    {
        try
        {
            RlTrainingResult result = await run.ConfigureAwait(false);
            await FinishRlAsync(job, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Our own cancel token is the only reason a run stops on purpose; the run's own deadline (RlContract.JobDeadlineMinutes) cancels it as well.
            if (job.Cancellation.IsCancellationRequested)
            {
                Finalize(job, JobEvent.Stopped, "Cancelled.");
            }
            else
            {
                Finalize(job, JobEvent.TimedOut, "The run exceeded its time limit.");
            }
        }
        catch (TimeoutException error)
        {
            Finalize(job, JobEvent.TimedOut, error.Message);
        }
        catch (Exception error)
        {
            Finalize(job, JobEvent.ProcessFailed, DescribeRlFailure(job, error));
        }
    }

    private async Task FinishRlAsync(TrainingJobRecord job, RlTrainingResult result)
    {
        if (!SettleCancellation(job))
        {
            DeleteFolderOfFinishedJob(job);
            return;
        }

        string jobDirectory = _store.JobDirectory(job.JobId);
        if (!IsInside(jobDirectory, result.ModelPath) || !IsInside(jobDirectory, result.EvaluationPath) || !File.Exists(result.ModelPath) || !File.Exists(result.EvaluationPath))
        {
            Finalize(job, JobEvent.ProcessFailed, "The run produced no model.");
            return;
        }

        lock (_jobs.Gate)
        {
            if (job.State != TrainingJobState.Running)
            {
                return;
            }

            _jobs.Apply(job, JobEvent.ProcessSucceeded, DateTimeOffset.UtcNow);
        }

        EphemeralRlResult? description = await Task.Run(() => TryDescribeRl(result)).ConfigureAwait(false);
        bool cancelledEarly = false;
        lock (_jobs.Gate)
        {
            if (job.State != TrainingJobState.Validating)
            {
                cancelledEarly = true;
            }
            else if (description is null)
            {
                _jobs.Apply(job, JobEvent.OnnxInvalid, DateTimeOffset.UtcNow);
                AddMessage(job, "The produced file is not a loadable ONNX model.");
            }
            else
            {
                job.RlResult = description;
                _jobs.Apply(job, JobEvent.OnnxLoaded, DateTimeOffset.UtcNow);
            }
        }

        if (cancelledEarly)
        {
            DeleteFolderOfFinishedJob(job);
            return;
        }

        _logger.LogInformation("Reinforcement-learning job {JobId} finished; state {State}.", job.JobId, job.State);
        if (job.RlRequest?.RetainArtifact == true && description is not null)
        {
            job.Artifact = TryDescribeRlArtifact(job.JobId, result.ModelPath, result.EvaluationPath);
            if (job.Artifact is not null)
            {
                ApplyRetention();
                return;
            }
        }

        DeleteFolderOfFinishedJob(job);
    }

    // The model loads with at least one input and one output, and the evaluation report says what a client may read; anything else is "not a valid result".
    private static EphemeralRlResult? TryDescribeRl(RlTrainingResult result)
    {
        try
        {
            using (var session = new InferenceSession(result.ModelPath))
            {
                if (session.InputMetadata.Count == 0 || session.OutputMetadata.Count == 0)
                {
                    return null;
                }
            }

            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(result.EvaluationPath));
            JsonElement root = report.RootElement;
            string meanReturn = root.GetProperty("validation_mean_return").GetString() ?? throw new InvalidDataException("No validation mean return.");
            int seed = root.GetProperty("candidate_seed").GetInt32();
            int checkpoint = root.GetProperty("checkpoint_step").GetInt32();
            int transitions = root.GetProperty("training_transitions").GetProperty(seed.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetInt32();
            using FileStream stream = File.OpenRead(result.ModelPath);
            return new EphemeralRlResult(
                meanReturn,
                seed,
                checkpoint,
                transitions,
                stream.Length,
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
                result.SourceRevision);
        }
        catch (Exception error) when (error is OnnxRuntimeException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
        {
            return null;
        }
    }

    // The worker's last stderr line says why a run failed (a missing package, a pin that does not match); the error text itself only names the exit code.
    private string DescribeRlFailure(TrainingJobRecord job, Exception error)
    {
        string? line = LastWorkerErrorLine(_store.JobDirectory(job.JobId));
        return line is null ? error.Message : $"{error.Message} Worker: {line}";
    }

    private async Task RunPortfolioRlAsync(TrainingJobRecord job, Task<RlPortfolioTrainingResult> run)
    {
        try
        {
            RlPortfolioTrainingResult result = await run.ConfigureAwait(false);
            await FinishPortfolioRlAsync(job, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (job.Cancellation.IsCancellationRequested)
            {
                Finalize(job, JobEvent.Stopped, "Cancelled.");
            }
            else
            {
                Finalize(job, JobEvent.TimedOut, "The run exceeded its time limit.");
            }
        }
        catch (TimeoutException error)
        {
            Finalize(job, JobEvent.TimedOut, error.Message);
        }
        catch (Exception error)
        {
            Finalize(job, JobEvent.ProcessFailed, DescribeRlFailure(job, error));
        }
    }

    private async Task FinishPortfolioRlAsync(TrainingJobRecord job, RlPortfolioTrainingResult result)
    {
        if (!SettleCancellation(job))
        {
            DeleteFolderOfFinishedJob(job);
            return;
        }

        string jobDirectory = _store.JobDirectory(job.JobId);
        if (!IsInside(jobDirectory, result.ModelPath) || !IsInside(jobDirectory, result.ReportPath) || !File.Exists(result.ModelPath) || !File.Exists(result.ReportPath))
        {
            Finalize(job, JobEvent.ProcessFailed, "The run produced no model or evaluation report.");
            return;
        }

        lock (_jobs.Gate)
        {
            if (job.State != TrainingJobState.Running)
            {
                return;
            }

            _jobs.Apply(job, JobEvent.ProcessSucceeded, DateTimeOffset.UtcNow);
        }

        EphemeralPortfolioRlResult? description = await Task.Run(() => TryDescribePortfolioRl(result)).ConfigureAwait(false);
        bool cancelledEarly = false;
        lock (_jobs.Gate)
        {
            if (job.State != TrainingJobState.Validating)
            {
                cancelledEarly = true;
            }
            else if (description is null)
            {
                _jobs.Apply(job, JobEvent.OnnxInvalid, DateTimeOffset.UtcNow);
                AddMessage(job, "The produced file is not a loadable ONNX model or valid evaluation.");
            }
            else
            {
                job.PortfolioRlResult = description;
                _jobs.Apply(job, JobEvent.OnnxLoaded, DateTimeOffset.UtcNow);
            }
        }

        if (cancelledEarly)
        {
            DeleteFolderOfFinishedJob(job);
            return;
        }

        _logger.LogInformation("Reinforcement-learning portfolio job {JobId} finished; state {State}.", job.JobId, job.State);
        if (job.PortfolioRlRequest?.RetainArtifact == true && description is not null)
        {
            job.Artifact = TryDescribeRlArtifact(job.JobId, result.ModelPath, result.ReportPath);
            if (job.Artifact is not null)
            {
                ApplyRetention();
                return;
            }
        }

        DeleteFolderOfFinishedJob(job);
    }

    private static EphemeralPortfolioRlResult? TryDescribePortfolioRl(RlPortfolioTrainingResult result)
    {
        try
        {
            using (var session = new InferenceSession(result.ModelPath))
            {
                if (session.InputMetadata.Count == 0 || session.OutputMetadata.Count == 0)
                {
                    return null;
                }
            }

            var evaluableBlocks = result.Evaluation.Cells
                .Where(c => c.Evaluable)
                .SelectMany(c => c.Blocks)
                .ToList();

            decimal policyMean = 0m;
            decimal cashMean = 0m;
            decimal equalBuyHoldMean = 0m;
            decimal equalDailyRebalancedMean = 0m;
            decimal? policyCompound = null;
            decimal? cashCompound = null;
            decimal? equalBuyHoldCompound = null;
            decimal? equalDailyRebalancedCompound = null;
            int? evaluatedBlockCount = null;

            if (evaluableBlocks.Count > 0)
            {
                policyMean = evaluableBlocks.Sum(b => b.PolicyReturn) / evaluableBlocks.Count;
                cashMean = evaluableBlocks.Sum(b => b.CashReturn) / evaluableBlocks.Count;
                equalBuyHoldMean = evaluableBlocks.Sum(b => b.EqualBuyHoldReturn) / evaluableBlocks.Count;
                equalDailyRebalancedMean = evaluableBlocks.Sum(b => b.EqualDailyRebalancedReturn) / evaluableBlocks.Count;

                evaluatedBlockCount = evaluableBlocks.Count;
                decimal polComp = 1m;
                decimal cashComp = 1m;
                decimal ebhComp = 1m;
                decimal edrComp = 1m;
                foreach (var b in evaluableBlocks)
                {
                    polComp *= (1m + b.PolicyReturn);
                    cashComp *= (1m + b.CashReturn);
                    ebhComp *= (1m + b.EqualBuyHoldReturn);
                    edrComp *= (1m + b.EqualDailyRebalancedReturn);
                }
                policyCompound = polComp - 1m;
                cashCompound = cashComp - 1m;
                equalBuyHoldCompound = ebhComp - 1m;
                equalDailyRebalancedCompound = edrComp - 1m;
            }

            using FileStream stream = File.OpenRead(result.ModelPath);
            long modelSize = stream.Length;
            string modelSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

            return new EphemeralPortfolioRlResult(
                policyMean.ToString("F6", CultureInfo.InvariantCulture),
                cashMean.ToString("F6", CultureInfo.InvariantCulture),
                equalBuyHoldMean.ToString("F6", CultureInfo.InvariantCulture),
                equalDailyRebalancedMean.ToString("F6", CultureInfo.InvariantCulture),
                result.SelectedSeed,
                result.SelectedCheckpoint,
                result.UniverseSha256,
                result.CalendarSha256,
                modelSize,
                modelSha256,
                policyCompound,
                cashCompound,
                equalBuyHoldCompound,
                equalDailyRebalancedCompound,
                evaluatedBlockCount);
        }
        catch (Exception error) when (error is OnnxRuntimeException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
        {
            return null;
        }
    }

    private static string? LastWorkerErrorLine(string jobDirectory)
    {
        try
        {
            string runs = Path.Combine(jobDirectory, RlFiles.TrainingFolder, RlFiles.RlFolder);
            if (!Directory.Exists(runs))
            {
                return null;
            }

            string? log = Directory.EnumerateFiles(runs, "*.stderr.log", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            return log is null ? null : File.ReadLines(log).Where(l => !string.IsNullOrWhiteSpace(l)).LastOrDefault()?.Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnRlProgress(TrainingJobRecord job, RlTrainingProgress update)
    {
        lock (_jobs.Gate)
        {
            if (job.State != TrainingJobState.Running)
            {
                return;
            }

            string stage = Cut(update.State, ExecutionLimits.MaxStageChars);
            if (!string.Equals(stage, job.Stage, StringComparison.Ordinal))
            {
                job.Stage = stage;
                AddMessage(job, $"Stage: {stage}");
            }

            if (update.CompletedSteps >= 0)
            {
                // A later phase may count from zero again; the percentage a client sees never goes back.
                job.ProgressPercent = Math.Max(job.ProgressPercent, (int)Math.Clamp(update.CompletedSteps * 100L / RlContract.SmokeTrainingTransitions, 0, 100));
            }
        }
    }

    private async Task RunAsync(TrainingJobRecord job, Task<TrainingRunResult> training)
    {
        try
        {
            TrainingRunResult result = await training.ConfigureAwait(false);
            await FinishAsync(job, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finalize(job, JobEvent.Stopped, "Cancelled.");
        }
        catch (TimeoutException error)
        {
            Finalize(job, JobEvent.TimedOut, error.Message);
        }
        catch (Exception error)
        {
            Finalize(job, JobEvent.ProcessFailed, error.Message);
        }
    }

    // A run that ended while a cancellation was pending is a cancelled job. Returns whether the job is still running, read in the same lock hold as the
    // transition (a value read outside the lock could be stale; the later checks under the lock would still stop it, but the folder clean-up would be late).
    private bool SettleCancellation(TrainingJobRecord job)
    {
        lock (_jobs.Gate)
        {
            if (job.State == TrainingJobState.Cancelling)
            {
                _jobs.Apply(job, JobEvent.Stopped, DateTimeOffset.UtcNow);
            }

            return job.State == TrainingJobState.Running;
        }
    }

    private async Task FinishAsync(TrainingJobRecord job, TrainingRunResult result)
    {
        if (!SettleCancellation(job))
        {
            DeleteFolderOfFinishedJob(job);
            return;
        }

        string jobDirectory = _store.JobDirectory(job.JobId);
        if (!result.Success || result.OnnxArtifactPath is null || !IsInside(jobDirectory, result.OnnxArtifactPath) || !File.Exists(result.OnnxArtifactPath))
        {
            Finalize(job, JobEvent.ProcessFailed, result.Message ?? "The trainer produced no model.");
            return;
        }

        lock (_jobs.Gate)
        {
            if (job.State != TrainingJobState.Running)
            {
                return;
            }

            _jobs.Apply(job, JobEvent.ProcessSucceeded, DateTimeOffset.UtcNow);
            job.Metrics = result.Metrics.ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        EphemeralArtifact? artifact = await Task.Run(() => TryDescribe(job.JobId, result.OnnxArtifactPath)).ConfigureAwait(false);
        bool cancelledEarly = false;
        lock (_jobs.Gate)
        {
            if (job.State != TrainingJobState.Validating)
            {
                cancelledEarly = true;
            }
            else if (artifact is null)
            {
                _jobs.Apply(job, JobEvent.OnnxInvalid, DateTimeOffset.UtcNow);
                AddMessage(job, "The produced file is not a loadable ONNX model.");
            }
            else
            {
                job.Artifact = artifact;
                _jobs.Apply(job, JobEvent.OnnxLoaded, DateTimeOffset.UtcNow);
            }
        }

        if (cancelledEarly)
        {
            DeleteFolderOfFinishedJob(job);
            return;
        }

        _logger.LogInformation("Training job {JobId} finished; state {State}.", job.JobId, job.State);
        if (artifact is null)
        {
            DeleteFolderOfFinishedJob(job);
        }
        else
        {
            ApplyRetention();
        }
    }

    // Moves a running or cancelling job to its end by the event, records the message, and removes its folder.
    private void Finalize(TrainingJobRecord job, JobEvent jobEvent, string message)
    {
        lock (_jobs.Gate)
        {
            // A job that was cancelled while the run ended in another way is a cancelled job; an end the table has no event for in
            // the job's state (a cancellation nobody asked for, an error while validating) is a failure of that state.
            JobEvent? effective = job.State switch
            {
                TrainingJobState.Running => jobEvent == JobEvent.Stopped ? JobEvent.ProcessFailed : jobEvent,
                TrainingJobState.Cancelling => JobEvent.Stopped,
                TrainingJobState.Validating => JobEvent.OnnxInvalid,
                _ => null,
            };
            if (effective is { } applied)
            {
                _jobs.Apply(job, applied, DateTimeOffset.UtcNow);
                if (applied != JobEvent.Stopped)
                {
                    AddMessage(job, message);
                }
            }
        }

        _logger.LogInformation("Training job {JobId} finished; state {State}.", job.JobId, job.State);
        DeleteFolderOfFinishedJob(job);
    }

    private async Task ForceCancelledAfterGraceAsync(TrainingJobRecord job)
    {
        await Task.Delay(_cancelGrace).ConfigureAwait(false);
        lock (_jobs.Gate)
        {
            if (job.State != TrainingJobState.Cancelling)
            {
                return;
            }

            _jobs.Apply(job, JobEvent.Stopped, DateTimeOffset.UtcNow);
        }

        _logger.LogInformation("Training job {JobId} reported as cancelled after the grace period.", job.JobId);
        DeleteFolderOfFinishedJob(job);
    }

    private void OnProgress(TrainingJobRecord job, TrainingProgress update)
    {
        lock (_jobs.Gate)
        {
            if (job.State != TrainingJobState.Running)
            {
                return;
            }

            string? stage = update.Stage is null ? null : Cut(update.Stage, ExecutionLimits.MaxStageChars);
            if (stage is not null && !string.Equals(stage, job.Stage, StringComparison.Ordinal))
            {
                job.Stage = stage;
                AddMessage(job, $"Stage: {stage}");
            }

            job.ProgressPercent = Math.Clamp(update.Percent, 0, 100);
            if (update.Metric is not null)
            {
                job.Metrics = update.Metric.ToDictionary(pair => pair.Key, pair => pair.Value);
            }
        }
    }

    // ---- artifacts ----------------------------------------------------------------------------------------------------------

    // A valid ONNX: the model and its metrics file are in the job folder and the model loads with at least one input and one output.
    private EphemeralArtifact? TryDescribe(string jobId, string onnxPath)
    {
        string metricsPath = onnxPath + MetricsFileSuffix;
        if (!File.Exists(metricsPath))
        {
            return null;
        }

        try
        {
            using var session = new InferenceSession(onnxPath);
            if (session.InputMetadata.Count == 0 || session.OutputMetadata.Count == 0)
            {
                return null;
            }

            using FileStream stream = File.OpenRead(onnxPath);
            string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return new EphemeralArtifact(
                jobId,
                Path.GetFileName(onnxPath),
                new FileInfo(onnxPath).Length,
                hash,
                Path.GetFileName(metricsPath),
                Loads: true,
                session.InputMetadata.First().Value.Dimensions.Select(d => (long)d).ToArray(),
                session.OutputMetadata.First().Value.Dimensions.Select(d => (long)d).ToArray());
        }
        catch (OnnxRuntimeException)
        {
            return null;
        }
    }

    private EphemeralArtifact? TryDescribeRlArtifact(string jobId, string onnxPath, string evaluationPath)
    {
        if (!File.Exists(onnxPath) || !File.Exists(evaluationPath))
        {
            return null;
        }

        try
        {
            using var session = new InferenceSession(onnxPath);
            if (session.InputMetadata.Count == 0 || session.OutputMetadata.Count == 0)
            {
                return null;
            }

            string jobDir = _store.JobDirectory(jobId);
            string relOnnx = Path.GetRelativePath(jobDir, onnxPath);
            string relEval = Path.GetRelativePath(jobDir, evaluationPath);

            using FileStream stream = File.OpenRead(onnxPath);
            string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return new EphemeralArtifact(
                jobId,
                relOnnx,
                new FileInfo(onnxPath).Length,
                hash,
                relEval,
                Loads: true,
                session.InputMetadata.First().Value.Dimensions.Select(d => (long)d).ToArray(),
                session.OutputMetadata.First().Value.Dimensions.Select(d => (long)d).ToArray());
        }
        catch (Exception error) when (error is OnnxRuntimeException or IOException)
        {
            return null;
        }
    }

    // Keeps the newest KeepLatestRuns succeeded jobs per job kind; the folders of the others are deleted unless a lease holds them (then a later release tries again).
    private void ApplyRetention()
    {
        List<string> jobsToDelete = new();
        lock (_jobs.Gate)
        {
            var snapshot = _jobs.Snapshot();

            // Retain Supervised jobs separately so RL runs do not evict prediction models
            IEnumerable<TrainingJobRecord> olderSupervised = snapshot
                .Where(job => job.Kind == TrainingJobKind.Supervised && job.State == TrainingJobState.Succeeded && job.Artifact is not null)
                .OrderByDescending(job => job.CompletedUtc)
                .ThenByDescending(job => job.JobId, StringComparer.Ordinal)
                .Skip(_settings.KeepLatestRuns);
            foreach (TrainingJobRecord job in olderSupervised)
            {
                jobsToDelete.Add(job.JobId);
            }

            // Retain RL / Portfolio RL jobs separately
            IEnumerable<TrainingJobRecord> olderOther = snapshot
                .Where(job => job.Kind != TrainingJobKind.Supervised && job.State == TrainingJobState.Succeeded && job.Artifact is not null)
                .OrderByDescending(job => job.CompletedUtc)
                .ThenByDescending(job => job.JobId, StringComparer.Ordinal)
                .Skip(_settings.KeepLatestRuns);
            foreach (TrainingJobRecord job in olderOther)
            {
                jobsToDelete.Add(job.JobId);
            }
        }

        foreach (string jobId in jobsToDelete)
        {
            if (_store.TryDeleteJobDirectory(jobId))
            {
                lock (_jobs.Gate)
                {
                    if (_jobs.Find(jobId) is { } record)
                    {
                        record.Artifact = null;
                    }
                }
            }
        }
    }

    private TrainingJobRecord? NewestSucceeded(TrainingJobKind kind = TrainingJobKind.Supervised)
        => _jobs.Snapshot()
            .Where(job => job.Kind == kind && job.State == TrainingJobState.Succeeded && job.Artifact is not null)
            .OrderByDescending(job => job.CompletedUtc)
            .ThenByDescending(job => job.JobId, StringComparer.Ordinal)
            .FirstOrDefault();

    private void DeleteFolderOfFinishedJob(TrainingJobRecord job)
    {
        bool shouldDelete;
        lock (_jobs.Gate)
        {
            shouldDelete = ShouldDeleteJobFolder(job);
        }

        if (shouldDelete)
        {
            _store.TryDeleteJobDirectory(job.JobId);
        }
    }

    private static bool ShouldDeleteJobFolder(TrainingJobRecord job)
        // A supervised model is kept for the prediction tool; nothing reads a reinforcement-learning run's files, so every end removes them.
        => job.State is TrainingJobState.Failed or TrainingJobState.Cancelled
            || (job.Kind is TrainingJobKind.ReinforcementLearning or TrainingJobKind.PortfolioReinforcementLearning && TrainingJobTransitions.IsFinal(job.State));

    private void AddMessage(TrainingJobRecord job, string message)
    {
        job.Messages.Add(Cut(message, ExecutionLimits.MaxStatusMessageChars));
        while (job.Messages.Count > _settings.StatusTailMessages)
        {
            job.Messages.RemoveAt(0);
        }
    }

    private static string Cut(string text, int maximum) => text.Length <= maximum ? text : text[..maximum];

    private static bool IsInside(string directory, string path)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private sealed class InlineProgress(Action<TrainingProgress> report) : IProgress<TrainingProgress>
    {
        public void Report(TrainingProgress value) => report(value);
    }

    private sealed class InlineRlProgress(Action<RlTrainingProgress> report) : IProgress<RlTrainingProgress>
    {
        public void Report(RlTrainingProgress value) => report(value);
    }
}
