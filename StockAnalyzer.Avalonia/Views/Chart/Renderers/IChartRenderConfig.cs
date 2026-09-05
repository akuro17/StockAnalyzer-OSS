using Avalonia;
using SkiaSharp;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Avalonia.Models;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// 全チャートタイプ共通の描画設定を定義するインターフェース。
/// </summary>
/// <remarks>
/// <para>
/// 各チャートタイプ固有の設定は派生インターフェースで定義される。
/// Renderer は自タイプ専用のインターフェースにキャストして使用する。
/// </para>
/// <para>
/// <see cref="ChartRenderContext"/> がこのインターフェースと全タイプ別インターフェースを実装するため、
/// 既存の Pipeline コードは一切変更不要 (段階的移行の安全性を確保)。
/// </para>
/// </remarks>
public interface IChartRenderConfig
{
    Core.Theme.IThemeManager ThemeManager { get; }
    ChartType ChartType { get; }
    double CurrentPrice { get; }
    IndicatorColor BullishColor { get; }
    IndicatorColor BearishColor { get; }
    IndicatorColor NeutralColor { get; }
    NamedColor ReversalLabelColor { get; }
    NamedColor PriceLabelColor { get; }
    int VisibleStartIndex { get; }
    int VisibleCandleCount { get; }
    Drawing.ICoordinateTransform? Transform { get; }
    Core.Models.Point MousePosition { get; }

    bool ShowMultiWavePatterns { get; }
    bool ShowGhostProjections { get; }
    int MultiWavePatternMaxLines { get; }
    IndicatorColor? MultiWaveBullishColor { get; }
    IndicatorColor? MultiWaveBearishColor { get; }
    float GhostProjectionFontSize { get; }
    bool ShowGhostLabelsOnHoverOnly { get; }
    double RenderScaling { get; }
    bool IsSubWindowVisible { get; }
    bool InvertOscillator { get; }
    double DefaultDrawingThickness { get; }
    bool CrosshairLabelVisible { get; }
}

// ─────────────────────────────────────────────
// タイプ別 Config インターフェース
// ─────────────────────────────────────────────

/// <summary>Candlestick 固有の設定。共通色のみ使用。</summary>
public interface ICandlestickRenderConfig : IChartRenderConfig { }

/// <summary>HeikinAshi 固有の設定。専用の Bullish/Bearish 色を持つ。</summary>
public interface IHeikinAshiRenderConfig : IChartRenderConfig
{
    IndicatorColor HeikinBullishColor { get; }
    IndicatorColor HeikinBearishColor { get; }
}

/// <summary>OHLCBar 固有の設定。専用の Bullish/Bearish 色を持つ。</summary>
public interface IOhlcBarRenderConfig : IChartRenderConfig
{
    IndicatorColor OhlcBullishColor { get; }
    IndicatorColor OhlcBearishColor { get; }
}

/// <summary>Line チャート固有の設定。</summary>
public interface ILineChartRenderConfig : IChartRenderConfig
{
    IndicatorColor LineChartColor { get; }
    bool ShowLineMarkers { get; }
}

/// <summary>Area チャート固有の設定。</summary>
public interface IAreaChartRenderConfig : IChartRenderConfig
{
    IndicatorColor AreaChartColor { get; }
    bool ShowAreaMarkers { get; }
}

/// <summary>Renko チャート固有の設定。</summary>
public interface IRenkoRenderConfig : IChartRenderConfig
{
    IndicatorColor RenkoBullishColor { get; }
    IndicatorColor RenkoBearishColor { get; }
    double RenkoBrickSize { get; }
    int RenkoReversalBricks { get; }
}

/// <summary>P&amp;F チャート固有の設定。</summary>
public interface IPnfRenderConfig : IChartRenderConfig
{
    IndicatorColor PnfBullishColor { get; }
    IndicatorColor PnfBearishColor { get; }
    decimal PnfBoxSize { get; }
    int PnfReversalAmount { get; }
    bool PnfShowDoubleBreakout { get; }
    bool PnfShowTripleBreakout { get; }
    bool PnfShowTrendlineBreakout { get; }
    bool PnfShowTriangleBreakout { get; }
    bool PnfShowCatapultBreakout { get; }
    IndicatorColor PnfBreakoutBullishColor { get; }
    IndicatorColor PnfBreakoutBearishColor { get; }
}

/// <summary>Kagi チャート固有の設定。</summary>
public interface IKagiRenderConfig : IChartRenderConfig
{
    IndicatorColor KagiBullishColor { get; }
    IndicatorColor KagiBearishColor { get; }
    double KagiReversalAmount { get; }
    bool IsKagiPercentageMode { get; }
    double KagiReversalPercent { get; }
    float KagiLineThickness { get; }
    int KagiInitialColumn { get; }
}

/// <summary>ThreeLineBreak 固有の設定。</summary>
public interface IThreeLineBreakRenderConfig : IChartRenderConfig
{
    IndicatorColor ThreeLineBreakBullishColor { get; }
    IndicatorColor ThreeLineBreakBearishColor { get; }
    int ThreeLineBreakLineCount { get; }
    bool ShowReversalLine { get; }
    bool ShowReversalPrice { get; }
    IndicatorColor ReversalLineColor { get; }
}

/// <summary>ReverseWatch 固有の設定。</summary>
public interface IReverseWatchRenderConfig : IChartRenderConfig
{
    ReverseWatchCurveData? ReverseWatchData { get; }
    IndicatorColor GetPhaseColor(ReverseWatchPhase phase);
    bool ShowReverseWatchGrid { get; }
    float ReverseWatchLineThickness { get; }
    bool ReverseWatchIsMaBased { get; }
    bool ReverseWatchIsLogScaleVolume { get; }
    int ReverseWatchDataCount { get; }
}
/// <summary>RelativeComparison チャート固有の設定。</summary>
public interface IComparisonRenderConfig : IChartRenderConfig
{
    ComparisonAlignedData? ComparisonData { get; }
    ComparisonMode ComparisonMode { get; }
    int ComparisonZScorePeriod { get; }
    SeriesColorIndex SeriesColorIndex { get; }
    bool ShowTickerInsteadOfValue { get; }
}
