using SkiaSharp;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Live-updating theme/font values for the handful of IChartObject drawing tools whose text
/// labels need to stay readable across theme changes (e.g. LongShortPositionObject,
/// TargetPriceProjectionObject). IChartObject implementers are plain POCOs with no DI and no
/// access to IThemeManager/IFontSettingsManager (Render(SKCanvas, ICoordinateTransform) carries
/// neither); changing that shared interface to thread settings through would touch every one of
/// the ~44 drawing-tool classes for the benefit of just two. This static context is initialized
/// once at app startup and kept in sync via PropertyChanged subscriptions, so drawing objects can
/// read a cheap, always-current value at render time without any per-object wiring.
/// </summary>
public static class DrawingThemeContext
{
    public static SKColor TextColor { get; private set; } = SKColors.Black;
    public static SKColor ChartBackground { get; private set; } = SKColors.White;
    public static float FontSize { get; private set; } = 12f;
    public static float DetailFontSize => FontSize;
    public static float HelperFontSize { get; private set; } = 12f;
    public static float BaseFontSize { get; private set; } = 16f;

    // Settings > Theme > "Main Text" / "App Background", used as the default (non-live-rebinding)
    // color for chart-object text paints and background boxes, following the same "captured at
    // construction time" pattern as DefaultColor/DefaultStrokeThickness below.
    public static SKColor MainTextSkColor { get; private set; } = SKColors.Black;
    public static global::Avalonia.Media.Color MainTextColor { get; private set; } = global::Avalonia.Media.Colors.Black;
    public static global::Avalonia.Media.Color AppBackgroundColor { get; private set; } = global::Avalonia.Media.Colors.White;

    // Drawing Tool Settings
    public static SKColor DefaultSkColor { get; private set; } = SKColor.Parse(StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultDrawingColor);
    public static global::Avalonia.Media.Color DefaultColor { get; private set; } = global::Avalonia.Media.Color.Parse(StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultDrawingColor);
    public static SKColor HandleColor { get; private set; } = SKColor.Parse(StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultDrawingHandleColor);
    public static global::Avalonia.Media.Color HandleAvaloniaColor { get; private set; } = global::Avalonia.Media.Color.Parse(StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultDrawingHandleColor);
    public static SKColor AnchorPointColor { get; private set; } = SKColor.Parse(StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultAnchorPointColor);
    public static global::Avalonia.Media.Color AnchorPointAvaloniaColor { get; private set; } = global::Avalonia.Media.Color.Parse(StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultAnchorPointColor);
    public static float DrawingFontSize { get; private set; } = StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultDrawingFontSize;
    public static double DefaultStrokeThickness { get; private set; } = 1.0;
    public static bool SmartGuidesEnabled { get; private set; } = StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultSmartGuidesEnabled;
    public static double SmartGuideSnapDistance { get; private set; } = StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultSmartGuideSnapDistance;
    public static int ControlPointHideTimeoutMs { get; private set; } = StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultControlPointHideTimeoutSeconds * 1000;
    public static StockAnalyzer.Core.Models.DrawingToolContinuationMode DrawingToolContinuationMode { get; private set; } = StockAnalyzer.Core.Models.DrawingToolContinuationMode.ReturnToPointer;

    private static bool _initialized;

    /// <summary>Testability seam: sets the continuation mode directly, bypassing the once-only Initialize() guard.</summary>
    internal static void SetDrawingToolContinuationModeForTesting(StockAnalyzer.Core.Models.DrawingToolContinuationMode mode) => DrawingToolContinuationMode = mode;

    /// <summary>Testability seam: sets the control-point hide timeout directly, bypassing the once-only Initialize() guard.</summary>
    internal static void SetControlPointHideTimeoutMsForTesting(int ms) => ControlPointHideTimeoutMs = ms;

