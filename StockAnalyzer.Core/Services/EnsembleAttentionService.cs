using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Computes the attention bands of a saved classification ensemble. It never runs on the UI thread and never from a render path; the result is an
/// immutable snapshot, like the single-model attention.
/// </summary>
public interface IEnsembleAttentionService
{
    /// <summary>
    /// The weight-mixed attention of the LAST bar of <paramref name="history"/>. <paramref name="key"/> must carry <see cref="EnsembleSpec.Fingerprint"/>
    /// of <paramref name="spec"/> as its model id. Members without a usable attention sidecar (or with weight zero) do not contribute; when none
    /// contributes the result is <see cref="AttentionStatus.Unsupported"/> (<see cref="AttentionContract.UnsupportedReason.NoAttentionMember"/>), or
    /// <see cref="AttentionStatus.Failed"/> when a sidecar exists but cannot be run. Cancellation throws.
    /// </summary>
    Task<AttentionResult> ComputeAsync(EnsembleSpec spec, AttentionRequestKey key, IReadOnlyList<CandleData> history, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IEnsembleAttentionService"/> over the single-model attention code: every contributing member is computed through
/// <see cref="IAttentionService.ComputeMemberAsync"/> (sequentially, so at most one sidecar session is alive), then the member vectors are mixed by
/// <see cref="Combine"/>. Results are reused for the same key (ensemble fingerprint, symbol, timeframe, anchor bar, data revision) and the same
/// supplied history (<see cref="ComputeHistoryHash"/>), because the data revision is a per-chart counter and the service is shared by all charts.
/// </summary>
public sealed class EnsembleAttentionService : IEnsembleAttentionService
{
    /// <summary>Layout tag of <see cref="ComputeHistoryHash"/>; a change of the byte layout must change the tag.</summary>
    internal const string HistoryHashLayout = "SA-ATT-HIST-v1";

    private const int DecimalParts = 4;   // decimal.GetBits yields lo, mid, hi, flags
    private const int PricesPerCandle = 4;   // open, high, low, close
    private const int CandleBytes = sizeof(long) + PricesPerCandle * DecimalParts * sizeof(int) + sizeof(long);   // ticks + prices + volume

    /// <summary>The request key plus the content hash of the supplied history.</summary>
    private readonly record struct ResultKey(AttentionRequestKey Request, string HistoryHash);

    private readonly IAttentionService _members;
    private readonly BoundedLruCache<ResultKey, AttentionResult> _results;

    public EnsembleAttentionService(IAttentionService members)
        : this(members, ModelExplanationLimits.MaxCachedGenerations)
    {
    }

    /// <summary>A smaller capacity than the contract bound, so tests can exercise eviction.</summary>
    internal EnsembleAttentionService(IAttentionService members, int cacheCapacity)
    {
        _members = members ?? throw new ArgumentNullException(nameof(members));
        _results = new BoundedLruCache<ResultKey, AttentionResult>(cacheCapacity);
    }

    public async Task<AttentionResult> ComputeAsync(EnsembleSpec spec, AttentionRequestKey key, IReadOnlyList<CandleData> history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(history);
        if (key.ModelId != spec.Fingerprint())
            throw new ArgumentException("The request key does not carry the fingerprint of the ensemble.", nameof(key));
        cancellationToken.ThrowIfCancellationRequested();
        var resultKey = new ResultKey(key, ComputeHistoryHash(history));
        if (_results.TryGet(resultKey, out var reused)) return reused;

        var contributors = new List<(double Weight, AttentionSnapshot Snapshot)>();
        string? failure = null;
        foreach (var member in spec.Members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member.Weight <= 0) continue;   // it adds nothing to the prediction, so it adds nothing to the attention either
            var outcome = await _members.ComputeMemberAsync(key with { ModelId = member.ModelId }, history, cancellationToken).ConfigureAwait(false);
            if (outcome.Status == AttentionStatus.Ready && outcome.Snapshot is { } snapshot) contributors.Add((member.Weight, snapshot));
            else if (outcome.Status == AttentionStatus.Failed) failure ??= outcome.Detail;
        }

        var result = contributors.Count > 0
            ? Combine(key, contributors, spec.Members.Count, spec.Members.Sum(m => m.Weight))
            : failure is not null
                ? AttentionResult.Failed(failure)
                : AttentionResult.Unsupported(AttentionContract.UnsupportedReason.NoAttentionMember);
        if (result.Status != AttentionStatus.Failed) _results.TryAdd(resultKey, result);   // a failure may be transient: it is asked again
        return result;
    }

    /// <summary>
    /// Lower-case hexadecimal SHA-256 over <c>ASCII(layout + "\n") + Int32LE(count)</c> and, per candle in order, <c>Int64LE(Timestamp.Ticks)</c>
    /// (the <see cref="DateTimeKind"/> is ignored), the four <see cref="decimal.GetBits(decimal)"/> parts (lo, mid, hi, flags; little-endian) of open,
    /// high, low and close, and <c>Int64LE(Volume)</c>. It covers the WHOLE supplied history because composed-feature channels may read warm-up bars
    /// before the model window. Numerically equal decimals of different scale (<c>1.0m</c>, <c>1.00m</c>) hash differently, which can only cause a
    /// cache miss, never a wrong hit. Time O(N), memory O(1).
    /// </summary>
    internal static string ComputeHistoryHash(IReadOnlyList<CandleData> history)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(HistoryHashLayout + "\n"));
        Span<byte> buffer = stackalloc byte[CandleBytes];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, history.Count);
        hash.AppendData(buffer[..sizeof(int)]);
        Span<int> parts = stackalloc int[DecimalParts];
        for (int i = 0; i < history.Count; i++)
        {
            var candle = history[i];
            int offset = 0;
            BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], candle.Timestamp.Ticks);
            offset += sizeof(long);
            WriteDecimal(buffer, ref offset, candle.Open, parts);
            WriteDecimal(buffer, ref offset, candle.High, parts);
            WriteDecimal(buffer, ref offset, candle.Low, parts);
            WriteDecimal(buffer, ref offset, candle.Close, parts);
            BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], candle.Volume);
            hash.AppendData(buffer[..CandleBytes]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteDecimal(Span<byte> buffer, ref int offset, decimal value, Span<int> parts)
    {
        decimal.GetBits(value, parts);
        foreach (int part in parts)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer[offset..], part);
            offset += sizeof(int);
        }
    }

    /// <summary>
    /// Mixes the members' last-row attention vectors. The newest bar of every member is the anchor bar, so the vectors are right-aligned; a bar older
    /// than a member's window gets 0 from that member. The shares are the member weights renormalised over the contributing members, so the mixed
    /// vector is a convex combination (it still sums to 1); <c>Relative = mixed / max(mixed)</c>. <paramref name="totalWeight"/> is the sum of the weights of
    /// ALL members (also those that do not contribute); it only feeds <see cref="AttentionSnapshot.CoverageWeight"/>, which stays 0 when it is not given.
    /// </summary>
    internal static AttentionResult Combine(AttentionRequestKey key, IReadOnlyList<(double Weight, AttentionSnapshot Snapshot)> contributors, int memberCount,
        double totalWeight = 0d)
    {
        int length = contributors.Max(c => c.Snapshot.Weights.Length);
        var longest = contributors.First(c => c.Snapshot.Weights.Length == length).Snapshot;
        double total = contributors.Sum(c => c.Weight);
        var mixed = new double[length];
        foreach (var (weight, snapshot) in contributors)
        {
            int offset = length - snapshot.Weights.Length;
            for (int i = 0; i < snapshot.Weights.Length; i++)
                if (snapshot.Timestamps[i] != longest.Timestamps[offset + i])
                    return AttentionResult.Failed("the attention windows of the ensemble members do not end at the same bar");
            double share = weight / total;
            for (int i = 0; i < snapshot.Weights.Length; i++) mixed[offset + i] += share * snapshot.Weights[i];
        }

        double max = mixed.Max();
        if (!(max > 0) || !double.IsFinite(max)) return AttentionResult.Failed("the mixed attention is not usable");
        var weights = ImmutableArray.CreateRange(mixed.Select(v => (float)v));
        var relative = ImmutableArray.CreateRange(mixed.Select(v => (float)(v / max)));
        return AttentionResult.Ready(new AttentionSnapshot(key, contributors.Max(c => c.Snapshot.Heads), longest.Timestamps, weights, relative,
            contributors.Count, memberCount, CoverageWeight: totalWeight > 0 ? total / totalWeight : 0d,
            WindowsDiffer: contributors.Select(c => c.Snapshot.Weights.Length).Distinct().Count() > 1));
    }
}
