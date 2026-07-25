using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.RSI)]
public class CoreRsiIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;

    public override string Name => $"RSI ({Period})";
    public override bool IsOverlay => false;

    public List<decimal?> BullishSignals { get; } = new();
    public List<decimal?> BearishSignals { get; } = new();


    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter param)
        {
            Period = param.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles.Count < 2) return IndicatorResult.Success(_values);

        // RSI is recursive (Wilder's Smoothing), so we use a warmup/overlap strategy for parallel chunks.
        // Parallelization Logic:
        // Each chunk calculates its own RSI sequence.
        // To mitigate the dependency on previous state, each chunk starts calculating 'Overlap' steps earlier.
        // Overlap = Period * EmaConvergenceMultiplier is usually sufficient for convergence.

        var results = CalculateParallel(candles, CalculateSegment);

        _values.Clear();
        _values.AddRange(results);

        // Signal Detection
        BullishSignals.Clear();
        BearishSignals.Clear();
        decimal? prevRsi = null;
        foreach (var rsi in results)
        {
            if (prevRsi.HasValue && rsi.HasValue)
            {
                // Bullish: exit oversold (< 30)
                if (prevRsi <= 30m && rsi > 30m) BullishSignals.Add(1m);
                else BullishSignals.Add(null);

                // Bearish: exit overbought (> 70)
                if (prevRsi >= 70m && rsi < 70m) BearishSignals.Add(1m);
                else BearishSignals.Add(null);
            }
            else
            {
                BullishSignals.Add(null);
                BearishSignals.Add(null);
            }
            prevRsi = rsi;
        }

        return CreateAutomaticResult();
    }

    private List<decimal?> CalculateSegment(IReadOnlyList<CoreCandleData> candles, int start, int end)
    {
        var results = new List<decimal?>(end - start);

        // 1. Determining Warmup Start
        // If we are at the very beginning (start == 0), no warmup needed (follow standard logic).
        // If we are in the middle, we need to look back to prime the recursive calculation.
        
        int overlap = Period * ChartConstants.EmaConvergenceMultiplier; 
        int calcStart = Math.Max(0, start - overlap);

        // Local state for RSI
        decimal avgGain = 0;
        decimal avgLoss = 0;
        bool isInitialized = false;

        // Loop from calcStart to end
        for (int i = calcStart; i < end; i++)
        {
            // Skip index 0 (diff requires i-1)
            // But if calcStart == 0, we must handle i=0 case (add null)
            if (i == 0)
            {
                if (i >= start) results.Add(null);
                continue;
            }

            decimal change = candles[i].Close - candles[i - 1].Close;
            decimal gain = change > 0 ? change : 0;
            decimal loss = change < 0 ? -change : 0;

            // Determine relative index from the "Logic Start"
            // The logic effectively starts accumulating at calcStart (or index 1 if calcStart=0)
            // But Wilder's RSI logic is state-dependent based on strict count.
            // We need to simulate the state machine:
            // State 1: Accumulating first 'Period' gains/losses
            // State 2: Initial Average Calculation
            // State 3: Wilder's Smoothing

            // How many steps have we processed in this local sequence?
            // Local Step 0 is at calcStart (or 1 if calcStart=0)
            int localStep = i - calcStart;
             if (calcStart == 0) localStep = i - 1; // Correction for 0-index skip

            decimal? currentRsi = null;

            if (localStep < Period)
            {
                // Accumulate
                avgGain += gain;
                avgLoss += loss;
                currentRsi = null;
            }
            else if (localStep == Period)
            {
                // Initial Average
                avgGain = (avgGain + gain) / Period;
                avgLoss = (avgLoss + loss) / Period;
                decimal rs = avgLoss == 0 ? 100 : avgGain / avgLoss;
                currentRsi = 100 - (100 / (1 + rs));
                isInitialized = true;
            }
            else
            {
                // Wilder's Smoothing
                if (!isInitialized)
                {
                     // Should not happen if logic is correct, but safe fallback logic
                     // effectively treated as simple average in some variations, but we stick to strict RSI
                     // If we jumped in, we might be uninitialized? No, we started at calcStart.
                }

                // If calcStart > 0, the "Accumulation" phase is fake (based on incomplete history), 
                // but it primes the variables.
                avgGain = (avgGain * (Period - 1) + gain) / Period;
                avgLoss = (avgLoss * (Period - 1) + loss) / Period;
                
                decimal rs = avgLoss == 0 ? 100 : avgGain / avgLoss;
                currentRsi = 100 - (100 / (1 + rs));
            }

            // Only output if we are within the requested range [start, end)
            if (i >= start)
            {
                results.Add(currentRsi);
            }
        }

        return results;
    }
}
