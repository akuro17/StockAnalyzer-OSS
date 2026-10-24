using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.Core.Services.StoredPeriod;

namespace StockAnalyzer.Core.Execution;

/// <summary>
/// What the gateway needs from the reinforcement-learning side, as a seam: the gateway's tests replace it (no Python), the real one is
/// <see cref="EphemeralRlRunner"/>.
/// </summary>
internal interface IEphemeralRlRunner
{
    /// <summary>Whether the managed environment (the application's own <c>.venv</c>) exists and can import the trainer's packages; see <see cref="RlEnvironmentProbe"/>.</summary>
    Task<RlEnvironmentStatus> CheckEnvironmentAsync(CancellationToken cancellationToken);

    /// <summary>The period the stored data of <paramref name="symbol"/> allows, as the RL window pre-fills it.</summary>
    Task<StoredPeriodProposal> ProposePeriodAsync(string symbol, int requiredBars, DateOnly? endBoundInclusive, CancellationToken cancellationToken);

    /// <summary>Runs the job with every file below <paramref name="jobDirectory"/>.</summary>
    Task<RlTrainingResult> RunAsync(RlTrainingJob job, string jobDirectory, IProgress<RlTrainingProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// The real runner: the application's <see cref="RlTrainingService"/> with the data root, the model store and the job lock all inside the job's
/// folder, and the stored bars read from the configured folders without creating any. The store is the application's own registry class on a throw-away
/// root (the service requires one); a smoke run never registers, so it stays empty.
/// </summary>
internal sealed class EphemeralRlRunner : IEphemeralRlRunner
{
    private const string RegistryFolder = "registry";
    private const string LockFolder = "locks";

    private readonly RlLocalSourceFolders _folders;
    private readonly TimeProvider _clock;
    private readonly RlEnvironmentProbe _environment;

    public EphemeralRlRunner(RlLocalSourceFolders folders, TimeProvider? clock = null)
    {
        _folders = folders;
        _clock = clock ?? TimeProvider.System;
        _environment = new RlEnvironmentProbe(() =>
        {
            var (_, pythonRoot, python) = RlWorkerProcess.ResolveInterpreter(RlFiles.TrainerScript);
            return (python, pythonRoot);
        });
    }

    public Task<RlEnvironmentStatus> CheckEnvironmentAsync(CancellationToken cancellationToken) => _environment.CheckAsync(cancellationToken);

    public Task<StoredPeriodProposal> ProposePeriodAsync(string symbol, int requiredBars, DateOnly? endBoundInclusive, CancellationToken cancellationToken)
        => new StoredPeriodAdvisor(clock: _clock).ProposeForRlAsync(symbol, requiredBars, endBoundInclusive, _folders.Daily, _folders.Metadata, cancellationToken);

    public async Task<RlTrainingResult> RunAsync(RlTrainingJob job, string jobDirectory, IProgress<RlTrainingProgress> progress, CancellationToken cancellationToken)
    {
        using var registry = new ModelGenerationRegistry(Path.Combine(jobDirectory, RegistryFolder));
        var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(jobDirectory, LockFolder)), dataRoot: jobDirectory)
        {
            LocalSourceFolders = _folders,
        };
        return await service.RunAsync(job, progress, cancellationToken).ConfigureAwait(false);
    }
}
