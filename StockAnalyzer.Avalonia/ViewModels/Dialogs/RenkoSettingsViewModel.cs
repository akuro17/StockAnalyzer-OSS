using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class RenkoSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "ChartType_Renko";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private ChartSizingMode _sizingMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private decimal _brickSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private decimal _brickPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private int _atrPeriod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private decimal _atrMultiplier;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _bullishColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _bearishColor;

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
    private float _ghostProjectionFontSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private ChartRoundingMode _roundingMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private AutoFallbackMode _fallbackMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private int _reversal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showGhostLabelsOnHoverOnly;

    private HsvData _initialBullishColor;
    private HsvData _initialBearishColor;
    private HsvData _initialBreakoutBullishColor;
    private HsvData _initialBreakoutBearishColor;

    public static IReadOnlyList<ChartSizingMode> SizingModeOptions { get; } = new[]
    {
        ChartSizingMode.Fixed,
        ChartSizingMode.Percentage,
        ChartSizingMode.AutoAtr
    };

    public static IReadOnlyList<ChartRoundingMode> RoundingModeOptions { get; } = Enum.GetValues<ChartRoundingMode>();
    public static IReadOnlyList<AutoFallbackMode> FallbackModeOptions { get; } = Enum.GetValues<AutoFallbackMode>();

    public RenkoSettingsViewModel(IChartSettingsManager settingsManager, ILogger<RenkoSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        SizingMode = settings.RenkoSizingMode;
        BrickSize = settings.RenkoBrickSize;
        BrickPercent = settings.RenkoBrickPercent;
        AtrPeriod = settings.RenkoAtrPeriod;
        AtrMultiplier = settings.RenkoAtrMultiplier;
        RoundingMode = settings.RenkoRoundingMode;
        FallbackMode = settings.RenkoFallbackMode;
        Reversal = settings.RenkoReversal;
        BullishColor = HsvData.FromHtmlSafe(settings.RenkoBullishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBullishColor));
        BearishColor = HsvData.FromHtmlSafe(settings.RenkoBearishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBearishColor));
        BreakoutBullishColor = HsvData.FromHtmlSafe(settings.RenkoMultiWaveBullishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBreakoutBullishColor));
        BreakoutBearishColor = HsvData.FromHtmlSafe(settings.RenkoMultiWaveBearishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBreakoutBearishColor));
        ShowMultiWavePatterns = settings.RenkoShowMultiWavePatterns;
        MultiWaveMaxLines = settings.RenkoMultiWaveMaxLines;
        ShowGhostProjections = settings.RenkoShowGhostProjections;
        GhostProjectionFontSize = settings.RenkoGhostProjectionFontSize;
        ShowGhostLabelsOnHoverOnly = settings.RenkoShowGhostLabelsOnHoverOnly;
    }

    protected override void UpdateInitialState()
    {
        _initialBullishColor = BullishColor;
        _initialBearishColor = BearishColor;
        _initialBreakoutBullishColor = BreakoutBullishColor;
        _initialBreakoutBearishColor = BreakoutBearishColor;
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            RenkoSizingMode = SizingMode,
            RenkoBrickSize = Math.Max(0.0001m, BrickSize),
            RenkoBrickPercent = Math.Clamp(BrickPercent, 0.01m, 100m),
            RenkoAtrPeriod = Math.Clamp(AtrPeriod, 1, 500),
            RenkoAtrMultiplier = Math.Max(0.1m, AtrMultiplier),
            RenkoRoundingMode = RoundingMode,
            RenkoFallbackMode = FallbackMode,
            RenkoReversal = Math.Clamp(Reversal, 1, 10),
            RenkoBullishColor = BullishColor.ToHtml(),
            RenkoBearishColor = BearishColor.ToHtml(),
            RenkoMultiWaveBullishColor = BreakoutBullishColor.ToHtml(),
            RenkoMultiWaveBearishColor = BreakoutBearishColor.ToHtml(),
            RenkoShowMultiWavePatterns = ShowMultiWavePatterns,
            RenkoMultiWaveMaxLines = Math.Clamp(MultiWaveMaxLines, 1, 20),
            RenkoShowGhostProjections = ShowGhostProjections,
            RenkoGhostProjectionFontSize = (float)Math.Clamp(GhostProjectionFontSize, 8.0f, 32.0f),
            RenkoShowGhostLabelsOnHoverOnly = ShowGhostLabelsOnHoverOnly
        };
    }

    public override bool IsModified =>
        SizingMode != Snapshot.RenkoSizingMode ||
        BrickSize != Snapshot.RenkoBrickSize ||
        BrickPercent != Snapshot.RenkoBrickPercent ||
        AtrPeriod != Snapshot.RenkoAtrPeriod ||
        AtrMultiplier != Snapshot.RenkoAtrMultiplier ||
        RoundingMode != Snapshot.RenkoRoundingMode ||
        FallbackMode != Snapshot.RenkoFallbackMode ||
        Reversal != Snapshot.RenkoReversal ||
        BullishColor != _initialBullishColor ||
        BearishColor != _initialBearishColor ||
        BreakoutBullishColor != _initialBreakoutBullishColor ||
        BreakoutBearishColor != _initialBreakoutBearishColor ||
        ShowMultiWavePatterns != Snapshot.RenkoShowMultiWavePatterns ||
        MultiWaveMaxLines != Snapshot.RenkoMultiWaveMaxLines ||
        ShowGhostProjections != Snapshot.RenkoShowGhostProjections ||
        Math.Abs(GhostProjectionFontSize - Snapshot.RenkoGhostProjectionFontSize) > 0.01f ||
        ShowGhostLabelsOnHoverOnly != Snapshot.RenkoShowGhostLabelsOnHoverOnly;
}
