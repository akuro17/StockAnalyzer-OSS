using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.CoppockCurve)]
public class CoreCoppockCurveIndicator : CoreIndicatorBase
{
    public int LongRocPeriod { get; set; } = 14;
    public int ShortRocPeriod { get; set; } = 11;
    public int WmaPeriod { get; set; } = 10;

    public override string Name => $"Coppock Curve ({LongRocPeriod},{ShortRocPeriod},{WmaPeriod})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreCoppockCurveParameter p)
        {
            LongRocPeriod = p.LongRocPeriod;
            ShortRocPeriod = p.ShortRocPeriod;
            WmaPeriod = p.WmaPeriod;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        // Coppock = WMA[10] of (ROC[14] + ROC[11])
        int maxRoc = Math.Max(LongRocPeriod, ShortRocPeriod);
        
        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        var rocSum = new List<decimal>();

        for (int i = 0; i < candles.Count; i++)
        {
           if (i < maxRoc)
           {
               rocSum.Add(0); // Placeholder
               continue;
           }

           decimal longBase = priceSeries[i - LongRocPeriod] ?? 0m;
           decimal shortBase = priceSeries[i - ShortRocPeriod] ?? 0m;
           decimal current = priceSeries[i] ?? 0m;
           decimal rocLong = (current - longBase) / longBase * 100;
           decimal rocShort = (current - shortBase) / shortBase * 100;
           rocSum.Add(rocLong + rocShort);
        }

        // Apply WMA to rocSum
        // WMA calculation... 
        // For simplicity, reusing CoreWma logic would be best, but implementing inline here for now
        // Or better: use helper if available. 
        // Implementing standard WMA loop for now.
        
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < maxRoc + WmaPeriod - 1)
            {
                _values.Add(null);
                continue;
            }

            decimal numerator = 0;
            decimal denominator = 0;
            for (int j = 0; j < WmaPeriod; j++)
            {
                decimal val = rocSum[i - j];
                decimal weight = WmaPeriod - j;
                numerator += val * weight;
                denominator += weight;
            }
            _values.Add(numerator / denominator);
        }

        return IndicatorResult.Success(_values);
    }
}
