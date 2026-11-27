using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// Tool options that change which values a drawing-value calculator returns. <c>new DrawingValueOptions()</c> and every omitted argument give the defaults
/// the GUI objects start with; only <c>default(DrawingValueOptions)</c> is all zero, and nothing should use it.
/// </summary>
public readonly record struct DrawingValueOptions
{
    /// <summary>The GUI defaults.</summary>
    public DrawingValueOptions() : this(false)
    {
    }

    /// <param name="ShowExtensions">FibonacciRetracement only: use the extension level table instead of the default one.</param>
    /// <param name="PitchforkKind">Pitchfork only: the kind of fork (Andrews, Schiff, ModifiedSchiff).</param>
    /// <param name="StdDevMultiplier">RegressionTrend only: the multiplier of the band (the "+2σ Band" items).</param>
    /// <param name="ExtendRight">RegressionTrend only: the regression is extended over the existing bars after the range.</param>
    /// <param name="PriceSource">AnchoredVWAP only: the price the VWAP is weighted from.</param>
    /// <param name="ShowBand1">AnchoredVWAP only: add the first band pair.</param>
    /// <param name="Band1Multiplier">AnchoredVWAP only: multiplier of the first band pair.</param>
    /// <param name="ShowBand2">AnchoredVWAP only: add the second band pair.</param>
    /// <param name="Band2Multiplier">AnchoredVWAP only: multiplier of the second band pair.</param>
    /// <param name="PivotPeriod">ClassicPivotPoints only: the period the levels are taken from.</param>
    /// <param name="PivotExtendMode">ClassicPivotPoints only: the line extension (listed as an item of the values).</param>
    /// <param name="ProjectionTargets">TargetPriceProjection only: which of the five projections are shown; null shows all five (the chart object's start).</param>
    /// <param name="IndicatorValueAtEntry">LongPosition and ShortPosition only: the ATR the stop was bound to in the chart, when there is one; it adds the "ATR (Entry)" item.</param>
    /// <param name="GarchForecastBars">GarchVolatilityCone only: the number of future bars of the cone (5..100).</param>
    /// <param name="RegimeCardPeriodsPerYear">RangeRegimeDiagnosticCard only: the bars per year of the annualised rows (Sharpe, Sortino, CAGR).</param>
    /// <param name="GarchPriceSource">GarchVolatilityCone only: the price the log returns are taken from (the close by default; independent of <paramref name="PriceSource"/>, whose default is Typical).</param>
    public DrawingValueOptions(
        bool ShowExtensions = false,
        PitchforkKind PitchforkKind = PitchforkKind.Andrews,
        decimal StdDevMultiplier = RegressionTrendValues.DefaultStdDevMultiplier,
        bool ExtendRight = false,
        PriceType PriceSource = PriceType.Typical,
        bool ShowBand1 = false,
        decimal Band1Multiplier = AnchoredVwapValues.DefaultBand1Multiplier,
        bool ShowBand2 = false,
        decimal Band2Multiplier = AnchoredVwapValues.DefaultBand2Multiplier,
        PivotPeriod PivotPeriod = PivotPeriod.Day,
        PivotLineExtendMode PivotExtendMode = PivotLineExtendMode.None,
        decimal? IndicatorValueAtEntry = null,
        ProjectionTargetSelection? ProjectionTargets = null,
        DistributionDataMode DistributionDataMode = DistributionDataMode.LogReturn,
        PriceType DistributionPriceSource = PriceType.Close,
        DistributionBinningRule DistributionBinningRule = DistributionBinningRule.FreedmanDiaconis,
        int DistributionFixedBinCount = DistributionRangeParameters.DefaultFixedBinCount,
        ReferenceDistributionType DistributionReferenceDistribution = ReferenceDistributionType.Normal,
        bool DistributionOverrideStudentTDegreesOfFreedom = false,
        decimal DistributionStudentTDegreesOfFreedom = DistributionRangeParameters.DefaultStudentTDegreesOfFreedom,
        decimal DistributionGevShapeParameter = DistributionRangeParameters.DefaultGevShapeParameter,
        double DistributionBandwidthMultiplier = DistributionRangeParameters.DefaultBandwidthMultiplier,
        int GarchForecastBars = GarchVolatilityConeValues.DefaultHorizon,
        int RegimeCardPeriodsPerYear = RangeRegimeDiagnosticCardValues.DefaultPeriodsPerYear,
        PriceType GarchPriceSource = PriceType.Close)
    {
        this.ShowExtensions = ShowExtensions;
        this.PitchforkKind = PitchforkKind;
        this.StdDevMultiplier = StdDevMultiplier;
        this.ExtendRight = ExtendRight;
        this.PriceSource = PriceSource;
        this.ShowBand1 = ShowBand1;
        this.Band1Multiplier = Band1Multiplier;
        this.ShowBand2 = ShowBand2;
        this.Band2Multiplier = Band2Multiplier;
        this.PivotPeriod = PivotPeriod;
        this.PivotExtendMode = PivotExtendMode;
        this.IndicatorValueAtEntry = IndicatorValueAtEntry;
        this.ProjectionTargets = ProjectionTargets;
        this.DistributionDataMode = DistributionDataMode;
        this.DistributionPriceSource = DistributionPriceSource;
        this.DistributionBinningRule = DistributionBinningRule;
        this.DistributionFixedBinCount = DistributionFixedBinCount;
        this.DistributionReferenceDistribution = DistributionReferenceDistribution;
        this.DistributionOverrideStudentTDegreesOfFreedom = DistributionOverrideStudentTDegreesOfFreedom;
        this.DistributionStudentTDegreesOfFreedom = DistributionStudentTDegreesOfFreedom;
        this.DistributionGevShapeParameter = DistributionGevShapeParameter;
        this.DistributionBandwidthMultiplier = DistributionBandwidthMultiplier;
        this.GarchForecastBars = GarchForecastBars;
        this.RegimeCardPeriodsPerYear = RegimeCardPeriodsPerYear;
        this.GarchPriceSource = GarchPriceSource;
    }

    public bool ShowExtensions { get; init; }

    public PitchforkKind PitchforkKind { get; init; }

    public decimal StdDevMultiplier { get; init; }

    public bool ExtendRight { get; init; }

    public PriceType PriceSource { get; init; }

    public bool ShowBand1 { get; init; }

    public decimal Band1Multiplier { get; init; }

    public bool ShowBand2 { get; init; }

    public decimal Band2Multiplier { get; init; }

    public PivotPeriod PivotPeriod { get; init; }

    public PivotLineExtendMode PivotExtendMode { get; init; }

    public decimal? IndicatorValueAtEntry { get; init; }

    public ProjectionTargetSelection? ProjectionTargets { get; init; }

    public DistributionDataMode DistributionDataMode { get; init; }

    public PriceType DistributionPriceSource { get; init; }

    public DistributionBinningRule DistributionBinningRule { get; init; }

    public int DistributionFixedBinCount { get; init; }

    public ReferenceDistributionType DistributionReferenceDistribution { get; init; }

    public bool DistributionOverrideStudentTDegreesOfFreedom { get; init; }

    public decimal DistributionStudentTDegreesOfFreedom { get; init; }

    public decimal DistributionGevShapeParameter { get; init; }

    public double DistributionBandwidthMultiplier { get; init; }

    public int GarchForecastBars { get; init; }

    public int RegimeCardPeriodsPerYear { get; init; }

    public PriceType GarchPriceSource { get; init; }
}
