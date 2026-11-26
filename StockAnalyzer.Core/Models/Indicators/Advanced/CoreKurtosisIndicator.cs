using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Rolling sample excess kurtosis (<see cref="WindowMoments.SampleExcessKurtosis"/>: Fisher-adjusted <c>G2</c>, normal distribution = 0)
/// of the last <see cref="CoreReturnMomentIndicatorBase.Period"/> one-bar returns. Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.Kurtosis)]
public sealed class CoreKurtosisIndicator : CoreReturnMomentIndicatorBase
{
    protected override string DisplayTypeName => "Kurtosis";

    protected override double SelectMoment(in WindowMoments moments) => moments.SampleExcessKurtosis;

    protected override CoreReturnMomentParameterBase CreateParameter() => new CoreKurtosisParameter();
}
