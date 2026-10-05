#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Operational continuation contract; does not alter the numerical T16 contract.</summary>
public static class RlCheckpointContract
{
    public const string SpecificationId = "SA-T16-SAME-ENVIRONMENT-CONTINUATION-R09-v1";
    public const int SchemaVersion = 2;
    public const int DefaultKeepCount = 2;
    public const string Folder = "checkpoints";
    public const string Pointer = "resume.json";
    public const string Manifest = "checkpoint.json";
    public const string CoreState = "core.json";
    public const string PythonState = "python.pt";
    public const string Budget = "resume_budget.json";
    public const string Journal = "resume_work.log";
    public const string Attempt = "resume_attempt.json";
    public const string ExportFolder = "exports";
    public const int JournalRecordBytes = sizeof(int) * 2 + sizeof(long);
    public const string PauseState = "Paused";
    public const string WorkerState = "worker.json";
    public const string StagingPrefix = "pending_";
    internal static string[] Phases { get; } = new[] { "train", "selected", "oos", "parity", "worker_complete" };
}

public sealed record RlCheckpointReview(Guid RunId, string RunDirectory, string CheckpointId,
    int Seed, int Steps, string Phase, string Identity, bool Verified, string? Error);
public interface IRlResumableTrainingService : IRlTrainingService
{
    Task<RlTrainingResult> RunResumableAsync(RlTrainingJob job, CancellationToken pauseRequest,
        IProgress<RlTrainingProgress>? progress = null, CancellationToken ct = default);
    Task<RlTrainingResult> ResumeAsync(RlCheckpointReview reviewed, CancellationToken pauseRequest,
        IProgress<RlTrainingProgress>? progress = null, CancellationToken ct = default);
}

public sealed class RlTrainingPausedException : OperationCanceledException
{
    public RlTrainingPausedException(Guid runId, string directory) : base("T16 paused at a committed optimizer boundary.")
    { RunId = runId; Directory = directory; }
    public Guid RunId { get; }
    public string Directory { get; }
}
