using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

[StockAnalyzerIndicator(IndicatorType.YangZhangVolatility)]
public class CoreYangZhangVolatilityIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 10;
    public override string Name => $"Yang-Zhang Vol ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period; 
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        if (candles == null || candles.Count < Period)
        {
             if (candles != null) _values.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
            return IndicatorResult.Success(_values);
        }

        var k = 0.34m / (1.34m + (decimal)(Period + 1) / (Period - 1));

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
                continue;
            }

            decimal overnightVolatilitySum = 0;
            decimal openCloseVolatilitySum = 0;
            decimal highLowVolatilitySum = 0;

            for (int j = 0; j < Period; j++)
            {
                var index = i - j;
                var current = candles[index];
                var prevClose = (index > 0) ? candles[index - 1].Close : current.Open;

                // Overnight volatility component
                var logPrevCloseCurrentOpen = (decimal)Math.Log((double)(current.Open != 0 ? current.Open / prevClose : 1));
                overnightVolatilitySum += logPrevCloseCurrentOpen * logPrevCloseCurrentOpen;

                // Open-to-close volatility component
                var logCurrentCloseOpen = (decimal)Math.Log((double)(current.Open != 0 ? current.Close / current.Open : 1));
                openCloseVolatilitySum += logCurrentCloseOpen * logCurrentCloseOpen;

                // Rogers-Satchell (high/low) component
                var logHighLow = (decimal)Math.Log((double)(current.Low != 0 ? current.High / current.Low : 1));
                var logCloseOpen = (decimal)Math.Log((double)(current.Open != 0 ? current.Close / current.Open : 1));
                highLowVolatilitySum += logHighLow * logHighLow - 2 * logCloseOpen * logCloseOpen;
            }

            var overnightVariance = overnightVolatilitySum / (Period - 1);
            var openCloseVariance = openCloseVolatilitySum / (Period - 1);
            var rogersSatchellVariance = highLowVolatilitySum / (Period - 1);

            var variance = overnightVariance + k * openCloseVariance + (1 - k) * rogersSatchellVariance;

            _values.Add((decimal)Math.Sqrt((double)variance) * 100);
        }

        return IndicatorResult.Success(_values);
    }
}
