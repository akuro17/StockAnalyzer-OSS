using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// The bars of one calculation plus what every pattern engine reads from them: which bars are OHLC-consistent (spec 1.1), and the recursive and moving averages of the closes
/// (spec 2.1). Created once per <c>Calculate</c>; the averages are built on first use and cached per period.
/// <para>
/// An inconsistent bar (decision U2-15, plan R-14/R-15) is an ordinary input: its close is passed as <c>null</c> to the shared averages, so the EMA keeps its previous state across it
/// (one bad bar never contaminates later values) and an SMA whose window contains it is undefined. Windows are asked with <see cref="AllValid"/> (the read-set rule R-16).
/// </para>
/// Not thread-safe. The bars are read through <see cref="IReadOnlyList{T}"/>; only indices below <see cref="Count"/> are read (the list may be a longer pooled array).
/// </summary>
public sealed class PatternSeriesContext
{
    private readonly IReadOnlyList<CandleData> _bars;
    private readonly bool[] _valid;
    private readonly int[] _invalidPrefix;
    private decimal?[]? _closesOrNull;
    private readonly Dictionary<int, decimal?[]> _ema = new();
    private readonly Dictionary<int, decimal?[]> _sma = new();
    private decimal[]? _bodyPrefix;
    private decimal[]? _trueRangePrefix;
    private bool _contextPrefixComputed;
    private bool _contextPrefixAvailable;

    /// <param name="bars">The bars in chronological order.</param>
    /// <param name="count">How many of <paramref name="bars"/> belong to the series (the list may be longer).</param>
    public PatternSeriesContext(IReadOnlyList<CandleData> bars, int count)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (count < 0 || count > bars.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _bars = bars;
        Count = count;
        _valid = new bool[count];
        _invalidPrefix = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            CandleData bar = bars[i];
            _valid[i] = PatternMath.IsOhlcConsistent(in bar);
            _invalidPrefix[i + 1] = _invalidPrefix[i] + (_valid[i] ? 0 : 1);
        }
    }

    /// <summary>Number of bars of the series.</summary>
    public int Count { get; }

    /// <summary>The bars (read only indices below <see cref="Count"/>).</summary>
    public IReadOnlyList<CandleData> Bars => _bars;

    /// <summary>Bar <paramref name="index"/>.</summary>
    public CandleData this[int index] => _bars[index];

    /// <summary>True when bar <paramref name="index"/> exists and is OHLC-consistent.</summary>
    public bool IsValid(int index) => (uint)index < (uint)Count && _valid[index];

    /// <summary>True when every bar of <c>[from, to]</c> exists and is OHLC-consistent (an empty or out-of-range window is false).</summary>
    public bool AllValid(int from, int to)
    {
        if (from < 0 || to >= Count || to < from)
        {
            return false;
        }

        return _invalidPrefix[to + 1] - _invalidPrefix[from] == 0;
    }

    /// <summary>EMA of the closes (seeded with the SMA, alpha = 2/(period+1)) at <paramref name="index"/>; null while not defined or when the bar itself is inconsistent.</summary>
    public decimal? Ema(int period, int index)
    {
        if ((uint)index >= (uint)Count)
        {
            return null;
        }

        if (!_ema.TryGetValue(period, out decimal?[]? values))
        {
            values = IndicatorCalculationHelper.CalculateEmaWithNulls(ClosesOrNull(), period).ToArray();
            _ema[period] = values;
        }

        return values[index];
    }

    /// <summary>SMA of the closes at <paramref name="index"/>; null while not defined or when its window contains an inconsistent bar.</summary>
    public decimal? Sma(int period, int index)
    {
        if ((uint)index >= (uint)Count)
        {
            return null;
        }

        if (!_sma.TryGetValue(period, out decimal?[]? values))
        {
            values = IndicatorCalculationHelper.CalculateSmaWithNulls(ClosesOrNull(), period).ToArray();
            _sma[period] = values;
        }

        return values[index];
    }

    /// <summary>
    /// Computes context <c>(avgBody, atr)</c> over the <paramref name="period"/> bars ending at <paramref name="contextEnd"/> in O(1)
    /// via lazy prefix sums once built in O(N), matching <see cref="CandlePatternDetector.ComputeContext"/>.
    /// Returns false only when prefix sums cannot safely represent extreme input scales/magnitudes, signaling caller fallback.
    /// </summary>
    internal bool TryGetContextStats(int contextEnd, int period, out decimal avgBody, out decimal atr)
    {
        avgBody = 0m;
        atr = 0m;
        if (!_contextPrefixComputed)
        {
            BuildContextPrefixSums();
        }

        if (!_contextPrefixAvailable)
        {
            return false;
        }

        int start = contextEnd - period + 1;
        avgBody = (_bodyPrefix![contextEnd + 1] - _bodyPrefix[start]) / period;
        atr = (_trueRangePrefix![contextEnd + 1] - _trueRangePrefix[start]) / period;
        return true;
    }

    private void BuildContextPrefixSums()
    {
        _contextPrefixComputed = true;
        // Guard cumulative decimal sums against overflow when bars combine high decimal scale (up to 18) with long series lengths.
        const decimal MaxSafeBarValue = 1_000_000m;
        const int MaxSafeDecimalScale = 18;
        const decimal MaxSafeScaledCumulativeSum = 70_000_000_000m;
        decimal maxSafePerBar = Count > 0
            ? Math.Min(MaxSafeBarValue, MaxSafeScaledCumulativeSum / Count)
            : MaxSafeBarValue;

        var bodyPrefix = new decimal[Count + 1];
        var trPrefix = new decimal[Count + 1];

        for (int i = 0; i < Count; i++)
        {
            decimal bSum = bodyPrefix[i];
            decimal tSum = trPrefix[i];
            if (_valid[i])
            {
                CandleData bar = _bars[i];
                if (bar.High > maxSafePerBar
                    || bar.Open.Scale > MaxSafeDecimalScale
                    || bar.High.Scale > MaxSafeDecimalScale
                    || bar.Low.Scale > MaxSafeDecimalScale
                    || bar.Close.Scale > MaxSafeDecimalScale)
                {
                    _contextPrefixAvailable = false;
                    return;
                }

                bSum += Math.Abs(bar.Open - bar.Close);
                decimal tr = bar.High - bar.Low;
                if (i > 0 && _valid[i - 1])
                {
                    decimal prevClose = _bars[i - 1].Close;
                    tr = Math.Max(tr, Math.Max(
                        Math.Abs(bar.High - prevClose),
                        Math.Abs(bar.Low - prevClose)));
                }

                tSum += tr;
            }

            bodyPrefix[i + 1] = bSum;
            trPrefix[i + 1] = tSum;
        }

        _bodyPrefix = bodyPrefix;
        _trueRangePrefix = trPrefix;
        _contextPrefixAvailable = true;
    }

    private decimal?[] ClosesOrNull()
    {
        if (_closesOrNull != null)
        {
            return _closesOrNull;
        }

        var closes = new decimal?[Count];
        for (int i = 0; i < Count; i++)
        {
            closes[i] = _valid[i] ? _bars[i].Close : null;
        }

        _closesOrNull = closes;
        return closes;
    }
}