    public static void Initialize(
        IThemeManager themeManager, 
        IFontSettingsManager fontSettingsManager, 
        StockAnalyzer.Core.Services.IChartSettingsManager? chartSettingsManager = null)
    {
        if (_initialized) return;
        _initialized = true;

        UpdateTextColor(themeManager);
        UpdateChartBackground(themeManager);
        UpdateFontSize(fontSettingsManager);
        if (chartSettingsManager != null)
        {
            UpdateChartDrawingSettings(chartSettingsManager);
            chartSettingsManager.SettingsChanged += () => UpdateChartDrawingSettings(chartSettingsManager);
        }

        themeManager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == null || e.PropertyName == nameof(IThemeManager.CurrentTheme))
            {
                UpdateTextColor(themeManager);
                UpdateChartBackground(themeManager);
            }
        };

        fontSettingsManager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == null ||
                e.PropertyName == nameof(IFontSettingsManager.DetailFontSize) ||
                e.PropertyName == nameof(IFontSettingsManager.HelperFontSize) ||
                e.PropertyName == nameof(IFontSettingsManager.BaseFontSize))
            {
                UpdateFontSize(fontSettingsManager);
            }
        };
    }

    private static void UpdateTextColor(IThemeManager themeManager)
    {
        TextColor = themeManager.CurrentTheme.AxisText.ToSkColor();

        var mainTextSk = themeManager.CurrentTheme.ShellText.ToSkColor();
        MainTextSkColor = mainTextSk;
        MainTextColor = global::Avalonia.Media.Color.FromArgb(mainTextSk.Alpha, mainTextSk.Red, mainTextSk.Green, mainTextSk.Blue);
    }

    private static void UpdateChartBackground(IThemeManager themeManager)
    {
        ChartBackground = themeManager.CurrentTheme.ChartBackground.ToSkColor();

        var appBgSk = themeManager.CurrentTheme.ShellBackground.ToSkColor();
        AppBackgroundColor = global::Avalonia.Media.Color.FromArgb(appBgSk.Alpha, appBgSk.Red, appBgSk.Green, appBgSk.Blue);
    }

    private static void UpdateFontSize(IFontSettingsManager fontSettingsManager)
    {
        FontSize = (float)fontSettingsManager.DetailFontSize;
        HelperFontSize = (float)fontSettingsManager.HelperFontSize;
        BaseFontSize = (float)fontSettingsManager.BaseFontSize;
    }

    private static void UpdateChartDrawingSettings(StockAnalyzer.Core.Services.IChartSettingsManager chartSettingsManager)
    {
        var settings = chartSettingsManager.Current;
        DrawingFontSize = settings.DrawingFontSize;
        DefaultStrokeThickness = settings.DefaultStrokeThickness;

        var defColorStr = !string.IsNullOrWhiteSpace(settings.DrawingDefaultColor)
            ? settings.DrawingDefaultColor
            : StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultDrawingColor;

        if (SKColor.TryParse(defColorStr, out var defSk))
        {
            DefaultSkColor = defSk;
            DefaultColor = global::Avalonia.Media.Color.FromArgb(defSk.Alpha, defSk.Red, defSk.Green, defSk.Blue);
        }

        var handleColorStr = !string.IsNullOrWhiteSpace(settings.DrawingHandleColor)
            ? settings.DrawingHandleColor
            : StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultDrawingHandleColor;

        if (SKColor.TryParse(handleColorStr, out var hSk))
        {
            HandleColor = hSk;
            HandleAvaloniaColor = global::Avalonia.Media.Color.FromArgb(hSk.Alpha, hSk.Red, hSk.Green, hSk.Blue);
        }

        var anchorColorStr = !string.IsNullOrWhiteSpace(settings.AnchorPointColor)
            ? settings.AnchorPointColor
            : StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultAnchorPointColor;

        if (SKColor.TryParse(anchorColorStr, out var aSk))
        {
            AnchorPointColor = aSk;
            AnchorPointAvaloniaColor = global::Avalonia.Media.Color.FromArgb(aSk.Alpha, aSk.Red, aSk.Green, aSk.Blue);
        }

        SmartGuidesEnabled = settings.SmartGuidesEnabled;
        SmartGuideSnapDistance = settings.SmartGuideSnapDistance;
        ControlPointHideTimeoutMs = settings.ControlPointHideTimeoutSeconds * 1000;
        DrawingToolContinuationMode = settings.DrawingToolContinuationMode;
    }
}
