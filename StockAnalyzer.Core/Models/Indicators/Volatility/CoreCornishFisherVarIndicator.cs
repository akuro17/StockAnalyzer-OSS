using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// Rolling one-bar Value at Risk of the last <see cref="Period"/> simple returns at <see cref="ConfidenceLevel"/> with the Cornish-Fisher skewness / kurtosis correction
/// (<see cref="NormalityAndTailMath"/>). Both VaR series are the lower-tail RETURN quantile (a loss is NEGATIVE, the sign of <c>CoreVarIndicator</c>; the portfolio-risk
/// <c>valueAtRisk</c> of the MCP server is its positive loss): <c>Main</c> = mean + z_CF x s, <c>GaussianVaR</c> = mean + z x s with the sample mean and the sample standard deviation (divisor n - 1).
/// <c>Main</c> is null for a flat window (skewness and kurtosis undefined) and where the expansion is not monotone (<see cref="NormalityAndTailMath.CornishFisherDerivative"/> not positive);
/// <c>GaussianVaR</c> is a plain magnitude and has a value for a flat window too. <c>ProbabilisticSharpeRatio</c> = P(true per-bar Sharpe ratio &gt; 0) in [0, 1] (null for a flat window and for a
/// radicand not above <see cref="ReturnStatistics.VarianceEpsilon"/>) is chart-suppressed (scale), see <see cref="IndicatorChartSuppression"/>.
/// Null for bars before bar <see cref="Period"/> and for a window with an unusable price. Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.CornishFisherVaR)]
public class CoreCornishFisherVarIndicator : CoreIndicatorBase
{
    public const string GaussianVarSeriesName = nameof(GaussianVaR);
    public const string ProbabilisticSharpeRatioSeriesName = nameof(ProbabilisticSharpeRatio);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.CornishFisherVarDefaultPeriod;

    public double ConfidenceLevel { get; set; } = IndicatorDefaultConstants.CornishFisherVarDefaultConfidenceLevel;

    public List<decimal?> GaussianVaR { get; } = new();

    public List<decimal?> ProbabilisticSharpeRatio { get; } = new();

    public override string Name => $"Cornish-Fisher VaR ({Period}, {ConfidenceLevel})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreCornishFisherVarParameter p)
        {
            p.Validate();
            Period = p.Period;
            ConfidenceLevel = p.ConfidenceLevel;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        GaussianVaR.Clear();
        ProbabilisticSharpeRatio.Clear();

        var parameters = new CoreCornishFisherVarParameter { Period = Period, ConfidenceLevel = ConfidenceLevel };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        var returns = ReturnSeriesWindow.FromPrices(PriceDataHelper.ExtractPriceSeries(candles, PriceSource), useLogReturn: false);
        for (int i = 0; i < returns.Count; i++)
        {
            decimal? cornishFisher = null;
            decimal? gaussian = null;
            decimal? sharpe = null;
            if (i >= Period && returns.IsValid(i - Period + 1, i)
                && WindowMoments.TryCompute(returns.Returns.AsSpan(i - Period + 1, Period), FlatWindowRule.Returns, out WindowMoments moments)
                && NormalityAndTailMath.TryCornishFisherValueAtRisk(moments, ConfidenceLevel, out double gaussianVar, out double cornishFisherVar))
            {
                cornishFisher = IndicatorCalculationHelper.ToDecimalOrNull(cornishFisherVar);
                gaussian = IndicatorCalculationHelper.ToDecimalOrNull(gaussianVar);
                sharpe = IndicatorCalculationHelper.ToDecimalOrNull(NormalityAndTailMath.ProbabilisticSharpeRatio(moments));
            }

            _values.Add(cornishFisher);
            GaussianVaR.Add(gaussian);
            ProbabilisticSharpeRatio.Add(sharpe);
        }

        return CreateAutomaticResult();
    }
}
