using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Confluence;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Avalonia.Views.Chart
{
    /// <summary>
    /// Wraps an IIndicatorResult and provides sliced access to its data series.
    /// Used by ChartDataSnapshot to provided view-specific data subsets.
    /// </summary>
    public class SlicedIndicatorResult : IIndicatorResult, IPrimitiveAwareIndicatorResult
    {
        private readonly IIndicatorResult _original;
        private readonly int _startIndex;
        private readonly int _count;
        private readonly Dictionary<string, IReadOnlyList<decimal?>> _listCache = new();

        public SlicedIndicatorResult(IIndicatorResult original, int startIndex, int count)
        {
            _original = original;
            _startIndex = startIndex;
            _count = count;
        }

        public bool IsSuccessful => _original.IsSuccessful;
        public string? ErrorMessage => _original.ErrorMessage;
        public object? CustomData => _original.CustomData;
        public IConfluenceSignalProvider? SignalProvider => _original.SignalProvider;
        public IReadOnlyList<decimal?>? EffectiveDynamicPeriods =>
            _original.EffectiveDynamicPeriods is { } dp ? new VirtualSlicedList(dp, _startIndex, _count) : null;

        public IReadOnlyList<decimal?> MainValues => GetSeries("Main");

        public bool HasSeries(string name) => _original.HasSeries(name);

        public IReadOnlyList<decimal?> GetSeries(string name)
        {
            if (_listCache.TryGetValue(name, out var cached)) return cached;

            var source = _original.GetSeries(name);
            var sliced = new VirtualSlicedList(source, _startIndex, _count);
            _listCache[name] = sliced;
            return sliced;
        }

        public IEnumerable<string> SeriesNames => _original.SeriesNames;
        public IReadOnlyList<string> SeriesNamesList => _original.SeriesNamesList;
        public IReadOnlyDictionary<string, string> SeriesLabels => _original.SeriesLabels;
        public IReadOnlyList<SeriesPrimitiveDescriptor> PrimitiveDescriptors =>
            _original is IPrimitiveAwareIndicatorResult aware
                ? aware.PrimitiveDescriptors
                : Array.Empty<SeriesPrimitiveDescriptor>();

        public decimal? this[int index] => MainValues[index];
        public int Count => MainValues.Count;
        public IEnumerator<decimal?> GetEnumerator() => MainValues.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => MainValues.GetEnumerator();

        private class VirtualSlicedList : IReadOnlyList<decimal?>
        {
            private readonly IReadOnlyList<decimal?> _source;
            private readonly int _startIndex;
            private readonly int _count;

            public VirtualSlicedList(IReadOnlyList<decimal?> source, int startIndex, int count)
            {
                _source = source;
                _startIndex = startIndex;
                _count = count;
            }

            public decimal? this[int index]
            {
                get
                {
                    if (index < 0 || index >= _count) return null;
                    int sourceIdx = _startIndex + index;
                    if (sourceIdx < 0 || sourceIdx >= _source.Count) return null;
                    return _source[sourceIdx];
                }
            }

            public int Count => _count;

            public IEnumerator<decimal?> GetEnumerator()
            {
                for (int i = 0; i < _count; i++) yield return this[i];
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
