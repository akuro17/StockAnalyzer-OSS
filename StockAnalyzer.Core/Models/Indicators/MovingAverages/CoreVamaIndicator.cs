using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.VAMA)]
public class CoreVamaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;

    public override string Name => $"VAMA ({Period})";

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
        int requiredDataCount = 2 * Period - 1;
        if (candles.Count < requiredDataCount)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
            return IndicatorResult.Success(_values);
        }

        var closePrices = candles.Select(c => c.Close).ToList();
        var stdDevValues = StdDev(closePrices, Period);
        var smaOfStdDev = Sma(stdDevValues, Period);

        decimal? prevVama = null;

        for (int i = 0; i < candles.Count; i++)
        {
            // The first point where StdDev and SMA(StdDev) are non-null is at index (2 * period - 2)
            int firstValidIndex = 2 * Period - 2;

            if (i < firstValidIndex || smaOfStdDev[i] == null || smaOfStdDev[i] == 0)
            {
                _values.Add(null);
            }
            else
            {
                if (prevVama == null)
                {
                    // Seed the first VAMA value with an SMA of the close price
                    prevVama = closePrices.Skip(i - Period + 1).Take(Period).Average();
                }

                var volatilityRatio = stdDevValues[i]!.Value / smaOfStdDev[i]!.Value;
                var smoothingFactor = 2 / (Period * volatilityRatio + 1);
                var currentVama = prevVama!.Value + smoothingFactor * (closePrices[i] - prevVama!.Value);
                _values.Add(currentVama);
                prevVama = currentVama;
            }
        }

        return IndicatorResult.Success(_values);
    }

    private List<decimal?> StdDev(IReadOnlyList<decimal> data, int period)
    {
        var results = new List<decimal?>();
        for (int i = 0; i < data.Count; i++)
        {
            if (i < period - 1)
            {
                results.Add(null);
            }
            else
            {
                var slice = data.Skip(i - period + 1).Take(period).ToList();
                if (slice.Count < period)
                {
                    results.Add(null);
                    continue;
                }
                var avg = slice.Average();
                var sum = slice.Sum(d => (d - avg) * (d - avg));
                results.Add((decimal)Math.Sqrt((double)(sum / period)));
            }
        }
        return results;
    }

    private List<decimal?> Sma(IReadOnlyList<decimal?> data, int period)
    {
        var results = new List<decimal?>();
        for (int i = 0; i < data.Count; i++)
        {
            if (i < period - 1)
            {
                results.Add(null);
            }
            else
            {
                decimal? sum = 0;
                int count = 0;
                for (int j = 0; j < period; j++)
                {
                    var index = i - j;
                    if (index >= 0 && data[index].HasValue)
                    {
                        sum += data[index]!.Value;
                        count++;
                    }
                }

                if (count == period)
                {
                    results.Add(sum / period);
                }
                else
                {
                    results.Add(null);
                }
            }
        }
        return results;
    }
}
