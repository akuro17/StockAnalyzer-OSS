using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// Defines a named group of indicators / columns for the Screener registration tab.
/// Organized by Indicator Manager groups, Column Customization groups, and Criteria groups.
/// </summary>
public class ScreenerIndicatorGroup
{
    /// <summary>
    /// Display name of the group (e.g., "Trend", "Oscillator", "Price/Volume").
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Category / section type ("Indicator", "Column", "Criteria").
    /// </summary>
    public string CategoryType { get; }

    /// <summary>
    /// Localization key for the group name (e.g., "ScreenerGroup_Trend").
    /// </summary>
    public string? LocalizationKey { get; }

    /// <summary>
    /// The indicator types belonging to this group (for Indicator groups).
    /// </summary>
    public IReadOnlyList<IndicatorType> IndicatorTypes { get; }

    public ScreenerIndicatorGroup(string name, IReadOnlyList<IndicatorType> indicatorTypes, string categoryType = "Indicator", string? localizationKey = null)
    {
        Name = name;
        IndicatorTypes = indicatorTypes;
        CategoryType = categoryType;
        LocalizationKey = localizationKey;
    }

    /// <summary>
    /// Gets all predefined indicator and column groups for the screener.
    /// Combines Indicator Manager groups, Column Customization groups, and Criteria groups with zero duplicate indicator assignments.
    /// </summary>
    public static IReadOnlyList<ScreenerIndicatorGroup> GetDefaultGroups()
    {
        return new List<ScreenerIndicatorGroup>
        {
            // === Indicator Manager Groups ===
            new("Trend", new[]
            {
                IndicatorType.SMA, IndicatorType.EMA, IndicatorType.WMA,
                IndicatorType.DEMA, IndicatorType.TEMA,
                IndicatorType.KAMA, IndicatorType.HMA, IndicatorType.ZLEMA,
                IndicatorType.FRAMA, IndicatorType.ALMA, IndicatorType.JMA,
                IndicatorType.LSMA, IndicatorType.VWMA,
                IndicatorType.SuperTrend, IndicatorType.Ichimoku,
                IndicatorType.ParabolicSAR, IndicatorType.Aroon,
                IndicatorType.ADX, IndicatorType.ADXR,
                IndicatorType.DMI, IndicatorType.Vortex,
                IndicatorType.TRIX, IndicatorType.SchaffTrendCycle,
                IndicatorType.LRMA, IndicatorType.TMA, IndicatorType.VAMA,
                IndicatorType.SMMA, IndicatorType.GMA, IndicatorType.Dma,
                IndicatorType.MESA, IndicatorType.KalmanFilter,
                IndicatorType.MtfLaggedEma, IndicatorType.MovingAverageCross,
                IndicatorType.FFTTrendFilter
            }, "Indicator", "ScreenerGroup_Trend"),

            new("Oscillator", new[]
            {
                IndicatorType.RSI, IndicatorType.MACD,
                IndicatorType.Stoch, IndicatorType.SMI,
                IndicatorType.WilliamsR, IndicatorType.CCI,
                IndicatorType.ROC, IndicatorType.Momentum,
                IndicatorType.RCI, IndicatorType.UltimateOscillator,
                IndicatorType.PPO, IndicatorType.PriceOscillator,
                IndicatorType.ConnorsRsi, IndicatorType.CMO,
                IndicatorType.FisherTransform, IndicatorType.RVI,
                IndicatorType.DPO, IndicatorType.CoppockCurve,
                IndicatorType.KST, IndicatorType.ElderForceIndex,
                IndicatorType.EOM, IndicatorType.BOP,
                IndicatorType.AwesomeOscillator,
                IndicatorType.AccelerationOscillator,
                IndicatorType.ChaikinOscillator, IndicatorType.DeMarker,
                IndicatorType.ElderRay, IndicatorType.ElderImpulse,
                IndicatorType.MassIndex, IndicatorType.PsychologicalLine,
                IndicatorType.MaDeviationRate, IndicatorType.QStick,
                IndicatorType.StructuralDtw
            }, "Indicator", "ScreenerGroup_Oscillator"),

            new("Volume", new[]
            {
                IndicatorType.OBV, IndicatorType.VWAP,
                IndicatorType.ADL, IndicatorType.CMF, IndicatorType.MFI,
                IndicatorType.ForceIndex, IndicatorType.NegativeVolumeIndex,
                IndicatorType.PVI, IndicatorType.VolumeMA,
                IndicatorType.VolumeROC, IndicatorType.VPT,
                IndicatorType.PVT, IndicatorType.AnchoredVWAP,
                IndicatorType.Volume, IndicatorType.AmihudIlliquidity,
                IndicatorType.KylesLambda
            }, "Indicator", "ScreenerGroup_Volume"),

            new("Volatility", new[]
            {
                IndicatorType.BB, IndicatorType.ATR, IndicatorType.NATR,
                IndicatorType.Keltner, IndicatorType.Donchian,
                IndicatorType.StandardDeviation, IndicatorType.HistoricalVolatility,
                IndicatorType.Envelope, IndicatorType.ChaikinVolatility,
                IndicatorType.UlcerIndex, IndicatorType.BandWidth,
                IndicatorType.BollingerBandsRatio, IndicatorType.YangZhangVolatility,
                IndicatorType.ChoppinessIndex, IndicatorType.WilliamsVixFix,
                IndicatorType.GarmanKlassVolatility, IndicatorType.RviVolatility,
                IndicatorType.Highest, IndicatorType.Lowest, IndicatorType.ExpectedMove,
                IndicatorType.TrueHigh, IndicatorType.TrueLow, IndicatorType.TrueRange
            }, "Indicator", "ScreenerGroup_Volatility"),

            new("Math", new[]
            {
                IndicatorType.LinearRegression, IndicatorType.RegressionSlope,
                IndicatorType.Correlation, IndicatorType.CorrelationProxy,
                IndicatorType.ZScore, IndicatorType.FourierTransform,
                IndicatorType.FFTCycle, IndicatorType.HilbertTransform,
                IndicatorType.Garch11, IndicatorType.Egarch,
                IndicatorType.EngleGrangerTest, IndicatorType.WilderSmoothing,
                IndicatorType.Variance, IndicatorType.VaR, IndicatorType.CVaR,
                IndicatorType.HurstExponent, IndicatorType.Entropy
            }, "Indicator", "ScreenerGroup_Math"),

            new("Chart", new[]
            {
                IndicatorType.PivotPointsAdvanced, IndicatorType.ClassicPivotPoints,
                IndicatorType.MurreyMath, IndicatorType.ZigZag,
                IndicatorType.Renko, IndicatorType.RenkoChart,
                IndicatorType.RenkoOverlay, IndicatorType.Kagi,
                IndicatorType.KagiChart, IndicatorType.KagiOverlay,
                IndicatorType.PointAndFigure, IndicatorType.PointAndFigureChart,
                IndicatorType.PointAndFigureOverlay, IndicatorType.ThreeLineBreak,
                IndicatorType.ThreeLineBreakSignal, IndicatorType.DarvasBox,
                IndicatorType.DarvasBoxSignal, IndicatorType.VortexSignal,
                IndicatorType.MarketStructureShift
            }, "Indicator", "ScreenerGroup_Chart"),

            // === Column Customization Groups ===
            new("Basic", System.Array.Empty<IndicatorType>(), "Column", "ScreenerGroup_Basic"),
            new("Price/Volume", System.Array.Empty<IndicatorType>(), "Column", "ScreenerGroup_PriceVolume"),
            new("Valuation", System.Array.Empty<IndicatorType>(), "Column", "ScreenerGroup_Valuation"),
            new("Profitability", System.Array.Empty<IndicatorType>(), "Column", "ScreenerGroup_Profitability"),
            new("Financial Health", System.Array.Empty<IndicatorType>(), "Column", "ScreenerGroup_FinancialHealth"),

            // === Criteria-specific groups ===
            new("Candlestick Patterns", System.Array.Empty<IndicatorType>(), "Criteria", "ScreenerGroup_CandlestickPatterns"),
            new("Pattern Recognition", System.Array.Empty<IndicatorType>(), "Criteria", "ScreenerGroup_PatternRecognition"),
            new("Market Structure", System.Array.Empty<IndicatorType>(), "Criteria", "ScreenerGroup_MarketStructure"),
            new("Harmonic", System.Array.Empty<IndicatorType>(), "Criteria", "ScreenerGroup_Harmonic"),
            new("Trading Rules", System.Array.Empty<IndicatorType>(), "Criteria", "ScreenerGroup_TradingRules"),
            new("Wave Theory", System.Array.Empty<IndicatorType>(), "Criteria", "ScreenerGroup_WaveTheory"),
        };
    }
}
