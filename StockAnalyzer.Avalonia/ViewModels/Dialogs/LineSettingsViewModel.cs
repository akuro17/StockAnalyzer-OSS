using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class LineSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "ChartType_Line";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showLineMarkers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _lineColor;

    private bool _initialShowLineMarkers;
    private HsvData _initialLineColor;

    public LineSettingsViewModel(IChartSettingsManager settingsManager, ILogger<LineSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        ShowLineMarkers = settings.ShowLineMarkers;
        LineColor = HsvData.FromHtmlSafe(settings.LineColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultLineColor));
    }

    protected override void UpdateInitialState()
    {
        _initialShowLineMarkers = ShowLineMarkers;
        _initialLineColor = LineColor;
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            ShowLineMarkers = ShowLineMarkers,
            LineColor = LineColor.ToHtml()
        };
    }

    public override bool IsModified => 
        ShowLineMarkers != _initialShowLineMarkers ||
        LineColor != _initialLineColor;
}
