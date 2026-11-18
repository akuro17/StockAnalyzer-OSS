using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Rolling sample skewness and excess kurtosis of the last <see cref="Period"/> one-bar returns (<see cref="WindowMoments"/>: Fisher-adjusted
/// <c>G1</c> and <c>G2</c>, a normal distribution is 0 for both). Return basis: log returns when <see cref="UseLogReturn"/> is true, otherwise simple returns.
/// <c>Main</c> = G1 (dimensionless), <c>ExcessKurtosis</c> = G2 (dimensionless). Null for bars before bar <see cref="Period"/>, when the window holds an unusable price,
/// and when the window is flat (population variance not above <see cref="ReturnStatistics.VarianceEpsilon"/>). Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.SkewnessKurtosis)]
public class CoreSkewnessKurtosisIndicator : CoreIndicatorBase
{
    public const string ExcessKurtosisSeriesName = nameof(ExcessKurtosis);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.SkewnessKurtosisDefaultPeriod;

    public bool UseLogReturn { get; set; } = IndicatorDefaultConstants.SkewnessKurtosisDefaultUseLogReturn;

    public List<decimal?> ExcessKurtosis { get; } = new();

    public override string Name => UseLogReturn ? $"Skewness/Kurtosis ({Period})" : $"Skewness/Kurtosis ({Period}, simple)";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSkewnessKurtosisParameter p)
        {
            p.Validate();
            Period = p.Period;
            UseLogReturn = p.UseLogReturn;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        ExcessKurtosis.Clear();

        var parameters = new CoreSkewnessKurtosisParameter { Period = Period, UseLogReturn = UseLogReturn };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        var returns = ReturnSeriesWindow.FromPrices(PriceDataHelper.ExtractPriceSeries(candles, PriceSource), UseLogReturn);
        for (int i = 0; i < returns.Count; i++)
        {
            if (i < Period || !returns.IsValid(i - Period + 1, i)
                || !WindowMoments.TryCompute(returns.Returns.AsSpan(i - Period + 1, Period), FlatWindowRule.Returns, out WindowMoments moments))
            {
                _values.Add(null);
                ExcessKurtosis.Add(null);
                continue;
            }

            _values.Add(IndicatorCalculationHelper.ToDecimalOrNull(moments.SampleSkewness));
            ExcessKurtosis.Add(IndicatorCalculationHelper.ToDecimalOrNull(moments.SampleExcessKurtosis));
        }

        return CreateAutomaticResult();
    }
}
