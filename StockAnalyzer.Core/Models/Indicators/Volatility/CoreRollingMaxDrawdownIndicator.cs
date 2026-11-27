using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// Rolling maximum drawdown of the last <see cref="Period"/> prices (<see cref="DslQuantAndInfoMath.MaxDrawdown"/>, pure <see cref="decimal"/>): the largest fall from a running peak of the window,
/// <c>max_t (M_t - P_t) / M_t</c> with <c>M_t = max_(s &lt;= t) P_s</c>. A POSITIVE depth in [0, 1] (a ratio of the peak), multiplied by 100 with <see cref="AsPercent"/>.
/// Null for bars before bar <c>Period - 1</c>, for a window with an unusable price, and for a window with a price that is not positive. Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.RollingMaxDrawdown)]
public class CoreRollingMaxDrawdownIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.RollingMaxDrawdownDefaultPeriod;

    public bool AsPercent { get; set; } = IndicatorDefaultConstants.RollingMaxDrawdownDefaultAsPercent;

    public override string Name => $"Max Drawdown ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreRollingMaxDrawdownParameter p)
        {
            p.Validate();
            Period = p.Period;
            AsPercent = p.AsPercent;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        var parameters = new CoreRollingMaxDrawdownParameter { Period = Period, AsPercent = AsPercent };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        var prices = new PriceSeriesWindow(PriceDataHelper.ExtractPriceSeries(candles, PriceSource));
        for (int i = 0; i < prices.Count; i++)
        {
            decimal? drawdown = null;
            if (i >= Period - 1 && prices.IsComplete(i - Period + 1, i))
            {
                MathResult<decimal> result = DslQuantAndInfoMath.MaxDrawdown(prices.Values.AsSpan(i - Period + 1, Period));
                if (result.IsOk)
                {
                    drawdown = AsPercent ? result.Value * IndicatorDefaultConstants.PercentFactor : result.Value;
                }
            }

            _values.Add(drawdown);
        }

        return CreateAutomaticResult();
    }
}
