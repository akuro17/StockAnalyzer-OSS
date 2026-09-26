using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[Obsolete("De-registered from IndicatorFactory in favor of KAMA.")]
public class CoreAmaIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => true;

    public int Period { get; set; } = IndicatorDefaultConstants.AmaPeriod;
    public int Fast { get; set; } = IndicatorDefaultConstants.AmaFastPeriod;
    public int Slow { get; set; } = IndicatorDefaultConstants.AmaSlowPeriod;

    public override string Name => $"AMA ({Period}, {Fast}, {Slow})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreAmaParameter p)
        {
            Period = p.Period;
            Fast = p.Fast;
            Slow = p.Slow;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (series == null || series.Count == 0) return IndicatorResult.Success(_values);

        var amaValues = IndicatorCalculationHelper.CalculateAma(series, Period, Fast, Slow);

        _values.Clear();
        _values.AddRange(amaValues);
        return IndicatorResult.Success(_values);
    }
}
