using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class HeikinAshiSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "ChartType_HeikinAshi";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _heikinBullishColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _heikinBearishColor;

    private HsvData _initialHeikinBullishColor;
    private HsvData _initialHeikinBearishColor;

    public HeikinAshiSettingsViewModel(IChartSettingsManager settingsManager, ILogger<HeikinAshiSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        HeikinBullishColor = HsvData.FromHtmlSafe(settings.HeikinBullishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultHeikinBullishColor));
        HeikinBearishColor = HsvData.FromHtmlSafe(settings.HeikinBearishColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultHeikinBearishColor));
    }

    protected override void UpdateInitialState()
    {
        _initialHeikinBullishColor = HeikinBullishColor;
        _initialHeikinBearishColor = HeikinBearishColor;
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            HeikinBullishColor = HeikinBullishColor.ToHtml(),
            HeikinBearishColor = HeikinBearishColor.ToHtml()
        };
    }

    public override bool IsModified => 
        HeikinBullishColor != _initialHeikinBullishColor ||
        HeikinBearishColor != _initialHeikinBearishColor;
}
