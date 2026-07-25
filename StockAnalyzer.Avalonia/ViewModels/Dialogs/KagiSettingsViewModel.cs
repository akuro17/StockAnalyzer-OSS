using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class KagiSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "ChartType_Kagi";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private ChartSizingMode _reversalMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private decimal _reversalAmount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private decimal _reversalPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private int _atrPeriod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private decimal _atrMultiplier;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private ChartRoundingMode _roundingMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private AutoFallbackMode _fallbackMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _lineThickness;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private int _initialColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _bullishColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _bearishColor;

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

    public KagiSettingsViewModel(IChartSettingsManager settingsManager, ILogger<KagiSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        ReversalMode = settings.KagiReversalMode;
        ReversalAmount = settings.KagiReversalAmount;
        ReversalPercent = settings.KagiReversalPercent;
        AtrPeriod = settings.KagiAtrPeriod;
        AtrMultiplier = settings.KagiAtrMultiplier;
        RoundingMode = settings.KagiRoundingMode;
        FallbackMode = settings.KagiFallbackMode;
        LineThickness = settings.KagiLineThickness;
        InitialColumn = settings.KagiInitialColumn;
        BullishColor = HsvData.FromHtmlSafe(settings.KagiBullishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBullishColor));
        BearishColor = HsvData.FromHtmlSafe(settings.KagiBearishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBearishColor));
        ShowMultiWavePatterns = settings.KagiShowMultiWavePatterns;
        ShowGhostProjections = settings.KagiShowGhostProjections;
        GhostProjectionFontSize = settings.KagiGhostProjectionFontSize;
        ShowGhostLabelsOnHoverOnly = settings.KagiShowGhostLabelsOnHoverOnly;
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
            KagiReversalMode = ReversalMode,
            KagiReversalAmount = Math.Max(0.0001m, ReversalAmount),
            KagiReversalPercent = Math.Clamp(ReversalPercent, 0.01m, 100m),
            KagiAtrPeriod = Math.Clamp(AtrPeriod, 1, 500),
            KagiAtrMultiplier = Math.Max(0.1m, AtrMultiplier),
            KagiRoundingMode = RoundingMode,
            KagiFallbackMode = FallbackMode,
            KagiLineThickness = Math.Clamp(LineThickness, 0.5f, 10.0f),
            KagiInitialColumn = InitialColumn,
            KagiBullishColor = BullishColor.ToHtml(),
            KagiBearishColor = BearishColor.ToHtml(),
            KagiShowMultiWavePatterns = ShowMultiWavePatterns,
            KagiShowGhostProjections = ShowGhostProjections,
            KagiGhostProjectionFontSize = (float)Math.Clamp(GhostProjectionFontSize, 8.0f, 32.0f),
            KagiShowGhostLabelsOnHoverOnly = ShowGhostLabelsOnHoverOnly
        };
    }

    public override bool IsModified =>
        ReversalMode != Snapshot.KagiReversalMode ||
        ReversalAmount != Snapshot.KagiReversalAmount ||
        ReversalPercent != Snapshot.KagiReversalPercent ||
        AtrPeriod != Snapshot.KagiAtrPeriod ||
        AtrMultiplier != Snapshot.KagiAtrMultiplier ||
        RoundingMode != Snapshot.KagiRoundingMode ||
        FallbackMode != Snapshot.KagiFallbackMode ||
        Math.Abs(LineThickness - Snapshot.KagiLineThickness) > 0.01f ||
        InitialColumn != Snapshot.KagiInitialColumn ||
        BullishColor != _initialBullishColor ||
        BearishColor != _initialBearishColor ||
        ShowMultiWavePatterns != Snapshot.KagiShowMultiWavePatterns ||
        ShowGhostProjections != Snapshot.KagiShowGhostProjections ||
        Math.Abs(GhostProjectionFontSize - Snapshot.KagiGhostProjectionFontSize) > 0.01f ||
        ShowGhostLabelsOnHoverOnly != Snapshot.KagiShowGhostLabelsOnHoverOnly;
}
