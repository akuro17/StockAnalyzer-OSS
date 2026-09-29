using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class CandlestickSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "Settings_Chart_Candlestick";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _bullishColor = new(1, 120, 1, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _bearishColor = new(1, 0, 1, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _neutralColor = new(1, 0, 0, 0.5);

    private HsvData _initialBullishColor;
    private HsvData _initialBearishColor;
    private HsvData _initialNeutralColor;

    public override bool IsModified => 
        BullishColor != _initialBullishColor ||
        BearishColor != _initialBearishColor ||
        NeutralColor != _initialNeutralColor;

    public CandlestickSettingsViewModel(IChartSettingsManager settingsManager, ILogger<CandlestickSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        BullishColor = HsvData.FromHtmlSafe(settings.CandlestickBullishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBullishColor));
        BearishColor = HsvData.FromHtmlSafe(settings.CandlestickBearishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBearishColor));
        NeutralColor = HsvData.FromHtmlSafe(settings.CandlestickNeutralColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultNeutralColor));
    }

    protected override void UpdateInitialState()
    {
        _initialBullishColor = BullishColor;
        _initialBearishColor = BearishColor;
        _initialNeutralColor = NeutralColor;
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            CandlestickBullishColor = BullishColor.ToHtml(),
            CandlestickBearishColor = BearishColor.ToHtml(),
            CandlestickNeutralColor = NeutralColor.ToHtml()
        };
    }
}
