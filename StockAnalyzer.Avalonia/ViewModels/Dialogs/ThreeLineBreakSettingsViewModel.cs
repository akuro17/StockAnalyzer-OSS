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
    private HsvData _breakoutBullishColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _breakoutBearishColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showMultiWavePatterns;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private int _multiWaveMaxLines;

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
    private HsvData _initialBreakoutBullishColor;
    private HsvData _initialBreakoutBearishColor;

    public override bool IsModified =>
        UpColor != _initialUpColor ||
        DownColor != _initialDownColor ||
        BreakoutBullishColor != _initialBreakoutBullishColor ||
        BreakoutBearishColor != _initialBreakoutBearishColor ||
        ShowMultiWavePatterns != Snapshot.ThreeLineBreakShowMultiWavePatterns ||
        MultiWaveMaxLines != Snapshot.ThreeLineBreakMultiWaveMaxLines ||
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
        BreakoutBullishColor = HsvData.FromHtmlSafe(settings.ThreeLineBreakMultiWaveBullishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBreakoutBullishColor));
        BreakoutBearishColor = HsvData.FromHtmlSafe(settings.ThreeLineBreakMultiWaveBearishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBreakoutBearishColor));
        ShowMultiWavePatterns = settings.ThreeLineBreakShowMultiWavePatterns;
        MultiWaveMaxLines = settings.ThreeLineBreakMultiWaveMaxLines;
        ShowGhostProjections = settings.ThreeLineBreakShowGhostProjections;
        GhostFontSize = settings.ThreeLineBreakGhostProjectionFontSize;
        ShowLabelsOnHoverOnly = settings.ThreeLineBreakShowGhostLabelsOnHoverOnly;
    }

    protected override void UpdateInitialState()
    {
        _initialUpColor = UpColor;
        _initialDownColor = DownColor;
        _initialBreakoutBullishColor = BreakoutBullishColor;
        _initialBreakoutBearishColor = BreakoutBearishColor;
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            ThreeLineBreakBullishColor = UpColor.ToHtml(),
            ThreeLineBreakBearishColor = DownColor.ToHtml(),
            ThreeLineBreakMultiWaveBullishColor = BreakoutBullishColor.ToHtml(),
            ThreeLineBreakMultiWaveBearishColor = BreakoutBearishColor.ToHtml(),
            ThreeLineBreakShowMultiWavePatterns = ShowMultiWavePatterns,
            ThreeLineBreakMultiWaveMaxLines = Math.Clamp(MultiWaveMaxLines, 1, 20),
            ThreeLineBreakShowGhostProjections = ShowGhostProjections,
            ThreeLineBreakGhostProjectionFontSize = (float)Math.Clamp(GhostFontSize, 8.0f, 32.0f),
            ThreeLineBreakShowGhostLabelsOnHoverOnly = ShowLabelsOnHoverOnly
        };
    }
}
