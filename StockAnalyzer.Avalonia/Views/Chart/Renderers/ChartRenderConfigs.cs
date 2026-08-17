using Avalonia;
using SkiaSharp;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Utilities;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// 共通設定を IChartRenderSettings から抽出するためのヘルパーメソッド群。
/// </summary>
public static class ChartRenderConfigFactoryHelper
{
    public static (NamedColor reversal, NamedColor price) GetLabelsColors(IChartRenderSettings settings)
    {
        var (revHex, priceHex) = settings.ChartType switch
        {
            ChartType.Renko => (settings.RenkoBullishColor.ToIndicatorColor().ToHex(), settings.RenkoBearishColor.ToIndicatorColor().ToHex()),
            ChartType.PointAndFigure => (settings.PnfBullishColor.ToIndicatorColor().ToHex(), settings.PnfBearishColor.ToIndicatorColor().ToHex()),
            ChartType.Kagi => (settings.KagiBullishColor.ToIndicatorColor().ToHex(), settings.KagiBearishColor.ToIndicatorColor().ToHex()),
            ChartType.ThreeLineBreak => (settings.ThreeLineBreakBullishColor.ToIndicatorColor().ToHex(), settings.ThreeLineBreakBearishColor.ToIndicatorColor().ToHex()),
            ChartType.HeikinAshi => (settings.HeikinBullishColor.ToIndicatorColor().ToHex(), settings.HeikinBearishColor.ToIndicatorColor().ToHex()),
            ChartType.OHLCBar => (settings.OhlcBullishColor.ToIndicatorColor().ToHex(), settings.OhlcBearishColor.ToIndicatorColor().ToHex()),
            ChartType.Line => (settings.LineChartColor.ToIndicatorColor().ToHex(), settings.LineChartColor.ToIndicatorColor().ToHex()),
            ChartType.Area => (settings.AreaChartColor.ToIndicatorColor().ToHex(), settings.AreaChartColor.ToIndicatorColor().ToHex()),
            _ => (settings.BullishColor.ToIndicatorColor().ToHex(), settings.BearishColor.ToIndicatorColor().ToHex()) 
        };
        return (new NamedColor("Custom", revHex), new NamedColor("Custom", priceHex));
    }
}

/// <summary>
/// ローソク足 (Candlestick) 専用の設定データ構造。
/// 構造体化によりホットパスでのヒープ割り当てを排除。
/// </summary>
public readonly record struct CandlestickRenderConfig(
    IThemeManager ThemeManager,
    ChartType ChartType,
    double CurrentPrice,
    IndicatorColor BullishColor,
    IndicatorColor BearishColor,
    IndicatorColor NeutralColor,
    NamedColor ReversalLabelColor,
    NamedColor PriceLabelColor,
    int VisibleStartIndex,
    int VisibleCandleCount,
    ICoordinateTransform? Transform,
    StockAnalyzer.Core.Models.Point MousePosition, bool ShowMultiWavePatterns, bool ShowGhostProjections,
    float GhostProjectionFontSize, bool ShowGhostLabelsOnHoverOnly,
    double RenderScaling,
    bool IsSubWindowVisible,
    bool InvertOscillator,
    double DefaultDrawingThickness,
    bool CrosshairLabelVisible
) : ICandlestickRenderConfig
{
    public static CandlestickRenderConfig Create(
        IChartRenderSettings settings, 
        decimal currentPrice, 
        int visibleStartIndex, 
        int visibleCandleCount, 
        StockAnalyzer.Core.Models.Point mousePosition, 
        ICoordinateTransform? transform,
        double renderScaling)
    {
        var colors = ChartRenderConfigFactoryHelper.GetLabelsColors(settings);
        
        return new CandlestickRenderConfig(
            ThemeManager: settings.ThemeManager!,
            ChartType: settings.ChartType,
            CurrentPrice: (double)currentPrice,
            BullishColor: settings.BullishColor.ToIndicatorColor(),
            BearishColor: settings.BearishColor.ToIndicatorColor(),
            NeutralColor: settings.NeutralColor.ToIndicatorColor(),
            ReversalLabelColor: colors.reversal,
            PriceLabelColor: colors.price,
            VisibleStartIndex: visibleStartIndex,
            VisibleCandleCount: visibleCandleCount,
            Transform: transform,
            MousePosition: mousePosition, ShowMultiWavePatterns: false, ShowGhostProjections: false,
            GhostProjectionFontSize: settings.GhostProjectionFontSize, ShowGhostLabelsOnHoverOnly: settings.ShowGhostLabelsOnHoverOnly,
            RenderScaling: renderScaling,
            IsSubWindowVisible: settings.IsSubWindowVisible,
            InvertOscillator: settings.InvertOscillator,
            DefaultDrawingThickness: settings.DefaultDrawingThickness,
            CrosshairLabelVisible: settings.CrosshairLabelVisible
        );
    }
}

