using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// Immutable snapshot of heatmap color palette, effective threshold ratio, step count,
/// font size, text color, and text alignment.
/// Thread-safe for passing into rendering draw operations.
/// </summary>
public sealed record HeatmapPalette(
    IndicatorColor Up,
    IndicatorColor Neutral,
    IndicatorColor Down,
    double ThresholdRatio,
    int StepCount,
    double FontSize,
    IndicatorColor TextColor,
    HeatmapTextAlignment TextAlignment)
{
    /// <summary>
    /// Backward-compatible constructor overload with default text formatting.
    /// </summary>
    public HeatmapPalette(
        IndicatorColor up,
        IndicatorColor neutral,
        IndicatorColor down,
        double thresholdRatio,
        int stepCount)
        : this(
            up,
            neutral,
            down,
            thresholdRatio,
            stepCount,
            ChartSettingsConstants.DefaultScatterHeatmapFontSize,
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapTextColor).ToIndicatorColor(),
            ChartSettingsConstants.DefaultScatterHeatmapTextAlignment)
    {
    }

    /// <summary>
    /// Default heatmap palette adhering to standard settings constants (Classic colors, 5% threshold, 1.0 multiplier, 10 steps, 16px font, Main Text color, Center alignment).
    /// </summary>
    public static HeatmapPalette Default { get; } = new(
        Up: HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapUpColor).ToIndicatorColor(),
        Neutral: HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapNeutralColor).ToIndicatorColor(),
        Down: HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapDownColor).ToIndicatorColor(),
        ThresholdRatio: HeatmapColorMapper.ResolveThresholdRatio(
            ChartSettingsConstants.DefaultScatterHeatmapThresholdPercent,
            ChartSettingsConstants.DefaultScatterHeatmapMultiplier),
        StepCount: (int)ChartSettingsConstants.DefaultScatterHeatmapStepCount,
        FontSize: ChartSettingsConstants.DefaultScatterHeatmapFontSize,
        TextColor: HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapTextColor).ToIndicatorColor(),
        TextAlignment: ChartSettingsConstants.DefaultScatterHeatmapTextAlignment);

    /// <summary>
    /// Creates an immutable palette snapshot from <see cref="GlobalChartSettings"/>.
    /// </summary>
    public static HeatmapPalette FromSettings(GlobalChartSettings settings)
    {
        var up = HsvData.FromHtmlSafe(
            settings.ScatterHeatmapUpColor,
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapUpColor)).ToIndicatorColor();

        var neutral = HsvData.FromHtmlSafe(
            settings.ScatterHeatmapNeutralColor,
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapNeutralColor)).ToIndicatorColor();

        var down = HsvData.FromHtmlSafe(
            settings.ScatterHeatmapDownColor,
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapDownColor)).ToIndicatorColor();

        double threshold = HeatmapColorMapper.ResolveThresholdRatio(
            settings.ScatterHeatmapThresholdPercent,
            settings.ScatterHeatmapMultiplier);

        int steps = (int)settings.ScatterHeatmapStepCount;
        double fontSize = settings.ScatterHeatmapFontSize;

        var textColor = HsvData.FromHtmlSafe(
            settings.ScatterHeatmapTextColor,
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapTextColor)).ToIndicatorColor();

        var textAlignment = settings.ScatterHeatmapTextAlignment;

        return new HeatmapPalette(up, neutral, down, threshold, steps, fontSize, textColor, textAlignment);
    }
}
