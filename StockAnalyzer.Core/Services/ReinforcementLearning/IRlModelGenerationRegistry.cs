using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

public interface IRlModelGenerationRegistry
{
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
    Task<bool> RecoverRlPointerAsync(RlPointerRecovery reviewed, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(new System.NotSupportedException("Registry recovery is unavailable."));
    RlArtifactPolicy? RlArtifactPolicy => null;
    Task SaveRlArtifactPolicyAsync(RlArtifactPolicy policy, CancellationToken ct = default) => Task.FromException(new System.NotSupportedException());
    RlArtifactPreview? PreviewRlArtifacts() => null;
    Task<RlArtifactCleanupResult> CleanupRlArtifactsAsync(RlArtifactPreview reviewed, CancellationToken ct = default)
        => Task.FromException<RlArtifactCleanupResult>(new System.NotSupportedException());
    Task<RlArtifactRunLease?> BeginRlArtifactRunAsync(System.Guid runId, string dataRoot, CancellationToken ct = default)
        => Task.FromResult<RlArtifactRunLease?>(null);
    IReadOnlyList<RlCheckpointReview> ListRlCheckpoints() => System.Array.Empty<RlCheckpointReview>();
    Task AbandonRlResumeAsync(RlCheckpointReview reviewed, CancellationToken ct = default)
        => Task.FromException(new System.NotSupportedException());
}
