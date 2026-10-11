using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Computes the attention of the newest prediction of one registered model generation from its attention sidecar. It never runs on the UI thread and never
/// from a render path; the result is an immutable snapshot.
/// </summary>
public interface IAttentionService
{
    /// <summary>
    /// Attention of the LAST bar of <paramref name="history"/> over the model window (heads averaged). A model without a valid sidecar, of another timeframe, or
    /// unknown to the registry is <see cref="AttentionStatus.Unsupported"/> (never an error); a sidecar that is present but cannot be run is
    /// <see cref="AttentionStatus.Failed"/>. Cancellation throws.
    /// </summary>
    Task<AttentionResult> ComputeAsync(AttentionRequestKey key, IReadOnlyList<CandleData> history, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same attention for one member of an ensemble: the model is read under its OWN contract (window, feature mode and feature spec come from the
    /// model's metadata, as the ensemble prediction reads them), so the configured single-model window and feature settings do not apply. Everything
    /// else — sidecar binding checks, causal mask, row sums, the tensor builders — is the code <see cref="ComputeAsync"/> runs.
    /// </summary>
    Task<AttentionResult> ComputeMemberAsync(AttentionRequestKey key, IReadOnlyList<CandleData> history, CancellationToken cancellationToken = default);
}
