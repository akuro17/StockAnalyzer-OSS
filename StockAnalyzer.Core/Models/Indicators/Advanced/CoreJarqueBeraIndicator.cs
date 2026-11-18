using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Rolling Jarque-Bera normality test of the last <see cref="Period"/> one-bar returns (<see cref="NormalityAndTailMath.TryJarqueBera"/>: population skewness and excess
/// kurtosis; the p-value is the exact upper tail of the chi-square law with two degrees of freedom). Return basis: log returns when <see cref="UseLogReturn"/> is true, otherwise simple.
/// <c>Main</c> = p-value in [0, 1] (below the significance level the normality of the returns is rejected). <c>SignificanceLevel</c> = <see cref="Alpha"/> as a constant reference line,
/// present exactly where <c>Main</c> has a value. <c>JBStatistic</c> = the test statistic (unbounded, dimensionless; not drawn on the fixed 0..1 axis, see <see cref="IndicatorChartSuppression"/>).
/// Null for bars before bar <see cref="Period"/>, for an unusable price in the window and for a flat window. Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.JarqueBera)]
public class CoreJarqueBeraIndicator : CoreIndicatorBase
{
    public const string SignificanceLevelSeriesName = nameof(SignificanceLevel);
    public const string JBStatisticSeriesName = nameof(JBStatistic);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.JarqueBeraDefaultPeriod;

    public double Alpha { get; set; } = IndicatorDefaultConstants.TestSignificanceDefault;

    public bool UseLogReturn { get; set; } = IndicatorDefaultConstants.JarqueBeraDefaultUseLogReturn;

    public List<decimal?> SignificanceLevel { get; } = new();

    public List<decimal?> JBStatistic { get; } = new();

    public override string Name => UseLogReturn ? $"Jarque-Bera ({Period})" : $"Jarque-Bera ({Period}, simple)";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreJarqueBeraParameter p)
        {
            p.Validate();
            Period = p.Period;
            Alpha = p.Alpha;
            UseLogReturn = p.UseLogReturn;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        SignificanceLevel.Clear();
        JBStatistic.Clear();

        var parameters = new CoreJarqueBeraParameter { Period = Period, Alpha = Alpha, UseLogReturn = UseLogReturn };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        decimal? level = IndicatorCalculationHelper.ToDecimalOrNull(Alpha);
        var returns = ReturnSeriesWindow.FromPrices(PriceDataHelper.ExtractPriceSeries(candles, PriceSource), UseLogReturn);
        for (int i = 0; i < returns.Count; i++)
        {
            decimal? pValue = null;
            decimal? statistic = null;
            if (i >= Period && returns.IsValid(i - Period + 1, i)
                && WindowMoments.TryCompute(returns.Returns.AsSpan(i - Period + 1, Period), FlatWindowRule.Returns, out WindowMoments moments)
                && NormalityAndTailMath.TryJarqueBera(moments, out double jb, out double p))
            {
                pValue = IndicatorCalculationHelper.ToDecimalOrNull(p);
                statistic = IndicatorCalculationHelper.ToDecimalOrNull(jb);
            }

            _values.Add(pValue);
            JBStatistic.Add(statistic);
            SignificanceLevel.Add(pValue.HasValue ? level : null);
        }

        return CreateAutomaticResult();
    }
}
