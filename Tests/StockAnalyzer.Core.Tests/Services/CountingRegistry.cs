#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Presents one real generation under any number of ids ("alias-*") and counts how often a service reads it. An id
/// starting with "gated" blocks inside <c>Acquire</c> until <see cref="Gate"/> is set, to hold a load in flight.
/// </summary>
internal sealed class CountingRegistry(IModelGenerationRegistry inner, string realId) : IModelGenerationRegistry
{
    private int _acquires;
    public int Acquires => Volatile.Read(ref _acquires);
    public ManualResetEventSlim Gate { get; } = new(false);
    public ManualResetEventSlim Entered { get; } = new(false);

    private string Real(string id) => id.StartsWith("alias", StringComparison.Ordinal) || id.StartsWith("gated", StringComparison.Ordinal)
        ? realId : id;

    public ModelGenerationLease? Acquire(string modelId)
    {
        Interlocked.Increment(ref _acquires);
        if (modelId.StartsWith("gated", StringComparison.Ordinal))
        {
            Entered.Set();
            Gate.Wait(TimeSpan.FromSeconds(30));
        }

        return inner.Acquire(Real(modelId));
    }

    public ModelGenerationManifest? GetManifest(string modelId) => inner.GetManifest(Real(modelId));
    public string? ActiveId => inner.ActiveId;
    public string? PreviousId => inner.PreviousId;
    public IReadOnlyList<ModelGenerationSummary> List() => inner.List();
    public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null, CancellationToken ct = default) =>
        inner.RegisterAsync(onnxPath, metricsPath, ct);
    public Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false, CancellationToken ct = default) =>
        inner.ActivateAsync(modelId, manual, confirmLowerScore, ct);
    public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId, CancellationToken ct = default) =>
        inner.ConfirmActivationAsync(modelId, expectedActiveId, ct);
    public Task<bool> RollbackAsync(bool confirmLowerScore = false, CancellationToken ct = default) =>
        inner.RollbackAsync(confirmLowerScore, ct);
    public ModelGenerationLease? AcquireActive() => inner.AcquireActive();
}
