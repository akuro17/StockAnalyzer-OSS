namespace StockAnalyzer.Core.Rendering;

/// <summary>
/// The indicator types an AI chart image does not draw (owner decision D-15q=A: refused with a hint, never drawn wrongly). The application's chart draws each of them with a
/// renderer of its own (<c>IndicatorRenderer</c>): dots, a cloud, bars, columns, a profile, or a different chart type altogether, which a plain line would misstate.
/// Volume has its own panel in the image (<c>includeVolume</c>). The numbers of these indicators stay available from sa_calculate_indicators.
/// Parabolic SAR (dots) and the causal lines and the cloud of Ichimoku are drawn since owner decision U-14 (<see cref="AiChartLineShape.Dots"/>, <see cref="AiChartCloud"/>).
/// </summary>
public static class AiChartUndrawableTypes
{
    private static readonly HashSet<IndicatorType> Types = new()
    {
        IndicatorType.Volume,
        IndicatorType.VolumeProfile,
        IndicatorType.TimeAtPrice,
        IndicatorType.Renko,
        IndicatorType.RenkoOverlay,
        IndicatorType.Kagi,
        IndicatorType.KagiOverlay,
        IndicatorType.PointAndFigure,
        IndicatorType.PointAndFigureOverlay,
        IndicatorType.ThreeLineBreak,
        IndicatorType.MarketStructureShift,
        IndicatorType.GranvilleLaw,
    };

    public static IEnumerable<IndicatorType> All => Types;

    public static bool Contains(IndicatorType type) => Types.Contains(type);
}
