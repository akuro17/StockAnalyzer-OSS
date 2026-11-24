using System.Collections;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Confluence;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// A result calculated on a higher time frame (W1/MN1) and carried onto the chart's own bars: every named series has the chart's bar count and holds, at each
/// chart bar, the value of the most recently CLOSED higher-frame period (null before the first one). Only named series survive; the payload and signals of the
/// higher-frame calculation are bound to its own bars and are therefore not carried (<see cref="CustomData"/> and <see cref="SignalProvider"/> are null).
/// Series labels are read from the calculated result so the legend text is the indicator's own.
/// </summary>
public sealed class TimeframeAlignedIndicatorResult : IIndicatorResult, IPrimitiveAwareIndicatorResult
{
    private readonly IIndicatorResult _source;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<decimal?>> _series;
    private readonly IReadOnlyList<string> _names;

    /// <param name="source">The successful higher-frame result (only its labels are read).</param>
    /// <param name="alignedSeries">Chart-length series by name, in the order of <paramref name="names"/>.</param>
    /// <param name="names">The series names, in the order of the source result.</param>
    public TimeframeAlignedIndicatorResult(IIndicatorResult source, IReadOnlyDictionary<string, IReadOnlyList<decimal?>> alignedSeries, IReadOnlyList<string> names)
    {
        _source = source;
        _series = alignedSeries;
        _names = names;
    }

    public bool IsSuccessful => true;
    public string? ErrorMessage => null;
    public IReadOnlyList<decimal?> MainValues => GetSeries(IndicatorResult.MainSeriesName);
    public IEnumerable<string> SeriesNames => _names;
    public IReadOnlyList<string> SeriesNamesList => _names;
    public IReadOnlyDictionary<string, string> SeriesLabels => _source.SeriesLabels;
    public IReadOnlyList<SeriesPrimitiveDescriptor> PrimitiveDescriptors =>
        _source is IPrimitiveAwareIndicatorResult aware
            ? aware.PrimitiveDescriptors
            : System.Array.Empty<SeriesPrimitiveDescriptor>();
    public object? CustomData => null;
    public IConfluenceSignalProvider? SignalProvider => null;

    public bool HasSeries(string name) => !string.IsNullOrEmpty(name) && _series.ContainsKey(name);

    public IReadOnlyList<decimal?> GetSeries(string name)
        => !string.IsNullOrEmpty(name) && _series.TryGetValue(name, out var values) ? values : System.Array.Empty<decimal?>();

    public int Count => MainValues.Count;
    public decimal? this[int index] => MainValues[index];
    public IEnumerator<decimal?> GetEnumerator() => MainValues.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
