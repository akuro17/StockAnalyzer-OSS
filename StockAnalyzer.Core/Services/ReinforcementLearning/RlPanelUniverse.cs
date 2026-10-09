using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Pure rules of a portfolio universe: input validation and canonical order, the deterministic symbol split and the identity hashes.
/// Nothing here reads a file.
/// </summary>
public static class RlPanelUniverse
{
    private const string Prefix = RlPortfolioContract.UniverseErrorPrefix;
    private const int SeedHexDigits = 8;

    private sealed record SymbolSetDocument(int Schema, ImmutableArray<string> Symbols);
    private sealed record MembershipDocument(int Schema, ImmutableArray<string> Symbols, ImmutableArray<string> SourceManifestSha256);
    private sealed record UniverseDocument(int Schema, string MembershipSha256, uint SplitSeed, bool HoldoutEnabled, int HoldoutPercent,
        ImmutableArray<string> HoldoutSymbols);

    /// <summary>
    /// Validates the symbol list in the documented order (safe names: <see cref="AttestedFileNames.IsPlain"/>, so also no Windows device
    /// name, no trailing period and Unicode NFC only; no duplicates by OrdinalIgnoreCase, count bounds) and returns it in
    /// canonical Ordinal order. A violation throws <see cref="ArgumentException"/> with the universe prefix.
    /// </summary>
    public static ImmutableArray<string> Canonicalize(IReadOnlyList<string> symbols, bool holdoutEnabled, int maxSymbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var trimmed = new List<string>(symbols.Count);
        foreach (string? raw in symbols)
        {
            string symbol = raw?.Trim() ?? string.Empty;
            // The symbol becomes a folder name and a source file name, so it must pass the same rule as the attested instrument id.
            if (!AttestedFileNames.IsPlain(symbol))
                throw new ArgumentException($"{Prefix} every symbol must be a safe, non-empty name.", nameof(symbols));
            trimmed.Add(symbol);
        }
        var duplicate = trimmed.GroupBy(s => s, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"{Prefix} duplicate symbol '{duplicate.Key}'.", nameof(symbols));
        int minimum = holdoutEnabled ? RlPortfolioContract.MinimumSymbolsWithHoldout : RlPortfolioContract.MinimumSymbols;
        if (trimmed.Count < minimum)
            throw new ArgumentException($"{Prefix} {trimmed.Count} symbols are fewer than the minimum {minimum}.", nameof(symbols));
        if (trimmed.Count > maxSymbols)
            throw new ArgumentException($"{Prefix} {trimmed.Count} symbols exceed the maximum {maxSymbols}.", nameof(symbols));
        trimmed.Sort(StringComparer.Ordinal);
        return trimmed.ToImmutableArray();
    }

    /// <summary>Holdout size: <c>max(2, ceil(percent * n / 100))</c> in exact integer arithmetic.</summary>
    public static int HoldoutCount(int symbolCount, int holdoutPercent)
    {
        RequirePercent(holdoutPercent);
        return Math.Max(RlPortfolioContract.MinimumHoldoutSymbols,
            checked((symbolCount * holdoutPercent + RlPercent.Divisor - 1) / RlPercent.Divisor));
    }