/// <summary>
/// 逆時計曲線 (Reverse Watch) 専用の設定データ構造。
/// </summary>
public readonly record struct ReverseWatchRenderConfig(
    IThemeManager ThemeManager, ChartType ChartType,
    double CurrentPrice, IndicatorColor BullishColor, IndicatorColor BearishColor, NamedColor ReversalLabelColor, NamedColor PriceLabelColor,
    int VisibleStartIndex, int VisibleCandleCount, ICoordinateTransform? Transform, StockAnalyzer.Core.Models.Point MousePosition, bool ShowMultiWavePatterns, bool ShowGhostProjections,
    float GhostProjectionFontSize, bool ShowGhostLabelsOnHoverOnly,
    double RenderScaling,
    bool IsSubWindowVisible,
    bool InvertOscillator,
    double DefaultDrawingThickness,
    bool CrosshairLabelVisible,
    IndicatorColor NeutralColor,
    ReverseWatchCurveData? ReverseWatchData, bool ShowReverseWatchGrid, float ReverseWatchLineThickness,
    bool ReverseWatchIsMaBased, bool ReverseWatchIsLogScaleVolume, int ReverseWatchDataCount,
    IndicatorColor Phase1Color, IndicatorColor Phase2Color, IndicatorColor Phase3Color, IndicatorColor Phase4Color,
    IndicatorColor Phase5Color, IndicatorColor Phase6Color, IndicatorColor Phase7Color, IndicatorColor Phase8Color
) : IReverseWatchRenderConfig
{
    public IndicatorColor GetPhaseColor(ReverseWatchPhase phase) => phase switch
    {
        ReverseWatchPhase.Phase1 => Phase1Color,
        ReverseWatchPhase.Phase2 => Phase2Color,
        ReverseWatchPhase.Phase3 => Phase3Color,
        ReverseWatchPhase.Phase4 => Phase4Color,
        ReverseWatchPhase.Phase5 => Phase5Color,
        ReverseWatchPhase.Phase6 => Phase6Color,
        ReverseWatchPhase.Phase7 => Phase7Color,
        ReverseWatchPhase.Phase8 => Phase8Color,
        _ => BullishColor
    };

    public static ReverseWatchRenderConfig Create(IChartRenderSettings settings, decimal currentPrice, int visibleStartIndex, int visibleCandleCount, StockAnalyzer.Core.Models.Point mousePosition, ICoordinateTransform? transform, double renderScaling, ReverseWatchCurveData? reverseWatchData)
    {
        var colors = ChartRenderConfigFactoryHelper.GetLabelsColors(settings);
        return new ReverseWatchRenderConfig(
            settings.ThemeManager!, settings.ChartType,
            (double)currentPrice, settings.BullishColor.ToIndicatorColor(), settings.BearishColor.ToIndicatorColor(), colors.reversal, colors.price,
            visibleStartIndex, visibleCandleCount, transform, mousePosition, false, false,
            settings.GhostProjectionFontSize, settings.ShowGhostLabelsOnHoverOnly,
            renderScaling, settings.IsSubWindowVisible,
            settings.InvertOscillator,
            settings.DefaultDrawingThickness, settings.CrosshairLabelVisible,
            settings.NeutralColor.ToIndicatorColor(),
            reverseWatchData, settings.ShowReverseWatchGrid, settings.ReverseWatchLineThickness,
            settings.ReverseWatchIsMaBased, settings.ReverseWatchIsLogScaleVolume, settings.ReverseWatchDataCount,
            settings.ReverseWatchPhase1Color.ToIndicatorColor(), settings.ReverseWatchPhase2Color.ToIndicatorColor(),
            settings.ReverseWatchPhase3Color.ToIndicatorColor(), settings.ReverseWatchPhase4Color.ToIndicatorColor(),
            settings.ReverseWatchPhase5Color.ToIndicatorColor(), settings.ReverseWatchPhase6Color.ToIndicatorColor(),
            settings.ReverseWatchPhase7Color.ToIndicatorColor(), settings.ReverseWatchPhase8Color.ToIndicatorColor());
    }
}

