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

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        if (candles == null || candles.Count < Period)
        {
             if (candles != null) _values.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
            return IndicatorResult.Success(_values);
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        var smaValues = new List<decimal?>();
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1)
            {
                smaValues.Add(null);
            }
            else
            {
                smaValues.Add(priceSeries.Skip(i - Period + 1).Take(Period).Average(v => v ?? 0m));
            }
        }

        // Displace the SMA values
        for (int i = 0; i < candles.Count; i++)
        {
            if (i >= Displacement && i - Displacement < smaValues.Count && i - Displacement >= 0) // Ensure index validity
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
