using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.EfficiencyRatio)]
public class CoreEfficiencyRatioIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.EfficiencyRatioPeriod;

    public override string Name => $"ER ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreEfficiencyRatioParameter p)
        {
            Period = p.Period;
        }
        else if (parameters is CoreSmaParameter sp)
        {
            Period = sp.Period;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (series == null || series.Count == 0) return IndicatorResult.Success(_values);

        var erValues = IndicatorCalculationHelper.CalculateEfficiencyRatio(series, Period);

        _values.Clear();
        _values.AddRange(erValues);
        return IndicatorResult.Success(_values);
    }
}