/// <summary>
/// 練行足 (Renko) 専用の設定データ構造。
/// </summary>
public readonly record struct RenkoRenderConfig(
    IThemeManager ThemeManager, ChartType ChartType,
    double CurrentPrice, IndicatorColor BullishColor, IndicatorColor BearishColor, NamedColor ReversalLabelColor, NamedColor PriceLabelColor,
    int VisibleStartIndex, int VisibleCandleCount, ICoordinateTransform? Transform, StockAnalyzer.Core.Models.Point MousePosition, bool ShowMultiWavePatterns, bool ShowGhostProjections,
    float GhostProjectionFontSize, bool ShowGhostLabelsOnHoverOnly,
    double RenderScaling,
    bool IsSubWindowVisible,
    bool InvertOscillator,
    double DefaultDrawingThickness,
    bool CrosshairLabelVisible,
    IndicatorColor NeutralColor,
    IndicatorColor RenkoBullishColor, IndicatorColor RenkoBearishColor, double RenkoBrickSize, int RenkoReversalBricks
) : IRenkoRenderConfig
{
    public static RenkoRenderConfig Create(IChartRenderSettings settings, decimal currentPrice, int visibleStartIndex, int visibleCandleCount, StockAnalyzer.Core.Models.Point mousePosition, ICoordinateTransform? transform, double renderScaling)
    {
        var colors = ChartRenderConfigFactoryHelper.GetLabelsColors(settings);
        return new RenkoRenderConfig(
            settings.ThemeManager!, settings.ChartType,
            (double)currentPrice, settings.BullishColor.ToIndicatorColor(), settings.BearishColor.ToIndicatorColor(), colors.reversal, colors.price,
            visibleStartIndex, visibleCandleCount, transform, mousePosition, settings.RenkoShowMultiWavePatterns, settings.RenkoShowGhostProjections,
            settings.RenkoGhostProjectionFontSize, settings.RenkoShowGhostLabelsOnHoverOnly,
            renderScaling, settings.IsSubWindowVisible,
            settings.InvertOscillator,
            settings.DefaultDrawingThickness, settings.CrosshairLabelVisible,
            settings.NeutralColor.ToIndicatorColor(),
            settings.RenkoBullishColor.ToIndicatorColor(), settings.RenkoBearishColor.ToIndicatorColor(),
            (double)settings.EffectiveRenkoBrickSize, settings.RenkoReversal);
    }
}

/// <summary>
/// ポイント＆フィギュア (P&F) 専用の設定データ構造。
/// </summary>
public readonly record struct PnfRenderConfig(
    IThemeManager ThemeManager, ChartType ChartType,
    double CurrentPrice, IndicatorColor BullishColor, IndicatorColor BearishColor, NamedColor ReversalLabelColor, NamedColor PriceLabelColor,
    int VisibleStartIndex, int VisibleCandleCount, ICoordinateTransform? Transform, StockAnalyzer.Core.Models.Point MousePosition, bool ShowMultiWavePatterns, bool ShowGhostProjections,
    float GhostProjectionFontSize, bool ShowGhostLabelsOnHoverOnly,
    double RenderScaling,
    bool IsSubWindowVisible,
    bool InvertOscillator,
    double DefaultDrawingThickness,
    bool CrosshairLabelVisible,
    IndicatorColor NeutralColor,
    IndicatorColor PnfBullishColor, IndicatorColor PnfBearishColor, decimal PnfBoxSize, int PnfReversalAmount,
    bool PnfShowDoubleBreakout, bool PnfShowTripleBreakout, bool PnfShowTrendlineBreakout, bool PnfShowTriangleBreakout, bool PnfShowCatapultBreakout
) : IPnfRenderConfig
{
    public static PnfRenderConfig Create(IChartRenderSettings settings, decimal currentPrice, int visibleStartIndex, int visibleCandleCount, StockAnalyzer.Core.Models.Point mousePosition, ICoordinateTransform? transform, double renderScaling)
    {
        var colors = ChartRenderConfigFactoryHelper.GetLabelsColors(settings);
        return new PnfRenderConfig(
            settings.ThemeManager!, settings.ChartType,
            (double)currentPrice, settings.BullishColor.ToIndicatorColor(), settings.BearishColor.ToIndicatorColor(), colors.reversal, colors.price,
            visibleStartIndex, visibleCandleCount, transform, mousePosition, settings.PnfShowMultiWavePatterns, settings.PnfShowGhostProjections,
            settings.PnfGhostProjectionFontSize, settings.PnfShowGhostLabelsOnHoverOnly,
            renderScaling, settings.IsSubWindowVisible,
            settings.InvertOscillator,
            settings.DefaultDrawingThickness, settings.CrosshairLabelVisible,
            settings.NeutralColor.ToIndicatorColor(),
            settings.PnfBullishColor.ToIndicatorColor(), settings.PnfBearishColor.ToIndicatorColor(),
            settings.EffectivePnfBoxSize, settings.PnfReversal,
            settings.PnfShowDoubleBreakout, settings.PnfShowTripleBreakout, settings.PnfShowTrendlineBreakout, settings.PnfShowTriangleBreakout, settings.PnfShowCatapultBreakout);
    }
}

