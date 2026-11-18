using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Rolling Ljung-Box autocorrelation test of the last <see cref="Period"/> log returns (<see cref="SerialDependenceMath.TryLjungBox"/>) at the lag
/// <c>m = min(MaxLag, floor(Period / 5))</c> (<see cref="SerialDependenceMath.EffectiveLjungBoxLag"/>; a bar with <c>m &lt; 1</c> has no value).
/// <c>Main</c> = p-value in [0, 1] (below the significance level the absence of autocorrelation is rejected). <c>SignificanceLevel</c> = <see cref="Alpha"/> as a constant reference line,
/// present exactly where <c>Main</c> has a value. <c>QStatistic</c> = the Q statistic (unbounded, dimensionless; not drawn on the fixed 0..1 axis, see <see cref="IndicatorChartSuppression"/>).
/// Null for bars before bar <see cref="Period"/>, for an unusable price in the window and for a flat window. Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.LjungBoxQ)]
public class CoreLjungBoxQIndicator : CoreIndicatorBase
{
    public const string SignificanceLevelSeriesName = nameof(SignificanceLevel);
    public const string QStatisticSeriesName = nameof(QStatistic);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.LjungBoxDefaultPeriod;

    public int MaxLag { get; set; } = IndicatorDefaultConstants.LjungBoxDefaultMaxLag;

    public double Alpha { get; set; } = IndicatorDefaultConstants.TestSignificanceDefault;

    public List<decimal?> SignificanceLevel { get; } = new();

    public List<decimal?> QStatistic { get; } = new();

    public override string Name => $"Ljung-Box Q ({Period}, {MaxLag})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreLjungBoxQParameter p)
        {
            p.Validate();
            Period = p.Period;
            MaxLag = p.MaxLag;
            Alpha = p.Alpha;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        SignificanceLevel.Clear();
        QStatistic.Clear();

        var parameters = new CoreLjungBoxQParameter { Period = Period, MaxLag = MaxLag, Alpha = Alpha };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        decimal? level = IndicatorCalculationHelper.ToDecimalOrNull(Alpha);
        int lags = SerialDependenceMath.EffectiveLjungBoxLag(Period, MaxLag);
        var returns = ReturnSeriesWindow.FromPrices(PriceDataHelper.ExtractPriceSeries(candles, PriceSource), useLogReturn: true);
        for (int i = 0; i < returns.Count; i++)
        {
            decimal? pValue = null;
            decimal? statistic = null;
            if (i >= Period && returns.IsValid(i - Period + 1, i)
                && SerialDependenceMath.TryLjungBox(returns.Returns.AsSpan(i - Period + 1, Period), lags, out double q, out double p))
            {
                pValue = IndicatorCalculationHelper.ToDecimalOrNull(p);
                statistic = IndicatorCalculationHelper.ToDecimalOrNull(q);
            }

            _values.Add(pValue);
            QStatistic.Add(statistic);
            SignificanceLevel.Add(pValue.HasValue ? level : null);
        }

        return CreateAutomaticResult();
    }
}
