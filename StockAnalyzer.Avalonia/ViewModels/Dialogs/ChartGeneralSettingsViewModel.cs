using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the Global Chart Settings page.
/// Implements deterministic change management with Rx-based throttling.
/// </summary>
public partial class ChartGeneralSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "Settings_Chart";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _crosshairLabelVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _ghostProjectionFontSize = 12.0f;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showGhostLabelsOnHoverOnly = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showFloatingTooltip = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _topMargin = 0f;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _bottomMargin = 30f;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _rightMargin = 65f;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _isSubWindowVisible = true;

    public override bool IsModified
    {
        get
        {
            return CrosshairLabelVisible != Snapshot.CrosshairLabelVisible ||
                   Math.Abs(GhostProjectionFontSize - Snapshot.GhostProjectionFontSize) > 0.01f ||
                   ShowGhostLabelsOnHoverOnly != Snapshot.ShowGhostLabelsOnHoverOnly ||
                   ShowFloatingTooltip != Snapshot.ShowFloatingTooltip ||
                   Math.Abs(TopMargin - Snapshot.TopMargin) > 0.01f ||
                   Math.Abs(BottomMargin - Snapshot.BottomMargin) > 0.01f ||
                   Math.Abs(RightMargin - Snapshot.RightMargin) > 0.01f ||
                   IsSubWindowVisible != Snapshot.IsSubWindowVisible;
        }
    }

    public ChartGeneralSettingsViewModel(IChartSettingsManager settingsManager, ILogger<ChartGeneralSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    // Design-time constructor
    public ChartGeneralSettingsViewModel()
        : base(null!, null)
    {
        Snapshot = new GlobalChartSettings();
        LoadFromSettings(Snapshot);
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        _crosshairLabelVisible = settings.CrosshairLabelVisible;
        _ghostProjectionFontSize = settings.GhostProjectionFontSize;
        _showGhostLabelsOnHoverOnly = settings.ShowGhostLabelsOnHoverOnly;
        _showFloatingTooltip = settings.ShowFloatingTooltip;
        _topMargin = settings.TopMargin;
        _bottomMargin = settings.BottomMargin;
        _rightMargin = settings.RightMargin;
        _isSubWindowVisible = settings.IsSubWindowVisible;
        
        OnPropertyChanged(nameof(CrosshairLabelVisible));
        OnPropertyChanged(nameof(GhostProjectionFontSize));
        OnPropertyChanged(nameof(ShowGhostLabelsOnHoverOnly));
        OnPropertyChanged(nameof(ShowFloatingTooltip));
        OnPropertyChanged(nameof(TopMargin));
        OnPropertyChanged(nameof(BottomMargin));
        OnPropertyChanged(nameof(RightMargin));
        OnPropertyChanged(nameof(IsSubWindowVisible));
        OnPropertyChanged(nameof(IsModified));
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            CrosshairLabelVisible = CrosshairLabelVisible,
            GhostProjectionFontSize = Math.Clamp(GhostProjectionFontSize, ChartSettingsConstants.MinDrawingFontSize, ChartSettingsConstants.MaxDrawingFontSize),
            ShowGhostLabelsOnHoverOnly = ShowGhostLabelsOnHoverOnly,
            ShowFloatingTooltip = ShowFloatingTooltip,
            TopMargin = (float)Math.Clamp(TopMargin, 0f, 200f),
            BottomMargin = (float)Math.Clamp(BottomMargin, 0f, 200f),
            RightMargin = (float)Math.Clamp(RightMargin, 0f, 200f),
            IsSubWindowVisible = IsSubWindowVisible
        };
    }
}
