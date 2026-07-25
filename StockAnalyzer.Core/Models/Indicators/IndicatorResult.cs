using System.Collections;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Confluence;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Default implementation of IIndicatorResult.
/// </summary>
public class IndicatorResult : IIndicatorResult
{
    public IEnumerator<decimal?> GetEnumerator() => MainValues.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    
    public int Count => MainValues.Count;
    public decimal? this[int index] => MainValues[index];

    private readonly Dictionary<string, IReadOnlyList<decimal?>> _series;
    private readonly IReadOnlyList<string> _seriesNamesList;
    private Dictionary<string, string>? _seriesLabels;

    /// <summary>
    /// Well-known series name for the primary/default values.
    /// </summary>
    public const string MainSeriesName = "Main";

    public bool IsSuccessful { get; }
    public string? ErrorMessage { get; }
    public IReadOnlyList<decimal?> MainValues => GetSeries(MainSeriesName);
    public IEnumerable<string> SeriesNames => SeriesNamesList;
    public IReadOnlyList<string> SeriesNamesList => _seriesNamesList;
    public IReadOnlyDictionary<string, string> SeriesLabels => _seriesLabels ??= new Dictionary<string, string>();
    public object? CustomData { get; }
    public IConfluenceSignalProvider? SignalProvider { get; private set; }

    private IndicatorResult(bool isSuccessful, string? errorMessage, Dictionary<string, IReadOnlyList<decimal?>> series, object? customData = null, IConfluenceSignalProvider? signalProvider = null)
    {
        IsSuccessful = isSuccessful;
        ErrorMessage = errorMessage;
        _series = series;
        CustomData = customData;
        SignalProvider = signalProvider;

        var names = new List<string>(series.Keys);
        _seriesNamesList = names.AsReadOnly();
    }

    public bool HasSeries(string name) => _series.ContainsKey(name);

    public IReadOnlyList<decimal?> GetSeries(string name)
    {
        return _series.TryGetValue(name, out var values) ? values : Array.Empty<decimal?>();
    }

    /// <summary>
    /// Generates and caches display labels for all series using the provided indicator name.
    /// This ensures ZeroAllocation in the UI layer during rendering.
    /// </summary>
    public void ApplyMetadata(string indicatorShortName)
    {
        if (_seriesLabels != null) return;

        _seriesLabels = new Dictionary<string, string>();
        foreach (var seriesName in SeriesNamesList)
        {
            _seriesLabels[seriesName] = seriesName == MainSeriesName 
                ? indicatorShortName 
                : $"{indicatorShortName} ({seriesName})";
        }
    }

    #region Factory Methods

    /// <summary>
    /// Creates a successful result with a single series (backward compatible).
    /// </summary>
    public static IndicatorResult Success(IReadOnlyList<decimal?> values)
    {
        return new IndicatorResult(true, null, new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { MainSeriesName, values }
        });
    }

    /// <summary>
    /// Creates a successful result with multiple named series.
    /// The first series added should be the "Main" series for backward compatibility.
    /// </summary>
    public static IndicatorResult Success(Dictionary<string, IReadOnlyList<decimal?>> series, object? customData = null, IConfluenceSignalProvider? signalProvider = null)
    {
        return new IndicatorResult(true, null, series, customData, signalProvider);
    }

    /// <summary>
    /// Creates a failed result with an error message.
    /// </summary>
    public static IndicatorResult Failure(string errorMessage)
    {
        return new IndicatorResult(false, errorMessage, new Dictionary<string, IReadOnlyList<decimal?>>());
    }

    /// <summary>
    /// Creates an empty result (no data, but not an error).
    /// </summary>
    public static IndicatorResult Empty()
    {
        return new IndicatorResult(true, null, new Dictionary<string, IReadOnlyList<decimal?>>());
    }

    #endregion
}
