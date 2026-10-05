using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

public enum RlArtifactRunState { Running, Completed, Failed, Cancelled }
public enum RlArtifactKind { FailedRun, CompletedRun, Generation, PendingDeletion, UnknownRun, UnknownGeneration, Checkpoint }
public enum RlArtifactRetentionReason { Active, Rollback, Backup, Dependency, Lease, Newest, NotExpired,
    SettingsIncomplete, UnverifiedPointers, UnknownOwner, Interrupted, UnregisteredResult, CompletedCopy, ExpiredFailure, ExpiredGeneration, RetryDeletion,
    Resumable, SurplusCheckpoint }
public sealed record RlArtifactEntry(string Id, string Kind, string Path, long Bytes, DateTimeOffset CreatedUtc,
    string Identity, ImmutableArray<string> Reasons, bool Candidate);
public sealed record RlArtifactPreview(RlArtifactPolicy Policy, string Context, long UsedBytes, long FreeBytes,
    ImmutableArray<RlArtifactEntry> Entries);
public sealed record RlArtifactCleanupResult(ImmutableArray<string> Deleted, ImmutableArray<string> Failures,
    long RemainingBytes);
public sealed class RlArtifactRunLease : IDisposable
{
    private IDisposable? _lease;
    private readonly Func<RlArtifactRunState, string?, CancellationToken, Task> _finish;
    internal RlArtifactRunLease(string directory, RlArtifactPolicy policy, IDisposable lease,
        Func<RlArtifactRunState, string?, CancellationToken, Task> finish)
    { Directory = directory; Policy = policy; _lease = lease; _finish = finish; }
    public string Directory { get; }
    public RlArtifactPolicy Policy { get; }
    public Task FinishAsync(RlArtifactRunState state, string? modelId = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _lease) is null, this);
        return _finish(state, modelId, ct);
    }
    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}
