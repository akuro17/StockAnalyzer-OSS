using System;
using System.Collections.Generic;
using System.Globalization;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Specific OHLCV invariant violation codes for <see cref="CandleData"/>.
/// </summary>
public enum CandleViolationCode
{
    HighBelowLow,
    HighBelowOpen,
    HighBelowClose,
    LowAboveOpen,
    LowAboveClose,
    NegativeVolume,
    NonPositivePrice,
}

/// <summary>
/// Single source of truth for diagnosing invalid <see cref="CandleData"/> OHLCV bars without modifying or repairing data.
/// </summary>
public static class CandleInvariant
{
    /// <summary>
    /// True iff <paramref name="bar"/> satisfies <see cref="CandleData.IsValid"/> and has strictly positive Open, High, Low, and Close prices.
    /// </summary>
    public static bool IsValidBar(CandleData bar)
        => bar.IsValid() && bar.Open > 0m && bar.High > 0m && bar.Low > 0m && bar.Close > 0m;

    /// <summary>
    /// Returns all OHLCV invariant violations on <paramref name="bar"/> in deterministic enum order, or an empty list when valid.
    /// </summary>
    public static IReadOnlyList<CandleViolationCode> Describe(CandleData bar)
    {
        if (IsValidBar(bar))
        {
            return Array.Empty<CandleViolationCode>();
        }

        var violations = new List<CandleViolationCode>(7);
        if (bar.High < bar.Low) violations.Add(CandleViolationCode.HighBelowLow);
        if (bar.High < bar.Open) violations.Add(CandleViolationCode.HighBelowOpen);
        if (bar.High < bar.Close) violations.Add(CandleViolationCode.HighBelowClose);
        if (bar.Low > bar.Open) violations.Add(CandleViolationCode.LowAboveOpen);
        if (bar.Low > bar.Close) violations.Add(CandleViolationCode.LowAboveClose);
        if (bar.Volume < 0) violations.Add(CandleViolationCode.NegativeVolume);
        if (bar.Open <= 0m || bar.High <= 0m || bar.Low <= 0m || bar.Close <= 0m) violations.Add(CandleViolationCode.NonPositivePrice);
        return violations;
    }

    /// <summary>
    /// Formats violation codes on <paramref name="bar"/> as human-readable diagnostic clauses using invariant culture.
    /// </summary>
    public static string FormatViolationDetail(CandleData bar, IReadOnlyList<CandleViolationCode> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        if (codes.Count == 0)
        {
            return string.Empty;
        }

        var parts = new string[codes.Count];
        for (int i = 0; i < codes.Count; i++)
        {
            parts[i] = codes[i] switch
            {
                CandleViolationCode.HighBelowLow => FormattableString.Invariant($"High {bar.High} < Low {bar.Low}"),
                CandleViolationCode.HighBelowOpen => FormattableString.Invariant($"High {bar.High} < Open {bar.Open}"),
                CandleViolationCode.HighBelowClose => FormattableString.Invariant($"Close {bar.Close} > High {bar.High}"),
                CandleViolationCode.LowAboveOpen => FormattableString.Invariant($"Low {bar.Low} > Open {bar.Open}"),
                CandleViolationCode.LowAboveClose => FormattableString.Invariant($"Close {bar.Close} < Low {bar.Low}"),
                CandleViolationCode.NegativeVolume => FormattableString.Invariant($"Volume {bar.Volume} < 0"),
                CandleViolationCode.NonPositivePrice => FormattableString.Invariant($"Non-positive price (O={bar.Open}, H={bar.High}, L={bar.Low}, C={bar.Close})"),
                _ => throw new ArgumentOutOfRangeException(nameof(codes), codes[i], "Unsupported candle violation code."),
            };
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Formats a path-free diagnostic sentence naming symbol, date (yyyy-MM-dd), OHLCV values, and violations.
    /// </summary>
    public static string FormatBarViolation(string symbol, CandleData bar, IReadOnlyList<CandleViolationCode> codes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        return FormattableString.Invariant(
            $"{symbol} {bar.Timestamp:yyyy-MM-dd} (O={bar.Open}, H={bar.High}, L={bar.Low}, C={bar.Close}, V={bar.Volume}): {FormatViolationDetail(bar, codes)}");
    }

    /// <summary>
    /// Scans <paramref name="bars"/> in chronological order and returns the first invalid bar and its violations, optionally bounded by date.
    /// </summary>
    public static bool TryFindFirstViolation(
        IReadOnlyList<CandleData> bars,
        out CandleData invalidBar,
        out IReadOnlyList<CandleViolationCode> violations,
        DateOnly? startDateInclusive = null,
        DateOnly? endDateExclusive = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        for (int i = 0; i < bars.Count; i++)
        {
            CandleData bar = bars[i];
            if (startDateInclusive is not null || endDateExclusive is not null)
            {
                DateOnly date = DateOnly.FromDateTime(bar.Timestamp);
                if (startDateInclusive is { } start && date < start)
                {
                    continue;
                }

                if (endDateExclusive is { } end && date >= end)
                {
                    continue;
                }
            }

            IReadOnlyList<CandleViolationCode> found = Describe(bar);
            if (found.Count > 0)
            {
                invalidBar = bar;
                violations = found;
                return true;
            }
        }

        invalidBar = default;
        violations = Array.Empty<CandleViolationCode>();
        return false;
    }
}
