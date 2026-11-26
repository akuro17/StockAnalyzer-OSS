using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Shared implementation base for independent rolling return-moment indicators (<see cref="CoreSkewnessIndicator"/>, <see cref="CoreKurtosisIndicator"/>),
/// delegating moment calculation to <see cref="ReturnSeriesWindow"/> and <see cref="WindowMoments"/>.
/// </summary>
public abstract class CoreReturnMomentIndicatorBase : CoreIndicatorBase
{
    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.SkewnessKurtosisDefaultPeriod;

    public bool UseLogReturn { get; set; } = IndicatorDefaultConstants.SkewnessKurtosisDefaultUseLogReturn;

    protected abstract string DisplayTypeName { get; }

    public override string Name => UseLogReturn ? $"{DisplayTypeName} ({Period})" : $"{DisplayTypeName} ({Period}, simple)";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreReturnMomentParameterBase p)
        {
            p.Validate();
            Period = p.Period;
            UseLogReturn = p.UseLogReturn;
        }
    }

    protected abstract double SelectMoment(in WindowMoments moments);

    protected abstract CoreReturnMomentParameterBase CreateParameter();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        var parameters = CreateParameter();
        parameters.Period = Period;
        parameters.UseLogReturn = UseLogReturn;
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
                continue;
            }

            _values.Add(IndicatorCalculationHelper.ToDecimalOrNull(SelectMoment(in moments)));
        }

        return CreateAutomaticResult();
    }
}

/// <summary>
/// Rolling sample skewness (<see cref="WindowMoments.SampleSkewness"/>: Fisher-adjusted <c>G1</c>, normal distribution = 0)
/// of the last <see cref="CoreReturnMomentIndicatorBase.Period"/> one-bar returns. Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.Skewness)]
public sealed class CoreSkewnessIndicator : CoreReturnMomentIndicatorBase
{
    protected override string DisplayTypeName => "Skewness";

    protected override double SelectMoment(in WindowMoments moments) => moments.SampleSkewness;

    protected override CoreReturnMomentParameterBase CreateParameter() => new CoreSkewnessParameter();
}
