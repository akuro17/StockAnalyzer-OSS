using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Services.Export;

/// <summary>
/// Proxy wrapping any IChartRenderConfig to substitute the ThemeManager during export.
/// Implements all chart-specific config interfaces to preserve renderer compatibility.
/// </summary>
public sealed class ExportChartRenderConfigProxy : 
    ICandlestickRenderConfig,
    IHeikinAshiRenderConfig,
    IRenkoRenderConfig,
    IPnfRenderConfig,
    IKagiRenderConfig,
    IThreeLineBreakRenderConfig,
    IOhlcBarRenderConfig,
    ILineChartRenderConfig,
    IAreaChartRenderConfig,
    IReverseWatchRenderConfig,
    IComparisonRenderConfig
{
    private readonly IChartRenderConfig _inner;
    private readonly IThemeManager _themeManager;

    public ExportChartRenderConfigProxy(IChartRenderConfig inner, IThemeManager themeManager)
    {
        _inner = inner;
        _themeManager = themeManager;
    }

    public IThemeManager ThemeManager => _themeManager;
    public ChartType ChartType => _inner.ChartType;
    public double CurrentPrice => _inner.CurrentPrice;
    public IndicatorColor BullishColor => _inner.BullishColor;
    public IndicatorColor BearishColor => _inner.BearishColor;
    public IndicatorColor NeutralColor => _inner.NeutralColor;
    public NamedColor ReversalLabelColor => _inner.ReversalLabelColor;
    public NamedColor PriceLabelColor => _inner.PriceLabelColor;
    public int VisibleStartIndex => _inner.VisibleStartIndex;
    public int VisibleCandleCount => _inner.VisibleCandleCount;
    public ICoordinateTransform? Transform => _inner.Transform;
    public StockAnalyzer.Core.Models.Point MousePosition => _inner.MousePosition;

    public bool ShowMultiWavePatterns => _inner.ShowMultiWavePatterns;
    public bool ShowGhostProjections => _inner.ShowGhostProjections;
    public float GhostProjectionFontSize => _inner.GhostProjectionFontSize;
    public bool ShowGhostLabelsOnHoverOnly => _inner.ShowGhostLabelsOnHoverOnly;
    public double RenderScaling => _inner.RenderScaling;
    public bool IsSubWindowVisible => _inner.IsSubWindowVisible;
    public bool InvertOscillator => _inner.InvertOscillator;
    public double DefaultDrawingThickness => _inner.DefaultDrawingThickness;
    public bool CrosshairLabelVisible => _inner.CrosshairLabelVisible;

    // HeikinAshi
    public IndicatorColor HeikinBullishColor => _inner is IHeikinAshiRenderConfig c ? c.HeikinBullishColor : _inner.BullishColor;
    public IndicatorColor HeikinBearishColor => _inner is IHeikinAshiRenderConfig c ? c.HeikinBearishColor : _inner.BearishColor;

    // OHLC Bar
    public IndicatorColor OhlcBullishColor => _inner is IOhlcBarRenderConfig c ? c.OhlcBullishColor : _inner.BullishColor;
    public IndicatorColor OhlcBearishColor => _inner is IOhlcBarRenderConfig c ? c.OhlcBearishColor : _inner.BearishColor;

    // Line
    public IndicatorColor LineChartColor => _inner is ILineChartRenderConfig c ? c.LineChartColor : _inner.BullishColor;
    public bool ShowLineMarkers => _inner is ILineChartRenderConfig c && c.ShowLineMarkers;

    // Area
    public IndicatorColor AreaChartColor => _inner is IAreaChartRenderConfig c ? c.AreaChartColor : _inner.BullishColor;
    public bool ShowAreaMarkers => _inner is IAreaChartRenderConfig c && c.ShowAreaMarkers;

    // Renko
    public IndicatorColor RenkoBullishColor => _inner is IRenkoRenderConfig c ? c.RenkoBullishColor : _inner.BullishColor;
    public IndicatorColor RenkoBearishColor => _inner is IRenkoRenderConfig c ? c.RenkoBearishColor : _inner.BearishColor;
    public double RenkoBrickSize => _inner is IRenkoRenderConfig c ? c.RenkoBrickSize : 1.0;
    public int RenkoReversalBricks => _inner is IRenkoRenderConfig c ? c.RenkoReversalBricks : 1;

    // PnF
    public IndicatorColor PnfBullishColor => _inner is IPnfRenderConfig c ? c.PnfBullishColor : _inner.BullishColor;
    public IndicatorColor PnfBearishColor => _inner is IPnfRenderConfig c ? c.PnfBearishColor : _inner.BearishColor;
    public decimal PnfBoxSize => _inner is IPnfRenderConfig c ? c.PnfBoxSize : 1m;
    public int PnfReversalAmount => _inner is IPnfRenderConfig c ? c.PnfReversalAmount : 3;
    public bool PnfShowDoubleBreakout => _inner is IPnfRenderConfig c && c.PnfShowDoubleBreakout;
    public bool PnfShowTripleBreakout => _inner is IPnfRenderConfig c && c.PnfShowTripleBreakout;
    public bool PnfShowTrendlineBreakout => _inner is IPnfRenderConfig c && c.PnfShowTrendlineBreakout;
    public bool PnfShowTriangleBreakout => _inner is IPnfRenderConfig c && c.PnfShowTriangleBreakout;
    public bool PnfShowCatapultBreakout => _inner is IPnfRenderConfig c && c.PnfShowCatapultBreakout;

    // Kagi
    public IndicatorColor KagiBullishColor => _inner is IKagiRenderConfig c ? c.KagiBullishColor : _inner.BullishColor;
    public IndicatorColor KagiBearishColor => _inner is IKagiRenderConfig c ? c.KagiBearishColor : _inner.BearishColor;
    public double KagiReversalAmount => _inner is IKagiRenderConfig c ? c.KagiReversalAmount : 1.0;
    public bool IsKagiPercentageMode => _inner is IKagiRenderConfig c && c.IsKagiPercentageMode;
    public double KagiReversalPercent => _inner is IKagiRenderConfig c ? c.KagiReversalPercent : 1.0;
    public float KagiLineThickness => _inner is IKagiRenderConfig c ? c.KagiLineThickness : 1f;
    public int KagiInitialColumn => _inner is IKagiRenderConfig c ? c.KagiInitialColumn : 0;

    // ThreeLineBreak
    public IndicatorColor ThreeLineBreakBullishColor => _inner is IThreeLineBreakRenderConfig c ? c.ThreeLineBreakBullishColor : _inner.BullishColor;
    public IndicatorColor ThreeLineBreakBearishColor => _inner is IThreeLineBreakRenderConfig c ? c.ThreeLineBreakBearishColor : _inner.BearishColor;
    public int ThreeLineBreakLineCount => _inner is IThreeLineBreakRenderConfig c ? c.ThreeLineBreakLineCount : 3;
    public bool ShowReversalLine => _inner is IThreeLineBreakRenderConfig c && c.ShowReversalLine;
    public bool ShowReversalPrice => _inner is IThreeLineBreakRenderConfig c && c.ShowReversalPrice;
    public IndicatorColor ReversalLineColor => _inner is IThreeLineBreakRenderConfig c ? c.ReversalLineColor : _inner.BullishColor;

    // ReverseWatch
    public ReverseWatchCurveData? ReverseWatchData => _inner is IReverseWatchRenderConfig c ? c.ReverseWatchData : null;
    public IndicatorColor GetPhaseColor(ReverseWatchPhase phase) => _inner is IReverseWatchRenderConfig c ? c.GetPhaseColor(phase) : _inner.BullishColor;
    public bool ShowReverseWatchGrid => _inner is IReverseWatchRenderConfig c && c.ShowReverseWatchGrid;
    public float ReverseWatchLineThickness => _inner is IReverseWatchRenderConfig c ? c.ReverseWatchLineThickness : 1f;
    public bool ReverseWatchIsMaBased => _inner is IReverseWatchRenderConfig c && c.ReverseWatchIsMaBased;
    public bool ReverseWatchIsLogScaleVolume => _inner is IReverseWatchRenderConfig c && c.ReverseWatchIsLogScaleVolume;
    public int ReverseWatchDataCount => _inner is IReverseWatchRenderConfig c ? c.ReverseWatchDataCount : 0;

    // Comparison
    public ComparisonAlignedData? ComparisonData => _inner is IComparisonRenderConfig c ? c.ComparisonData : null;
    public ComparisonMode ComparisonMode => _inner is IComparisonRenderConfig c ? c.ComparisonMode : ComparisonMode.Performance;
    public int ComparisonZScorePeriod => _inner is IComparisonRenderConfig c ? c.ComparisonZScorePeriod : 20;
    public SeriesColorIndex SeriesColorIndex => _inner is IComparisonRenderConfig c ? c.SeriesColorIndex : new SeriesColorIndex();
    public bool ShowTickerInsteadOfValue => _inner is IComparisonRenderConfig c && c.ShowTickerInsteadOfValue;
}
