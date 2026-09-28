using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class AreaSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "ChartType_Area";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showAreaMarkers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _areaBaseColor;

    private bool _initialShowAreaMarkers;
    private HsvData _initialAreaBaseColor;

    public AreaSettingsViewModel(IChartSettingsManager settingsManager, ILogger<AreaSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        ShowAreaMarkers = settings.ShowAreaMarkers;
        AreaBaseColor = HsvData.FromHtmlSafe(settings.AreaBaseColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultAreaColor));
    }

    protected override void UpdateInitialState()
    {
        _initialShowAreaMarkers = ShowAreaMarkers;
        _initialAreaBaseColor = AreaBaseColor;
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            ShowAreaMarkers = ShowAreaMarkers,
            AreaBaseColor = AreaBaseColor.ToHtml()
        };
    }

    public override bool IsModified => 
        ShowAreaMarkers != _initialShowAreaMarkers ||
        AreaBaseColor != _initialAreaBaseColor;
}
