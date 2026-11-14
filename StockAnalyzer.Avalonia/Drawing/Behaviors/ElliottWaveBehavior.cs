using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// Variable-point placement behavior for <see cref="ElliottWaveObject"/>.
/// Adds a pivot per click, auto-finalizes once the active <see cref="WavePatternMode"/> point limit is reached,
/// and deduplicates trailing identical coordinates when finalizing (canceling if fewer than 2 points remain).
/// </summary>
public sealed class ElliottWaveBehavior : IDrawingToolBehavior
{
    public int RequiredSteps => 0; // Variable (up to PatternMode point count)
    public bool FinishesOnRelease => false;

    public IChartObject CreateObject(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        var points = new List<ChartPoint> { chartPoint, chartPoint };
        return new ElliottWaveObject(points)
        {
            Color = DrawingThemeContext.DefaultColor,
            Thickness = DrawingThemeContext.DefaultStrokeThickness
        };
    }

    public void UpdatePoint(IChartObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles = null)
    {
        if (obj is ElliottWaveObject wave && wave.Points.Count > 0)
        {
            wave.Points[wave.Points.Count - 1] = chartPoint;
        }
    }

    /// <summary>
    /// Commits a subsequent click position on an in-progress <see cref="ElliottWaveObject"/>.
    /// Returns <see langword="true"/> when the pattern's required point count is reached (auto-finalize).
    /// </summary>
    public bool TryAdvanceClick(ElliottWaveObject wave, ChartPoint chartPoint)
    {
        if (wave.Points.Count == 0)
        {
            wave.Points.Add(chartPoint);
            wave.Points.Add(chartPoint);
            return false;
        }

        wave.Points[wave.Points.Count - 1] = chartPoint;
        if (wave.Points.Count >= wave.RequiredPointCount)
        {
            return true;
        }

        wave.AddPoint(chartPoint);
        return false;
    }

    /// <summary>
    /// Cleans up trailing hover/duplicate coordinates prior to finalizing an <see cref="ElliottWaveObject"/>.
    /// Returns <see langword="false"/> when fewer than 2 points remain (caller must cancel creation).
    /// </summary>
    public static bool PrepareForFinalize(ElliottWaveObject wave, bool dropTrailingPreview)
    {
        if (dropTrailingPreview && wave.Points.Count > 1)
        {
            wave.Points.RemoveAt(wave.Points.Count - 1);
        }

        while (wave.Points.Count >= 2 &&
               wave.Points[wave.Points.Count - 1].Time == wave.Points[wave.Points.Count - 2].Time &&
               wave.Points[wave.Points.Count - 1].Price == wave.Points[wave.Points.Count - 2].Price)
        {
            wave.Points.RemoveAt(wave.Points.Count - 1);
        }

        return wave.Points.Count >= 2;
    }
}
