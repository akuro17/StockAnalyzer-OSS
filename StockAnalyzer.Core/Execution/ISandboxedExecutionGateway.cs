using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Execution;

/// <summary>
/// Runs supervised training in the ephemeral sandbox (the OS temp folder): nothing is written under <c>Data/</c>, nothing is registered, and a
/// session's files disappear when the process ends. A client starts a job, polls its status, may cancel it, and reads the model it produced.
/// </summary>
public interface ISandboxedExecutionGateway
{
    /// <summary>Starts a job and returns at once; the job runs in the background.</summary>
    /// <exception cref="ExecutionRequestInvalidException">A rule of the application (the request, <c>TrainingJobConfig.Validate</c>, <c>TrainingResourceGuard</c>) refuses the request. No job is created.</exception>
    /// <exception cref="ExecutionBusyException">The allowed number of running jobs is reached, or the same job is already running. No job is created.</exception>
    /// <exception cref="PythonEnvironmentMissingException">No interpreter is installed. No job is created.</exception>
    /// <exception cref="ExecutionPathTooLongException">The job folder is too deep for the trainer's longest file path. No job and no folder is created.</exception>
    Task<TrainingJobTicket> StartTrainingAsync(EphemeralTrainingRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Starts one reinforcement-learning smoke run (the RL window's trial run) and returns at once. The job shares the table, the state machine and the
    /// concurrency limit of the supervised jobs; nothing is registered and nothing is written under <c>Data/</c>.
    /// </summary>
    /// <exception cref="ExecutionRequestInvalidException">A rule of the application refuses the request. No job is created.</exception>
    /// <exception cref="InsufficientStoredDataException">The stored data cannot give the period. No job is created.</exception>
    /// <exception cref="ExecutionBusyException">The allowed number of running jobs is reached. No job is created.</exception>
    /// <exception cref="RlEnvironmentMissingException">The managed RL environment is not installed. No job is created.</exception>
    /// <exception cref="ExecutionPathTooLongException">The job folder is too deep for the run's longest file path. No job and no folder is created.</exception>
    Task<TrainingJobTicket> StartRlTrainingAsync(EphemeralRlRequest request, CancellationToken cancellationToken);

    /// <exception cref="JobNotFoundException">No job has this id.</exception>
    TrainingJobStatus GetStatus(string jobId);

    /// <summary>Asks a job to stop. A job that already ended is left as it is; the current state is returned.</summary>
    /// <exception cref="JobNotFoundException">No job has this id.</exception>
    TrainingJobState Cancel(string jobId);

    /// <summary>The artifact of a succeeded job, or of the newest succeeded job for <c>latest</c>; null when there is none.</summary>
    EphemeralArtifact? FindArtifact(string artifactIdOrLatest);

    /// <summary>The artifact's ONNX file, kept from deletion until the lease is disposed.</summary>
    /// <exception cref="ArtifactNotFoundException">No such artifact.</exception>
    ArtifactLease LeaseArtifact(string artifactId);
}

/// <summary>A read lease on an artifact: the files stay until it is disposed.</summary>
public sealed class ArtifactLease : IDisposable
{
    private readonly Action _release;
    private int _released;

    public ArtifactLease(EphemeralArtifact artifact, string onnxPath, Action? release = null)
    {
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        OnnxPath = onnxPath ?? throw new ArgumentNullException(nameof(onnxPath));
        _release = release ?? (() => { });
    }

    /// <summary>Creates a standalone lease directly on an onnx file on disk.</summary>
    public static ArtifactLease FromPath(string onnxPath, Action? release = null)
    {
        var fileInfo = new FileInfo(onnxPath);
        var hash = ContentHash.OfFile(onnxPath);
        var artifact = new EphemeralArtifact(
            ArtifactId: "file-" + Guid.NewGuid().ToString("N"),
            OnnxFileName: fileInfo.Name,
            SizeBytes: fileInfo.Length,
            Sha256: hash,
            MetricsFileName: Path.ChangeExtension(fileInfo.Name, ".metrics.json"),
            Loads: true,
            InputShape: Array.Empty<long>(),
            OutputShape: Array.Empty<long>());
        return new ArtifactLease(artifact, onnxPath, release);
    }

    public EphemeralArtifact Artifact { get; }

    /// <summary>The model file. Valid only while the lease is held; it must never be shown to a client.</summary>
    public string OnnxPath { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release();
        }
    }
}