    /// <summary>
    /// Deterministic symbol split. Symbols are ordered by <c>SHA-256(UTF-8(symbol + "|" + seed))</c> as unsigned big-endian bytes (ties by
    /// Ordinal symbol); the first <see cref="HoldoutCount"/> are held out. The result is aligned with <paramref name="symbols"/> and does not
    /// depend on the order of that list.
    /// </summary>
    public static ImmutableArray<bool> AssignHoldout(IReadOnlyList<string> symbols, uint seed, int holdoutPercent)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (symbols.Count < RlPortfolioContract.MinimumSymbolsWithHoldout)
            throw new ArgumentException($"{Prefix} a symbol holdout needs at least {RlPortfolioContract.MinimumSymbolsWithHoldout} symbols.", nameof(symbols));
        int holdout = HoldoutCount(symbols.Count, holdoutPercent);
        if (symbols.Count - holdout < RlPortfolioContract.MinimumTrainSymbols)
            throw new ArgumentException($"{Prefix} the holdout would leave fewer than {RlPortfolioContract.MinimumTrainSymbols} training symbols.", nameof(holdoutPercent));
        string suffix = "|" + seed.ToString(CultureInfo.InvariantCulture);
        var ranked = Enumerable.Range(0, symbols.Count)
            .Select(i => (Index: i, Key: SHA256.HashData(Encoding.UTF8.GetBytes(symbols[i] + suffix))))
            .OrderBy(p => p.Key, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))
            .ThenBy(p => symbols[p.Index], StringComparer.Ordinal)
            .ToArray();
        var flags = new bool[symbols.Count];
        for (int rank = 0; rank < holdout; rank++) flags[ranked[rank].Index] = true;
        return flags.ToImmutableArray();
    }

    /// <summary>
    /// Hash of {schema, ordered symbols} only. It is the single input of the split seed, so the held-out symbols depend on the symbol list and
    /// on nothing else: not on capture timestamps, source file hashes, the period or the holdout share.
    /// </summary>
    public static string SymbolSetSha256(IReadOnlyList<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        return ContentHash.OfText(JsonSerializer.Serialize(new SymbolSetDocument(RlPortfolioContract.PanelSchemaVersion, symbols.ToImmutableArray()), RlFiles.Json));
    }

    /// <summary>Hash of {schema, ordered symbols, ordered source manifest hashes}: the identity of the captured data. It does not feed the split seed.</summary>
    public static string MembershipSha256(IReadOnlyList<string> symbols, IReadOnlyList<string> sourceManifestSha256)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(sourceManifestSha256);
        if (symbols.Count != sourceManifestSha256.Count) throw new ArgumentException($"{Prefix} symbols and source hashes differ in length.");
        return ContentHash.OfText(JsonSerializer.Serialize(new MembershipDocument(RlPortfolioContract.PanelSchemaVersion,
            symbols.ToImmutableArray(), sourceManifestSha256.ToImmutableArray()), RlFiles.Json));
    }

    /// <summary>Split seed: the first eight hexadecimal digits of the symbol-set hash (<see cref="SymbolSetSha256"/>) as an unsigned 32-bit number.</summary>
    public static uint SplitSeed(string symbolSetSha256)
    {
        if (!ContentHash.IsLowerHex64(symbolSetSha256)) throw new ArgumentException($"{Prefix} the symbol-set hash is malformed.", nameof(symbolSetSha256));
        return uint.Parse(symbolSetSha256.AsSpan(0, SeedHexDigits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    /// <summary>Hash of the membership, the seed and the complete split definition: a changed ratio or holdout flag is a different universe.</summary>
    public static string UniverseSha256(string membershipSha256, uint splitSeed, bool holdoutEnabled, int holdoutPercent,
        IReadOnlyList<string> holdoutSymbols)
    {
        ArgumentNullException.ThrowIfNull(holdoutSymbols);
        return ContentHash.OfText(JsonSerializer.Serialize(new UniverseDocument(RlPortfolioContract.PanelSchemaVersion, membershipSha256,
            splitSeed, holdoutEnabled, holdoutPercent, holdoutSymbols.ToImmutableArray()), RlFiles.Json));
    }

    /// <summary>Hash of the ISO-8601 dates of the union calendar joined by line feeds.</summary>
    public static string CalendarSha256(IReadOnlyList<DateTime> calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return ContentHash.OfText(string.Join("\n", calendar.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
    }

    private static void RequirePercent(int holdoutPercent)
    {
        var band = RlPortfolioLimits.HoldoutPercent;
        if (!band.IsWithinHardLimits(holdoutPercent))
            throw new ArgumentOutOfRangeException(nameof(holdoutPercent), $"{Prefix} the holdout percent must be {band.HardMin} to {band.HardMax}.");
    }
}