/// <summary>
/// カギ足 (Kagi) 専用の設定データ構造。
/// </summary>
public readonly record struct KagiRenderConfig(
    IThemeManager ThemeManager, ChartType ChartType,
    double CurrentPrice, IndicatorColor BullishColor, IndicatorColor BearishColor, NamedColor ReversalLabelColor, NamedColor PriceLabelColor,
    int VisibleStartIndex, int VisibleCandleCount, ICoordinateTransform? Transform, StockAnalyzer.Core.Models.Point MousePosition, bool ShowMultiWavePatterns, bool ShowGhostProjections,
    float GhostProjectionFontSize, bool ShowGhostLabelsOnHoverOnly,
    double RenderScaling,
    bool IsSubWindowVisible,
    bool InvertOscillator,
    double DefaultDrawingThickness,
    bool CrosshairLabelVisible,
    IndicatorColor NeutralColor,
    IndicatorColor KagiBullishColor, IndicatorColor KagiBearishColor, double KagiReversalAmount, bool IsKagiPercentageMode,
    double KagiReversalPercent, float KagiLineThickness, int KagiInitialColumn
) : IKagiRenderConfig
{
    public static KagiRenderConfig Create(IChartRenderSettings settings, decimal currentPrice, int visibleStartIndex, int visibleCandleCount, StockAnalyzer.Core.Models.Point mousePosition, ICoordinateTransform? transform, double renderScaling)
    {
        var colors = ChartRenderConfigFactoryHelper.GetLabelsColors(settings);
        return new KagiRenderConfig(
            settings.ThemeManager!, settings.ChartType,
            (double)currentPrice, settings.BullishColor.ToIndicatorColor(), settings.BearishColor.ToIndicatorColor(), colors.reversal, colors.price,
            visibleStartIndex, visibleCandleCount, transform, mousePosition, settings.KagiShowMultiWavePatterns, settings.KagiShowGhostProjections,
            settings.KagiGhostProjectionFontSize, settings.KagiShowGhostLabelsOnHoverOnly,
            renderScaling, settings.IsSubWindowVisible,
            settings.InvertOscillator,
            settings.DefaultDrawingThickness, settings.CrosshairLabelVisible,
            settings.NeutralColor.ToIndicatorColor(),
            settings.KagiBullishColor.ToIndicatorColor(), settings.KagiBearishColor.ToIndicatorColor(),
            (double)settings.EffectiveKagiReversalAmount, settings.KagiReversalMode == StockAnalyzer.Core.Models.ChartSizingMode.Percentage,
            (double)settings.KagiReversalPercent, settings.KagiLineThickness, settings.KagiInitialColumn);
    }
}

