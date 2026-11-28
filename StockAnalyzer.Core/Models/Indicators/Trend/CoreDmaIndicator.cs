using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.Dma)]
public class CoreDmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public int Displacement { get; set; } = 5;

    public override string Name => $"DMA ({Period}, {Displacement})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreDmaParameter p)
        {
            Period = p.Period;
            Displacement = p.Displacement;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        if (Period <= 0 || series.Count < Period)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, series.Count));
            return IndicatorResult.Success(_values);
        }

        var smaValues = IndicatorCalculationHelper.CalculateSmaWithNulls(series, Period);

        // Displace the SMA values
        for (int i = 0; i < series.Count; i++)
        {
            if (i >= Displacement && i - Displacement < smaValues.Count && i - Displacement >= 0)
            {
                _values.Add(smaValues[i - Displacement]);
            }
            else
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
