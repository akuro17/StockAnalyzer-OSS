using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.KylesLambda)]
public class CoreKylesLambdaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public override string Name => $"Kyle's Lambda ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
         if (parameters is CoreSmaParameter p) Period = p.Period;
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        
        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        for(int i=0; i<candles.Count; i++)
        {
            if (i < Period)
            {
                _values.Add(null);
                continue;
            }

            // Lambda = Slope of Price Change / Volume?
            // Simplified: Sum(|DeltaP|) / Sum(Volume) over Period
            decimal sumAbsOnyx = 0;
            decimal sumVol = 0;

            for(int j=0; j<Period; j++)
            {
                sumAbsOnyx += Math.Abs((priceSeries[i-j] ?? 0m) - (priceSeries[i-j-1] ?? 0m));
                sumVol += candles[i-j].Volume;
            }
            
            if (sumVol == 0) _values.Add(0);
            else _values.Add(sumAbsOnyx / sumVol * 1000000); // Scale up
        }

        return IndicatorResult.Success(_values);
    }
}
