using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

public interface IRlModelGenerationRegistry
{
    RlRegistryCapabilities Capabilities => RlRegistryCapabilities.None;
    RlRegistrySnapshot CaptureRlSnapshot(CancellationToken ct = default)
        => throw new System.NotSupportedException("A coherent RL registry snapshot is unavailable.");
    string? RlActiveId { get; }
    string? RlContinuousActiveId => null;
    Task<RlGeneration> RegisterRlAsync(string artifactDirectory, CancellationToken cancellationToken = default);
    IReadOnlyList<RlGeneration> ListRl();
    Task<bool> ActivateRlAsync(string modelId, CancellationToken cancellationToken = default);
    ModelGenerationLease? AcquireRl(string modelId);
    ModelGenerationLease? AcquireRlActive();
    RlGeneration? GetRlGeneration(string modelId);
    RlTemporalEvidence? CaptureRlTemporalEvidence(string modelId) => null;
    RlPointerRecovery? PreviewRlRecovery(string target) => null;
    RlPointerRecovery? PreviewRlRecovery(string target, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return PreviewRlRecovery(target); }
    Task<bool> RecoverRlPointerAsync(RlPointerRecovery reviewed, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(new System.NotSupportedException("Registry recovery is unavailable."));
    RlArtifactPolicy? RlArtifactPolicy => null;
    Task SaveRlArtifactPolicyAsync(RlArtifactPolicy policy, CancellationToken ct = default) => Task.FromException(new System.NotSupportedException());
    RlArtifactPreview? PreviewRlArtifacts() => null;
    RlArtifactPreview? PreviewRlArtifacts(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return PreviewRlArtifacts(); }
    Task<RlArtifactCleanupResult> CleanupRlArtifactsAsync(RlArtifactPreview reviewed, CancellationToken ct = default)
        => Task.FromException<RlArtifactCleanupResult>(new System.NotSupportedException());
    /// <summary>Registered models whose work folder no longer exists; empty when that cannot be determined.</summary>
    IReadOnlySet<string> RlModelIdsWithoutWorkFolder() => System.Collections.Immutable.ImmutableHashSet<string>.Empty;
    /// <summary>Removes one registered model whose work folder is gone; refused (nothing deleted) with the reasons otherwise.</summary>
    Task<RlGenerationRemoval> RemoveRlGenerationAsync(string modelId, CancellationToken ct = default)
        => Task.FromException<RlGenerationRemoval>(new System.NotSupportedException());
    Task<RlArtifactRunLease?> BeginRlArtifactRunAsync(System.Guid runId, string dataRoot, CancellationToken ct = default)
        => Task.FromResult<RlArtifactRunLease?>(null);
    IReadOnlyList<RlCheckpointReview> ListRlCheckpoints() => System.Array.Empty<RlCheckpointReview>();
    IReadOnlyList<RlCheckpointReview> ListRlCheckpoints(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ListRlCheckpoints(); }
    Task AbandonRlResumeAsync(RlCheckpointReview reviewed, CancellationToken ct = default)
        => Task.FromException(new System.NotSupportedException());
}
