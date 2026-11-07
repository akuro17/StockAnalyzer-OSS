namespace StockAnalyzer.Core.Rendering;

/// <summary>A drawn line with its look: which line of which indicator, the colour, the pattern and the width.</summary>
public sealed record AiChartStyledLine(string IndicatorId, string SeriesName, string Label, IReadOnlyList<decimal?> Values, string ColorHex, AiChartLineStyle LineStyle, float WidthPx);

/// <summary>
/// The rule that gives every line of an AI chart image its colour, pattern and width, the same way for the same input (plan 15, 6.3; owner decisions D-15h and D-15k).
/// A panel's lines are numbered 0, 1, 2, ... in drawing order and the n-th line gets the pattern <c>n mod 3</c> (solid, dash, dot), so a line is never told apart by colour alone.
/// On the price panel the moving-average overlays come first, ranked by their period (equal periods by the order of the request, a type without a period last): the first three
/// take the short, mid and long moving-average colour (2.5 px); every other line takes the next colour of the auxiliary list (1.5 px), which cycles. The colour follows the rank of the
/// overlay, not its period: a single long moving average is the "short" colour. A panel of an oscillator uses the auxiliary colours from the start.
/// </summary>
public static class AiChartLineAssigner
{
    private static readonly string[] MovingAverageColors =
    {
        AiChartImageStyle.MovingAverageShortHex,
        AiChartImageStyle.MovingAverageMidHex,
        AiChartImageStyle.MovingAverageLongHex,
    };

    /// <summary>The lines of the price panel in drawing order.</summary>
    public static IReadOnlyList<AiChartStyledLine> AssignOverlays(IReadOnlyList<AiChartOverlay> overlays)
    {
        // Rank the moving averages: period ascending, a missing period last, ties by the order of the request (OrderBy is stable).
        List<(AiChartOverlay Overlay, int Rank)> averages = overlays
            .Where(o => AiChartMovingAverageSet.Contains(o.Type))
            .OrderBy(o => o.RepresentativePeriod.HasValue ? 0 : 1)
            .ThenBy(o => o.RepresentativePeriod ?? 0)
            .Select((o, rank) => (o, rank))
            .ToList();
        IEnumerable<AiChartOverlay> others = overlays.Where(o => !AiChartMovingAverageSet.Contains(o.Type));

        var lines = new List<AiChartStyledLine>();
        int auxiliary = 0;
        foreach ((AiChartOverlay overlay, int rank) in averages)
        {
            Append(lines, overlay.Id, overlay.Series, rank < MovingAverageColors.Length ? MovingAverageColors[rank] : null, ref auxiliary);
        }

        foreach (AiChartOverlay overlay in others)
        {
            Append(lines, overlay.Id, overlay.Series, null, ref auxiliary);
        }

        return lines;
    }

    /// <summary>The lines of an oscillator panel in drawing order.</summary>
    public static IReadOnlyList<AiChartStyledLine> AssignOscillator(AiChartOscillator oscillator)
    {
        var lines = new List<AiChartStyledLine>();
        int auxiliary = 0;
        Append(lines, oscillator.Id, oscillator.Series, null, ref auxiliary);
        return lines;
    }

    /// <summary>
    /// Adds the lines of one indicator. <paramref name="firstColor"/> colours its first line (a moving average of rank 0..2); every other line takes the next auxiliary colour.
    /// A line's pattern is its number in the panel mod 3, counted over <paramref name="lines"/>.
    /// </summary>
    private static void Append(List<AiChartStyledLine> lines, string id, IReadOnlyList<AiChartLineSeries> series, string? firstColor, ref int auxiliary)
    {
        for (int i = 0; i < series.Count; i++)
        {
            bool movingAverageColor = i == 0 && firstColor is not null;
            string color = movingAverageColor ? firstColor! : AiChartImageStyle.AuxiliaryHex[auxiliary++ % AiChartImageStyle.AuxiliaryHex.Count];
            float width = movingAverageColor ? AiChartImageStyle.MovingAverageWidthPx : AiChartImageStyle.AuxiliaryWidthPx;
            AiChartLineStyle pattern = AiChartImageStyle.LinePatterns[lines.Count % AiChartImageStyle.LinePatterns.Count];
            string label = series.Count == 1 || series[i].Name == StockAnalyzer.Core.Models.Indicators.IndicatorResult.MainSeriesName ? id : $"{id}.{series[i].Name}";
            lines.Add(new AiChartStyledLine(id, series[i].Name, label, series[i].Values, color, pattern, width));
        }
    }
}
