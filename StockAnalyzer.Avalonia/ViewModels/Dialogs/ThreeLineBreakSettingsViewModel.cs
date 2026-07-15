using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class ThreeLineBreakSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "ChartType_ThreeLineBreak";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _upColor = new(1, 120, 1, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _downColor = new(1, 0, 1, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showMultiWavePatterns;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showGhostProjections;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _ghostFontSize = 10.0f;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showLabelsOnHoverOnly = false;

    private HsvData _initialUpColor;
    private HsvData _initialDownColor;

    public override bool IsModified => 
        UpColor != _initialUpColor ||
        DownColor != _initialDownColor ||
        ShowMultiWavePatterns != Snapshot.ThreeLineBreakShowMultiWavePatterns ||
        ShowGhostProjections != Snapshot.ThreeLineBreakShowGhostProjections ||
        Math.Abs(GhostFontSize - Snapshot.ThreeLineBreakGhostProjectionFontSize) > 0.01f ||
        ShowLabelsOnHoverOnly != Snapshot.ThreeLineBreakShowGhostLabelsOnHoverOnly;

    public ThreeLineBreakSettingsViewModel(IChartSettingsManager settingsManager, ILogger<ThreeLineBreakSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        UpColor = HsvData.FromHtmlSafe(settings.ThreeLineBreakBullishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBullishColor));
        DownColor = HsvData.FromHtmlSafe(settings.ThreeLineBreakBearishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBearishColor));
        ShowMultiWavePatterns = settings.ThreeLineBreakShowMultiWavePatterns;
        ShowGhostProjections = settings.ThreeLineBreakShowGhostProjections;
        GhostFontSize = settings.ThreeLineBreakGhostProjectionFontSize;
        ShowLabelsOnHoverOnly = settings.ThreeLineBreakShowGhostLabelsOnHoverOnly;
    }

    protected override void UpdateInitialState()
    {
        _initialUpColor = UpColor;
        _initialDownColor = DownColor;
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            ThreeLineBreakBullishColor = UpColor.ToHtml(),
            ThreeLineBreakBearishColor = DownColor.ToHtml(),
            ThreeLineBreakShowMultiWavePatterns = ShowMultiWavePatterns,
            ThreeLineBreakShowGhostProjections = ShowGhostProjections,
            ThreeLineBreakGhostProjectionFontSize = (float)Math.Clamp(GhostFontSize, 8.0f, 32.0f),
            ThreeLineBreakShowGhostLabelsOnHoverOnly = ShowLabelsOnHoverOnly
        };
    }
}
