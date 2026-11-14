using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Hurst exponent by R/S analysis (see <see cref="HurstMath"/>): the slope of ln (R/S) against ln m over the sub-interval lengths m = n, n/2, n/4, ... (&gt;= 8) of the window
/// of the last <see cref="Period"/> log returns r_k = ln(P_k / P_(k-1)); no small-sample (Anis-Lloyd) correction.
/// <c>Main</c> = H, clamped to [0, 1]. Unit: ratio (dimensionless, 0 to 1). Null for bars before bar <see cref="Period"/> and when the window has an unusable price or fewer than two usable
/// sub-interval lengths. Causal: bar t uses bars &lt;= t only. Output length equals the candle count.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.HurstExponent)]
public class CoreHurstExponentIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.HurstDefaultPeriod;

    public override string Name => $"Hurst Exponent ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreHurstExponentParameter p)
        {
            p.Validate();
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        var parameters = new CoreHurstExponentParameter { Period = Period };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        var returns = new LogReturnWindow(PriceDataHelper.ExtractPriceSeries(candles, PriceSource));
        for (int i = 0; i < returns.Count; i++)
        {
            if (i < Period || !returns.IsValid(i - Period + 1, i))
            {
                _values.Add(null);
                continue;
            }

            double hurst = HurstMath.Exponent(returns.Returns.AsSpan(i - Period + 1, Period));
            _values.Add(IndicatorCalculationHelper.ToDecimalOrNull(hurst));
        }

        return IndicatorResult.Success(_values);
    }
}
