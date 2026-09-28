using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.ZScore)]
public class CoreZScoreIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public override string Name => $"Z-Score ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
         if (parameters is CoreSmaParameter p) Period = p.Period;
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

         var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
         for(int i=0; i<candles.Count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
                continue;
            }

            // Mean and StdDev
            decimal sum = 0;
            for(int j=0; j<Period; j++) sum += priceSeries[i-j] ?? 0m;
            decimal mean = sum / Period;

            decimal sumSqDiff = 0;
             for(int j=0; j<Period; j++)
             {
                 decimal diff = (priceSeries[i-j] ?? 0m) - mean;
                 sumSqDiff += diff * diff;
             }
             decimal stdDev = (decimal)Math.Sqrt((double)(sumSqDiff / Period));

             if (stdDev == 0) _values.Add(0);
             else _values.Add(((priceSeries[i] ?? 0m) - mean) / stdDev);
        }

        return IndicatorResult.Success(_values);
    }
}
