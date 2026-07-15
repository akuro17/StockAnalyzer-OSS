using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class PnfSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "ChartType_PointAndFigure";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private ChartSizingMode _sizingMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private decimal _boxSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private decimal _boxPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private int _atrPeriod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private decimal _atrMultiplier;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private int _reversal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private ChartRoundingMode _roundingMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private AutoFallbackMode _fallbackMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _bullishColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _bearishColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showDoubleBreakout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showTripleBreakout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showTrendlineBreakout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showTriangleBreakout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showCatapultBreakout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showMultiWavePatterns;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showGhostProjections;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _ghostProjectionFontSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showGhostLabelsOnHoverOnly;

    private HsvData _initialBullishColor;
    private HsvData _initialBearishColor;

    public static IReadOnlyList<ChartSizingMode> SizingModeOptions { get; } = new[]
    {
        ChartSizingMode.Fixed,
        ChartSizingMode.Percentage,
        ChartSizingMode.AutoAtr
    };

    public static IReadOnlyList<ChartRoundingMode> RoundingModeOptions { get; } = Enum.GetValues<ChartRoundingMode>();
    public static IReadOnlyList<AutoFallbackMode> FallbackModeOptions { get; } = Enum.GetValues<AutoFallbackMode>();
    public static IReadOnlyList<int> ReversalOptions { get; } = new[] { 1, 2, 3, 4, 5 };

    public PnfSettingsViewModel(IChartSettingsManager settingsManager, ILogger<PnfSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        SizingMode = settings.PnfSizingMode;
        BoxSize = settings.PnfBoxSize;
        BoxPercent = settings.PnfBoxPercent;
        AtrPeriod = settings.PnfAtrPeriod;
        AtrMultiplier = settings.PnfAtrMultiplier;
        Reversal = settings.PnfReversal;
        RoundingMode = settings.PnfRoundingMode;
        FallbackMode = settings.PnfFallbackMode;
        BullishColor = HsvData.FromHtmlSafe(settings.PnfBullishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBullishColor));
        BearishColor = HsvData.FromHtmlSafe(settings.PnfBearishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBearishColor));
        ShowDoubleBreakout = settings.PnfShowDoubleBreakout;
        ShowTripleBreakout = settings.PnfShowTripleBreakout;
        ShowTrendlineBreakout = settings.PnfShowTrendlineBreakout;
        ShowTriangleBreakout = settings.PnfShowTriangleBreakout;
        ShowCatapultBreakout = settings.PnfShowCatapultBreakout;
        ShowMultiWavePatterns = settings.PnfShowMultiWavePatterns;
        ShowGhostProjections = settings.PnfShowGhostProjections;
        GhostProjectionFontSize = settings.PnfGhostProjectionFontSize;
        ShowGhostLabelsOnHoverOnly = settings.PnfShowGhostLabelsOnHoverOnly;
    }

    protected override void UpdateInitialState()
    {
        _initialBullishColor = BullishColor;
        _initialBearishColor = BearishColor;
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            PnfSizingMode = SizingMode,
            PnfBoxSize = Math.Max(0.0001m, BoxSize),
            PnfBoxPercent = Math.Clamp(BoxPercent, 0.01m, 100m),
            PnfAtrPeriod = Math.Clamp(AtrPeriod, 1, 500),
            PnfAtrMultiplier = Math.Max(0.1m, AtrMultiplier),
            PnfReversal = Math.Clamp(Reversal, 1, 10),
            PnfRoundingMode = RoundingMode,
            PnfFallbackMode = FallbackMode,
            PnfBullishColor = BullishColor.ToHtml(),
            PnfBearishColor = BearishColor.ToHtml(),
            PnfShowDoubleBreakout = ShowDoubleBreakout,
            PnfShowTripleBreakout = ShowTripleBreakout,
            PnfShowTrendlineBreakout = ShowTrendlineBreakout,
            PnfShowTriangleBreakout = ShowTriangleBreakout,
            PnfShowCatapultBreakout = ShowCatapultBreakout,
            PnfShowMultiWavePatterns = ShowMultiWavePatterns,
            PnfShowGhostProjections = ShowGhostProjections,
            PnfGhostProjectionFontSize = (float)Math.Clamp(GhostProjectionFontSize, 8.0f, 32.0f),
            PnfShowGhostLabelsOnHoverOnly = ShowGhostLabelsOnHoverOnly
        };
    }

    public override bool IsModified =>
        SizingMode != Snapshot.PnfSizingMode ||
        BoxSize != Snapshot.PnfBoxSize ||
        BoxPercent != Snapshot.PnfBoxPercent ||
        AtrPeriod != Snapshot.PnfAtrPeriod ||
        AtrMultiplier != Snapshot.PnfAtrMultiplier ||
        Reversal != Snapshot.PnfReversal ||
        RoundingMode != Snapshot.PnfRoundingMode ||
        FallbackMode != Snapshot.PnfFallbackMode ||
        BullishColor != _initialBullishColor ||
        BearishColor != _initialBearishColor ||
        ShowDoubleBreakout != Snapshot.PnfShowDoubleBreakout ||
        ShowTripleBreakout != Snapshot.PnfShowTripleBreakout ||
        ShowTrendlineBreakout != Snapshot.PnfShowTrendlineBreakout ||
        ShowTriangleBreakout != Snapshot.PnfShowTriangleBreakout ||
        ShowCatapultBreakout != Snapshot.PnfShowCatapultBreakout ||
        ShowMultiWavePatterns != Snapshot.PnfShowMultiWavePatterns ||
        ShowGhostProjections != Snapshot.PnfShowGhostProjections ||
        Math.Abs(GhostProjectionFontSize - Snapshot.PnfGhostProjectionFontSize) > 0.01f ||
        ShowGhostLabelsOnHoverOnly != Snapshot.PnfShowGhostLabelsOnHoverOnly;
}
