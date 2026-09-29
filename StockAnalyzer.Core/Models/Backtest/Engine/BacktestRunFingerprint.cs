using System;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// Versioned canonical identity of one run - its exact input, configuration, strategy manifest, full output and status (owner decision G5,
/// A1 (Run fingerprint)). It is ADDITIVE: <see cref="BacktestResult.ReproducibilityHash"/> and its algorithm are unchanged.
/// A run whose strategy cannot state its own settings has an unavailable fingerprint with an explicit reason - never a partial hash presented as complete.
/// The value is immutable and only hands out copies of its bytes. SHA-256 is treated as a strong hash, not as mathematically collision-free.
/// </summary>
public sealed class BacktestRunFingerprint : IEquatable<BacktestRunFingerprint>
{
    private readonly ImmutableArray<byte> _hash;

    internal BacktestRunFingerprint(ImmutableArray<byte> hash, int schemaVersion, int executionSemanticsVersion)
    {
        _hash = hash;
        SchemaVersion = schemaVersion;
        ExecutionSemanticsVersion = executionSemanticsVersion;
    }

    private BacktestRunFingerprint(string unavailableReason)
    {
        _hash = ImmutableArray<byte>.Empty;
        UnavailableReason = unavailableReason;
    }

    /// <summary>Version of the canonical byte layout.</summary>
    public int SchemaVersion { get; }

    /// <summary>Version of the engine's execution rules that produced the output (<c>BacktestExecutionSemantics.Version</c>).</summary>
    public int ExecutionSemanticsVersion { get; }

    public bool IsAvailable => UnavailableReason is null;

    /// <summary>Why no complete fingerprint exists; null when <see cref="IsAvailable"/>.</summary>
    public string? UnavailableReason { get; }

    public static BacktestRunFingerprint Unavailable(string reason)
        => new(reason ?? throw new ArgumentNullException(nameof(reason)));

    /// <summary>A copy of the SHA-256 digest (empty when unavailable); mutating it never changes this value.</summary>
    public byte[] GetHash() => _hash.ToArray();

    public string ToHexString() => Convert.ToHexString(_hash.AsSpan());

    public bool Equals(BacktestRunFingerprint? other)
        => other is not null
            && UnavailableReason == other.UnavailableReason
            && SchemaVersion == other.SchemaVersion
            && ExecutionSemanticsVersion == other.ExecutionSemanticsVersion
            && _hash.AsSpan().SequenceEqual(other._hash.AsSpan());

    public override bool Equals(object? obj) => Equals(obj as BacktestRunFingerprint);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(ExecutionSemanticsVersion);
        hash.Add(UnavailableReason);
        hash.AddBytes(_hash.AsSpan());
        return hash.ToHashCode();
    }

    public override string ToString() => IsAvailable ? ToHexString() : $"unavailable: {UnavailableReason}";
}