/// <summary>
/// 新値三段可 (Three Line Break) 専用の設定データ構造。
/// </summary>
public readonly record struct ThreeLineBreakRenderConfig(
    IThemeManager ThemeManager, ChartType ChartType,
    double CurrentPrice, IndicatorColor BullishColor, IndicatorColor BearishColor, NamedColor ReversalLabelColor, NamedColor PriceLabelColor,
    int VisibleStartIndex, int VisibleCandleCount, ICoordinateTransform? Transform, StockAnalyzer.Core.Models.Point MousePosition, bool ShowMultiWavePatterns, bool ShowGhostProjections,
    float GhostProjectionFontSize, bool ShowGhostLabelsOnHoverOnly,
    double RenderScaling,
    bool IsSubWindowVisible,
    bool InvertOscillator,
    double DefaultDrawingThickness,
    bool CrosshairLabelVisible,
    IndicatorColor NeutralColor,
    IndicatorColor ThreeLineBreakBullishColor, IndicatorColor ThreeLineBreakBearishColor, int ThreeLineBreakLineCount,
    bool ShowReversalLine, bool ShowReversalPrice, IndicatorColor ReversalLineColor
) : IThreeLineBreakRenderConfig
{
    public static ThreeLineBreakRenderConfig Create(IChartRenderSettings settings, decimal currentPrice, int visibleStartIndex, int visibleCandleCount, StockAnalyzer.Core.Models.Point mousePosition, ICoordinateTransform? transform, double renderScaling)
    {
        var colors = ChartRenderConfigFactoryHelper.GetLabelsColors(settings);
        return new ThreeLineBreakRenderConfig(
            settings.ThemeManager!, settings.ChartType,
            (double)currentPrice, settings.BullishColor.ToIndicatorColor(), settings.BearishColor.ToIndicatorColor(), colors.reversal, colors.price,
            visibleStartIndex, visibleCandleCount, transform, mousePosition, settings.TlbShowMultiWavePatterns, settings.TlbShowGhostProjections,
            settings.TlbGhostProjectionFontSize, settings.TlbShowGhostLabelsOnHoverOnly,
            renderScaling, settings.IsSubWindowVisible,
            settings.InvertOscillator,
            settings.DefaultDrawingThickness, settings.CrosshairLabelVisible,
            settings.NeutralColor.ToIndicatorColor(),
            settings.ThreeLineBreakBullishColor.ToIndicatorColor(), settings.ThreeLineBreakBearishColor.ToIndicatorColor(),
            settings.ThreeLineBreakLineCount, settings.ShowThreeLineBreakReversalLine, settings.ShowThreeLineBreakReversalPrice, settings.ThreeLineBreakReversalLineColor.ToIndicatorColor());
    }
}

/// <summary>
/// OHLCバーチャート専用の設定データ構造。
/// </summary>
public readonly record struct OhlcBarRenderConfig(
    IThemeManager ThemeManager, ChartType ChartType,
    double CurrentPrice, IndicatorColor BullishColor, IndicatorColor BearishColor, NamedColor ReversalLabelColor, NamedColor PriceLabelColor,
    int VisibleStartIndex, int VisibleCandleCount, ICoordinateTransform? Transform, StockAnalyzer.Core.Models.Point MousePosition, bool ShowMultiWavePatterns, bool ShowGhostProjections,
    float GhostProjectionFontSize, bool ShowGhostLabelsOnHoverOnly,
    double RenderScaling,
    bool IsSubWindowVisible,
    bool InvertOscillator,
    double DefaultDrawingThickness,
    bool CrosshairLabelVisible,
    IndicatorColor NeutralColor,
    IndicatorColor OhlcBullishColor, IndicatorColor OhlcBearishColor
) : IOhlcBarRenderConfig
{
    public static OhlcBarRenderConfig Create(IChartRenderSettings settings, decimal currentPrice, int visibleStartIndex, int visibleCandleCount, StockAnalyzer.Core.Models.Point mousePosition, ICoordinateTransform? transform, double renderScaling)
    {
        var colors = ChartRenderConfigFactoryHelper.GetLabelsColors(settings);
        return new OhlcBarRenderConfig(
            settings.ThemeManager!, settings.ChartType,
            (double)currentPrice, settings.BullishColor.ToIndicatorColor(), settings.BearishColor.ToIndicatorColor(), colors.reversal, colors.price,
            visibleStartIndex, visibleCandleCount, transform, mousePosition, false, false,
            settings.GhostProjectionFontSize, settings.ShowGhostLabelsOnHoverOnly,
            renderScaling, settings.IsSubWindowVisible,
            settings.InvertOscillator,
            settings.DefaultDrawingThickness, settings.CrosshairLabelVisible,
            settings.NeutralColor.ToIndicatorColor(),
            settings.OhlcBullishColor.ToIndicatorColor(), settings.OhlcBearishColor.ToIndicatorColor());
    }
}

