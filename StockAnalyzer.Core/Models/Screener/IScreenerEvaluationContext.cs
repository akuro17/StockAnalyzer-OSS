using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Analysis;

namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// Per-scan cross-sectional values precomputed across the screening universe before individual symbol evaluation.
/// </summary>
public interface IScreenerEvaluationContext
{
    /// <summary>
    /// Gets the precomputed IBD-style RS Rating (1-99) for <paramref name="symbol"/>, or <c>null</c> when unavailable.
    /// </summary>
    int? GetRsRating(string? symbol);

    /// <summary>
    /// Attempts to retrieve a precomputed <see cref="MarketBreadthResult"/> for <paramref name="key"/>
    /// computed once at scan start, returning <c>true</c> if that key was looked up during pre-fetch.
    /// </summary>
    bool TryGetUniverseBreadth(UniverseBreadthKey key, out MarketBreadthResult? result)
    {
        result = null;
        return false;
    }

    /// <summary>
    /// Optional single scan <see cref="TimeFrame"/> used by plain one-frame scans when side configurations store <see cref="TimeFrame.D1"/>.
    /// </summary>
    TimeFrame? ScanTimeFrame => null;

    /// <summary>
    /// Optional inclusive as-of date cutoff for historical scans so universe-breadth lookups
    /// exclude post-cutoff bars.
    /// </summary>
    DateOnly? AsOfDate => null;
}

/// <summary>
/// Default immutable <see cref="IScreenerEvaluationContext"/> backed by precomputed per-symbol and per-universe lookups.
/// </summary>
public sealed class ScreenerEvaluationContext : IScreenerEvaluationContext
{
    /// <summary>
    /// An empty evaluation context that always returns <c>null</c> (used when RS Rating is unavailable or disabled, e.g. as-of scans).
    /// </summary>
    public static ScreenerEvaluationContext Empty { get; } =
        new ScreenerEvaluationContext(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    private readonly IReadOnlyDictionary<string, int> _rsRatings;
    private readonly IReadOnlyDictionary<UniverseBreadthKey, MarketBreadthResult?>? _universeBreadths;
    private readonly IScreenerEvaluationContext? _inner;
    private readonly TimeFrame? _scanTimeFrame;
    private readonly DateOnly? _asOfDate;
    private readonly bool _blockRsRating;

    public ScreenerEvaluationContext(IReadOnlyDictionary<string, int>? rsRatings)
        : this(rsRatings, universeBreadths: null, scanTimeFrame: null, asOfDate: null, blockRsRating: false, inner: null)
    {
    }

    public ScreenerEvaluationContext(
        IReadOnlyDictionary<string, int>? rsRatings,
        IReadOnlyDictionary<UniverseBreadthKey, MarketBreadthResult?>? universeBreadths,
        TimeFrame? scanTimeFrame = null)
        : this(rsRatings, universeBreadths, scanTimeFrame, asOfDate: null, blockRsRating: false, inner: null)
    {
    }

    private ScreenerEvaluationContext(
        IReadOnlyDictionary<string, int>? rsRatings,
        IReadOnlyDictionary<UniverseBreadthKey, MarketBreadthResult?>? universeBreadths,
        TimeFrame? scanTimeFrame,
        DateOnly? asOfDate,
        bool blockRsRating,
        IScreenerEvaluationContext? inner)
    {
        _rsRatings = rsRatings ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _universeBreadths = universeBreadths;
        _scanTimeFrame = scanTimeFrame;
        _asOfDate = asOfDate;
        _blockRsRating = blockRsRating;
        _inner = inner;
    }

    /// <inheritdoc />
    public TimeFrame? ScanTimeFrame => _scanTimeFrame ?? _inner?.ScanTimeFrame;

    /// <inheritdoc />
    public DateOnly? AsOfDate => _asOfDate ?? _inner?.AsOfDate;

    /// <inheritdoc />
    public int? GetRsRating(string? symbol)
    {
        if (_blockRsRating)
        {
            return null;
        }

        if (_inner != null)
        {
            return _inner.GetRsRating(symbol);
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        return _rsRatings.TryGetValue(symbol, out int rating) ? rating : null;
    }

    /// <inheritdoc />
    public bool TryGetUniverseBreadth(UniverseBreadthKey key, out MarketBreadthResult? result)
    {
        if (_universeBreadths != null && _universeBreadths.TryGetValue(key, out result))
        {
            return true;
        }

        if (_inner != null && _inner.TryGetUniverseBreadth(key, out result))
        {
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Creates an evaluation context scoped to a historical <paramref name="asOfDate"/>:
    /// blocks RS Rating (which uses today's prices) while preserving universe-breadth lookups
    /// and exposing <see cref="AsOfDate"/> for cutoff-aligned breadth resolution.
    /// </summary>
    public static IScreenerEvaluationContext ForAsOfDate(DateOnly asOfDate, IScreenerEvaluationContext? inner = null)
        => new ScreenerEvaluationContext(
            rsRatings: null,
            universeBreadths: null,
            scanTimeFrame: inner?.ScanTimeFrame,
            asOfDate: asOfDate,
            blockRsRating: true,
            inner: inner);

    /// <summary>
    /// Creates an evaluation context combining <paramref name="inner"/> (if any) with pre-fetched
    /// <paramref name="universeBreadths"/>, optional single-frame <paramref name="scanTimeFrame"/>,
    /// and optional historical <paramref name="asOfDate"/>.
    /// </summary>
    public static IScreenerEvaluationContext Merge(
        IScreenerEvaluationContext? inner,
        IReadOnlyDictionary<UniverseBreadthKey, MarketBreadthResult?>? universeBreadths,
        TimeFrame? scanTimeFrame = null,
        DateOnly? asOfDate = null)
    {
        DateOnly? effectiveAsOfDate = asOfDate ?? inner?.AsOfDate;
        if ((universeBreadths == null || universeBreadths.Count == 0)
            && (!scanTimeFrame.HasValue || inner?.ScanTimeFrame == scanTimeFrame)
            && (!effectiveAsOfDate.HasValue || inner?.AsOfDate == effectiveAsOfDate))
        {
            return inner ?? Empty;
        }

        return new ScreenerEvaluationContext(
            rsRatings: null,
            universeBreadths,
            scanTimeFrame,
            effectiveAsOfDate,
            blockRsRating: false,
            inner);
    }
}
