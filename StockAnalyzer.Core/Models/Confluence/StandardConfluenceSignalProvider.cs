using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.DivergenceCross;

namespace StockAnalyzer.Core.Models.Confluence;

/// <summary>
/// A reusable implementation of IConfluenceSignalProvider that extracts signals 
/// from specific named series in an IIndicatorResult.
/// </summary>
public class StandardConfluenceSignalProvider : IConfluenceSignalProvider
{
    private class SignalMapping
    {
        public string SeriesName { get; init; } = string.Empty;
        public SignalType Type { get; init; }
        public SignalDirection Direction { get; init; }
    }

    private readonly List<SignalMapping> _mappings = new();
    public int SeriesCount => _mappings.Count;

    /// <summary>
    /// Maps a series name to a specific signal type and direction.
    /// Uses non-null/non-zero values in the series as active signal markers.
    /// </summary>
    public void MapSeries(string seriesName, SignalType type, SignalDirection direction)
    {
        _mappings.Add(new SignalMapping { SeriesName = seriesName, Type = type, Direction = direction });
    }

    public IEnumerable<ConfluenceSignal> GetSignals(int index, IIndicatorResult result, CoreIndicatorSettings settings)
    {
        foreach (var mapping in _mappings)
        {
            var series = result.GetSeries(mapping.SeriesName);
            if (index >= 0 && index < series.Count)
            {
                var val = series[index];
                if (val.HasValue && val.Value != 0)
                {
                    yield return new ConfluenceSignal(
                        index,
                        mapping.Type,
                        mapping.Direction,
                        settings.ConfluenceGroup,
                        Weight: (double)val.Value * settings.ConfluenceWeight,
                        Strength: 1.0 // Default strength
                    );
                }
            }
        }
    }
}