/// <summary>
/// ラインチャート専用の設定データ構造。
/// </summary>
public readonly record struct LineChartRenderConfig(
    IThemeManager ThemeManager, ChartType ChartType,
    double CurrentPrice, IndicatorColor BullishColor, IndicatorColor BearishColor, NamedColor ReversalLabelColor, NamedColor PriceLabelColor,
    int VisibleStartIndex, int VisibleCandleCount, ICoordinateTransform? Transform, StockAnalyzer.Core.Models.Point MousePosition, bool ShowMultiWavePatterns, bool ShowGhostProjections,
    float GhostProjectionFontSize, bool ShowGhostLabelsOnHoverOnly,
    double RenderScaling,
    bool IsSubWindowVisible,
    bool InvertOscillator,
    double DefaultDrawingThickness,
    bool CrosshairLabelVisible,
    IndicatorColor NeutralColor,
    IndicatorColor LineChartColor, bool ShowLineMarkers
) : ILineChartRenderConfig
{
    public static LineChartRenderConfig Create(IChartRenderSettings settings, decimal currentPrice, int visibleStartIndex, int visibleCandleCount, StockAnalyzer.Core.Models.Point mousePosition, ICoordinateTransform? transform, double renderScaling)
    {
        var colors = ChartRenderConfigFactoryHelper.GetLabelsColors(settings);
        return new LineChartRenderConfig(
            settings.ThemeManager!, settings.ChartType,
            (double)currentPrice, settings.BullishColor.ToIndicatorColor(), settings.BearishColor.ToIndicatorColor(), colors.reversal, colors.price,
            visibleStartIndex, visibleCandleCount, transform, mousePosition, false, false,
            settings.GhostProjectionFontSize, settings.ShowGhostLabelsOnHoverOnly,
            renderScaling, settings.IsSubWindowVisible,
            settings.InvertOscillator,
            settings.DefaultDrawingThickness, settings.CrosshairLabelVisible,
            settings.NeutralColor.ToIndicatorColor(),
            settings.LineChartColor.ToIndicatorColor(), settings.ShowLineMarkers);
    }
}

/// <summary>
/// エリアチャート専用の設定データ構造。
/// </summary>
public readonly record struct AreaChartRenderConfig(
    IThemeManager ThemeManager, ChartType ChartType,
    double CurrentPrice, IndicatorColor BullishColor, IndicatorColor BearishColor, NamedColor ReversalLabelColor, NamedColor PriceLabelColor,
    int VisibleStartIndex, int VisibleCandleCount, ICoordinateTransform? Transform, StockAnalyzer.Core.Models.Point MousePosition, bool ShowMultiWavePatterns, bool ShowGhostProjections,
    float GhostProjectionFontSize, bool ShowGhostLabelsOnHoverOnly,
    double RenderScaling,
    bool IsSubWindowVisible,
    bool InvertOscillator,
    double DefaultDrawingThickness,
    bool CrosshairLabelVisible,
    IndicatorColor NeutralColor,
    IndicatorColor AreaChartColor, bool ShowAreaMarkers
) : IAreaChartRenderConfig
{
    public static AreaChartRenderConfig Create(IChartRenderSettings settings, decimal currentPrice, int visibleStartIndex, int visibleCandleCount, StockAnalyzer.Core.Models.Point mousePosition, ICoordinateTransform? transform, double renderScaling)
    {
        var colors = ChartRenderConfigFactoryHelper.GetLabelsColors(settings);
        return new AreaChartRenderConfig(
            settings.ThemeManager!, settings.ChartType,
            (double)currentPrice, settings.BullishColor.ToIndicatorColor(), settings.BearishColor.ToIndicatorColor(), colors.reversal, colors.price,
            visibleStartIndex, visibleCandleCount, transform, mousePosition, false, false,
            settings.GhostProjectionFontSize, settings.ShowGhostLabelsOnHoverOnly,
            renderScaling, settings.IsSubWindowVisible,
            settings.InvertOscillator,
            settings.DefaultDrawingThickness, settings.CrosshairLabelVisible,
            settings.NeutralColor.ToIndicatorColor(),
            settings.AreaChartColor.ToIndicatorColor(), settings.ShowAreaMarkers);
    }
}

