using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// Parkinson (1980) high-low range volatility. Per-bar contribution v_k = ln(H_k / L_k)^2 / (4 ln 2); a bar with H &lt;= 0, L &lt;= 0 or a non-finite v_k is invalid.
/// <c>Main</c> = sqrt(max(0, mean of v_k over the last <see cref="Period"/> bars)) x 100 (x sqrt(<see cref="PeriodsPerYear"/>) when <see cref="Annualize"/> is on).
/// Unit: percentage per bar by default, so the meaning does not change with the chart time frame; annual percentage when Annualize is on
/// (use 252 on daily, 52 on weekly, 12 on monthly bars). Null while fewer than Period bars exist and when any bar of the window is invalid.
/// Causal: bar t uses bars &lt;= t only. Output length equals the candle count.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.ParkinsonVolatility)]
public class CoreParkinsonVolatilityIndicator : CoreIndicatorBase
{
    // 4 ln 2, the denominator of the Parkinson estimator.
    private static readonly double VarianceDenominator = 4.0 * Math.Log(2.0);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.RangeVolatilityDefaultPeriod;

    /// <summary>When true the value is multiplied by sqrt(<see cref="PeriodsPerYear"/>).</summary>
    public bool Annualize { get; set; }

    /// <summary>Bars per year for the annualisation; used only when <see cref="Annualize"/> is true.</summary>
    public int PeriodsPerYear { get; set; } = IndicatorDefaultConstants.PeriodsPerYear;

    public override string Name => $"Parkinson Volatility ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreParkinsonVolatilityParameter p)
        {
            p.Validate();
            Period = p.Period;
            Annualize = p.Annualize;
            PeriodsPerYear = p.PeriodsPerYear;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        int count = candles.Count;
        var contribution = new double[count];
        var valid = new bool[count];
        for (int k = 0; k < count; k++)
        {
            CoreCandleData candle = candles[k];
            if (candle.High <= 0m || candle.Low <= 0m)
            {
                continue;
            }

            double logRange = Math.Log((double)(candle.High / candle.Low));
            double v = logRange * logRange / VarianceDenominator;
            if (double.IsFinite(v))
            {
                contribution[k] = v;
                valid[k] = true;
            }
        }

        _values.AddRange(RangeVolatilityMath.Rolling(contribution, valid, Period, Annualize, PeriodsPerYear));
        return IndicatorResult.Success(_values);
    }
}
