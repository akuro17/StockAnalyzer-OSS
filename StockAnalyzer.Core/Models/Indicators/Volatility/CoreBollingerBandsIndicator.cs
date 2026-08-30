using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

[StockAnalyzerIndicator(IndicatorType.BB)]
public class CoreBollingerBandsIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public decimal StdDevMultiplier { get; set; } = 2.0m;

    public override string Name => $"BB ({Period}, {StdDevMultiplier})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreBollingerBandsParameter p)
        {
            Period = p.Period;
            StdDevMultiplier = p.StdDevMultiplier;
        }
    }

    // Well-known series names for Bollinger Bands
    public const string MiddleSeriesName = IndicatorResult.MainSeriesName;
    public const string UpperSeriesName = "Upper";
    public const string LowerSeriesName = "Lower";

    // Middle, Upper, Lower bands
    public IReadOnlyList<decimal?> MiddleBand => _values;
    public List<decimal?> UpperBand { get; } = new();
    public List<decimal?> LowerBand { get; } = new();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        // Define the result type for parallel calculation
        // Tuple: (Middle, Upper, Lower)
        var results = CalculateParallel(candles, CalculateSegment);

        // Clear and Populate
        _values.Clear();
        UpperBand.Clear();
        LowerBand.Clear();

        // Capacity optimization
        if (_values is List<decimal?> vList) vList.Capacity = results.Count;
        UpperBand.Capacity = results.Count;
        LowerBand.Capacity = results.Count;

        foreach (var (m, u, l) in results)
        {
            _values.Add(m);
            UpperBand.Add(u);
            LowerBand.Add(l);
        }

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { MiddleSeriesName, _values },
            { UpperSeriesName, UpperBand },
            { LowerSeriesName, LowerBand }
        });
    }

    private List<(decimal? Middle, decimal? Upper, decimal? Lower)> CalculateSegment(IReadOnlyList<CoreCandleData> candles, int start, int end)
    {
        var results = new List<(decimal? Middle, decimal? Upper, decimal? Lower)>(end - start);
        int period = Period;
        decimal multiplier = StdDevMultiplier;

        // Optimization: Use locally accumulated sums if possible, 
        // but for Standard Deviation, naive O(N*Period) is safer and easier to parallelize without numerical instability of running variance algorithms.
        // Given Phase 3.6 scope, we focus on safe parallelization.

        for (int i = start; i < end; i++)
        {
            if (i < period - 1)
            {
                results.Add((null, null, null));
                continue;
            }

            // SMA
            decimal sum = 0;
            for (int j = 0; j < period; j++)
            {
                sum += candles[i - j].Close;
            }
            decimal sma = sum / period;

            // Standard Deviation
            decimal variance = 0;
            for (int j = 0; j < period; j++)
            {
                decimal diff = candles[i - j].Close - sma;
                variance += diff * diff;
            }
            
            // Sqrt is expensive, parallelization helps here
            decimal stdDev = (decimal)System.Math.Sqrt((double)(variance / period));

            decimal upper = sma + multiplier * stdDev;
            decimal lower = sma - multiplier * stdDev;

            results.Add((sma, upper, lower));
        }

        return results;
    }
}
