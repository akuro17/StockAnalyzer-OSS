using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Execution;

/// <summary>What happens to a job. The only way a job state changes (see <see cref="TrainingJobRegistry.Apply"/>).</summary>
internal enum JobEvent
{
    /// <summary>The trainer exited 0 and wrote an ONNX file.</summary>
    ProcessSucceeded,

    /// <summary>The trainer exited with an error or wrote no ONNX.</summary>
    ProcessFailed,

    /// <summary>The run exceeded its time limit.</summary>
    TimedOut,

    CancelRequested,

    /// <summary>The process of a cancelled job stopped, or the grace period ended.</summary>
    Stopped,

    OnnxLoaded,

    OnnxInvalid,
}

/// <summary>The transition table of a job. A pair that is not in it is a bug, not a state to tolerate.</summary>
internal static class TrainingJobTransitions
{
    private static readonly Dictionary<(TrainingJobState From, JobEvent Event), TrainingJobState> Table = new()
    {
        [(TrainingJobState.Running, JobEvent.ProcessSucceeded)] = TrainingJobState.Validating,
        [(TrainingJobState.Running, JobEvent.ProcessFailed)] = TrainingJobState.Failed,
        [(TrainingJobState.Running, JobEvent.TimedOut)] = TrainingJobState.Failed,
        [(TrainingJobState.Running, JobEvent.CancelRequested)] = TrainingJobState.Cancelling,
        [(TrainingJobState.Cancelling, JobEvent.Stopped)] = TrainingJobState.Cancelled,
        [(TrainingJobState.Validating, JobEvent.OnnxLoaded)] = TrainingJobState.Succeeded,
        [(TrainingJobState.Validating, JobEvent.OnnxInvalid)] = TrainingJobState.Failed,
        [(TrainingJobState.Validating, JobEvent.CancelRequested)] = TrainingJobState.Cancelled,
    };

    public static bool IsFinal(TrainingJobState state) => state is TrainingJobState.Succeeded or TrainingJobState.Failed or TrainingJobState.Cancelled;

    /// <summary>The state after <paramref name="jobEvent"/>; a cancel request on a finished job leaves it as it is.</summary>
    /// <exception cref="InvalidOperationException">The pair is not in the table.</exception>
    public static TrainingJobState Next(TrainingJobState from, JobEvent jobEvent)
    {
        if (Table.TryGetValue((from, jobEvent), out TrainingJobState next))
        {
            return next;
        }

        if (jobEvent == JobEvent.CancelRequested && IsFinal(from))
        {
            return from;
        }

        throw new InvalidOperationException($"A job in state {from} cannot take the event {jobEvent}.");
    }

    public static TrainingFailureReason? FailureReasonOf(TrainingJobState from, JobEvent jobEvent) => (from, jobEvent) switch
    {
        (TrainingJobState.Running, JobEvent.ProcessFailed) => TrainingFailureReason.TrainingFailed,
        (TrainingJobState.Running, JobEvent.TimedOut) => TrainingFailureReason.Timeout,
        (TrainingJobState.Validating, JobEvent.OnnxInvalid) => TrainingFailureReason.InvalidOnnx,
        _ => null,
    };
}

/// <summary>The mutable record of one job, owned by <see cref="TrainingJobRegistry"/> and changed only under its lock.</summary>
internal sealed class TrainingJobRecord
{
    public TrainingJobRecord(string jobId, EphemeralTrainingRequest request, DateTimeOffset startedUtc)
    {
        JobId = jobId;
        Kind = TrainingJobKind.Supervised;
        Request = request;
        StartedUtc = startedUtc;
    }

    public TrainingJobRecord(string jobId, EphemeralRlRequest request, DateTimeOffset startedUtc)
    {
        JobId = jobId;
        Kind = TrainingJobKind.ReinforcementLearning;
        RlRequest = request;
        StartedUtc = startedUtc;
    }

    public string JobId { get; }

    public TrainingJobKind Kind { get; }

    /// <summary>The supervised request; null for a reinforcement-learning job.</summary>
    public EphemeralTrainingRequest? Request { get; }

    /// <summary>The reinforcement-learning request with the period actually used; null for a supervised job.</summary>
    public EphemeralRlRequest? RlRequest { get; }

    /// <summary>What a succeeded reinforcement-learning job leaves to read.</summary>
    public EphemeralRlResult? RlResult { get; set; }

    public DateTimeOffset StartedUtc { get; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public TrainingJobState State { get; set; } = TrainingJobState.Running;

    public TrainingFailureReason? FailureReason { get; set; }

    public string? Stage { get; set; }

    public int ProgressPercent { get; set; }

    public IReadOnlyDictionary<string, double>? Metrics { get; set; }

    public List<string> Messages { get; } = new();

    public EphemeralArtifact? Artifact { get; set; }

    /// <summary>Cancels the background run; created with the job.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    public Task? Run { get; set; }
}

/// <summary>The in-memory table of jobs: counts the active ones, applies the transition table, and bounds its own size.</summary>
internal sealed class TrainingJobRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TrainingJobRecord> _jobs = new(StringComparer.Ordinal);

    /// <summary>The lock that guards every job record; a reader of a record takes it.</summary>
    public object Gate => _gate;

    /// <summary>Adds a running job when fewer than <paramref name="maxConcurrent"/> jobs are active; false (and nothing added) otherwise.</summary>
    public bool TryAdd(TrainingJobRecord job, int maxConcurrent)
    {
        lock (_gate)
        {
            if (_jobs.Values.Count(existing => !TrainingJobTransitions.IsFinal(existing.State)) >= maxConcurrent)
            {
                return false;
            }

            _jobs.Add(job.JobId, job);
            TrimFinishedJobs();
            return true;
        }
    }

    /// <summary>Removes a job that was refused before it started anything.</summary>
    public void Forget(TrainingJobRecord job)
    {
        lock (_gate)
        {
            _jobs.Remove(job.JobId);
        }
    }

    public TrainingJobRecord? Find(string jobId)
    {
        lock (_gate)
        {
            return _jobs.GetValueOrDefault(jobId);
        }
    }

    public IReadOnlyList<TrainingJobRecord> Snapshot()
    {
        lock (_gate)
        {
            return _jobs.Values.ToList();
        }
    }

    /// <summary>Applies the event under the lock and returns the state after it. A final state also records the time.</summary>
    /// <exception cref="InvalidOperationException">The pair is not in the table.</exception>
    public TrainingJobState Apply(TrainingJobRecord job, JobEvent jobEvent, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            TrainingJobState before = job.State;
            TrainingJobState after = TrainingJobTransitions.Next(before, jobEvent);
            if (after != before)
            {
                job.State = after;
                job.FailureReason ??= TrainingJobTransitions.FailureReasonOf(before, jobEvent);
                if (TrainingJobTransitions.IsFinal(after))
                {
                    job.CompletedUtc = nowUtc;
                }
            }

            return after;
        }
    }

    // Drops the oldest finished jobs until the table is within MaxTrackedJobs; an active job is never dropped.
    private void TrimFinishedJobs()
    {
        while (_jobs.Count > ExecutionLimits.MaxTrackedJobs)
        {
            TrainingJobRecord? oldest = _jobs.Values.Where(job => TrainingJobTransitions.IsFinal(job.State)).MinBy(job => job.StartedUtc);
            if (oldest is null)
            {
                return;
            }

            _jobs.Remove(oldest.JobId);
        }
    }
}
