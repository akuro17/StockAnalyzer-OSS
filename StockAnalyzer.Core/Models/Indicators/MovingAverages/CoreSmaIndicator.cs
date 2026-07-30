using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.SMA)]
public class CoreSmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;

    public override string Name => $"SMA ({Period})";

    /// <summary>
    /// Configures the indicator with the given parameters.
    /// </summary>
    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter smaParam)
        {
            Period = smaParam.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        // Use the parallel calculation infrastructure provided by CoreIndicatorBase
        var results = CalculateParallel(candles, CalculateSegment);
        
        _values.Clear();
        _values.AddRange(results);

        return IndicatorResult.Success(_values);
    }

    /// <summary>
    /// Calculates SMA for a specific segment/chunk of data.
    /// This method is designed to be thread-safe and independent (except for read-only access to source candles).
    /// </summary>
    private List<decimal?> CalculateSegment(IReadOnlyList<CoreCandleData> candles, int start, int end)
    {
        var results = new List<decimal?>(end - start);

        // Optimization: Maintain running sum to avoid re-summing window for every point
        decimal currentSum = 0;
        bool isSumValid = false;
        int period = Period; // Local copy for performance

        for (int i = start; i < end; i++)
        {
            // 1. Initial invalid period check
            if (i < period - 1)
            {
                results.Add(null);
                continue;
            }

            // 2. Initialize or Update Sum
            if (!isSumValid)
            {
                // First calculation in this chunk (or after invalid zone): Compute full sum
                currentSum = 0;
                for (int j = 0; j < period; j++)
                {
                    currentSum += candles[i - j].Close;
                }
                isSumValid = true;
            }
            else
            {
                // Sliding window: Remove oldest, Add newest
                // Oldest index was (i-1) - (period-1) = i - period
                currentSum -= candles[i - period].Close;
                currentSum += candles[i].Close;
            }

            // 3. Store Average
            results.Add(currentSum / period);
        }

        return results;
    }
}
