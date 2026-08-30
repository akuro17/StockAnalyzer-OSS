using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class OhlcBarSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "Settings_Chart_OHLCBar";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _bullishColor = new(1, 120, 1, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _bearishColor = new(1, 0, 1, 1);

    private HsvData _initialBullishColor;
    private HsvData _initialBearishColor;

    public override bool IsModified => 
        BullishColor != _initialBullishColor ||
        BearishColor != _initialBearishColor;

    public OhlcBarSettingsViewModel(IChartSettingsManager settingsManager, ILogger<OhlcBarSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        BullishColor = HsvData.FromHtmlSafe(settings.OhlcBullishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBullishColor));
        BearishColor = HsvData.FromHtmlSafe(settings.OhlcBearishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBearishColor));
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
            OhlcBullishColor = BullishColor.ToHtml(),
            OhlcBearishColor = BearishColor.ToHtml()
        };
    }
}
