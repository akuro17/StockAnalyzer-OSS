using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// The normalization a newly picked channel starts with in the Training Wizard's feature picker (the owner can still change it before adding the channel).
/// A channel is fed to the network as it is unless a normalization is chosen, so the default depends on the unit of the channel:
/// <list type="bullet">
/// <item><see cref="ChannelNormalization.None"/>: the value is already unit-stable (a bounded oscillator, a ratio, a percentage).</item>
/// <item><see cref="ChannelNormalization.WindowMinMax"/>: the value is in price units (a price field, a moving average, a band, a trailing stop).</item>
/// <item><see cref="ChannelNormalization.WindowZScore"/>: every other value (volume-based, range-based, price-difference based, unknown unit). A window
/// z-score is scale-free for any series, so it is the safe choice when the unit is not known to be unit-stable: a wrong <c>None</c> breaks the model
/// (raw prices of 100-250 saturate a recurrent network), a needless z-score only drops the absolute level.</item>
/// </list>
/// An indicator is listed as unit-stable only when its standard definition is bounded, a ratio or a percentage.
/// </summary>
public static class FeatureChannelNormalizationDefaults
{
    /// <summary>Bounded oscillators, ratios and percentages (standard definitions).</summary>
    private static readonly HashSet<IndicatorType> UnitStable = new()
    {
        // bounded oscillators
        IndicatorType.RSI, IndicatorType.ConnorsRsi, IndicatorType.AdaptiveRSI, IndicatorType.Stoch, IndicatorType.WilliamsR, IndicatorType.MFI,
        IndicatorType.CMO, IndicatorType.UltimateOscillator, IndicatorType.DeMarker, IndicatorType.RCI, IndicatorType.SchaffTrendCycle,
        IndicatorType.ADX, IndicatorType.ADXR, IndicatorType.DMI, IndicatorType.Aroon, IndicatorType.Vortex, IndicatorType.CMF, IndicatorType.BOP,
        IndicatorType.ChoppinessIndex, IndicatorType.PsychologicalLine, IndicatorType.EfficiencyRatio,
        IndicatorType.FisherTransform, IndicatorType.CCI,
        // ratios and percentages
        IndicatorType.ROC, IndicatorType.PPO, IndicatorType.PriceOscillator, IndicatorType.TRIX, IndicatorType.BollingerBandsRatio,
        IndicatorType.BandWidth, IndicatorType.NATR, IndicatorType.MaDeviationRate, IndicatorType.VolumeROC, IndicatorType.ZScore,
    };

    /// <summary>Values in price units (the channel moves with the price level).</summary>
    private static readonly HashSet<IndicatorType> PriceNative = new()
    {
        IndicatorType.Price,
        IndicatorType.SMA, IndicatorType.EMA, IndicatorType.WMA, IndicatorType.HMA, IndicatorType.TMA, IndicatorType.DEMA, IndicatorType.TEMA,
        IndicatorType.VWMA, IndicatorType.VAMA, IndicatorType.SMMA, IndicatorType.LRMA, IndicatorType.ALMA, IndicatorType.ZLEMA, IndicatorType.LSMA,
        IndicatorType.GMA, IndicatorType.KAMA, IndicatorType.JMA, IndicatorType.FRAMA, IndicatorType.VIDYA, IndicatorType.Dma, IndicatorType.MtfLaggedEma,
        IndicatorType.BB, IndicatorType.Keltner, IndicatorType.Donchian, IndicatorType.Envelope, IndicatorType.Ichimoku,
        IndicatorType.VWAP, IndicatorType.AnchoredVWAP, IndicatorType.RollingVWAP, IndicatorType.ParabolicSAR, IndicatorType.SuperTrend,
        IndicatorType.Highest, IndicatorType.Lowest, IndicatorType.TrueHigh, IndicatorType.TrueLow, IndicatorType.LinearRegression,
        IndicatorType.KalmanFilter, IndicatorType.HilbertTrendline,
    };

    /// <summary>The raw price fields (open, high, low, close, ...) are price units.</summary>
    public static ChannelNormalization ForPrice() => ChannelNormalization.WindowMinMax;

    public static ChannelNormalization ForIndicator(IndicatorType type) =>
        UnitStable.Contains(type) ? ChannelNormalization.None
        : PriceNative.Contains(type) ? ChannelNormalization.WindowMinMax
        : ChannelNormalization.WindowZScore;
}
