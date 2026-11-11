using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// The two reference-compared conversion caches shared by the seasonality plot controls, so a render pass allocates nothing while the
/// bound lists are unchanged. One instance per control; UI thread only (it is called from <c>Render</c>, never from the draw operation).
/// </summary>
internal sealed class SeasonalityPlotRenderCache
{
    private IReadOnlyList<Color>? _paletteCacheKey;
    private SKColor[]? _paletteCacheValue;
    private IReadOnlyList<SeasonalityYearDrawState>? _yearStateCacheKey;
    private Dictionary<int, SeasonalityYearDrawState>? _yearStateCacheValue;

    /// <summary>
    /// The bound year palette converted to Skia colours, cached against the list instance. Null when no palette is set.
    /// </summary>
    internal SKColor[]? ResolvePalette(IReadOnlyList<Color>? palette)
    {
        if (palette is null || palette.Count == 0)
        {
            return null;
        }

        if (!ReferenceEquals(palette, _paletteCacheKey) || _paletteCacheValue is null || _paletteCacheValue.Length != palette.Count)
        {
            var converted = new SKColor[palette.Count];
            for (int index = 0; index < palette.Count; index++)
            {
                Color color = palette[index];
                converted[index] = new SKColor(color.R, color.G, color.B, color.A);
            }

            _paletteCacheKey = palette;
            _paletteCacheValue = converted;
        }

        return _paletteCacheValue;
    }

    /// <summary>
    /// The bound year draw states as a calendar-year lookup, cached against the list instance. Null when unset.
    /// </summary>
    internal Dictionary<int, SeasonalityYearDrawState>? ResolveYearStates(IReadOnlyList<SeasonalityYearDrawState>? states)
    {
        if (states is null || states.Count == 0)
        {
            return null;
        }

        if (!ReferenceEquals(states, _yearStateCacheKey) || _yearStateCacheValue is null || _yearStateCacheValue.Count != states.Count)
        {
            var map = new Dictionary<int, SeasonalityYearDrawState>(states.Count);
            for (int index = 0; index < states.Count; index++)
            {
                map[states[index].CalendarYear] = states[index];
            }

            _yearStateCacheKey = states;
            _yearStateCacheValue = map;
        }

        return _yearStateCacheValue;
    }
}