/// <summary>
/// 平均足 (HeikinAshi) 専用の設定データ構造。
/// </summary>
public readonly record struct HeikinAshiRenderConfig(
    IThemeManager ThemeManager, ChartType ChartType,
    double CurrentPrice, IndicatorColor BullishColor, IndicatorColor BearishColor, NamedColor ReversalLabelColor, NamedColor PriceLabelColor,
    int VisibleStartIndex, int VisibleCandleCount, ICoordinateTransform? Transform, StockAnalyzer.Core.Models.Point MousePosition, bool ShowMultiWavePatterns, bool ShowGhostProjections,
    float GhostProjectionFontSize, bool ShowGhostLabelsOnHoverOnly,
    double RenderScaling,
    bool IsSubWindowVisible,
    bool InvertOscillator,
    double DefaultDrawingThickness,
    bool CrosshairLabelVisible,
    IndicatorColor NeutralColor,
    IndicatorColor HeikinBullishColor, IndicatorColor HeikinBearishColor
) : IHeikinAshiRenderConfig
{
    public static HeikinAshiRenderConfig Create(IChartRenderSettings settings, decimal currentPrice, int visibleStartIndex, int visibleCandleCount, StockAnalyzer.Core.Models.Point mousePosition, ICoordinateTransform? transform, double renderScaling)
    {
        var colors = ChartRenderConfigFactoryHelper.GetLabelsColors(settings);
        return new HeikinAshiRenderConfig(
            settings.ThemeManager!, settings.ChartType,
            (double)currentPrice, settings.BullishColor.ToIndicatorColor(), settings.BearishColor.ToIndicatorColor(), colors.reversal, colors.price,
            visibleStartIndex, visibleCandleCount, transform, mousePosition, false, false,
            settings.GhostProjectionFontSize, settings.ShowGhostLabelsOnHoverOnly,
            renderScaling, settings.IsSubWindowVisible,
            settings.InvertOscillator,
            settings.DefaultDrawingThickness, settings.CrosshairLabelVisible,
            settings.NeutralColor.ToIndicatorColor(),
            settings.HeikinBullishColor.ToIndicatorColor(), settings.HeikinBearishColor.ToIndicatorColor());
    }
}


/// <summary>
/// 相対比較 (Relative Comparison) 専用の設定データ構造。
/// </summary>
public readonly record struct ComparisonRenderConfig(
    IThemeManager ThemeManager, ChartType ChartType,
    double CurrentPrice, IndicatorColor BullishColor, IndicatorColor BearishColor, NamedColor ReversalLabelColor, NamedColor PriceLabelColor,
    int VisibleStartIndex, int VisibleCandleCount, ICoordinateTransform? Transform, StockAnalyzer.Core.Models.Point MousePosition, bool ShowMultiWavePatterns, bool ShowGhostProjections,
    float GhostProjectionFontSize, bool ShowGhostLabelsOnHoverOnly,
    double RenderScaling,
    bool IsSubWindowVisible,
    bool InvertOscillator,
    double DefaultDrawingThickness,
    bool CrosshairLabelVisible,
    IndicatorColor NeutralColor,
    ComparisonAlignedData? ComparisonData,
    ComparisonMode ComparisonMode,
    int ComparisonZScorePeriod,
    SeriesColorIndex SeriesColorIndex,
    bool ShowTickerInsteadOfValue
) : IComparisonRenderConfig
{
    public static ComparisonRenderConfig Create(
        IChartRenderSettings settings, 
        decimal currentPrice, 
        int visibleStartIndex, 
        int visibleCandleCount, 
        StockAnalyzer.Core.Models.Point mousePosition, 
        ICoordinateTransform? transform, 
        double renderScaling,
        ComparisonAlignedData? comparisonData)
    {
        var colors = ChartRenderConfigFactoryHelper.GetLabelsColors(settings);
        return new ComparisonRenderConfig(
            settings.ThemeManager!, settings.ChartType,
            (double)currentPrice, settings.BullishColor.ToIndicatorColor(), settings.BearishColor.ToIndicatorColor(), colors.reversal, colors.price,
            visibleStartIndex, visibleCandleCount, transform, mousePosition, false, false,
            settings.GhostProjectionFontSize, settings.ShowGhostLabelsOnHoverOnly,
            renderScaling, settings.IsSubWindowVisible,
            settings.InvertOscillator,
            settings.DefaultDrawingThickness, settings.CrosshairLabelVisible,
            settings.NeutralColor.ToIndicatorColor(),
            comparisonData,
            settings.ComparisonMode,
            settings.ComparisonZScorePeriod,
            settings.SeriesColorIndex,
            settings.ShowTickerInsteadOfValue);
    }
}
