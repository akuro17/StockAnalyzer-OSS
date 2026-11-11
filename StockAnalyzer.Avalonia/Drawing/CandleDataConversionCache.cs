using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Keeps the <see cref="CandleData"/> array converted from one candle list so that the repeated recalculations of a drag or of a
/// shape being drawn convert the candles once instead of on every pointer move. One instance belongs to one owner (the chart
/// interaction controller); it is used from the UI thread only and is never static.
/// </summary>
/// <remarks>
/// A cached array is reused only when ALL of these hold: the source is the same list instance, it has the same
/// <c>Count</c> (greater than 0), and its first and last elements are the same instances. The source is held weakly, so a
/// replaced list (and with it the old array's reason to exist) can be collected. Known residual risk: replacing only a
/// middle element of the same list instance while keeping Count, first and last would be missed; the owner therefore calls
/// <see cref="Invalidate"/> whenever a drawing or drag gesture starts or ends.
/// </remarks>
internal sealed class CandleDataConversionCache
{
    private WeakReference<IReadOnlyList<CoreCandleData>>? _source;
    private int _count;
    private CoreCandleData? _first;
    private CoreCandleData? _last;
    private CandleData[]? _converted;

    /// <summary>Number of times a conversion was actually performed (for tests).</summary>
    internal int ConversionCount { get; private set; }

    /// <summary>Returns the converted candles; reconverts only when the cached array no longer matches <paramref name="candles"/>.</summary>
    public IEnumerable<CandleData> Get(IReadOnlyList<CoreCandleData> candles)
    {
        int count = candles.Count;
        if (count == 0)
        {
            Invalidate();
            return Array.Empty<CandleData>();
        }

        if (_converted != null
            && _count == count
            && _source != null && _source.TryGetTarget(out var cachedSource) && ReferenceEquals(cachedSource, candles)
            && ReferenceEquals(candles[0], _first)
            && ReferenceEquals(candles[count - 1], _last))
        {
            return _converted;
        }

        _converted = CandleDataConverter.ToArray(candles);
        _source = new WeakReference<IReadOnlyList<CoreCandleData>>(candles);
        _count = count;
        _first = candles[0];
        _last = candles[count - 1];
        ConversionCount++;
        return _converted;
    }

    /// <summary>Drops the cached array and every reference to the source list.</summary>
    public void Invalidate()
    {
        _converted = null;
        _source = null;
        _first = null;
        _last = null;
        _count = 0;
    }
}
