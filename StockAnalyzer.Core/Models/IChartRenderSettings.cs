using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// UI-Agnostic interface for chart rendering settings.
/// Decouples the rendering pipeline and factories from the Avalonia-specific ViewModel.
/// </summary>
public interface IChartRenderSettings
{
    ChartType ChartType { get; }
    IThemeManager? ThemeManager { get; }
    int VisibleStartIndex { get; }
    bool ShowLineMarkers { get; }
    bool ShowAreaMarkers { get; }
    IReadOnlyList<string> ComparisonTargets { get; }
    SeriesColorIndex SeriesColorIndex { get; }
    
    /// <summary>Gets the display mode for relative performance (Performance, Ratio, Z-Score).</summary>
    ComparisonMode ComparisonMode { get; }
    
    /// <summary>Gets the lookback period for Z-Score calculation (Default: 20).</summary>
    int ComparisonZScorePeriod { get; }
    
    bool ShowTickerInsteadOfValue { get; }
    
    // Visibility Control (Step 68-1-4)
    bool IsSubWindowVisible { get; }
    bool InvertOscillator { get; }

    // Visibility Flags
    bool RenkoShowMultiWavePatterns { get; }
    int RenkoMultiWaveMaxLines { get; }
    HsvData RenkoMultiWaveBullishColor { get; }
    HsvData RenkoMultiWaveBearishColor { get; }
    bool RenkoShowGhostProjections { get; }
    float RenkoGhostProjectionFontSize { get; }
    bool RenkoShowGhostLabelsOnHoverOnly { get; }

    bool PnfShowMultiWavePatterns { get; }
    int PnfMultiWaveMaxLines { get; }
    bool PnfShowGhostProjections { get; }
    float PnfGhostProjectionFontSize { get; }
    bool PnfShowGhostLabelsOnHoverOnly { get; }

    bool KagiShowMultiWavePatterns { get; }
    int KagiMultiWaveMaxLines { get; }
    HsvData KagiMultiWaveBullishColor { get; }
    HsvData KagiMultiWaveBearishColor { get; }
    bool KagiShowGhostProjections { get; }
    float KagiGhostProjectionFontSize { get; }
    bool KagiShowGhostLabelsOnHoverOnly { get; }

    bool TlbShowMultiWavePatterns { get; }
    int TlbMultiWaveMaxLines { get; }
    HsvData TlbMultiWaveBullishColor { get; }
    HsvData TlbMultiWaveBearishColor { get; }
    bool TlbShowGhostProjections { get; }
    float TlbGhostProjectionFontSize { get; }
    bool TlbShowGhostLabelsOnHoverOnly { get; }

    bool ShowReverseWatchGrid { get; }
    bool ReverseWatchIsMaBased { get; }
    bool ReverseWatchIsLogScaleVolume { get; }
    int ReverseWatchDataCount { get; }

    // Unified Color Settings (using HsvData for framework independence)
    HsvData BullishColor { get; }
    HsvData BearishColor { get; }
    HsvData NeutralColor { get; }

    HsvData HeikinBullishColor { get; }
    HsvData HeikinBearishColor { get; }

    HsvData OhlcBullishColor { get; }
    HsvData OhlcBearishColor { get; }

    HsvData LineChartColor { get; }
    HsvData AreaChartColor { get; }

    HsvData RenkoBullishColor { get; }
    HsvData RenkoBearishColor { get; }

    HsvData PnfBullishColor { get; }
    HsvData PnfBearishColor { get; }
    HsvData PnfBreakoutBullishColor { get; }
    HsvData PnfBreakoutBearishColor { get; }

    HsvData KagiBullishColor { get; }
    HsvData KagiBearishColor { get; }

    HsvData ThreeLineBreakBullishColor { get; }
    HsvData ThreeLineBreakBearishColor { get; }

    HsvData ReverseWatchPhase1Color { get; }
    HsvData ReverseWatchPhase2Color { get; }
    HsvData ReverseWatchPhase3Color { get; }
    HsvData ReverseWatchPhase4Color { get; }
    HsvData ReverseWatchPhase5Color { get; }
    HsvData ReverseWatchPhase6Color { get; }
    HsvData ReverseWatchPhase7Color { get; }
    HsvData ReverseWatchPhase8Color { get; }

    // Specific Parameters
    decimal EffectiveRenkoBrickSize { get; }
    int RenkoReversal { get; }
    decimal EffectivePnfBoxSize { get; }
    int PnfReversal { get; }
    
    // P&F Breakout Pattern Toggles
    bool PnfShowDoubleBreakout { get; }
    bool PnfShowTripleBreakout { get; }
    bool PnfShowTrendlineBreakout { get; }
    bool PnfShowTriangleBreakout { get; }
    bool PnfShowCatapultBreakout { get; }
    
    decimal EffectiveKagiReversalAmount { get; }
    ChartSizingMode KagiReversalMode { get; }
    decimal KagiReversalPercent { get; }
    float KagiLineThickness { get; }
    int KagiInitialColumn { get; }
    int ThreeLineBreakLineCount { get; }
    bool ShowThreeLineBreakReversalLine { get; }
    bool ShowThreeLineBreakReversalPrice { get; }
    HsvData ThreeLineBreakReversalLineColor { get; }
    
    float ReverseWatchLineThickness { get; }

    // Ghost Projection Settings
    float GhostProjectionFontSize { get; }
    bool ShowGhostLabelsOnHoverOnly { get; }

    // GS-12: Global Configuration Extensions
    double DefaultDrawingThickness { get; }
    bool CrosshairLabelVisible { get; }
    PriceScaleType PriceScale { get; }
    /// <summary>
    /// Consumes (reads and resets) the accumulated render reasons.
    /// </summary>
    /// <returns>The accumulated <see cref="RenderReason"/>.</returns>
    RenderReason ConsumeRenderReason();

    event System.Action? SettingsChanged;
}
