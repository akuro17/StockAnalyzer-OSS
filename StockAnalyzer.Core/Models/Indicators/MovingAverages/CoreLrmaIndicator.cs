using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.LRMA)]
public class CoreLrmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;

    public override string Name => $"LRMA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);
        if (candles.Count < Period)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
            return IndicatorResult.Success(_values);
        }

        for (int i = 0; i < Period - 1; i++)
        {
            _values.Add(null);
        }

        for (int i = Period - 1; i < candles.Count; i++)
        {
            decimal sumX = 0, sumY = 0, sumXy = 0, sumX2 = 0;
            for (int j = 0; j < Period; j++)
            {
                decimal y = candles[i - j].Close;
                // For LRMA, the time series is typically reversed, so the most recent data point has the smallest x.
                decimal x = j;

                sumX += x;
                sumY += y;
                sumXy += x * y;
                sumX2 += x * x;
            }

            decimal n = Period;
            decimal denominator = n * sumX2 - sumX * sumX;

            if (denominator == 0)
            {
                _values.Add(null);
                continue;
            }

            decimal b = (n * sumXy - sumX * sumY) / denominator;
            decimal a = (sumY - b * sumX) / n;

            // The LRMA value is the endpoint of the regression line.
            // Since we've reversed the series (x=0 is the current candle), the endpoint is simply 'a'.
            _values.Add(a);
        }

        return IndicatorResult.Success(_values);
    }
}
