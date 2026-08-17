using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.EMA)]
public class CoreEmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;

    public override string Name => $"EMA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter param)
        {
            Period = param.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        // EMA is recursive, so we use an overlap strategy.
        // We calculate chunks in parallel, but each chunk starts calculating 'Overlap' steps earlier
        // to warm up the EMA value.
        var results = CalculateParallel(candles, CalculateSegment);

        _values.Clear();
        _values.AddRange(results);

        return IndicatorResult.Success(_values);
    }

    private List<decimal?> CalculateSegment(IReadOnlyList<CoreCandleData> candles, int start, int end)
    {
        var results = new List<decimal?>(end - start);

        // 1. Determine Warmup Start
        // Overlap = Period * EmaConvergenceMultiplier is usually sufficient for 99.9% convergence.
        int overlap = Period * ChartConstants.EmaConvergenceMultiplier;
        int calcStart = Math.Max(0, start - overlap);

        // Local state
        decimal? ema = null;
        decimal multiplier = 2m / (Period + 1);

        // Loop from calcStart to end
        for (int i = calcStart; i < end; i++)
        {
            // Logic for EMA initialization matches sequential logic:
            // First valid value is at index (Period - 1) which is SMA.
            // Indices before that are null.

            if (i < Period - 1)
            {
                ema = null;
            }
            else if (i == Period - 1)
            {
                // Initial SMA
                decimal sum = 0;
                for (int j = 0; j < Period; j++)
                    sum += candles[i - j].Close;
                ema = sum / Period;
            }
            else
            {
                // Recursive EMA
                 if (ema.HasValue)
                {
                    ema = (candles[i].Close - ema.Value) * multiplier + ema.Value;
                }
                else
                {
                    // Should not happen if i > Period-1, unless we jumped into the middle without enough history?
                    // If calcStart > Period-1, we need to initialize somehow.
                    // If we are warming up (calcStart > 0), the first value we compute might be effectively a "guess" or SMA?
                    // Wilder's RSI approach used accumulation.
                    // For EMA, usually we need an SMA start. 
                    // If calcStart > Period, we effectively treat the first processed value as a seed (SMA equivalent) or just current price?
                    // Valid EMA needs infinite history or SMA seed.
                    // With overlap, we assume the error from "bad seed" decays.
                    // So at calcStart, we can initialize with Price (as if it was the EMA) or SMA of previous Period candles.
                    
                    if (i >= Period) 
                    {
                         // If we jumped in middle, we can try to take SMA of previous Period if available.
                         // But simple approach for warmup: use SMA if we have enough data, or just Price.
                         // Standard approximation: Use Price as first EMA if history missing.
                         // Better: Use SMA of last Period candles relative to i.
                         if (i >= Period - 1)
                         {
                             decimal sum = 0;
                             for (int j = 0; j < Period; j++)
                                sum += candles[i - j].Close;
                             ema = sum / Period;
                         }
                         else
                         {
                             // Not enough data even for SMA? (Should be covered by i < Period - 1 check)
                             ema = null;
                         }
                    }
                }
            }

            // Store result if within requested range
            if (i >= start)
            {
                results.Add(ema);
            }
        }

        return results;
    }
}
