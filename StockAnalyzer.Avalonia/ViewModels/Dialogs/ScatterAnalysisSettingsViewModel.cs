using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Settings page for Scatter Analysis: Heatmap colours (Up, Neutral, Down),
/// return threshold (%), heat multiplier, colour steps, font size, font colour, and text alignment.
/// Persists on <see cref="GlobalChartSettings"/> as a top-level Settings category.
/// </summary>
public partial class ScatterAnalysisSettingsViewModel : BaseChartSettingsViewModel
{
    private const double NumericEpsilon = 0.0001;

    public override string TitleKey => "Tab_ScatterAnalysis";
    public override string IconKey => "SettingsChartIcon";

    public decimal MinThresholdPercent => (decimal)ChartSettingsConstants.MinScatterHeatmapThresholdPercent;
    public decimal MaxThresholdPercent => (decimal)ChartSettingsConstants.MaxScatterHeatmapThresholdPercent;
    public decimal ThresholdPercentStep => (decimal)ChartSettingsConstants.ScatterHeatmapThresholdPercentStep;

    public decimal MinMultiplier => (decimal)ChartSettingsConstants.MinScatterHeatmapMultiplier;
    public decimal MaxMultiplier => (decimal)ChartSettingsConstants.MaxScatterHeatmapMultiplier;
    public decimal MultiplierStep => (decimal)ChartSettingsConstants.ScatterHeatmapMultiplierStep;

    public decimal MinStepCount => (decimal)ChartSettingsConstants.MinScatterHeatmapStepCount;
    public decimal MaxStepCount => (decimal)ChartSettingsConstants.MaxScatterHeatmapStepCount;
    public decimal StepCountStep => 1m;

    public decimal MinFontSize => (decimal)ChartSettingsConstants.MinScatterHeatmapFontSize;
    public decimal MaxFontSize => (decimal)ChartSettingsConstants.MaxScatterHeatmapFontSize;
    public decimal FontSizeStep => (decimal)ChartSettingsConstants.ScatterHeatmapFontSizeStep;

    public IReadOnlyList<HeatmapTextAlignment> AvailableTextAlignments { get; } =
        Enum.GetValues<HeatmapTextAlignment>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _upColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _neutralColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _downColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private double _thresholdPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private double _heatMultiplier;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private uint _stepCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private double _fontSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _textColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HeatmapTextAlignment _textAlignment;

    public ScatterAnalysisSettingsViewModel(
        IChartSettingsManager settingsManager,
        ILogger<ScatterAnalysisSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    public override bool IsModified
    {
        get
        {
            if (UpColor != HsvData.FromHtmlSafe(Snapshot.ScatterHeatmapUpColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapUpColor)) ||
                NeutralColor != HsvData.FromHtmlSafe(Snapshot.ScatterHeatmapNeutralColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapNeutralColor)) ||
                DownColor != HsvData.FromHtmlSafe(Snapshot.ScatterHeatmapDownColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapDownColor)) ||
                Math.Abs(ThresholdPercent - Snapshot.ScatterHeatmapThresholdPercent) > NumericEpsilon ||
                Math.Abs(HeatMultiplier - Snapshot.ScatterHeatmapMultiplier) > NumericEpsilon ||
                StepCount != Snapshot.ScatterHeatmapStepCount ||
                Math.Abs(FontSize - Snapshot.ScatterHeatmapFontSize) > NumericEpsilon ||
                TextColor != HsvData.FromHtmlSafe(Snapshot.ScatterHeatmapTextColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapTextColor)) ||
                TextAlignment != Snapshot.ScatterHeatmapTextAlignment)
            {
                return true;
            }

            return false;
        }
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        UpColor = HsvData.FromHtmlSafe(
            settings.ScatterHeatmapUpColor,
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapUpColor));
        NeutralColor = HsvData.FromHtmlSafe(
            settings.ScatterHeatmapNeutralColor,
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapNeutralColor));
        DownColor = HsvData.FromHtmlSafe(
            settings.ScatterHeatmapDownColor,
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapDownColor));
        ThresholdPercent = settings.ScatterHeatmapThresholdPercent;
        HeatMultiplier = settings.ScatterHeatmapMultiplier;
        StepCount = settings.ScatterHeatmapStepCount;
        FontSize = settings.ScatterHeatmapFontSize;
        TextColor = HsvData.FromHtmlSafe(
            settings.ScatterHeatmapTextColor,
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapTextColor));
        TextAlignment = settings.ScatterHeatmapTextAlignment;
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            ScatterHeatmapUpColor = UpColor.ToHtml(),
            ScatterHeatmapNeutralColor = NeutralColor.ToHtml(),
            ScatterHeatmapDownColor = DownColor.ToHtml(),
            ScatterHeatmapThresholdPercent = ThresholdPercent,
            ScatterHeatmapMultiplier = HeatMultiplier,
            ScatterHeatmapStepCount = StepCount,
            ScatterHeatmapFontSize = FontSize,
            ScatterHeatmapTextColor = TextColor.ToHtml(),
            ScatterHeatmapTextAlignment = TextAlignment,
        };
    }
}
