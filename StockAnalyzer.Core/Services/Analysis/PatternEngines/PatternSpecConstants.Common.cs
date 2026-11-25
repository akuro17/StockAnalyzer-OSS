namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// The numbers of the chart-pattern specification (<c>stockanalyzer-chart-patterns-56-specs-v2\00_COMMON_MATHEMATICAL_DEFINITIONS.md</c>) that are definitions, not user settings. One definition each
/// (CLAUDE.md "no hardcoded values"); every engine reads them from here. A number the user may tune is a parameter of the indicator, and its default lives in <c>PatternFlagParameterLimits</c>
/// (that file may use these constants as its defaults). Family-specific numbers are in the sibling files <c>PatternSpecConstants.&lt;Family&gt;.cs</c>.
/// </summary>
public static partial class PatternSpecConstants
{
    /// <summary>Spec 2.2: period (bars) of the context ATR and of the context average body. Equal to <c>ChartConstants.CandlePatternAtrPeriod</c> (a test pins the equality).</summary>
    public const int AtrPeriod = 14;

    /// <summary>Spec 2.3: bars of the average volume.</summary>
    public const int VolumeAveragingBars = 20;

    /// <summary>Spec 4.1: bars of the short-term price change (default of <c>PriorTrendLookbackBars</c>).</summary>
    public const int ShortTrendLookbackBars = 10;

    /// <summary>Spec 4.1: the price change must be at least this many context ATRs.</summary>
    public const decimal ShortTrendAtrMultiplier = 1.5m;

    /// <summary>Spec 4.1: period of the EMA of the closes (decision U2-01 A: a constant, not tied to the lookback).</summary>
    public const int ShortTrendEmaPeriod = 10;

    /// <summary>Spec 4.1: the EMA must be rising against the EMA this many bars earlier.</summary>
    public const int ShortTrendEmaShift = 3;

    /// <summary>Spec 4.2: bars before the pattern start in which the mid-term trend is measured (the start bar itself is excluded).</summary>
    public const int MidTrendLookbackBars = 30;

    /// <summary>Spec 4.2: minimum move before the pattern as a fraction of the extreme price (0.10 = 10 percent).</summary>
    public const decimal MidTrendMinFraction = 0.10m;

    /// <summary>Spec 4.2: alternative minimum move, in context ATRs.</summary>
    public const decimal MidTrendAtrMultiplier = 3.0m;

    /// <summary>Spec 4.2: fast SMA period of the mid-term trend test.</summary>
    public const int SmaFastPeriod = 20;

    /// <summary>Spec 4.2: slow SMA period of the mid-term trend test.</summary>
    public const int SmaSlowPeriod = 50;

    /// <summary>Spec 4.2: the fast SMA is compared with itself this many bars earlier.</summary>
    public const int SmaShift = 10;

    /// <summary>Spec 4.3: bars of the price-zone range for reversal candlesticks.</summary>
    public const int ZoneBarsReversalCandle = 20;

    /// <summary>Spec 4.3: bars of the price-zone range for the main chart patterns.</summary>
    public const int ZoneBarsPattern = 60;

    /// <summary>Spec 5.1: default of the fractal bars on each side (k_L = k_R).</summary>
    public const int FractalBarsDefault = 3;

    /// <summary>Spec 5.2: default minimum swing amplitude in context ATRs (theta_atr_piv).</summary>
    public const decimal ZigZagMinAtrMultiplierDefault = 1.5m;
}
