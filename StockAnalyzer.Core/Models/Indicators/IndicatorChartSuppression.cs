using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Single source of truth for indicator output series that are computed and served (screener,
/// data export) but deliberately NOT charted: their unit / scale (degrees, bars) would dominate a
/// chart or sub-panel whose actually-drawn series are price-scaled or bounded to <c>[-1, 1]</c>.
///
/// The chart renderer (<c>IndicatorRenderer</c>), the price panel snapshot (<c>ChartDataSnapshot</c>),
/// the sub-panel auto-range calculator (<c>PanelValueRangeCalculator</c>), the AI chart image
/// (<c>AiChartSeriesSelector</c>) and the inspector (<c>DataWindowViewModel</c>) all consult this, so the
/// exclusion is defined exactly once instead of being duplicated per call site.
///
/// Besides the fixed per-type table, a series can be hidden only while a parameter of its indicator says so
/// (<c>ParameterDependentSuppressedSeries</c>, for example the KST Signal while <c>ShowSignal</c> is off): the chart,
/// the sub-panel range and the AI image then leave it out, while the series stays in the indicator result, the
/// DataWindow, the screener and the backtest. The chart-side callers pass the parameter object of the indicator setting.
/// </summary>
public static class IndicatorChartSuppression
{
    // Same values as the Main / Upper / Lower series under another name; must be declared before the tables that use it.
    private static readonly string[] SsaResidualBandAliasSeries =
    {
        nameof(CoreSSAResidualBandIndicator.CenterBand),
        nameof(CoreSSAResidualBandIndicator.UpperBand),
        nameof(CoreSSAResidualBandIndicator.LowerBand),
        nameof(CoreSSAResidualBandIndicator.Center),
    };

    // The direction flag of the block chart overlays (a helper series, not a public property of the indicators); must be declared before the tables that use it.
    private static readonly string[] BlockChartOverlayHelperSeries = { IndicatorSeriesNames.UpTrendFlag };

    // Series names are bound with nameof to the producing indicator's public series property, which
    // is the exact name CoreIndicatorBase.CreateAutomaticResult emits (prop.Name). "Main" has no
    // backing property, so it comes from IndicatorResult.MainSeriesName.
    private static readonly IReadOnlyDictionary<IndicatorType, IReadOnlySet<string>> SuppressedSeries =
        new Dictionary<IndicatorType, IReadOnlySet<string>>
        {
            [IndicatorType.IFFTInstantaneousPhase] = new HashSet<string>(StringComparer.Ordinal)
            {
                IndicatorResult.MainSeriesName,
                nameof(CoreIfftInstantaneousPhaseIndicator.PhaseDelta),
                nameof(CoreIfftInstantaneousPhaseIndicator.LocalPeriod),
                nameof(CoreIfftInstantaneousPhaseIndicator.PhaseStability),
            },
            [IndicatorType.PolarPhase] = new HashSet<string>(StringComparer.Ordinal)
            {
                IndicatorResult.MainSeriesName,
                nameof(CorePolarPhaseIndicator.PhaseStability),
            },
            [IndicatorType.DMI] = new HashSet<string>(StringComparer.Ordinal)
            {
                IndicatorResult.MainSeriesName,
            },
            [IndicatorType.ClothoidOscillator] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreClothoidOscillatorIndicator.RawCurvatureRate),
            },
            [IndicatorType.ClothoidMovingAverage] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreClothoidMovingAverageIndicator.EffectiveLag),
                nameof(CoreClothoidMovingAverageIndicator.EffectiveSampleSize),
            },
            [IndicatorType.CrossCorrelation] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreCrossCorrelationIndicator.OptimalLag),
            },
            [IndicatorType.CointegrationSpread] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreCointegrationSpreadIndicator.Intercept),
                nameof(CoreCointegrationSpreadIndicator.HalfLife),
            },
            [IndicatorType.Donchian] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreDonchianChannelIndicator.Position),
            },
            // Block chart overlays: the 1/0 direction flag is a colouring helper of the renderer (read by name), not a price line; it never joins the price-axis range or the drawn lines.
            [IndicatorType.Renko] = new HashSet<string>(BlockChartOverlayHelperSeries, StringComparer.Ordinal),
            [IndicatorType.PointAndFigure] = new HashSet<string>(BlockChartOverlayHelperSeries, StringComparer.Ordinal),
            [IndicatorType.Kagi] = new HashSet<string>(BlockChartOverlayHelperSeries, StringComparer.Ordinal),
            [IndicatorType.ThreeLineBreak] = new HashSet<string>(BlockChartOverlayHelperSeries, StringComparer.Ordinal),
            // BandWidth is a percentage (not drawn on the price panel, still shown in the DataWindow) and the aliases
            // repeat the drawn Main / Upper / Lower series; all of them stay in the result so the screener can resolve them.
            [IndicatorType.SSAResidualBand] = new HashSet<string>(SsaResidualBandAliasSeries, StringComparer.Ordinal)
            {
                nameof(CoreSSAResidualBandIndicator.BandWidth),
            },
            [IndicatorType.CrossTickerDtw] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreCrossTickerDtwIndicator.NormalizedDistance),
                nameof(CoreCrossTickerDtwIndicator.SimilarityScore),
            },
            [IndicatorType.McClellanOscillator] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreMcClellanOscillatorIndicator.SummationIndexSeriesName,
                CoreMcClellanOscillatorIndicator.AdLineSeriesName,
            },
            // Fixed [0, 1] panel axis: suppress off-scale statistical series from chart rendering (kept in IndicatorResult / DataWindow / screener).
            [IndicatorType.HurstExponent] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreHurstExponentIndicator.VarianceRatioSeriesName,
                CoreHurstExponentIndicator.VarianceRatioZScoreSeriesName,
                CoreHurstExponentIndicator.DfaAlphaSeriesName,
            },
            [IndicatorType.Entropy] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreEntropyIndicator.SampleEntropySeriesName,
            },
            // Leverage parameter gamma (dimensionless coefficient) differs in unit/scale from volatility percentage Main series.
            [IndicatorType.Garch11] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreGarch11Indicator.GjrGammaSeriesName,
            },
            // Auto-ARIMA integer orders (0..5) differ in unit from price-native overlay Main series.
            [IndicatorType.ARIMA] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreArimaIndicator.SelectedPSeriesName,
                CoreArimaIndicator.SelectedDSeriesName,
                CoreArimaIndicator.SelectedQSeriesName,
            },
            // Fixed [0, 1] panel axis: suppress off-scale run lengths and CUSUM series from chart rendering.
            [IndicatorType.Bocpd] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreBocpdIndicator.ExpectedRunLengthSeriesName,
                CoreBocpdIndicator.MapRunLengthSeriesName,
                CoreBocpdIndicator.CusumUpSeriesName,
                CoreBocpdIndicator.CusumDownSeriesName,
            },
            // Bounded [-1, 1] correlation differs in scale/unit from unbounded F-statistics on the same sub-panel.
            [IndicatorType.GrangerCausality] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreGrangerCausalityIndicator.DccCorrelationSeriesName,
            },
            // Spread Z-score (sigma) differs in unit/scale from likelihood-ratio Trace/MaxEigen statistics.
            [IndicatorType.JohansenCointegration] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreJohansenCointegrationIndicator.CointegrationSpreadZScoreSeriesName,
            },
            // Fixed [0, 1] panel axis of the p-value: the unbounded test statistic is kept out of the chart and the sub-panel range (still in the result, DataWindow, screener and MCP).
            [IndicatorType.JarqueBera] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreJarqueBeraIndicator.JBStatisticSeriesName,
            },
            [IndicatorType.LjungBoxQ] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreLjungBoxQIndicator.QStatisticSeriesName,
            },
            // Per-bar return ratios drawn together: the dimensionless Sortino ratio, the [-1, 1] asymmetry and the [0, 1] jump ratio differ in scale and would dominate the panel.
            [IndicatorType.DownsideDeviation] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreDownsideDeviationIndicator.RollingSortinoSeriesName,
                CoreDownsideDeviationIndicator.SemivarianceAsymmetrySeriesName,
            },
            [IndicatorType.BipowerVariation] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreBipowerVariationIndicator.JumpRatioSeriesName,
            },
            // The coefficient on the lagged level (about -1..0) and the half-life in bars differ in scale from the tau statistic and its critical value on the same sub-panel.
            [IndicatorType.ADFStationarity] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreAdfStationarityIndicator.HalfLifeSeriesName,
                CoreAdfStationarityIndicator.RhoSeriesName,
            },
            // The Z* statistic is unbounded and its p-value lies in [0, 1]: both differ in scale from the variance ratio around 1.
            [IndicatorType.VarianceRatio] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreVarianceRatioIndicator.ZStarStatisticSeriesName,
                CoreVarianceRatioIndicator.PValueSeriesName,
            },
            // Price-unit spreads of the window (not price levels of the bar): they would stretch the price axis of the overlay.
            [IndicatorType.QuantileBands] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreQuantileBandsIndicator.IqrSeriesName,
                CoreQuantileBandsIndicator.MadSeriesName,
            },
            // Slope (price per bar) and the window-relative intercept are not price levels of the bar.
            [IndicatorType.TheilSenRegression] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreTheilSenRegressionIndicator.SlopeSeriesName,
                CoreTheilSenRegressionIndicator.InterceptSeriesName,
            },
            // The [0, 1] probability differs in scale from the per-bar return quantiles on the same sub-panel.
            [IndicatorType.CornishFisherVaR] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreCornishFisherVarIndicator.ProbabilisticSharpeRatioSeriesName,
            },
            // The p-value lies in [0, 1] and differs in scale from the test statistic on the same sub-panel (DSL expansion run I-5).
            [IndicatorType.OneSampleTTest] = new HashSet<string>(StringComparer.Ordinal) { CoreOneSampleTTestIndicator.PValueSeriesName },
            [IndicatorType.TwoSampleTTest] = new HashSet<string>(StringComparer.Ordinal) { CoreTwoSampleTTestIndicator.PValueSeriesName },
            [IndicatorType.KsTwoSampleTest] = new HashSet<string>(StringComparer.Ordinal) { CoreKsTwoSampleTestIndicator.PValueSeriesName },
            [IndicatorType.MannWhitneyUTest] = new HashSet<string>(StringComparer.Ordinal) { CoreMannWhitneyUTestIndicator.PValueSeriesName },
            // The shape parameter b and the R-squared differ in scale from the fitted price on the price pane (DSL expansion run I-4).
            [IndicatorType.NonlinearRegression] = new HashSet<string>(StringComparer.Ordinal)
            {
                CoreNonlinearRegressionIndicator.ParameterBSeriesName,
                CoreNonlinearRegressionIndicator.RSquaredSeriesName,
            },
        };

    private static readonly IReadOnlyDictionary<IndicatorType, IReadOnlySet<string>> SuppressedDataWindowSeries =
        new Dictionary<IndicatorType, IReadOnlySet<string>>
        {
            [IndicatorType.IFFTInstantaneousPhase] = new HashSet<string>(StringComparer.Ordinal)
            {
                IndicatorResult.MainSeriesName,
                nameof(CoreIfftInstantaneousPhaseIndicator.PhaseDelta),
                nameof(CoreIfftInstantaneousPhaseIndicator.LocalPeriod),
                nameof(CoreIfftInstantaneousPhaseIndicator.PhaseStability),
            },
            [IndicatorType.PolarPhase] = new HashSet<string>(StringComparer.Ordinal)
            {
                IndicatorResult.MainSeriesName,
                nameof(CorePolarPhaseIndicator.PhaseStability),
            },
            [IndicatorType.DMI] = new HashSet<string>(StringComparer.Ordinal)
            {
                IndicatorResult.MainSeriesName,
            },
            [IndicatorType.ClothoidOscillator] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreClothoidOscillatorIndicator.RawCurvatureRate),
            },
            [IndicatorType.ClothoidMovingAverage] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreClothoidMovingAverageIndicator.EffectiveLag),
                nameof(CoreClothoidMovingAverageIndicator.EffectiveSampleSize),
            },
            [IndicatorType.SSAResidualBand] = new HashSet<string>(SsaResidualBandAliasSeries, StringComparer.Ordinal),
            // Block chart overlays: the 1/0 direction flag only colours the step line; it is not a value to read in the data window.
            [IndicatorType.Renko] = new HashSet<string>(BlockChartOverlayHelperSeries, StringComparer.Ordinal),
            [IndicatorType.PointAndFigure] = new HashSet<string>(BlockChartOverlayHelperSeries, StringComparer.Ordinal),
            [IndicatorType.Kagi] = new HashSet<string>(BlockChartOverlayHelperSeries, StringComparer.Ordinal),
            [IndicatorType.ThreeLineBreak] = new HashSet<string>(BlockChartOverlayHelperSeries, StringComparer.Ordinal),
        };

    /// <summary>
    /// A series that is hidden from the chart only while a parameter of its indicator says so (for example a
    /// "Show ..." switch). The series always stays in the indicator result, the DataWindow and the screener.
    /// </summary>
    private readonly record struct ParameterDependentSuppression(string SeriesName, Func<CoreIndicatorParameterBase?, bool> IsHidden);

    // KST Signal: always calculated; drawn only when ShowSignal is on. A missing parameter object means the defaults, so it is hidden too.
    private static readonly IReadOnlyDictionary<IndicatorType, ParameterDependentSuppression> ParameterDependentSuppressedSeries =
        new Dictionary<IndicatorType, ParameterDependentSuppression>
        {
            [IndicatorType.KST] = new ParameterDependentSuppression(
                nameof(CoreKstIndicator.Signal),
                static parameters => parameters is not CoreKstParameter { ShowSignal: true }),
        };

    /// <summary>
    /// True when <paramref name="seriesName"/> of the given indicator <paramref name="type"/> must
    /// not be drawn on the chart and must be kept out of the sub-panel auto-range. Allocation-free.
    /// <paramref name="parameters"/> is the parameter object of the indicator setting; it only matters for the series
    /// that a parameter hides (see <see cref="ParameterDependentSuppression"/>), so every other answer is unchanged.
    /// </summary>
    public static bool IsSuppressed(IndicatorType? type, string seriesName, CoreIndicatorParameterBase? parameters = null)
    {
        if (type is not { } t)
        {
            return false;
        }

        if (SuppressedSeries.TryGetValue(t, out IReadOnlySet<string>? set) && set.Contains(seriesName))
        {
            return true;
        }

        // Helper series of the bidirectional chart-pattern flag indicators (Main and Direction; the two named side series are the visible ones); the one definition is the catalog table.
        if (PatternFlagIndicatorCatalog.IsHelperSeries(t, seriesName))
        {
            return true;
        }

        return ParameterDependentSuppressedSeries.TryGetValue(t, out ParameterDependentSuppression rule)
               && string.Equals(rule.SeriesName, seriesName, StringComparison.Ordinal)
               && rule.IsHidden(parameters);
    }

    /// <summary>
    /// True when <paramref name="seriesName"/> of the given indicator <paramref name="type"/> must
    /// not be displayed in the DataWindow inspector. Allocation-free.
    /// </summary>
    public static bool IsDataWindowSuppressed(IndicatorType? type, string seriesName) =>
        type is { } t
        && ((SuppressedDataWindowSeries.TryGetValue(t, out IReadOnlySet<string>? set) && set.Contains(seriesName))
            // The Main and Direction series of a bidirectional chart-pattern flag are for the Screener and the Backtest; the data window shows the two named side series (one row each).
            || PatternFlagIndicatorCatalog.IsHelperSeries(t, seriesName));
}
