using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the Chart -> Drawing settings page.
/// Implements deterministic change management with Rx-based throttling.
/// </summary>
public partial class DrawingSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "Settings_Chart_Drawing_CategoryTitle";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private double _defaultStrokeThickness = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _drawingDefaultColor = HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultDrawingColor);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _drawingHandleColor = HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultDrawingHandleColor);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _drawingFontSize = ChartSettingsConstants.DefaultDrawingFontSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _smartGuidesEnabled = ChartSettingsConstants.DefaultSmartGuidesEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private double _smartGuideSnapDistance = ChartSettingsConstants.DefaultSmartGuideSnapDistance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private int _controlPointHideTimeoutSeconds = ChartSettingsConstants.DefaultControlPointHideTimeoutSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private StockAnalyzer.Core.Models.DrawingToolContinuationMode _drawingToolContinuationMode = StockAnalyzer.Core.Models.DrawingToolContinuationMode.ReturnToPointer;

    public static IReadOnlyList<StockAnalyzer.Core.Models.DrawingToolContinuationMode> ContinuationModeOptions { get; } =
        Enum.GetValues<StockAnalyzer.Core.Models.DrawingToolContinuationMode>();

    public override bool IsModified
    {
        get
        {
            return Math.Abs(DefaultStrokeThickness - Snapshot.DefaultStrokeThickness) > 0.0001 ||
                   DrawingDefaultColor != HsvData.FromHtmlSafe(Snapshot.DrawingDefaultColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultDrawingColor)) ||
                   DrawingHandleColor != HsvData.FromHtmlSafe(Snapshot.DrawingHandleColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultDrawingHandleColor)) ||
                   Math.Abs(DrawingFontSize - Snapshot.DrawingFontSize) > 0.01f ||
                   SmartGuidesEnabled != Snapshot.SmartGuidesEnabled ||
                   Math.Abs(SmartGuideSnapDistance - Snapshot.SmartGuideSnapDistance) > 0.0001 ||
                   ControlPointHideTimeoutSeconds != Snapshot.ControlPointHideTimeoutSeconds ||
                   DrawingToolContinuationMode != Snapshot.DrawingToolContinuationMode;
        }
    }

    public DrawingSettingsViewModel(IChartSettingsManager settingsManager, ILogger<DrawingSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    // Design-time constructor
    public DrawingSettingsViewModel()
        : base(null!, null)
    {
        Snapshot = new GlobalChartSettings();
        LoadFromSettings(Snapshot);
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        _defaultStrokeThickness = settings.DefaultStrokeThickness;
        _drawingDefaultColor = HsvData.FromHtmlSafe(settings.DrawingDefaultColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultDrawingColor));
        _drawingHandleColor = HsvData.FromHtmlSafe(settings.DrawingHandleColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultDrawingHandleColor));
        _drawingFontSize = settings.DrawingFontSize;
        _smartGuidesEnabled = settings.SmartGuidesEnabled;
        _smartGuideSnapDistance = settings.SmartGuideSnapDistance;
        _controlPointHideTimeoutSeconds = settings.ControlPointHideTimeoutSeconds;
        _drawingToolContinuationMode = settings.DrawingToolContinuationMode;

        OnPropertyChanged(nameof(DefaultStrokeThickness));
        OnPropertyChanged(nameof(DrawingDefaultColor));
        OnPropertyChanged(nameof(DrawingHandleColor));
        OnPropertyChanged(nameof(DrawingFontSize));
        OnPropertyChanged(nameof(SmartGuidesEnabled));
        OnPropertyChanged(nameof(SmartGuideSnapDistance));
        OnPropertyChanged(nameof(ControlPointHideTimeoutSeconds));
        OnPropertyChanged(nameof(DrawingToolContinuationMode));
        OnPropertyChanged(nameof(IsModified));
    }

    protected override GlobalChartSettings CreateSettings()
    {
        double thickness = DefaultStrokeThickness;
        if (double.IsNaN(thickness) || double.IsInfinity(thickness) || thickness < 0.1 || thickness > 10.0)
        {
            thickness = 1.0;
        }

        return Snapshot with
        {
            DefaultStrokeThickness = thickness,
            DrawingDefaultColor = DrawingDefaultColor.ToHtml(),
            DrawingHandleColor = DrawingHandleColor.ToHtml(),
            DrawingFontSize = Math.Clamp(DrawingFontSize, ChartSettingsConstants.MinDrawingFontSize, ChartSettingsConstants.MaxDrawingFontSize),
            SmartGuidesEnabled = SmartGuidesEnabled,
            SmartGuideSnapDistance = Math.Clamp(SmartGuideSnapDistance, ChartSettingsConstants.MinSmartGuideSnapDistance, ChartSettingsConstants.MaxSmartGuideSnapDistance),
            ControlPointHideTimeoutSeconds = Math.Clamp(ControlPointHideTimeoutSeconds, ChartSettingsConstants.MinControlPointHideTimeoutSeconds, ChartSettingsConstants.MaxControlPointHideTimeoutSeconds),
            DrawingToolContinuationMode = DrawingToolContinuationMode
        };
    }
}
