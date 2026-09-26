using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.KAMA)]
public class CoreKamaIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => true;

    public int Period { get; set; } = IndicatorDefaultConstants.KamaPeriod;
    public int Fast { get; set; } = IndicatorDefaultConstants.KamaFastPeriod;
    public int Slow { get; set; } = IndicatorDefaultConstants.KamaSlowPeriod;

    public override string Name => $"KAMA ({Period}, {Fast}, {Slow})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreKamaParameter p)
        {
            Period = p.Period;
            Fast = p.Fast;
            Slow = p.Slow;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (series == null || series.Count == 0) return IndicatorResult.Success(_values);

        var kamaValues = IndicatorCalculationHelper.CalculateKama(series, Period, Fast, Slow);

        _values.Clear();
        _values.AddRange(kamaValues);
        return IndicatorResult.Success(_values);
    }
}
