using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// Rolling Sharpe ratio of the last <see cref="Period"/> returns (<see cref="DslFinanceMath.SharpeRatio"/>): <c>(mean - rf) / s x sqrt(F)</c> with the per-bar risk-free rate
/// <c>rf = AnnualRiskFreeRate / PeriodsPerYear</c>, the sample standard deviation <c>s</c> (divisor n - 1) and <c>F = PeriodsPerYear</c> when <see cref="Annualize"/> is on, otherwise 1
/// (the value per bar). Unit: dimensionless ratio. Returns are simple returns, or log returns with <see cref="UseLogReturn"/>.
/// Null for bars before bar <see cref="Period"/> (<see cref="Period"/> returns need <see cref="Period"/> + 1 prices), for a window with an unusable price, and where the kernel refuses
/// (a flat window or a standard deviation not above 1e-15). Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.RollingSharpe)]
public class CoreRollingSharpeIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.RollingSharpeDefaultPeriod;

    public bool UseLogReturn { get; set; } = IndicatorDefaultConstants.RollingSharpeDefaultUseLogReturn;

    public bool Annualize { get; set; } = IndicatorDefaultConstants.RiskRatioAnnualizeDefault;

    public int PeriodsPerYear { get; set; } = IndicatorDefaultConstants.PeriodsPerYear;

    public decimal AnnualRiskFreeRate { get; set; } = IndicatorDefaultConstants.RiskRatioDefaultAnnualRiskFreeRate;

    public override string Name => Annualize ? $"Sharpe ({Period}, Ann)" : $"Sharpe ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreRollingSharpeParameter p)
        {
            p.Validate();
            Period = p.Period;
            UseLogReturn = p.UseLogReturn;
            Annualize = p.Annualize;
            PeriodsPerYear = p.PeriodsPerYear;
            AnnualRiskFreeRate = p.AnnualRiskFreeRate;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        var parameters = new CoreRollingSharpeParameter
        {
            Period = Period,
            UseLogReturn = UseLogReturn,
            Annualize = Annualize,
            PeriodsPerYear = PeriodsPerYear,
            AnnualRiskFreeRate = AnnualRiskFreeRate,
        };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        var returns = ReturnSeriesWindow.FromPrices(PriceDataHelper.ExtractPriceSeries(candles, PriceSource), UseLogReturn);

        // The annual rate over the bars of a year is the rate of one bar (a single conversion of the user setting to the kernel's double); the kernel multiplies by sqrt(periodsPerYear).
        double riskFreePerBar = (double)(AnnualRiskFreeRate / PeriodsPerYear);
        double kernelPeriodsPerYear = Annualize ? PeriodsPerYear : IndicatorDefaultConstants.UnannualizedPeriodsPerYear;
        for (int i = 0; i < returns.Count; i++)
        {
            decimal? ratio = null;
            if (i >= Period && returns.IsValid(i - Period + 1, i))
            {
                MathResult<double> result = DslFinanceMath.SharpeRatio(returns.Returns.AsSpan(i - Period + 1, Period), riskFreePerBar, kernelPeriodsPerYear);
                if (result.IsOk)
                {
                    ratio = IndicatorCalculationHelper.ToDecimalOrNull(result.Value);
                }
            }

            _values.Add(ratio);
        }

        return CreateAutomaticResult();
    }
}
