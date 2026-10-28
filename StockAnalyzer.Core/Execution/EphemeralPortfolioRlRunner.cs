using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Core.Execution;

internal interface IEphemeralPortfolioRlRunner
{
    /// <summary>The settings the runner's preflight and runs use; the start gate decides on the same values.</summary>
    RlPortfolioSettings Settings { get; }

    Task<RlEnvironmentStatus> CheckEnvironmentAsync(CancellationToken cancellationToken);

    Task<PanelPreflight> PreflightAsync(
        IReadOnlyList<string> symbols,
        DateOnly start,
        DateOnly endExclusive,
        bool holdoutRequested,
        RlPanelEvaluationMode mode,
        CancellationToken cancellationToken);

    Task<RlStartSuggestion?> SuggestStartAsync(
        IReadOnlyList<string> symbols,
        DateOnly endExclusive,
        RlPanelEvaluationMode mode,
        CancellationToken cancellationToken);

    Task<RlPortfolioTrainingResult> RunAsync(
        RlPortfolioRunRequest request,
        string jobDirectory,
        IProgress<RlTrainingProgress>? progress,
        CancellationToken cancellationToken);
}

internal sealed class EphemeralPortfolioRlRunner : IEphemeralPortfolioRlRunner
{
    private const string LockFolder = "locks";
    private readonly RlPortfolioSettings _settings;
    private readonly RlLocalSourceFolders? _folders;
    private readonly RlEnvironmentProbe _environment;

    /// <param name="folders">The market-data folders the panel is read from; null keeps the fixed <c>Data/Daily</c> and <c>Data/Metadata</c>.</param>
    public EphemeralPortfolioRlRunner(RlPortfolioSettings? settings = null, RlLocalSourceFolders? folders = null)
    {
        _settings = settings ?? new RlPortfolioSettings();
        _folders = folders;
        _environment = new RlEnvironmentProbe(() =>
        {
            var (_, pythonRoot, python) = RlWorkerProcess.ResolveInterpreter("training/train_rl_portfolio.py");
            return (python, pythonRoot);
        });
    }

    public RlPortfolioSettings Settings => _settings;

    public Task<RlEnvironmentStatus> CheckEnvironmentAsync(CancellationToken cancellationToken)
        => _environment.CheckAsync(cancellationToken);

    public Task<PanelPreflight> PreflightAsync(
        IReadOnlyList<string> symbols,
        DateOnly start,
        DateOnly endExclusive,
        bool holdoutRequested,
        RlPanelEvaluationMode mode,
        CancellationToken cancellationToken)
    {
        var service = new RlPortfolioService(_settings, sourceFolders: _folders);
        return service.PreflightAsync(symbols, start, endExclusive, holdoutRequested, mode, cancellationToken);
    }

    public Task<RlStartSuggestion?> SuggestStartAsync(
        IReadOnlyList<string> symbols,
        DateOnly endExclusive,
        RlPanelEvaluationMode mode,
        CancellationToken cancellationToken)
    {
        var service = new RlPortfolioService(_settings, sourceFolders: _folders);
        return service.SuggestStartAsync(symbols, endExclusive, mode, cancellationToken);
    }

    public async Task<RlPortfolioTrainingResult> RunAsync(
        RlPortfolioRunRequest request,
        string jobDirectory,
        IProgress<RlTrainingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var service = new RlPortfolioService(
            _settings,
            new TrainingJobGate(Path.Combine(jobDirectory, LockFolder)),
            dataRoot: jobDirectory,
            sourceFolders: _folders);
        return await service.RunAsync(request, progress, cancellationToken).ConfigureAwait(false);
    }
}
