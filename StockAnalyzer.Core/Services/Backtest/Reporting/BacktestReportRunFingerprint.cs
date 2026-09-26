using System;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// The run fingerprint as it appears in a <see cref="BacktestReport"/> and its JSON export (owner decision G5): the SHA-256 digest as a hex string, or - when the
/// strategy cannot state its own settings - the explicit reason no complete fingerprint exists. It is a separate, serialization-friendly type because
/// <see cref="BacktestRunFingerprint"/> only hands out its digest through methods, which a default JSON serialization would drop.
/// </summary>
public sealed record BacktestReportRunFingerprint
{
    /// <summary>False when no complete fingerprint exists; then <see cref="Sha256"/> is null and <see cref="UnavailableReason"/> says why.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Upper-case hex SHA-256 (64 characters); null when <see cref="IsAvailable"/> is false. Never an empty or partial hash.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Version of the canonical byte layout (<see cref="BacktestRunFingerprint.SchemaVersion"/>).</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Version of the engine's execution rules that produced the output (<see cref="BacktestRunFingerprint.ExecutionSemanticsVersion"/>).</summary>
    public int ExecutionSemanticsVersion { get; init; }

    public string? UnavailableReason { get; init; }

    public static BacktestReportRunFingerprint From(BacktestRunFingerprint fingerprint)
    {
        if (fingerprint is null) throw new ArgumentNullException(nameof(fingerprint));

        return fingerprint.IsAvailable
            ? new BacktestReportRunFingerprint
            {
                IsAvailable = true,
                Sha256 = fingerprint.ToHexString(),
                SchemaVersion = fingerprint.SchemaVersion,
                ExecutionSemanticsVersion = fingerprint.ExecutionSemanticsVersion,
            }
            : new BacktestReportRunFingerprint { IsAvailable = false, UnavailableReason = fingerprint.UnavailableReason };
    }
}
